#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Discord;
using NinjaBotCore.Database;
using NinjaBotCore.Models.Wow.Forever;
using NinjaBotCore.Services;

namespace NinjaBotCore.Modules.Interactions.Wow
{
    /// <summary>Embeds and buttons for /forever. Everything is labelled self-reported.</summary>
    public static class ForeverViews
    {
        public const string MainButtonPrefix = "forever_main";
        public const string ShareButtonPrefix = "forever_share";
        public const string RosterPagePrefix = "forever_roster";

        private const int MaxMembersPerPage = 15;
        private const int MaxPageChars = 3500;
        private static readonly Color ForeverColor = new(0xE0B04A);

        public static string Line(ForeverCharacter c)
            => $"**{c.FullName}** — {c.Class?.ToString() ?? "no class set"}";

        public static Embed Guild(ForeverGuild guild, string serverName)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"**Guild:** {Format.Sanitize(guild.Name)}");
            sb.AppendLine($"**Region:** {guild.Region}");
            sb.AppendLine($"**Ruleset:** {guild.Ruleset.Name()}");
            sb.AppendLine($"**Faction:** {guild.Faction.Emoji()} {guild.Faction}");
            sb.AppendLine($"**Roster:** {(guild.RosterEnabled ? "on — members can share characters with `/forever roster share`" : "off")}");

            var footer = "Self-reported, not verified with Blizzard";
            if (guild.TimeSet.HasValue)
                footer += $" · set by {guild.SetByName ?? "an officer"} on {guild.TimeSet.Value:yyyy-MM-dd}";

