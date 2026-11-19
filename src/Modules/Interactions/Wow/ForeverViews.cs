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
    public enum ForeverRosterSort
    {
        Rank,
        Level,
        Name,
        Class
    }

    /// <summary>How far this server and the caller are with setting Forever up. /forever help ticks these off.</summary>
    /// <param name="MainName">The caller's main in this server, or null when none is picked.</param>
    /// <param name="Shared">How many of the caller's characters are shared to this server's roster.</param>
    /// <param name="IsOfficer">Whether the caller can run the guild commands (Kick Members).</param>
    public sealed record ForeverSetup(ForeverGuild? Guild, int Characters, string? MainName, int Shared, bool IsOfficer);

    /// <summary>
    /// Components V2 cards for /forever, in the same shell as /char and /raid-recap: a titled header,
    /// one accent colour, emoji buttons inside the card and a single small footer line. Each card says
    /// whether its details are self-reported or from Blizzard's armory.
    /// </summary>
    public static class ForeverViews
    {
        public const string MainButtonPrefix = "forever_main";
        public const string ShareButtonPrefix = "forever_share";
        public const string RosterPagePrefix = "forever_roster";
        public const string RosterSortPrefix = "forever_rsort";
        public const string RosterPickPrefix = "forever_rpick";
        public const string CardButtonPrefix = "forever_card";
        public const string OpenCardPrefix = "forever_open";
        public const string CardOverview = "o";
        public const string CardGear = "g";
        /// <summary>Leads the card's view code when it was opened from the roster, so the card offers the way back.</summary>
        public const string FromRoster = "r";

        public const int MaxLevel = 60;

        private const string SelfReported = "Self-reported, not verified with Blizzard";
        private const int MaxMembersPerPage = 15;
        private const int MaxArmoryLinesPerPage = 20;
        // Discord allows 4,000 characters of text per message; the rest is for the header and footer.
        private const int MaxPageChars = 3000;
        private const int MaxBodyChars = 3400;
        // Blizzard's roster route stops here.
        private const int ArmoryRosterCap = 1000;
        // A character reaches the armory at this level.
        private const int ArmoryMinLevel = 10;

        private static readonly Color ForeverColor = new(0xE0B04A);
        private static readonly Color AllianceColor = new(0, 112, 221);
        private static readonly Color HordeColor = new(200, 35, 35);
        private static readonly Color DoneColor = new(0x3BA55D);
        private static readonly Color ProblemColor = new(0xF0B132);

        private static readonly string[][] GearGroups =
        {
            new[] { "HEAD", "NECK", "SHOULDER", "BACK", "CHEST", "SHIRT", "TABARD", "WRIST", "HANDS", "WAIST", "LEGS", "FEET" },
            new[] { "FINGER_1", "FINGER_2", "TRINKET_1", "TRINKET_2" },
            new[] { "MAIN_HAND", "OFF_HAND", "RANGED" }
        };

        private static readonly (ForeverRosterSort Sort, char Code, string Label, string Description)[] Sorts =
        {
            (ForeverRosterSort.Rank, 'r', "Guild rank", "Guild Master first, then by level"),
            (ForeverRosterSort.Level, 'l', "Level", "Highest level first"),
            (ForeverRosterSort.Name, 'n', "Name", "A to Z"),
            (ForeverRosterSort.Class, 'c', "Class", "Grouped by class, highest level first")
        };

        public static string Line(ForeverCharacter c)
            => $"{ClassEmoji(c.Class)} **{Format.Sanitize(c.FullName)}** — {c.Class?.ToString() ?? "no class set"}";

        // ===== Notices =====

        /// <summary>A short confirmation that something was done.</summary>
        public static MessageComponent Done(string message) => Notice(message, DoneColor);

        /// <summary>A short neutral answer: nothing changed and nothing went wrong.</summary>
        public static MessageComponent Info(string message) => Notice(message, ForeverColor);

        /// <summary>Why something couldn't be done and what to try.</summary>
        public static MessageComponent Problem(string? message)
            => Notice("⚠️ " + (string.IsNullOrWhiteSpace(message) ? "That didn't work. Please try again." : message), ProblemColor);

        private static MessageComponent Notice(string message, Color accent)
            => new ComponentBuilderV2()
                .AddComponent(new ContainerBuilder()
                    .WithAccentColor(accent)
                    .AddComponent(new TextDisplayBuilder(Clip(message, MaxBodyChars))))
                .Build();

        // ===== Help =====

        public const string HelpPointer = "New to this? `/forever help` walks you through it.";

        /// <summary>
        /// Getting started: the five setup steps with the ones already done ticked off, a pointer at the
        /// first step the caller can do right now, then the other commands and what to know about Forever.
        /// </summary>
        public static MessageComponent Help(string serverName, string? iconUrl, ulong userId, ForeverSetup setup)
        {
            var guild = setup.Guild;
            var rosterOn = guild is { RosterEnabled: true };
            var hasCharacter = setup.Characters > 0;
            // The guild steps need Kick Members, so everyone else is told who to ask.
            var byOfficer = setup.IsOfficer ? "" : "an officer runs ";

            var steps = new (bool Done, bool CanDoNow, string Text)[]
            {
                (guild != null, setup.IsOfficer, guild != null
                    ? $"**Guild set** · {Format.Sanitize(guild.Name)} · {ForeverDisplay.Context(guild.Region, guild.Ruleset, guild.Faction)}"
                    : $"**Set this server's guild** · {byOfficer}`/forever guild set`"),
                (rosterOn, setup.IsOfficer && guild != null, rosterOn
                    ? "**Roster on** · members choose what to share"
                    : $"**Turn the roster on** · {byOfficer}`/forever guild roster`"),
                (hasCharacter, true, hasCharacter
                    ? $"**{setup.Characters:N0} character{(setup.Characters == 1 ? "" : "s")} added** · add more with `/forever char add`"
                    : "**Add your character** · `/forever char add`"),
                (setup.MainName != null, hasCharacter, setup.MainName != null
                    ? $"**Main here** · ★ {Format.Sanitize(setup.MainName)} · change it with `/forever char main`"
                    : "**Pick your main here** · `/forever char main`"),
                (setup.Shared > 0, hasCharacter && rosterOn, setup.Shared > 0
                    ? $"**{setup.Shared:N0} shared to the roster**" + (rosterOn ? "" : ", hidden while the roster is off") + " · `/forever roster share` adds another"
                    : "**Share a character to the roster** · `/forever roster share`" + (rosterOn ? "" : ", once the roster is on"))
            };
            var done = steps.Count(s => s.Done);
            var next = Array.FindIndex(steps, s => !s.Done && s.CanDoNow);

            var card = new ContainerBuilder().WithAccentColor(guild != null ? FactionColor(guild.Faction) : ForeverColor);
            AddHeader(card,
                $"# 🧭 Getting started with WoW Forever\nin **{Format.Sanitize(serverName)}** · "
                + (done == steps.Length ? "all set up 🎉" : $"**{done} of {steps.Length}** steps done"),
                iconUrl, "Server icon");
            AddDivider(card);

            var checklist = new StringBuilder();
            for (var i = 0; i < steps.Length; i++)
                checklist.Append(steps[i].Done ? "✅ " : i == next ? "👉 " : "⬜ ").Append(steps[i].Text).Append('\n');
            card.AddComponent(new TextDisplayBuilder(checklist.ToString().TrimEnd('\n')));
            AddDivider(card);

            card.AddComponent(new TextDisplayBuilder(
                "### Commands\n"
                + "🪪 `/forever char view` · anyone's armory card: level, talents, professions and gear\n"
                + "👥 `/forever roster view` · who plays what in this server; `roster unshare` takes yours off it\n"
                + "📜 `/forever char list` · your characters; `char edit` and `char remove` change them\n"
                + "🏰 `/forever guild view` · this server's guild\n"
                + "🛡️ Officers (Kick Members): `/forever guild set`, `guild roster` and `guild unset`"));
            AddDivider(card);

            card.AddComponent(new TextDisplayBuilder(
                "### Good to know\n"
                + "- Forever has no realms: a character is a first name and a surname, unique in its region.\n"
                + $"- A character reaches Blizzard's armory at level {ArmoryMinLevel}, after it logs out. Before then, add it with its class, ruleset and faction.\n"
                + "- Your characters are private until you share them, and every `/forever` reply is shown only to you.\n"
                + "- Name fields suggest guildmates once the guild is matched to the armory and the roster is on."));

            if (rosterOn)
            {
                card.AddComponent(new ActionRowBuilder().WithButton(
                    new ButtonBuilder("View roster", RosterId(userId, 0, ForeverRosterSort.Rank), ButtonStyle.Primary, emote: new Emoji("👥"))));
            }

            AddFooter(card, "Class, level and rosters come from Blizzard's armory · nothing proves a character is yours");
            return new ComponentBuilderV2().AddComponent(card).Build();
        }

        // ===== Guild =====

        /// <param name="saved">What the armory said when the guild was just set; null when only showing it.</param>
        public static MessageComponent Guild(ForeverGuild guild, string serverName, string? iconUrl, ulong userId, ForeverSavedGuild? saved = null)
        {
            var card = new ContainerBuilder().WithAccentColor(FactionColor(guild.Faction));
            AddHeader(card,
                $"# 🏰 {Heading(guild.Name)}\nWoW Forever guild for **{Format.Sanitize(serverName)}**",
                iconUrl, "Server icon");
            AddDivider(card);

            var sb = new StringBuilder();
            sb.Append("🌍 **Region** ").Append(guild.Region).Append('\n');
            sb.Append("📜 **Ruleset** ").Append(guild.Ruleset.Name()).Append('\n');
            sb.Append(guild.Faction.Emoji()).Append(" **Faction** ").Append(guild.Faction).Append('\n');
            sb.Append("🪪 **Armory** ").Append(ArmoryState(guild, saved)).Append('\n');
            sb.Append("👥 **Roster** ").Append(guild.RosterEnabled
                ? "on — members can share characters with `/forever roster share`"
                : "off — an officer can turn it on with `/forever guild roster`");
            card.AddComponent(new TextDisplayBuilder(sb.ToString()));

            if (guild.RosterEnabled)
            {
                card.AddComponent(new ActionRowBuilder().WithButton(
                    new ButtonBuilder("View roster", RosterId(userId, 0, ForeverRosterSort.Rank), ButtonStyle.Primary, emote: new Emoji("👥"))));
            }

            var footer = guild.ArmorySlug != null ? "Ruleset and faction from Blizzard's armory" : SelfReported;
            if (guild.TimeSet.HasValue)
                footer += $" · set by {Format.Sanitize(guild.SetByName ?? "an officer")} on {guild.TimeSet.Value:yyyy-MM-dd}";
            AddFooter(card, footer);
            return new ComponentBuilderV2().AddComponent(card).Build();
        }

        public static MessageComponent NoGuild(string serverName, string? iconUrl)
        {
            var card = new ContainerBuilder().WithAccentColor(ForeverColor);
            AddHeader(card, $"# 🏰 WoW Forever guild\nNone set for **{Format.Sanitize(serverName)}** yet", iconUrl, "Server icon");
            AddDivider(card);
            card.AddComponent(new TextDisplayBuilder(
                "An officer can set one with `/forever guild set`.\nMembers can still add their own characters with `/forever char add`."));
            AddFooter(card, HelpPointer);
            return new ComponentBuilderV2().AddComponent(card).Build();
        }

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

        // ===== Saved characters =====

        public static MessageComponent CharacterSaved(
            ForeverSavedCharacter saved, string verb, bool isMain, bool isShared, ulong userId, bool canShare)
        {
            var c = saved.Character;
            var card = new ContainerBuilder().WithAccentColor(ClassColor(c.Class, c.Faction));

            var sb = new StringBuilder();
            sb.Append("## ✅ Forever character ").Append(verb).Append('\n');
            sb.Append("### ").Append(ClassEmoji(c.Class)).Append(' ').Append(Heading(c.FullName)).Append('\n');
            sb.Append(c.Class?.ToString() ?? "No class set").Append(" · ").Append(ForeverDisplay.Context(c.Region, c.Ruleset, c.Faction)).Append('\n');
            switch (saved.Lookup)
            {
                case ForeverLookupStatus.Found when saved.Armory != null:
                    sb.Append(ArmoryLine(saved.Armory)).Append('\n');
                    break;
                case ForeverLookupStatus.NotFound:
                    sb.Append("Not on the armory yet. ").Append(ForeverService.NotOnArmoryHint).Append('\n');
                    break;
                case ForeverLookupStatus.Unavailable:
                    sb.Append("Blizzard's armory didn't answer, so this is saved as typed.\n");
                    break;
            }
            card.AddComponent(new TextDisplayBuilder(sb.ToString().TrimEnd('\n')));
            AddDivider(card);
            card.AddComponent(new TextDisplayBuilder(
                (isMain ? "★ Your main in this server." : "Not your main in this server.") + "\n"
                + (isShared ? "📋 On this server's roster." : "🔒 Private: only you can see it.")));

            var row = new ActionRowBuilder();
            if (!isMain)
                row.WithButton(new ButtonBuilder("Make main here", $"{MainButtonPrefix}~{userId}~{c.Id}", ButtonStyle.Primary, emote: new Emoji("⭐")));
            if (canShare && !isShared)
                row.WithButton(new ButtonBuilder("Share to roster", $"{ShareButtonPrefix}~{userId}~{c.Id}", ButtonStyle.Success, emote: new Emoji("📋")));
            if (saved.Armory != null)
            {
                if (CardId(userId, CardOverview, saved.Armory) is { } cardId)
                    row.WithButton(new ButtonBuilder("Character card", cardId, ButtonStyle.Secondary, emote: new Emoji("🪪")));
                row.WithButton(new ButtonBuilder("Armory", style: ButtonStyle.Link, url: ForeverNames.ArmoryUrl(saved.Armory.Region, saved.Armory.Slug)));
            }
            if (row.Components.Count > 0) card.AddComponent(row);

            AddFooter(card, saved.Armory != null
                ? "Class, ruleset and faction from Blizzard's armory · ownership isn't verified"
                : SelfReported);
            return new ComponentBuilderV2().AddComponent(card).Build();
        }

        private static string ArmoryLine(ForeverArmoryCharacter armory)
        {
            var line = $"[On the armory]({ForeverNames.ArmoryUrl(armory.Region, armory.Slug)}): level {armory.Level}";
            if (!string.IsNullOrWhiteSpace(armory.Race)) line += $" {Format.Sanitize(armory.Race)}";
            if (!string.IsNullOrWhiteSpace(armory.GuildName)) line += $", in {Format.Sanitize(armory.GuildName)}";
            return line;
        }

        public static MessageComponent CharacterList(
            IReadOnlyList<ForeverCharacter> characters, long? mainId, IReadOnlySet<long> sharedIds, ulong userId)
        {
            var card = new ContainerBuilder().WithAccentColor(ForeverColor);
            var count = characters.Count;
            card.AddComponent(new TextDisplayBuilder(
                "# 📜 Your Forever characters\n"
                + (count == 0 ? "None yet" : $"**{count}** character{(count == 1 ? "" : "s")}")
                + " · only you can see this list"));
            AddDivider(card);

            if (count == 0)
            {
                card.AddComponent(new TextDisplayBuilder("You haven't added any yet. Use `/forever char add`."));
                AddFooter(card, HelpPointer);
                return new ComponentBuilderV2().AddComponent(card).Build();
            }

            var sb = new StringBuilder();
            foreach (var c in characters)
            {
                var marks = (c.Id == mainId ? "★ " : "") + (sharedIds.Contains(c.Id) ? "📋 " : "");
                sb.Append(marks).Append(Line(c)).Append(" · ").Append(ForeverDisplay.Context(c.Region, c.Ruleset, c.Faction)).Append('\n');
            }
            card.AddComponent(new TextDisplayBuilder(Clip(sb.ToString().TrimEnd('\n'), MaxBodyChars)));

            var menu = new SelectMenuBuilder()
                .WithCustomId($"{OpenCardPrefix}~{userId}")
                .WithPlaceholder("Open a character's armory card…")
                .WithMinValues(1)
                .WithMaxValues(1);
            foreach (var c in characters.Take(25))
            {
                menu.AddOption(
                    label: Clip(c.FullName, 100),
                    value: c.Id.ToString(),
                    description: Clip($"{c.Class?.ToString() ?? "No class set"} · {ForeverDisplay.Context(c.Region, c.Ruleset, c.Faction)}", 100),
                    emote: new Emoji(ClassEmoji(c.Class)));
            }
            card.AddComponent(new ActionRowBuilder().WithSelectMenu(menu));

            AddFooter(card, "★ main in this server · 📋 on this server's roster");
            return new ComponentBuilderV2().AddComponent(card).Build();
        }

        public static MessageComponent Member(string displayName, string? avatarUrl, IReadOnlyList<ForeverCharacter> characters, long? mainId)
        {
            var card = new ContainerBuilder().WithAccentColor(ForeverColor);
            AddHeader(card,
                $"# 👤 {Heading(displayName)}\n"
                + (characters.Count == 0
                    ? "in WoW Forever"
                    : $"**{characters.Count}** character{(characters.Count == 1 ? "" : "s")} shared in WoW Forever"),
                avatarUrl, "Member avatar");
            AddDivider(card);

            var sb = new StringBuilder();
            if (characters.Count == 0)
            {
                sb.Append("No characters shared to this server's roster.");
            }
            else
            {
                foreach (var c in characters)
                    sb.Append(c.Id == mainId ? "★ " : "").Append(Line(c)).Append('\n');
            }
            card.AddComponent(new TextDisplayBuilder(Clip(sb.ToString().TrimEnd('\n'), MaxBodyChars)));

            AddFooter(card, (characters.Any(c => c.Id == mainId) ? "★ main · " : "") + "Member-reported, not verified with Blizzard");
            return new ComponentBuilderV2().AddComponent(card).Build();
        }

        // ===== Roster =====

        public static string RosterId(ulong userId, int page, ForeverRosterSort sort)
            => $"{RosterPagePrefix}~{userId}~{page}{Sorts.First(s => s.Sort == sort).Code}";

        /// <summary>Reads a pager button's "page + sort letter". Buttons from before sorting carry only the page.</summary>
        public static (int Page, ForeverRosterSort Sort) ParseRosterPage(string? value)
        {
            value ??= string.Empty;
            var sort = ForeverRosterSort.Rank;
            if (value.Length > 0 && char.IsLetter(value[^1]))
            {
                sort = ParseSort(value[^1..]);
                value = value[..^1];
            }
            return (int.TryParse(value, out var page) && page > 0 ? page : 0, sort);
        }

        public static ForeverRosterSort ParseSort(string? code)
            => Sorts.FirstOrDefault(s => code is { Length: 1 } && s.Code == code[0]).Sort;

        /// <summary>Members sorted by their current server display name, then paginated.</summary>
        public static List<List<(string Name, ForeverRosterMember Member)>> RosterPages(
            ForeverRoster roster, Func<long, string?> displayName)
            => Paginate(roster.Members
                .Select(m => (Name: displayName(m.UserId) ?? "Unknown member", Member: m))
                .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(e => e.Member.UserId));

        /// <summary>Splits members into pages by count and by text size so no page overflows a card.</summary>
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
        public static (MessageComponent Card, int Page, int PageCount) RosterPage(
            ForeverRoster roster, Func<long, string?> displayName, int page, ulong userId,
            ForeverRosterSort sort = ForeverRosterSort.Rank, string? iconUrl = null)
        {
            if (roster.Armory == null)
            {
                var pages = RosterPages(roster, displayName);
                var shown = Math.Clamp(page, 0, Math.Max(0, pages.Count - 1));
                return (SharedRoster(roster, pages, shown, userId, iconUrl), shown, pages.Count);
            }

            var rows = ArmoryRosterRows(roster, displayName, sort);
            var armoryPages = PaginateRows(rows);
            var armoryPage = Math.Clamp(page, 0, Math.Max(0, armoryPages.Count - 1));
            return (ArmoryRoster(roster, armoryPages, armoryPage, userId, sort, iconUrl), armoryPage, armoryPages.Count);
        }

        /// <summary>
        /// Blizzard's members in the chosen order, each followed by whoever shared that character here.
        /// Shared characters the armory doesn't list in the guild come last, so nothing a member shared disappears.
        /// </summary>
        public static List<string> ArmoryRosterLines(
            ForeverRoster roster, Func<long, string?> displayName, ForeverRosterSort sort = ForeverRosterSort.Rank)
            => ArmoryRosterRows(roster, displayName, sort).Select(r => r.Line).ToList();

        private static List<(string Line, ForeverArmoryMember? Member)> ArmoryRosterRows(
            ForeverRoster roster, Func<long, string?> displayName, ForeverRosterSort sort)
        {
            var shared = roster.Members
                .SelectMany(m => m.Characters.Select(c => (Character: c, m.UserId, IsMain: c.Id == m.MainId)))
                .ToList();
            var byName = shared.ToLookup(s => (s.Character.NormalizedFirstName, s.Character.NormalizedLastName));
            var listed = new HashSet<long>();
            string Who((ForeverCharacter Character, long UserId, bool IsMain) s)
                => (s.IsMain ? "★ " : "") + Format.Sanitize(displayName(s.UserId) ?? "Unknown member");

            var rows = new List<(string Line, ForeverArmoryMember? Member)>();
            foreach (var member in Sorted(roster.Armory!.Members, sort))
            {
                var line = new StringBuilder($"{ClassEmoji(member.Class)} **{Format.Sanitize(member.FullName.Trim())}**");
                if (member.Level > 0 || member.Class != null)
                    line.Append(" · ").Append(string.Join(" ", new[] { member.Level > 0 ? member.Level.ToString() : null, member.Class?.ToString() }.Where(p => p != null)));
                if (member.Rank is { } rank) line.Append(" · ").Append(rank == 0 ? "Guild Master" : $"Rank {rank}");

                var owners = byName[(ForeverNames.Normalize(member.FirstName), ForeverNames.Normalize(member.Surname))].ToList();
                if (owners.Count > 0)
                {
                    line.Append(" — ").Append(string.Join(", ", owners.Select(Who)));
                    listed.UnionWith(owners.Select(o => o.Character.Id));
                }
                rows.Add((line.ToString(), member));
            }

            var unlisted = shared.Where(s => !listed.Contains(s.Character.Id))
                .OrderBy(s => s.Character.FullName, StringComparer.OrdinalIgnoreCase)
                .Select(s => $"{Line(s.Character)} — {Who(s)}")
                .ToList();
            if (unlisted.Count > 0)
            {
                // The heading rides on its first line so a page never ends with a bare heading.
                unlisted[0] = $"\n__Shared here, not in the guild on the armory__\n{unlisted[0]}";
                rows.AddRange(unlisted.Select(line => (line, (ForeverArmoryMember?)null)));
            }
            return rows;
        }

        private static IEnumerable<ForeverArmoryMember> Sorted(IEnumerable<ForeverArmoryMember> members, ForeverRosterSort sort)
        {
            IOrderedEnumerable<ForeverArmoryMember> ordered = sort switch
            {
                ForeverRosterSort.Level => members.OrderByDescending(m => m.Level).ThenBy(m => m.Rank ?? int.MaxValue),
                ForeverRosterSort.Name => members.OrderBy(m => m.FullName, StringComparer.OrdinalIgnoreCase),
                ForeverRosterSort.Class => members.OrderBy(m => m.Class.HasValue ? (int)m.Class.Value : int.MaxValue).ThenByDescending(m => m.Level),
                _ => members.OrderBy(m => m.Rank ?? int.MaxValue).ThenByDescending(m => m.Level)
            };
            return ordered.ThenBy(m => m.FullName, StringComparer.OrdinalIgnoreCase);
        }

        private static List<List<(string Line, ForeverArmoryMember? Member)>> PaginateRows(
            IEnumerable<(string Line, ForeverArmoryMember? Member)> rows)
        {
            var pages = new List<List<(string Line, ForeverArmoryMember? Member)>>();
            var current = new List<(string Line, ForeverArmoryMember? Member)>();
            var size = 0;
            foreach (var row in rows)
            {
                if (current.Count > 0 && (current.Count >= MaxArmoryLinesPerPage || size + row.Line.Length + 1 > MaxPageChars))
                {
                    pages.Add(current);
                    current = new List<(string Line, ForeverArmoryMember? Member)>();
                    size = 0;
                }
                current.Add(row);
                size += row.Line.Length + 1;
            }
            if (current.Count > 0) pages.Add(current);
            return pages;
        }

        private static MessageComponent ArmoryRoster(
            ForeverRoster roster, List<List<(string Line, ForeverArmoryMember? Member)>> pages, int page,
            ulong userId, ForeverRosterSort sort, string? iconUrl)
        {
            var guild = roster.Guild;
            var members = roster.Armory!.Members;
            var count = members.Count;
            var card = new ContainerBuilder().WithAccentColor(FactionColor(guild.Faction));

            var heading = new StringBuilder();
            heading.Append("# 👥 ").Append(Heading(guild.Name)).Append(" roster\n");
            heading.Append($"**{count:N0}** member{(count == 1 ? "" : "s")} on Blizzard's armory · {ForeverDisplay.Context(guild.Region, guild.Ruleset, guild.Faction)}");
            var breakdown = ClassBreakdown(members);
            if (breakdown.Length > 0) heading.Append('\n').Append(breakdown);
            AddHeader(card, heading.ToString(), iconUrl, "Server icon");
            AddDivider(card);

            card.AddComponent(new TextDisplayBuilder(pages.Count == 0
                ? "The armory lists no members for this guild yet."
                : Clip(string.Join('\n', pages[page].Select(r => r.Line)), MaxBodyChars)));

            if (pages.Count > 0)
            {
                // Only characters the armory can show: it has none below level 10.
                var pickable = pages[page]
                    .Where(r => r.Member is { Level: >= ArmoryMinLevel } m && PickValue(m) != null)
                    .Select(r => r.Member!)
                    .DistinctBy(PickValue)
                    .ToList();
                if (pickable.Count > 0)
                {
                    var pick = new SelectMenuBuilder()
                        .WithCustomId($"{RosterPickPrefix}~{userId}")
                        .WithPlaceholder("Open a character's armory card…")
                        .WithMinValues(1)
                        .WithMaxValues(1);
                    foreach (var m in pickable)
                    {
                        pick.AddOption(
                            label: Clip(m.FullName.Trim(), 100),
                            value: PickValue(m)!,
                            description: Clip(string.Join(" · ", new[]
                            {
                                $"{m.Level} {m.Class?.ToString()}".Trim(),
                                m.Rank is { } rank ? (rank == 0 ? "Guild Master" : $"Rank {rank}") : null
                            }.Where(p => !string.IsNullOrEmpty(p))), 100),
                            emote: new Emoji(ClassEmoji(m.Class)));
                    }
                    card.AddComponent(new ActionRowBuilder().WithSelectMenu(pick));
                }

                if (count > 1)
                {
                    var sortMenu = new SelectMenuBuilder()
                        .WithCustomId($"{RosterSortPrefix}~{userId}")
                        .WithPlaceholder("Sort by…")
                        .WithMinValues(1)
                        .WithMaxValues(1);
                    foreach (var option in Sorts)
                    {
                        sortMenu.AddOption(
                            label: "Sort: " + option.Label,
                            value: option.Code.ToString(),
                            description: option.Description,
                            isDefault: option.Sort == sort);
                    }
                    card.AddComponent(new ActionRowBuilder().WithSelectMenu(sortMenu));
                }
            }

            AddPager(card, userId, page, pages.Count, sort);

            var footer = "A name after the dash shared that character with `/forever roster share` · ★ main";
            footer += " · the armory's roster can be 3 hours behind";
            if (count >= ArmoryRosterCap) footer += $" · Blizzard lists the first {ArmoryRosterCap:N0}";
            AddFooter(card, footer);
            return new ComponentBuilderV2().AddComponent(card).Build();
        }

        private static MessageComponent SharedRoster(
            ForeverRoster roster, List<List<(string Name, ForeverRosterMember Member)>> pages, int page, ulong userId, string? iconUrl)
        {
            var guild = roster.Guild;
            var memberCount = pages.Sum(p => p.Count);
            var card = new ContainerBuilder().WithAccentColor(FactionColor(guild.Faction));
            AddHeader(card,
                $"# 👥 {Heading(guild.Name)} roster\n"
                + (memberCount > 0 ? $"**{memberCount:N0}** member{(memberCount == 1 ? "" : "s")} · " : "")
                + $"member-reported characters · {ForeverDisplay.Context(guild.Region, guild.Ruleset, guild.Faction)}"
                + (roster.ArmoryMissed ? "\nBlizzard's armory didn't send the guild's roster, so this only shows what members shared." : ""),
                iconUrl, "Server icon");
            AddDivider(card);

            if (pages.Count == 0)
            {
                card.AddComponent(new TextDisplayBuilder("Nobody has shared a character yet. Share yours with `/forever roster share`."));
                AddFooter(card, "Not verified with Blizzard");
                return new ComponentBuilderV2().AddComponent(card).Build();
            }

            var sb = new StringBuilder();
            foreach (var (name, member) in pages[page])
                sb.Append(MemberBlock(name, member));
            card.AddComponent(new TextDisplayBuilder(Clip(sb.ToString().TrimEnd('\n'), MaxBodyChars)));

            AddPager(card, userId, page, pages.Count, ForeverRosterSort.Rank);
            AddFooter(card, "★ main · Not verified with Blizzard");
            return new ComponentBuilderV2().AddComponent(card).Build();
        }

        private static void AddPager(ContainerBuilder card, ulong userId, int page, int pageCount, ForeverRosterSort sort)
        {
            if (pageCount <= 1) return;
            card.AddComponent(new ActionRowBuilder()
                .WithButton(new ButtonBuilder("Prev", RosterId(userId, page - 1, sort), ButtonStyle.Secondary, emote: new Emoji("◀️"), isDisabled: page <= 0))
                .WithButton(new ButtonBuilder($"Page {page + 1}/{pageCount}", $"{RosterPagePrefix}_info", ButtonStyle.Secondary, isDisabled: true))
                .WithButton(new ButtonBuilder("Next", RosterId(userId, page + 1, sort), ButtonStyle.Secondary, emote: new Emoji("▶️"), isDisabled: page >= pageCount - 1)));
        }

        /// <summary>"⚔️ 5 · 🔮 4 · **3** at level 60": what the guild is made of, biggest class first.</summary>
        private static string ClassBreakdown(IReadOnlyList<ForeverArmoryMember> members)
        {
            var parts = members
                .Where(m => m.Class != null)
                .GroupBy(m => m.Class!.Value)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => (int)g.Key)
                .Select(g => $"{g.Key.Emoji()} {g.Count():N0}")
                .ToList();
            var capped = members.Count(m => m.Level >= MaxLevel);
            if (capped > 0) parts.Add($"**{capped:N0}** at level {MaxLevel}");
            return string.Join(" · ", parts);
        }

        /// <summary>The roster menu's value for one member: both name parts, or null when the bot couldn't read them back.</summary>
        private static string? PickValue(ForeverArmoryMember member)
        {
            if (!ForeverNames.TryCleanPart(member.FirstName, "First name", out _, out _)
                || !ForeverNames.TryCleanPart(member.Surname, "Surname", out _, out _))
                return null;
            var value = $"{member.FirstName}~{member.Surname}";
            return value.Length <= 100 && value.Count(ch => ch == '~') == 1 ? value : null;
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

        public static bool IsGear(string? view) => view != null && view.EndsWith(CardGear, StringComparison.Ordinal);

        public static bool IsFromRoster(string? view) => view != null && view.StartsWith(FromRoster, StringComparison.Ordinal);

        /// <param name="view">"o" or "g", led by "r" when the card was opened from the roster.</param>
        public static MessageComponent Card(ForeverArmoryCard card, string view, ulong userId)
        {
            var c = card.Character;
            var gear = IsGear(view);
            var fromRoster = IsFromRoster(view);
            var container = new ContainerBuilder().WithAccentColor(ClassColor(c.Class, c.Faction));

            var heading = new StringBuilder();
            heading.Append("# ").Append(ClassEmoji(c.Class)).Append(' ').Append(Heading(c.FullName.Trim())).Append('\n');
            heading.Append("**Level ").Append(c.Level).Append("**");
            var kind = string.Join(" ", new[] { c.Race, c.ClassName ?? c.Class?.ToString() }.Where(p => !string.IsNullOrWhiteSpace(p)));
            if (kind.Length > 0) heading.Append(' ').Append(Format.Sanitize(kind));
            if (!string.IsNullOrWhiteSpace(c.GuildName)) heading.Append(" · <").Append(Format.Sanitize(c.GuildName)).Append('>');
            heading.Append('\n').Append(string.Join(" · ", new[]
            {
                c.Faction is { } faction ? $"{faction.Emoji()} {faction}" : null,
                c.Ruleset?.Name() ?? c.RulesetName,
                c.Region.ToString()
            }.Where(p => !string.IsNullOrWhiteSpace(p))));
            AddHeader(container, heading.ToString(), card.AvatarUrl, "Character portrait");

            container.AddComponent(CardButtons(userId, c, gear, fromRoster));
            AddDivider(container);
            container.AddComponent(new TextDisplayBuilder(Clip(gear ? GearText(card) : OverviewText(card), MaxBodyChars)));
            if (gear && card.RenderUrl != null)
                container.AddComponent(new MediaGalleryBuilder().AddItem(card.RenderUrl, $"{c.FullName.Trim()} in game"));

            AddFooter(container, "From Blizzard's armory · updates when the character logs out");
            return new ComponentBuilderV2().AddComponent(container).Build();
        }

        private static string OverviewText(ForeverArmoryCard card)
        {
            var c = card.Character;
            var sb = new StringBuilder();
            if (c.Level is > 0 and < MaxLevel)
                sb.Append("-# ").Append(LevelBar(c.Level)).Append($" level {c.Level} of {MaxLevel}\n");
            if (card.Talents.Count > 0)
                sb.Append("🌳 **Talents** ").Append(string.Join(" / ", card.Talents.Select(t => $"{Format.Sanitize(t.Name)} {t.Points}"))).Append('\n');
            if (c.ItemLevel is > 0) sb.Append("🛡️ **Item level** ").Append(c.ItemLevel).Append('\n');
            if (card.Professions.Count > 0)
            {
                sb.Append("🧰 **Professions** ")
                    .Append(string.Join(" · ", card.Professions.Select(p => $"{Format.Sanitize(p.Name)} {p.Skill}{(p.MaxSkill > 0 ? $"/{p.MaxSkill}" : "")}")))
                    .Append('\n');
            }
            if (card.Stats is { } stats)
            {
                var parts = new[]
                {
                    stats.Deaths is { } deaths ? $"💀 **Deaths** {deaths:N0}" : null,
                    stats.Quests is { } quests ? $"📜 **Quests** {quests:N0}" : null,
                    stats.Kills is { } kills ? $"⚔️ **Kills** {kills:N0}" : null,
                    stats.Dungeons is { } dungeons ? $"🏰 **Dungeons entered** {dungeons:N0}" : null
                }.Where(p => p != null);
                sb.Append(string.Join(" · ", parts)).Append('\n');
            }
            if (c.LastSeen is { } seen) sb.Append("🕒 **Last seen** <t:").Append(seen.ToUnixTimeSeconds()).Append(":R>\n");

            return sb.Length == 0 ? "*The armory has no more details for this character yet.*" : sb.ToString().TrimEnd('\n');
        }

        private static string GearText(ForeverArmoryCard card)
        {
            var sb = new StringBuilder();
            if (card.Character.ItemLevel is > 0) sb.Append("🛡️ **Item level** ").Append(card.Character.ItemLevel).Append("\n\n");
            if (card.Gear.Count == 0)
                return sb.Append("*The armory has no gear for this character right now.*").ToString();

            var known = GearGroups.SelectMany(g => g).ToHashSet();
            var groups = GearGroups
                .Select(slots => card.Gear.Where(i => slots.Contains(i.Slot)).OrderBy(i => Array.IndexOf(slots, i.Slot)).ToList())
                .Append(card.Gear.Where(i => !known.Contains(i.Slot)).ToList())
                .Where(g => g.Count > 0);
            foreach (var group in groups)
            {
                foreach (var item in group)
                {
                    sb.Append(CharViewHelpers.GetQualityEmoji(item.Quality!)).Append(" **").Append(Format.Sanitize(item.SlotName)).Append("** ")
                        .Append(Format.Sanitize(item.Name));
                    if (item.ItemLevel is > 0) sb.Append(" · ").Append(item.ItemLevel);
                    sb.Append('\n');
                }
                sb.Append('\n');
            }
            return sb.ToString().TrimEnd('\n');
        }

        /// <summary>The two view tabs, the way back to the roster when the card came from it, and a link to the armory.</summary>
        private static ActionRowBuilder CardButtons(ulong userId, ForeverArmoryCharacter c, bool gear, bool fromRoster)
        {
            var row = new ActionRowBuilder();
            var origin = fromRoster ? FromRoster : "";
            // A name the bot wouldn't accept back, or an id past Discord's limit, just loses the tabs.
            if (CardId(userId, origin + CardOverview, c) is { } overviewId && CardId(userId, origin + CardGear, c) is { } gearId)
            {
                row.WithButton(new ButtonBuilder("Overview", overviewId, gear ? ButtonStyle.Primary : ButtonStyle.Success, emote: new Emoji("📋")));
                row.WithButton(new ButtonBuilder("Gear", gearId, gear ? ButtonStyle.Success : ButtonStyle.Primary, emote: new Emoji("🛡️")));
            }
            if (fromRoster)
                row.WithButton(new ButtonBuilder("Roster", RosterId(userId, 0, ForeverRosterSort.Rank), ButtonStyle.Secondary, emote: new Emoji("👥")));
            row.WithButton(new ButtonBuilder("Armory", style: ButtonStyle.Link, url: ForeverNames.ArmoryUrl(c.Region, c.Slug)));
            return row;
        }

        /// <summary>The custom id that opens this character's card, or null when Discord or the bot couldn't carry it.</summary>
        private static string? CardId(ulong userId, string view, ForeverArmoryCharacter c)
        {
            var id = $"{CardButtonPrefix}~{userId}~{view}~{(int)c.Region}~{c.FirstName}~{c.Surname}";
            return id.Length <= 100
                && ForeverNames.TryCleanPart(c.FirstName, "First name", out _, out _)
                && ForeverNames.TryCleanPart(c.Surname, "Surname", out _, out _)
                ? id
                : null;
        }

        private static string LevelBar(int level)
        {
            var filled = Math.Clamp(level * 10 / MaxLevel, 0, 10);
            return new string('▰', filled) + new string('▱', 10 - filled);
        }

        // ===== Shell =====

        private static void AddHeader(ContainerBuilder card, string heading, string? thumbnailUrl, string thumbnailLabel)
        {
            var text = new TextDisplayBuilder(heading);
            if (IsHttps(thumbnailUrl))
            {
                card.AddComponent(new SectionBuilder(
                    new ThumbnailBuilder(new UnfurledMediaItemProperties(thumbnailUrl), thumbnailLabel),
                    text));
                return;
            }
            card.AddComponent(text);
        }

        private static void AddDivider(ContainerBuilder card)
            => card.AddComponent(new SeparatorBuilder().WithIsDivider(true).WithSpacing(SeparatorSpacingSize.Small));

        private static void AddFooter(ContainerBuilder card, string text)
            => card.AddComponent(new TextDisplayBuilder("-# " + text));

        private static bool IsHttps(string? url)
            => Uri.TryCreate(url, UriKind.Absolute, out var parsed) && parsed.Scheme == Uri.UriSchemeHttps;

        private static string ClassEmoji(ForeverClass? characterClass) => characterClass?.Emoji() ?? "❔";

        private static Color ClassColor(ForeverClass? characterClass, ForeverFaction? faction)
            => characterClass is { } known ? new Color(known.Color()) : FactionColor(faction);

        private static Color FactionColor(ForeverFaction? faction) => faction switch
        {
            ForeverFaction.Alliance => AllianceColor,
            ForeverFaction.Horde => HordeColor,
            _ => ForeverColor
        };

        /// <summary>Text safe for a heading line: one line, markdown escaped.</summary>
        private static string Heading(string value) => Format.Sanitize(value.Replace('\r', ' ').Replace('\n', ' ').Trim());

        private static string Clip(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
    }
}
