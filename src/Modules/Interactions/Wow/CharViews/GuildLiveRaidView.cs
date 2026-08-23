using System;
using System.Linq;
using System.Text;
using Discord;
using NinjaBotCore.Common;
using NinjaBotCore.Models.Wow;

namespace NinjaBotCore.Modules.Interactions.Wow.CharViews
{
    public static class GuildLiveRaidView
    {
        public static EmbedBuilder Build(RaiderIOModels.GuildLiveRaidResponse response)
        {
            var guild = response?.Guild;
            var raid = response?.Raid;
            var bosses = response?.Bosses ?? Array.Empty<RaiderIOModels.LiveRaidBoss>();
            var text = new StringBuilder();
            text.AppendLine($"## {raid?.Name ?? "Current Raid"}");
            text.AppendLine($"-# Fetched from Raider.IO <t:{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}:R>");

            if (bosses.Length == 0)
            {
                text.AppendLine("*No Live Tracking pulls are available for the current lockout.*");
                text.AppendLine("-# A raider must upload combat logs with the Raider.IO Desktop App.");
            }
            else
            {
                foreach (var entry in bosses.OrderBy(value => value.Boss?.Ordinal ?? int.MaxValue))
                {
                    var name = entry.Boss?.Name ?? "Unknown boss";
                    if (entry.IsDefeated)
                    {
                        text.AppendLine($"✅ {name} · defeated · {entry.PullCount ?? 0} pulls");
                    }
                    else if (entry.PullCount > 0)
                    {
                        var progress = entry.BestPercent.HasValue
                            ? $"{entry.BestPercent.Value:N1}%"
                            : "progress hidden";
                        text.AppendLine($"🟠 {name} · {progress} · {entry.PullCount} pulls");
                    }
                    else
                    {
                        text.AppendLine($"⚪ {name} · no tracked pulls");
                    }
                }
            }

            if (response?.GuildPrivacy?.WereRaidPullsRestricted == true
                || response?.GuildPrivacy?.WereRaidPercentsRestricted == true)
            {
                text.AppendLine();
                text.AppendLine("-# Some pull or percentage data is hidden by the guild's Raider.IO privacy settings.");
            }

            if (!string.IsNullOrWhiteSpace(guild?.Path))
            {
                text.AppendLine();
                text.AppendLine($"[Open live guild profile]({RaiderIoLinks.FromRelativePath(guild.Path)})");
            }

            return new EmbedBuilder()
                .WithTitle($"Live Raid — {guild?.Name ?? "Guild"}")
                .WithDescription(text.ToString())
                .WithColor(new Color(220, 38, 127))
                .WithFooter("Live Tracking data from Raider.IO Desktop App uploads")
                .WithCurrentTimestamp();
        }
    }
}
