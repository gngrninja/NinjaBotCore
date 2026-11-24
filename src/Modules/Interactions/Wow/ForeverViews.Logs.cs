#nullable enable

using System;
using System.Linq;
using System.Text;
using Discord;
using NinjaBotCore.Database;
using NinjaBotCore.Services;

namespace NinjaBotCore.Modules.Interactions.Wow
{
    /// <summary>Whether <c>/watchlogs</c> is on in this server, and where it posts.</summary>
    public sealed record ForeverLogWatch(bool On, long ChannelId);

    public static partial class ForeverViews
    {
        private const int MaxLogTitle = 80;
        private const int MaxLogZone = 60;
        private const int MaxLogOwner = 40;

        /// <summary>
        /// The server's Forever guild on Warcraft Logs: its newest logs, or what stood in the way of
        /// finding them, and whether new ones are posted in this server.
        /// </summary>
        public static MessageComponent Logs(ForeverGuild guild, ForeverLogAnswer answer, ForeverLogWatch? watch)
        {
            var card = new ContainerBuilder().WithAccentColor(
                answer.Status is ForeverLogStatus.Found ? FactionColor(guild.Faction) : ProblemColor);
            card.AddComponent(new TextDisplayBuilder(
                $"# ⚔️ {Heading(guild.Name)} · logs\n-# WoW Forever · {guild.Region} · from Warcraft Logs"));
            AddDivider(card);

            card.AddComponent(new TextDisplayBuilder(Clip(LogsBody(guild, answer), MaxBodyChars)));
            AddDivider(card);

            // Only a guild that can be found can be watched, so the other cards don't promise posts.
            if (answer.Status is ForeverLogStatus.Found or ForeverLogStatus.NotOnSite)
            {
                card.AddComponent(new TextDisplayBuilder(watch is { On: true }
                    ? $"🔔 New logs are posted in <#{watch.ChannelId}>. An officer can turn that off with `/watchlogs`."
                    : "🔕 New logs aren't posted here. An officer can run `/watchlogs` in the channel they should go to."));
            }

            AddFooter(card, answer.Server != null
                ? $"Warcraft Logs' Forever site · server {Format.Sanitize(answer.Server)}"
                : "Warcraft Logs' Forever site");
            return new ComponentBuilderV2().AddComponent(card).Build();
        }

        private static string LogsBody(ForeverGuild guild, ForeverLogAnswer answer)
        {
            var name = Format.Sanitize(guild.Name);
            switch (answer.Status)
            {
                case ForeverLogStatus.Found when answer.Reports.Count == 0:
                    return $"**{name}** is on Warcraft Logs, with no logs uploaded yet.";

                case ForeverLogStatus.Found:
                    var sb = new StringBuilder();
                    foreach (var report in answer.Reports)
                    {
                        sb.Append("**").Append(Linked(Clip(OneLine(report.Title), MaxLogTitle), report.Url)).Append("**");
                        if (report.Zone != null) sb.Append(" · ").Append(Format.Sanitize(Clip(OneLine(report.Zone), MaxLogZone)));
                        sb.Append('\n');
                        if (report.Start > DateTimeOffset.UnixEpoch)
                        {
                            sb.Append("<t:").Append(report.Start.ToUnixTimeSeconds()).Append(":f>");
                            if (Lasted(report) is { } lasted) sb.Append(" · ").Append(lasted);
                            if (report.Owner != null) sb.Append(" · ");
                        }
                        if (report.Owner != null) sb.Append("by ").Append(Format.Sanitize(Clip(OneLine(report.Owner), MaxLogOwner)));
                        sb.Append("\n\n");
                    }
                    return sb.ToString().TrimEnd('\n');

                case ForeverLogStatus.NotOnSite:
                    return $"Warcraft Logs has no guild called **{name}** in {guild.Region}.\n"
                        + "A guild shows up there once someone creates it on Warcraft Logs' Forever site and uploads a log to it. "
                        + "The name has to match the one in game.";

                case ForeverLogStatus.NoServers:
                    return $"Warcraft Logs lists no Forever servers for {guild.Region}, so there is nowhere to look for **{name}** yet.";

                case ForeverLogStatus.TooManyServers:
                    return $"Warcraft Logs lists {answer.ServerTotal:N0} Forever servers for {guild.Region}, more than NinjaBot looks through for one guild."
                        + ServerLine(answer);

                case ForeverLogStatus.Ambiguous:
                    return $"Warcraft Logs has a guild called **{name}** on more than one {guild.Region} server, and nothing says which is yours."
                        + ServerLine(answer);

                default:
                    return $"Warcraft Logs' Forever site couldn't be read: {answer.Detail ?? "something went wrong on the way"}. Try again in a few minutes.";
            }
        }

        private static string ServerLine(ForeverLogAnswer answer)
            => answer.Servers is { Count: > 0 } servers
                ? "\n-# Servers: " + string.Join(", ", servers.Take(12).Select(s => Format.Sanitize(OneLine(s))))
                : string.Empty;

        /// <summary>How long a log ran, or null when its times don't say.</summary>
        private static string? Lasted(ForeverLogReport report)
        {
            var span = report.End - report.Start;
            if (span <= TimeSpan.Zero || span > TimeSpan.FromDays(2)) return null;
            if (span.TotalMinutes < 1) return "under a minute";
            return span.TotalHours >= 1 ? $"{(int)span.TotalHours}h {span.Minutes}m" : $"{span.Minutes}m";
        }

        private static string OneLine(string value) => value.Replace('\r', ' ').Replace('\n', ' ').Trim();
    }
}
