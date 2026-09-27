using NinjaBotCore.Modules.Wow;

namespace NinjaBotCore.Modules.Interactions.Wow;

/// <summary>
/// The "How to read" text for each raid recap view. Everything a reader needs to interpret
/// the numbers lives here, so the cards themselves stay short.
/// </summary>
public static class RaidRecapHelp
{
    private const string Live =
        "Logs can change while a raid is still being uploaded. Press 🔄 Refresh for the latest.";

    private const string Partial =
        "⚠️ **Partial data** means WarcraftLogs returned incomplete results. Counts are minimums, never zero.";

    private const string Bands =
        "Parse dots: 🟡 100 · 🩷 99 · 🟠 95 · 🟣 75 · 🔵 50 · 🟢 25 · ⚪ below 25. Parses round down.";

    public static string For(string topic, RaidRecapSession s)
    {
        var body = topic switch
        {
            "overview" =>
                "✅ killed · 🟠 still progressing · ⏳ no result yet.\n"
                + "**Best** is the lowest boss health reached on a wipe. It is boss health, not how far into the fight you got.\n"
                + "Trash is left out. **Worth a look** suggests where to start a review. It does not grade anyone.",
            "bosses" =>
                "**Best pull** and **Last wipe** are boss health left when the raid wiped.\n"
                + "**Time in combat** adds up this boss's kills and wipes. **Median wipe** is the middle wipe length.\n"
                + "Pulls without a recorded length are left out of time figures, and the card says how many were timed.",
            "compare" =>
                "A and B are two finished pulls of the same boss and difficulty.\n"
                + "**First m:ss of each pull** compares both over the length of the shorter pull. "
                + "Equal time is not equal phase, and the roster may differ.\n"
                + "The first death is where to start looking, not who to blame. A player who dies twice counts as two deaths and one player.\n"
                + Partial,
            "damage" or "healing" => Output(topic, s),
            "deaths" =>
                "Times are since the pull started. The ability is the killing blow.\n"
                + "Only raid members are listed. Pets and enemies are left out.\n"
                + "The first death is where to start looking, not who to blame. Check what happened just before it on WarcraftLogs.\n"
                + Partial,
            "incoming" =>
                "Totals per ability for the whole raid, from WarcraftLogs' damage taken table.\n"
                + "Totals alone do not show what was avoidable. Check soaks, assignments and mitigation against the biggest rows.",
            "interrupts" =>
                "**Kicked** is successful interrupts. **Went off** is casts that finished.\n"
                + "Not every cast can or should be interrupted, so a cast that went off is not automatically a miss.\n"
                + "**Not on roster** means WarcraftLogs credited someone we could not match to this pull's raid.\n"
                + Partial,
            "dispels" =>
                "Successful dispels per debuff, with who did them.\n"
                + "Debuffs left alone are not automatically mistakes. Some are meant to stay.\n"
                + Partial,
            "mechanics" =>
                "Counts how often a specific boss ability landed on players, and when.\n"
                + "**Throw Junk** counts damage hits, including fully absorbed ones. **Shell Spin** counts debuff applications.\n"
                + "Only available for The Lost Explorers on Heroic for now. Other fights show as not supported.\n"
                + Partial,
            "players" =>
                "Pick a pull, then a player, then what to look at. Each choice loads only that data.\n"
                + "Damage and Healing need a kill. Wipes have no parses.\n"
                + "Player links open that player's view of the fight on WarcraftLogs.\n"
                + Bands,
            _ =>
                "Pick a finished pull, then a tab. Each tab loads only that pull's data."
        };

        return "**❔ How to read**\n" + body + "\n" + Live;
    }

    private static string Output(string topic, RaidRecapSession s)
    {
        var metric = topic == "healing" ? "HPS" : "DPS";
        var text = $"**{metric}** is the total divided by the full length of the kill, pets included. "
            + "The list order is output on this kill, not a ranking.\n"
            + "The dot and number are the WarcraftLogs parse for this kill against today's rankings.\n"
            + Bands + "\n"
            + "No dot means no parse was available. It is not a zero.\n";
        if (topic == "healing")
        {
            text += "Healing uses WarcraftLogs' healing total. Overhealing is not added.\n";
        }

        if (s.PerformanceParses is { } parses)
        {
            text += $"Parses checked <t:{parses.AsOf.ToUnixTimeSeconds()}:R> · partition {parses.Partition}. They can move as more logs are ranked.";
        }
        else
        {
            text += "Parses are unavailable right now, so only output is shown.";
        }

        return text;
    }
}
