using System.Globalization;
using Microsoft.Extensions.Logging;

namespace NinjaBotHelpers.Wago;

/// <summary>
/// Client for fetching WoW data from wago.tools.
/// Wago.tools provides bulk exports of WoW database tables as CSV files.
/// </summary>
public class WagoToolsClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<WagoToolsClient> _logger;

    private const string ItemSparseUrl = "https://wago.tools/db2/ItemSparse/csv";

    public WagoToolsClient(HttpClient httpClient, ILogger<WagoToolsClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    /// <summary>
    /// Downloads and parses all items from wago.tools ItemSparse CSV.
    /// Uses streaming to avoid loading entire file into memory at once.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Parsed items result with statistics</returns>
    public async Task<WagoItemsResult> GetAllItemsAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Fetching item data from wago.tools...");

        var result = new WagoItemsResult();

        try
        {
            using var response = await _httpClient.GetAsync(ItemSparseUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            var contentLength = response.Content.Headers.ContentLength;
            _logger.LogInformation("Response received. Content-Length: {ContentLength} bytes",
                contentLength?.ToString("N0") ?? "unknown");

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream);

            // Read header line to get column indices
            var headerLine = await reader.ReadLineAsync(cancellationToken);
            if (string.IsNullOrEmpty(headerLine))
            {
                throw new InvalidDataException("CSV file has no header row");
            }

            var columnIndices = ParseHeaderRow(headerLine);

            // Parse data rows
            string? line;
            int lineNumber = 1;
            int progressInterval = 10000;

            while ((line = await reader.ReadLineAsync(cancellationToken)) != null)
            {
                lineNumber++;
                result.TotalRows++;

                if (string.IsNullOrWhiteSpace(line))
                    continue;

                try
                {
                    var item = ParseItemRow(line, columnIndices);
                    if (item != null)
                    {
                        result.Items.Add(item);
                    }
                }
                catch (Exception ex)
                {
                    result.FailedRows++;
                    if (result.FailedRows <= 10)
                    {
                        _logger.LogWarning("Failed to parse line {LineNumber}: {Error}", lineNumber, ex.Message);
                    }
                }

                if (result.TotalRows % progressInterval == 0)
                {
                    _logger.LogInformation("Progress: {Count} rows processed, {Items} items parsed",
                        result.TotalRows, result.Items.Count);
                }
            }

            _logger.LogInformation("Completed fetching items from wago.tools: {Items} items parsed from {Total} rows ({Failed} failed)",
                result.Items.Count, result.TotalRows, result.FailedRows);

            return result;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error fetching data from wago.tools");
            throw;
        }
        catch (TaskCanceledException ex) when (ex.CancellationToken == cancellationToken)
        {
            _logger.LogWarning("Wago.tools fetch was cancelled");
            throw;
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogError(ex, "Timeout fetching data from wago.tools");
            throw new TimeoutException("Request to wago.tools timed out", ex);
        }
    }

    /// <summary>
    /// Parse the header row to find column indices for required fields
    /// </summary>
    private ColumnIndices ParseHeaderRow(string headerLine)
    {
        var columns = ParseCsvLine(headerLine);
        var indices = new ColumnIndices();

        for (int i = 0; i < columns.Length; i++)
        {
            var col = columns[i].Trim();
            switch (col)
            {
                case "ID":
                    indices.Id = i;
                    break;
                case "Display_lang":
                    indices.DisplayLang = i;
                    break;
                case "OverallQualityID":
                    indices.OverallQualityId = i;
                    break;
                case "ItemLevel":
                    indices.ItemLevel = i;
                    break;
                case "RequiredLevel":
                    indices.RequiredLevel = i;
                    break;
                case "InventoryType":
                    indices.InventoryType = i;
                    break;
                case "ExpansionID":
                    indices.ExpansionId = i;
                    break;
            }
        }

        // Validate required columns exist
        if (indices.Id < 0)
            throw new InvalidDataException("CSV missing required column: ID");
        if (indices.DisplayLang < 0)
            throw new InvalidDataException("CSV missing required column: Display_lang");

        _logger.LogDebug("Parsed CSV headers: ID={Id}, Display_lang={DisplayLang}, OverallQualityID={Quality}, " +
                         "ItemLevel={ItemLevel}, RequiredLevel={RequiredLevel}, InventoryType={InventoryType}, ExpansionID={ExpansionId}",
            indices.Id, indices.DisplayLang, indices.OverallQualityId, indices.ItemLevel,
            indices.RequiredLevel, indices.InventoryType, indices.ExpansionId);

        return indices;
    }

    /// <summary>
    /// Parse a single item row from the CSV
    /// </summary>
    private WagoItem? ParseItemRow(string line, ColumnIndices indices)
    {
        var columns = ParseCsvLine(line);

        // Get ID (required)
        if (indices.Id >= columns.Length || !long.TryParse(columns[indices.Id], out var id))
            return null;

        // Get name (required, skip items without names)
        var name = indices.DisplayLang < columns.Length ? columns[indices.DisplayLang].Trim() : string.Empty;
        if (string.IsNullOrEmpty(name))
            return null;

        // Remove quotes if present
        if (name.StartsWith('"') && name.EndsWith('"'))
            name = name[1..^1];

        // Truncate long names
        if (name.Length > 255)
            name = name[..255];

        var item = new WagoItem
        {
            Id = id,
            Name = name
        };

        // Parse optional numeric fields
        if (indices.OverallQualityId >= 0 && indices.OverallQualityId < columns.Length
            && int.TryParse(columns[indices.OverallQualityId], out var qualityId))
        {
            item.QualityId = qualityId;
        }

        if (indices.ItemLevel >= 0 && indices.ItemLevel < columns.Length
            && int.TryParse(columns[indices.ItemLevel], out var itemLevel))
        {
            item.ItemLevel = itemLevel;
        }

        if (indices.RequiredLevel >= 0 && indices.RequiredLevel < columns.Length
            && int.TryParse(columns[indices.RequiredLevel], out var requiredLevel))
        {
            item.RequiredLevel = requiredLevel;
        }

        if (indices.InventoryType >= 0 && indices.InventoryType < columns.Length
            && int.TryParse(columns[indices.InventoryType], out var inventoryTypeId))
        {
            item.InventoryTypeId = inventoryTypeId;
        }

        if (indices.ExpansionId >= 0 && indices.ExpansionId < columns.Length
            && int.TryParse(columns[indices.ExpansionId], out var expansionId))
        {
            item.ExpansionId = expansionId;
        }

        return item;
    }

    /// <summary>
    /// Parse a CSV line handling quoted fields with commas
    /// </summary>
    private string[] ParseCsvLine(string line)
    {
        var result = new List<string>();
        var inQuotes = false;
        var currentField = new System.Text.StringBuilder();

        for (int i = 0; i < line.Length; i++)
        {
            var c = line[i];

            if (c == '"')
            {
                // Check for escaped quote
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    currentField.Append('"');
                    i++; // Skip next quote
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (c == ',' && !inQuotes)
            {
                result.Add(currentField.ToString());
                currentField.Clear();
            }
            else
            {
                currentField.Append(c);
            }
        }

        // Add last field
        result.Add(currentField.ToString());

        return result.ToArray();
    }

    /// <summary>
    /// Holds column indices for required CSV fields
    /// </summary>
    private class ColumnIndices
    {
        public int Id { get; set; } = -1;
        public int DisplayLang { get; set; } = -1;
        public int OverallQualityId { get; set; } = -1;
        public int ItemLevel { get; set; } = -1;
        public int RequiredLevel { get; set; } = -1;
        public int InventoryType { get; set; } = -1;
        public int ExpansionId { get; set; } = -1;
    }
}
