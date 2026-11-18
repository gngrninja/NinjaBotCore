#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Discord;
using NinjaBotCore.Database;
using NinjaBotCore.Models.Wow.Forever;
using NinjaBotCore.Modules.Interactions.Wow.CharViews;
using NinjaBotCore.Services;

namespace NinjaBotCore.Modules.Interactions.Wow
{
    /// <summary>Embeds and buttons for /forever. Each one says whether its details are self-reported or from Blizzard's armory.</summary>
    public static class ForeverViews
    {
        public const string MainButtonPrefix = "forever_main";
        public const string ShareButtonPrefix = "forever_share";
        public const string RosterPagePrefix = "forever_roster";
        public const string CardButtonPrefix = "forever_card";
        public const string CardOverview = "o";
        public const string CardGear = "g";

        private const string SelfReported = "Self-reported, not verified with Blizzard";
        private const int MaxMembersPerPage = 15;
        private const int MaxArmoryLinesPerPage = 20;
        private const int MaxPageChars = 3500;
        // Blizzard's roster route stops here.
        private const int ArmoryRosterCap = 1000;
        private static readonly Color ForeverColor = new(0xE0B04A);
        private static readonly Color AllianceColor = new(0, 112, 221);
        private static readonly Color HordeColor = new(200, 35, 35);

        private static readonly string[] GearSlotOrder =
        {
            "HEAD", "NECK", "SHOULDER", "BACK", "CHEST", "SHIRT", "TABARD", "WRIST", "HANDS", "WAIST", "LEGS", "FEET",
            "FINGER_1", "FINGER_2", "TRINKET_1", "TRINKET_2", "MAIN_HAND", "OFF_HAND", "RANGED"
        };

        public static string Line(ForeverCharacter c)
            => $"**{c.FullName}** — {c.Class?.ToString() ?? "no class set"}";

        /// <param name="saved">What the armory said when the guild was just set; null when only showing it.</param>
        public static Embed Guild(ForeverGuild guild, string serverName, ForeverSavedGuild? saved = null)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"**Guild:** {Format.Sanitize(guild.Name)}");
            sb.AppendLine($"**Region:** {guild.Region}");
            sb.AppendLine($"**Ruleset:** {guild.Ruleset.Name()}");
            sb.AppendLine($"**Faction:** {guild.Faction.Emoji()} {guild.Faction}");
            sb.AppendLine($"**Armory:** {ArmoryState(guild, saved)}");
            sb.AppendLine($"**Roster:** {(guild.RosterEnabled ? "on — members can share characters with `/forever roster share`" : "off")}");

            var footer = guild.ArmorySlug != null ? "Ruleset and faction from Blizzard's armory" : SelfReported;
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

        private static string ArmoryState(ForeverGuild guild, ForeverSavedGuild? saved)
        {
            if (guild.ArmorySlug != null)
            {
                return saved?.Armory is { MemberCount: > 0 } armory
                    ? $"matched · {armory.MemberCount:N0} member{(armory.MemberCount == 1 ? "" : "s")}"
                    : "matched";
            }

            const string retry = "The roster only shows what members share until `/forever guild set` finds it.";
            return saved?.Lookup switch
            {
                ForeverLookupStatus.NotFound => $"no guild with this name on the {guild.Region} armory. {retry}",
                ForeverLookupStatus.Unavailable => $"Blizzard's armory didn't answer. {retry}",
                _ => $"not matched. {retry}"
            };
        }

        public static Embed CharacterSaved(ForeverSavedCharacter saved, string verb, bool isMain, bool isShared)
        {
            var c = saved.Character;
            var sb = new StringBuilder();
            sb.AppendLine(Line(c));
            sb.AppendLine(ForeverDisplay.Context(c.Region, c.Ruleset, c.Faction));
            switch (saved.Lookup)
            {
                case ForeverLookupStatus.Found when saved.Armory != null:
                    sb.AppendLine(ArmoryLine(saved.Armory));
                    break;
                case ForeverLookupStatus.NotFound:
                    sb.AppendLine($"Not on the armory yet. {ForeverService.NotOnArmoryHint}");
                    break;
                case ForeverLookupStatus.Unavailable:
                    sb.AppendLine("Blizzard's armory didn't answer, so this is saved as typed.");
                    break;
            }
            sb.AppendLine();
            sb.AppendLine(isMain ? "★ Your main in this server." : "Not your main in this server.");
            sb.AppendLine(isShared ? "📋 On this server's roster." : "🔒 Private: only you can see it.");

            return new EmbedBuilder()
                .WithTitle($"Forever character {verb}")
                .WithDescription(sb.ToString())
                .WithColor(ForeverColor)
                .WithFooter(saved.Armory != null
                    ? "Class, ruleset and faction from Blizzard's armory · ownership isn't verified"
                    : SelfReported)
                .Build();
        }

