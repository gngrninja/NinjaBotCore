#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NinjaBotCore.Models.Wow.Forever;

namespace NinjaBotCore.Services
{
    public interface IForeverPrices
    {
        /// <summary>The region's prices, or null when TradeSkillMaster has never answered for it.</summary>
        Task<ForeverPriceSheet?> GetAsync(ForeverRegion region, CancellationToken cancellationToken = default);

        /// <summary>The prices if they are already loaded; never waits. Autocomplete uses this.</summary>
        ForeverPriceSheet? IfLoaded(ForeverRegion region);
    }

    /// <summary>
    /// Forever auction prices from TradeSkillMaster's public files: one CSV per region, rebuilt about
    /// once a day. TSM allows bots to use it with the credit in <see cref="Credit"/>, asks that a file
    /// is downloaded at most once per update, and may change or stop the files at any time, so a
    /// missing sheet is an ordinary answer. The numbers are regional: an average over every auction
    /// house of every ruleset and faction, not what one house is asking right now.
    /// </summary>
    public class ForeverPrices : IForeverPrices
    {
        /// <summary>TSM's required credit, as markdown.</summary>
        public const string Credit = "Pricing data from [TradeSkillMaster](https://tradeskillmaster.com)";

        public const string ClientName = "ForeverPrices";

        // The files are served with a four-hour max-age; asking sooner returns the same bytes.
        private static readonly TimeSpan Refetch = TimeSpan.FromHours(4);
        private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
        // Guards against a changed file being read as prices: a real sheet has thousands of rows.
        private const int MaxBytes = 16 * 1024 * 1024;

        private sealed class Slot
        {
            public readonly SemaphoreSlim Gate = new(1, 1);
            public ForeverPriceSheet? Sheet;
            public string? ETag;
            // Ticks, so a reader outside the gate never sees half a value.
            public long NextFetchTicks;
        }

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<ForeverPrices> _logger;
        private readonly TimeProvider _clock;
        private readonly ConcurrentDictionary<ForeverRegion, Slot> _slots = new();

        public ForeverPrices(IHttpClientFactory httpClientFactory, ILogger<ForeverPrices> logger)
            : this(httpClientFactory, logger, TimeProvider.System) { }

        internal ForeverPrices(IHttpClientFactory httpClientFactory, ILogger<ForeverPrices> logger, TimeProvider clock)
        {
            _httpClientFactory = httpClientFactory;
            _logger = logger;
            _clock = clock;
        }

        public static string Url(ForeverRegion region)
            => $"https://public-data.tradeskillmaster.com/forever/{region.ToString().ToLowerInvariant()}/region/items.csv";

        public ForeverPriceSheet? IfLoaded(ForeverRegion region)
            => _slots.TryGetValue(region, out var slot) ? Volatile.Read(ref slot.Sheet) : null;

        public async Task<ForeverPriceSheet?> GetAsync(ForeverRegion region, CancellationToken cancellationToken = default)
        {
            var slot = _slots.GetOrAdd(region, _ => new Slot());
            if (_clock.GetUtcNow().UtcTicks < Volatile.Read(ref slot.NextFetchTicks)) return Volatile.Read(ref slot.Sheet);

            await slot.Gate.WaitAsync(cancellationToken);
            try
            {
                if (_clock.GetUtcNow().UtcTicks < Volatile.Read(ref slot.NextFetchTicks)) return Volatile.Read(ref slot.Sheet);

                var fetched = await FetchAsync(region, slot.ETag, cancellationToken);
                if (fetched.Sheet != null)
                {
                    Volatile.Write(ref slot.Sheet, fetched.Sheet);
                    slot.ETag = fetched.ETag;
                }
                // "Not modified" and a fresh sheet both wait out the full interval; a failure tries again sooner
                // and keeps serving the last sheet meanwhile.
                Volatile.Write(ref slot.NextFetchTicks, (_clock.GetUtcNow() + (fetched.Ok ? Refetch : RetryAfterFailure)).UtcTicks);
                return slot.Sheet;
            }
            finally
            {
                slot.Gate.Release();
            }
        }

        private async Task<(bool Ok, ForeverPriceSheet? Sheet, string? ETag)> FetchAsync(
            ForeverRegion region, string? etag, CancellationToken cancellationToken)
        {
            try
            {
                using var client = _httpClientFactory.CreateClient(ClientName);
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                budget.CancelAfter(Timeout);
                using var request = new HttpRequestMessage(HttpMethod.Get, Url(region));
                request.Headers.UserAgent.ParseAdd("NinjaBot/3 (+https://bot.gnja.io)");
                if (etag != null && EntityTagHeaderValue.TryParse(etag, out var tag)) request.Headers.IfNoneMatch.Add(tag);

                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, budget.Token);
                if (response.StatusCode == HttpStatusCode.NotModified) return (true, null, etag);
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    _logger.LogWarning("TradeSkillMaster returned {Status} for Forever {Region} prices", (int)response.StatusCode, region);
                    return (false, null, etag);
                }
                if (response.Content.Headers.ContentLength > MaxBytes)
                {
                    _logger.LogWarning("TradeSkillMaster's Forever {Region} price file is {Bytes} bytes, more than the bot reads", region, response.Content.Headers.ContentLength);
                    return (false, null, etag);
                }

