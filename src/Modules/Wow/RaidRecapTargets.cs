#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace NinjaBotCore.Modules.Wow;

/// <summary>Who one player's damage or healing went to on one kill.</summary>
public interface IRaidRecapTargetSource
{
    /// <summary>
    /// The damage or healing table for one kill, from one source, viewed by target. Returns
    /// the raw table; <see cref="RaidRecapTargets.Parse"/> reads it.
    /// </summary>
    Task<JObject> GetRaidRecapTargetsAsync(
        RaidRecapReport report,
        RaidRecapFight fight,
        bool healing,
        int sourceId,
        CancellationToken cancellationToken = default);
}

/// <summary>One target and its share of a player's damage or healing.</summary>
public sealed record RaidRecapTarget(string Name, double Share, bool Self)
{
    /// <summary>A player's pet. Pet names are typed by players, so the card shows "pet".</summary>
    public bool Pet { get; init; }

    /// <summary>The damage or healing that went to this target over the kill.</summary>
    public double? Amount { get; init; }
}

public static class RaidRecapTargets
{
    public const int Shown = 2;
    public const int MaxEntries = 500;

    /// <summary>How far the table's total may be from the player's own total before the line is dropped.</summary>
    public const double TotalTolerance = 0.10;

    /// <summary>
    /// The top targets in a by-target table, each as a share of everything in it. Rows with
    /// the same name, such as a boss made of several creatures, are counted as one target.
    /// Returns null when the table is not in the expected shape, or when its total is more
    /// than <see cref="TotalTolerance"/> away from <paramref name="expectedTotal"/>, the
    /// player's own total from the damage or healing table. A mismatch means the shares
    /// would not describe the number on the card, for example if pets were counted in one
    /// table and not the other, so the card leaves the line out rather than mislead.
    /// </summary>
    public static IReadOnlyList<RaidRecapTarget>? Parse(JObject? table, string? playerName, int sourceId, double? expectedTotal = null)
    {
        if (table?["data"]?["entries"] is not JArray entries || entries.Count > MaxEntries)
        {
            return null;
        }

        var rows = new Dictionary<string, (double Total, bool Self, bool Pet)>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (entry is not JObject row
                || row["name"]?.Type != JTokenType.String
                || RaidRecapRules.Number(row["total"]) is not double total
                || total < 0)
            {
                return null;
            }

            var name = ((string)row["name"]!).Trim();
            if (name.Length == 0)
            {
                return null;
            }

            // An id settles who it is; a name is only used when the row has no id.
            var id = RaidRecapAnalysisRules.Id(row["id"]);
            var self = id is int rowId ? rowId == sourceId : playerName != null && string.Equals(name, playerName, StringComparison.Ordinal);
            var pet = row["type"]?.Type == JTokenType.String && string.Equals((string)row["type"]!, "Pet", StringComparison.OrdinalIgnoreCase);
            rows[name] = rows.TryGetValue(name, out var known)
                ? (known.Total + total, known.Self || self, known.Pet && pet)
                : (total, self, pet);
        }

        var sum = rows.Values.Sum(r => r.Total);
        if (rows.Count == 0 || sum <= 0 || !double.IsFinite(sum))
        {
            return null;
        }

        if (expectedTotal is double expected && (expected <= 0 || Math.Abs(sum - expected) > expected * TotalTolerance))
        {
            return null;
        }

        return rows
            .OrderByDescending(r => r.Value.Total)
            .ThenBy(r => r.Key, StringComparer.Ordinal)
            .Take(Shown)
            .Where(r => r.Value.Total > 0)
            .Select(r => new RaidRecapTarget(r.Key, r.Value.Total / sum, r.Value.Self) { Pet = r.Value.Pet && !r.Value.Self, Amount = r.Value.Total })
            .ToArray();
    }
}