            return new EmbedBuilder()
                .WithTitle($"[{serverName}] WoW Forever guild")
                .WithDescription(sb.ToString())
                .WithColor(ForeverColor)
                .WithFooter(footer)
                .Build();
        }

        public static Embed NoGuild(string serverName) => new EmbedBuilder()
            .WithTitle($"[{serverName}] WoW Forever guild")
            .WithDescription(ForeverService.NoGuildMessage + "\n\nMembers can still add their own characters with `/forever char add`.")
            .WithColor(ForeverColor)
            .Build();

        public static Embed CharacterSaved(ForeverCharacter c, string verb, bool isMain, bool isShared)
        {
            var sb = new StringBuilder();
            sb.AppendLine(Line(c));
            sb.AppendLine(ForeverDisplay.Context(c.Region, c.Ruleset, c.Faction));
            sb.AppendLine();
            sb.AppendLine(isMain ? "★ Your main in this server." : "Not your main in this server.");
            sb.AppendLine(isShared ? "📋 On this server's roster." : "🔒 Private: only you can see it.");

            return new EmbedBuilder()
                .WithTitle($"Forever character {verb}")
                .WithDescription(sb.ToString())
                .WithColor(ForeverColor)
                .WithFooter("Self-reported, not verified with Blizzard")
                .Build();
        }

        /// <summary>Make-main / share buttons for a character the caller owns. Null when there's nothing to offer.</summary>
        public static MessageComponent? CharacterActions(ulong userId, ForeverCharacter c, bool isMain, bool canShare)
        {
            if (isMain && !canShare) return null;
            var builder = new ComponentBuilder();
            if (!isMain)
                builder.WithButton("Make main here", $"{MainButtonPrefix}~{userId}~{c.Id}", ButtonStyle.Primary, new Emoji("⭐"));
            if (canShare)
                builder.WithButton("Share to roster", $"{ShareButtonPrefix}~{userId}~{c.Id}", ButtonStyle.Success, new Emoji("📋"));
            return builder.Build();
        }

        public static Embed CharacterList(IReadOnlyList<ForeverCharacter> characters, long? mainId, IReadOnlySet<long> sharedIds)
        {
            var builder = new EmbedBuilder()
                .WithTitle("Your Forever characters")
                .WithColor(ForeverColor)
                .WithFooter("★ main in this server · 📋 on this server's roster · only you can see this list");

            if (characters.Count == 0)
                return builder.WithDescription("You haven't added any yet. Use `/forever char add`.").Build();

            var sb = new StringBuilder();
            foreach (var c in characters)
            {
                var marks = (c.Id == mainId ? "★ " : "") + (sharedIds.Contains(c.Id) ? "📋 " : "");
                sb.AppendLine($"{marks}{Line(c)} · {ForeverDisplay.Context(c.Region, c.Ruleset, c.Faction)}");
            }
            return builder.WithDescription(Truncate(sb.ToString())).Build();
        }

        public static Embed Member(string displayName, IReadOnlyList<ForeverCharacter> characters, long? mainId)
        {
            var sb = new StringBuilder();
            if (characters.Count == 0)
            {
                sb.AppendLine("No characters shared to this server's roster.");
            }
            else
            {
                foreach (var c in characters)
                    sb.AppendLine($"{(c.Id == mainId ? "★ " : "")}{Line(c)}");
            }

            return new EmbedBuilder()
                .WithTitle($"{Format.Sanitize(displayName)} in WoW Forever")
                .WithDescription(Truncate(sb.ToString()))
                .WithColor(ForeverColor)
                .WithFooter("Member-reported, not verified with Blizzard")
                .Build();
        }

        /// <summary>Members sorted by their current server display name, then paginated.</summary>
        public static List<List<(string Name, ForeverRosterMember Member)>> RosterPages(
            ForeverRoster roster, Func<long, string?> displayName)
            => Paginate(roster.Members
                .Select(m => (Name: displayName(m.UserId) ?? "Unknown member", Member: m))
                .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(e => e.Member.UserId));

        /// <summary>Splits members into pages by count and by text size so no page overflows an embed.</summary>
        public static List<List<(string Name, ForeverRosterMember Member)>> Paginate(
            IEnumerable<(string Name, ForeverRosterMember Member)> members)
        {
            var pages = new List<List<(string Name, ForeverRosterMember Member)>>();
            var current = new List<(string Name, ForeverRosterMember Member)>();
            var size = 0;
            foreach (var entry in members)
            {
                var block = MemberBlock(entry.Name, entry.Member).Length;
                if (current.Count > 0 && (current.Count >= MaxMembersPerPage || size + block > MaxPageChars))
                {
                    pages.Add(current);
                    current = new List<(string Name, ForeverRosterMember Member)>();
                    size = 0;
                }
                current.Add(entry);
                size += block;
            }
            if (current.Count > 0) pages.Add(current);
            return pages;
        }

        public static Embed Roster(ForeverGuild guild, List<List<(string Name, ForeverRosterMember Member)>> pages, int page)
        {
            var builder = new EmbedBuilder()
                .WithTitle($"{Format.Sanitize(guild.Name)} roster")
                .WithColor(ForeverColor);

            var header = $"Member-reported characters · {ForeverDisplay.Context(guild.Region, guild.Ruleset, guild.Faction)}\n\n";
            if (pages.Count == 0)
            {
                return builder
                    .WithDescription(header + "Nobody has shared a character yet. Share yours with `/forever roster share`.")
                    .WithFooter("Not verified with Blizzard")
                    .Build();
            }

            page = Math.Clamp(page, 0, pages.Count - 1);
            var sb = new StringBuilder(header);
            foreach (var (name, member) in pages[page])
                sb.Append(MemberBlock(name, member));

            var memberCount = pages.Sum(p => p.Count);
            return builder
                .WithDescription(Truncate(sb.ToString()))
                .WithFooter($"Page {page + 1}/{pages.Count} · {memberCount} member{(memberCount == 1 ? "" : "s")} · ★ main · Not verified with Blizzard")
                .Build();
        }

        public static MessageComponent? RosterPager(ulong userId, int page, int pageCount)
        {
            if (pageCount <= 1) return null;
            return new ComponentBuilder()
                .WithButton("◀ Prev", $"{RosterPagePrefix}~{userId}~{page - 1}", ButtonStyle.Secondary, disabled: page <= 0)
                .WithButton("Next ▶", $"{RosterPagePrefix}~{userId}~{page + 1}", ButtonStyle.Secondary, disabled: page >= pageCount - 1)
                .Build();
        }

        private static string MemberBlock(string name, ForeverRosterMember member)
        {
            var sb = new StringBuilder();
            sb.Append("__").Append(Format.Sanitize(name)).Append("__\n");
            foreach (var c in member.Characters)
                sb.Append(c.Id == member.MainId ? "★ " : "　").Append(Line(c)).Append('\n');
            sb.Append('\n');
            return sb.ToString();
        }

        private static string Truncate(string text) => text.Length <= 4000 ? text : text[..3997] + "...";
    }
}