        private static string ArmoryLine(ForeverArmoryCharacter armory)
        {
            var line = $"[On the armory]({ForeverNames.ArmoryUrl(armory.Region, armory.Slug)}): level {armory.Level}";
            if (!string.IsNullOrWhiteSpace(armory.Race)) line += $" {Format.Sanitize(armory.Race)}";
            if (!string.IsNullOrWhiteSpace(armory.GuildName)) line += $", in {Format.Sanitize(armory.GuildName)}";
            return line;
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

        /// <summary>
        /// One page of the roster: Blizzard's roster with the members who shared a character next to
        /// it when the guild is matched to the armory, otherwise only what members shared.
        /// </summary>
        public static (Embed Embed, int Page, int PageCount) RosterPage(ForeverRoster roster, Func<long, string?> displayName, int page)
        {
            if (roster.Armory == null)
            {
                var pages = RosterPages(roster, displayName);
                var shown = Math.Clamp(page, 0, Math.Max(0, pages.Count - 1));
                return (Roster(roster.Guild, pages, shown, roster.ArmoryMissed), shown, pages.Count);
            }

            var armoryPages = PaginateLines(ArmoryRosterLines(roster, displayName));
            var armoryPage = Math.Clamp(page, 0, Math.Max(0, armoryPages.Count - 1));
            return (ArmoryRoster(roster, armoryPages, armoryPage), armoryPage, armoryPages.Count);
        }

        /// <summary>
        /// Blizzard's members by rank, then level, each followed by whoever shared that character here.
        /// Shared characters the armory doesn't list in the guild come last, so nothing a member shared disappears.
        /// </summary>
        public static List<string> ArmoryRosterLines(ForeverRoster roster, Func<long, string?> displayName)
        {
            var shared = roster.Members
                .SelectMany(m => m.Characters.Select(c => (Character: c, m.UserId, IsMain: c.Id == m.MainId)))
                .ToList();
            var byName = shared.ToLookup(s => (s.Character.NormalizedFirstName, s.Character.NormalizedLastName));
            var listed = new HashSet<long>();
            string Who((ForeverCharacter Character, long UserId, bool IsMain) s)
                => (s.IsMain ? "★ " : "") + Format.Sanitize(displayName(s.UserId) ?? "Unknown member");

            var lines = new List<string>();
            foreach (var member in roster.Armory!.Members
                         .OrderBy(m => m.Rank ?? int.MaxValue)
                         .ThenByDescending(m => m.Level)
                         .ThenBy(m => m.FullName, StringComparer.OrdinalIgnoreCase))
            {
                var line = new StringBuilder($"**{Format.Sanitize(member.FullName.Trim())}**");
                if (member.Level > 0 || member.Class != null)
                    line.Append(" · ").Append(string.Join(" ", new[] { member.Level > 0 ? member.Level.ToString() : null, member.Class?.ToString() }.Where(p => p != null)));
                if (member.Rank is { } rank) line.Append(" · ").Append(rank == 0 ? "Guild Master" : $"Rank {rank}");

                var owners = byName[(ForeverNames.Normalize(member.FirstName), ForeverNames.Normalize(member.Surname))].ToList();
                if (owners.Count > 0)
                {
                    line.Append(" — ").Append(string.Join(", ", owners.Select(Who)));
                    listed.UnionWith(owners.Select(o => o.Character.Id));
                }
                lines.Add(line.ToString());
            }

            var unlisted = shared.Where(s => !listed.Contains(s.Character.Id))
                .OrderBy(s => s.Character.FullName, StringComparer.OrdinalIgnoreCase)
                .Select(s => $"{Line(s.Character)} — {Who(s)}")
                .ToList();
            if (unlisted.Count > 0)
            {
                // The heading rides on its first line so a page never ends with a bare heading.
                unlisted[0] = $"\n__Shared here, not in the guild on the armory__\n{unlisted[0]}";
                lines.AddRange(unlisted);
            }
            return lines;
        }

        private static List<List<string>> PaginateLines(IEnumerable<string> lines)
        {
            var pages = new List<List<string>>();
            var current = new List<string>();
            var size = 0;
            foreach (var line in lines)
            {
                if (current.Count > 0 && (current.Count >= MaxArmoryLinesPerPage || size + line.Length + 1 > MaxPageChars))
                {
                    pages.Add(current);
                    current = new List<string>();
                    size = 0;
                }
                current.Add(line);
                size += line.Length + 1;
            }
            if (current.Count > 0) pages.Add(current);
            return pages;
        }

        private static Embed ArmoryRoster(ForeverRoster roster, List<List<string>> pages, int page)
        {
            var guild = roster.Guild;
            var count = roster.Armory!.Members.Count;
            var sb = new StringBuilder();
            sb.AppendLine($"{count:N0} member{(count == 1 ? "" : "s")} on Blizzard's armory · {ForeverDisplay.Context(guild.Region, guild.Ruleset, guild.Faction)}");
            sb.AppendLine("A name after the dash is a member who shared that character with `/forever roster share`.");
            sb.AppendLine();
            if (pages.Count == 0)
                sb.AppendLine("The armory lists no members for this guild yet.");
            else
                sb.AppendJoin('\n', pages[page]);

            var footer = pages.Count > 1 ? $"Page {page + 1}/{pages.Count} · " : "";
            footer += "The armory's roster can be 3 hours behind · ★ main";
            if (count >= ArmoryRosterCap) footer += $" · Blizzard lists the first {ArmoryRosterCap:N0}";

            return new EmbedBuilder()
                .WithTitle($"{Format.Sanitize(guild.Name)} roster")
                .WithDescription(Truncate(sb.ToString()))
                .WithColor(ForeverColor)
                .WithFooter(footer)
                .Build();
        }

        /// <param name="armoryMissed">The guild is matched to the armory, but its roster didn't come back this time.</param>
        public static Embed Roster(ForeverGuild guild, List<List<(string Name, ForeverRosterMember Member)>> pages, int page, bool armoryMissed = false)
        {
            var builder = new EmbedBuilder()
                .WithTitle($"{Format.Sanitize(guild.Name)} roster")
                .WithColor(ForeverColor);

            var header = $"Member-reported characters · {ForeverDisplay.Context(guild.Region, guild.Ruleset, guild.Faction)}\n"
                + (armoryMissed ? "Blizzard's armory didn't send the guild's roster, so this only shows what members shared.\n" : "")
                + "\n";
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

        // ===== Armory character card =====

        public static string CardMissing(string fullName, ForeverRegion region, ForeverLookupStatus status)
            => status == ForeverLookupStatus.NotFound
                ? $"I couldn't find **{Format.Sanitize(fullName)}** on the {region} armory. {ForeverService.NotOnArmoryHint} Check the spelling and the region."
                : "Blizzard's armory didn't answer. Try again in a few minutes.";

        public static Embed Card(ForeverArmoryCard card, string view)
        {
            var c = card.Character;
            var sb = new StringBuilder();
            if (view == CardGear)
            {
                if (c.ItemLevel is > 0) sb.AppendLine($"**Item level:** {c.ItemLevel}").AppendLine();
                var gear = card.Gear
                    .OrderBy(i => Array.IndexOf(GearSlotOrder, i.Slot) is var slot and >= 0 ? slot : int.MaxValue)
                    .ToList();
                if (gear.Count == 0) sb.AppendLine("*The armory has no gear for this character right now.*");
                foreach (var item in gear)
                {
                    var level = item.ItemLevel is > 0 ? $" ({item.ItemLevel})" : "";
                    sb.AppendLine($"{CharViewHelpers.GetQualityEmoji(item.Quality)} **{Format.Sanitize(item.SlotName)}:** {Format.Sanitize(item.Name)}{level}");
                }
            }
            else
            {
                sb.AppendLine($"**Level {c.Level}** {Format.Sanitize(string.Join(" ", new[] { c.Race, c.ClassName ?? c.Class?.ToString() }.Where(p => !string.IsNullOrWhiteSpace(p))))}");
                sb.AppendLine(string.Join(" · ", new[]
                {
                    c.Faction is { } faction ? $"{faction.Emoji()} {faction}" : null,
                    c.Ruleset?.Name() ?? c.RulesetName,
                    c.Region.ToString()
                }.Where(p => !string.IsNullOrWhiteSpace(p))));
                if (!string.IsNullOrWhiteSpace(c.GuildName)) sb.AppendLine($"**Guild:** {Format.Sanitize(c.GuildName)}");
                if (card.Talents.Count > 0)
                    sb.AppendLine($"**Talents:** {string.Join(" / ", card.Talents.Select(t => $"{Format.Sanitize(t.Name)} {t.Points}"))}");
                if (c.ItemLevel is > 0) sb.AppendLine($"**Item level:** {c.ItemLevel}");
                if (card.Professions.Count > 0)
                    sb.AppendLine($"**Professions:** {string.Join(" · ", card.Professions.Select(p => $"{Format.Sanitize(p.Name)} {p.Skill}{(p.MaxSkill > 0 ? $"/{p.MaxSkill}" : "")}"))}");
                if (card.Stats is { } stats)
                {
                    var parts = new[]
                    {
                        stats.Deaths is { } deaths ? $"**Deaths:** {deaths:N0}" : null,
                        stats.Quests is { } quests ? $"**Quests:** {quests:N0}" : null,
                        stats.Kills is { } kills ? $"**Kills:** {kills:N0}" : null,
                        stats.Dungeons is { } dungeons ? $"**Dungeons entered:** {dungeons:N0}" : null
                    }.Where(p => p != null);
                    sb.AppendLine(string.Join(" · ", parts));
                }
                if (c.LastSeen is { } seen) sb.AppendLine($"**Last seen:** <t:{seen.ToUnixTimeSeconds()}:R>");
            }

            var builder = new EmbedBuilder()
                .WithTitle(Format.Sanitize(c.FullName.Trim()) + (view == CardGear ? " · Gear" : ""))
                .WithUrl(ForeverNames.ArmoryUrl(c.Region, c.Slug))
                .WithDescription(Truncate(sb.ToString()))
                .WithColor(c.Faction switch
                {
                    ForeverFaction.Alliance => AllianceColor,
                    ForeverFaction.Horde => HordeColor,
                    _ => ForeverColor
                })
                .WithFooter("From Blizzard's armory · updates when the character logs out");
            if (card.AvatarUrl != null) builder.WithThumbnailUrl(card.AvatarUrl);
            return builder.Build();
        }

        /// <summary>The other view's button plus a link to the armory. The buttons are bound to the caller.</summary>
        public static MessageComponent CardButtons(ulong userId, ForeverArmoryCharacter c, string view)
        {
            var builder = new ComponentBuilder();
            var other = view == CardGear ? CardOverview : CardGear;
            var id = $"{CardButtonPrefix}~{userId}~{other}~{(int)c.Region}~{c.FirstName}~{c.Surname}";
            // A name the bot wouldn't accept back, or an id past Discord's limit, just loses the toggle.
            if (id.Length <= 100
                && ForeverNames.TryCleanPart(c.FirstName, "First name", out _, out _)
                && ForeverNames.TryCleanPart(c.Surname, "Surname", out _, out _))
            {
                builder.WithButton(other == CardGear ? "Gear" : "Overview", id, ButtonStyle.Primary,
                    new Emoji(other == CardGear ? "🛡️" : "📋"));
            }
            builder.WithButton("Armory", style: ButtonStyle.Link, url: ForeverNames.ArmoryUrl(c.Region, c.Slug));
            return builder.Build();
        }

        private static string Truncate(string text) => text.Length <= 4000 ? text : text[..3997] + "...";
    }
}