                var bytes = await response.Content.ReadAsByteArrayAsync(budget.Token);
                if (bytes.Length > MaxBytes) return (false, null, etag);
                var sheet = Parse(region, Encoding.UTF8.GetString(bytes));
                if (sheet == null)
                {
                    _logger.LogWarning("TradeSkillMaster's Forever {Region} price file isn't in the shape the bot reads", region);
                    return (false, null, etag);
                }

                _logger.LogInformation("Forever {Region} prices loaded: {Items} items, scanned {ScannedAt}", region, sheet.Items.Count, sheet.ScannedAt);
                return (true, sheet, response.Headers.ETag?.ToString());
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Fetching Forever {Region} prices failed: {Type}: {Message}", region, ex.GetType().Name, ex.Message);
                return (false, null, etag);
            }
        }

        /// <summary>Reads TSM's CSV by its header names. Null when the header isn't theirs or no row could be read.</summary>
        public static ForeverPriceSheet? Parse(ForeverRegion region, string csv)
        {
            var rows = Rows(csv);
            if (rows.Count < 2) return null;

            var header = rows[0].Select((name, index) => (Name: name.Trim().TrimStart('﻿'), index))
                .GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().index, StringComparer.OrdinalIgnoreCase);
            if (!header.TryGetValue("itemId", out var idColumn) || !header.TryGetValue("marketValue", out var marketColumn)) return null;

            int Column(string name) => header.TryGetValue(name, out var index) ? index : -1;
            var nameColumn = Column("name");
            var historicalColumn = Column("historical");
            var saleColumn = Column("avgSalePrice");
            var rateColumn = Column("saleRate");
            var soldColumn = Column("soldPerDay");
            var updatedColumn = Column("updatedAt");

            static string Cell(List<string> row, int column) => column >= 0 && column < row.Count ? row[column] : string.Empty;
            static long Copper(string value)
                => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && number > 0 && number < long.MaxValue
                    ? (long)Math.Round(number)
                    : 0;
            static double Ratio(string value)
                => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && number > 0 && !double.IsInfinity(number)
                    ? number
                    : 0;

            var items = new Dictionary<int, ForeverPrice>(rows.Count);
            DateTimeOffset? scannedAt = null;
            foreach (var row in rows.Skip(1))
            {
                if (!int.TryParse(Cell(row, idColumn), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) || id <= 0) continue;
                items[id] = new ForeverPrice(
                    id,
                    Cell(row, nameColumn).Trim(),
                    Copper(Cell(row, marketColumn)),
                    Copper(Cell(row, historicalColumn)),
                    Copper(Cell(row, saleColumn)),
                    Ratio(Cell(row, rateColumn)),
                    Ratio(Cell(row, soldColumn)));

                if (scannedAt == null && DateTimeOffset.TryParse(Cell(row, updatedColumn), CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var updated))
                    scannedAt = updated;
            }
            return items.Count == 0 ? null : new ForeverPriceSheet(region, scannedAt, items);
        }

        /// <summary>CSV rows with quoted cells, doubled quotes and either line ending.</summary>
        private static List<List<string>> Rows(string csv)
        {
            var rows = new List<List<string>>();
            var row = new List<string>();
            var cell = new StringBuilder();
            var quoted = false;

            void EndCell()
            {
                row.Add(cell.ToString());
                cell.Clear();
            }
            void EndRow()
            {
                EndCell();
                if (row.Count > 1 || row[0].Length > 0) rows.Add(row);
                row = new List<string>();
            }

            for (var i = 0; i < csv.Length; i++)
            {
                var ch = csv[i];
                if (quoted)
                {
                    if (ch != '"') cell.Append(ch);
                    else if (i + 1 < csv.Length && csv[i + 1] == '"')
                    {
                        cell.Append('"');
                        i++;
                    }
                    else quoted = false;
                }
                else if (ch == '"') quoted = true;
                else if (ch == ',') EndCell();
                else if (ch == '\n') EndRow();
                else if (ch != '\r') cell.Append(ch);
            }
            if (cell.Length > 0 || row.Count > 0) EndRow();
            return rows;
        }
    }
}
