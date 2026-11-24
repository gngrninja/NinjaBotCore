#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Discord;
using NinjaBotCore.Database;
using NinjaBotCore.Models.Wow.Forever;
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
    /// <param name="Shared">How many of the caller's characters are on this server's roster.</param>
    /// <param name="IsOfficer">Whether the caller can run the guild commands (Kick Members).</param>
    public sealed record ForeverSetup(ForeverGuild? Guild, int Characters, string? MainName, int Shared, bool IsOfficer);

    /// <summary>
    /// Components V2 cards for /forever, in the same shell as /char and /raid-recap: a titled header,
    /// one accent colour, emoji buttons inside the card and a single small footer line. Each card says
    /// whether its details are self-reported or from Blizzard's armory.
    /// </summary>
    public static partial class ForeverViews
    {
        public const string MainButtonPrefix = "forever_main";
        public const string ShareButtonPrefix = "forever_share";
        public const string HideButtonPrefix = "forever_hide";
        public const string RemoveButtonPrefix = "forever_remove";
        public const string ManagePrefix = "forever_manage";
        public const string ListButtonPrefix = "forever_list";
        public const string CardMenuPrefix = "forever_cardmenu";
        public const string RosterPagePrefix = "forever_roster";
        public const string RosterSortPrefix = "forever_rsort";
        public const string RosterPickPrefix = "forever_rpick";
        public const string CardButtonPrefix = "forever_card";
        public const string OpenCardPrefix = "forever_open";
        public const string CardOverview = "o";
        public const string CardGear = "g";
        public const string CardProfessions = "p";
        /// <summary>Leads the id of the profession whose recipes the card lists: "k197" is Tailoring's.</summary>
        public const string CardRecipes = "k";
        /// <summary>Leads the card's view code when it was opened from the roster, so the card offers the way back.</summary>
        public const string FromRoster = "r";

        public const int MaxLevel = 60;

        private const string SelfReported = "Added by members, not checked against Blizzard's roster";
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

        private static readonly (string Title, string[] Slots)[] GearGroups =
        {
            ("Armor", new[] { "HEAD", "SHOULDER", "BACK", "CHEST", "SHIRT", "TABARD", "WRIST", "HANDS", "WAIST", "LEGS", "FEET" }),
            ("Jewelry", new[] { "NECK", "FINGER_1", "FINGER_2", "TRINKET_1", "TRINKET_2" }),
            ("Weapons", new[] { "MAIN_HAND", "OFF_HAND", "RANGED", "AMMO" })
        };

        // Slots every character can fill. A shirt, a tabard, an off hand and ammo are a choice, and
        // not every class has something for the ranged slot, so those are never called empty.
        private static readonly (string Slot, string Name)[] SlotsToFill =
        {
            ("HEAD", "Head"), ("NECK", "Neck"), ("SHOULDER", "Shoulders"), ("BACK", "Back"), ("CHEST", "Chest"), ("WRIST", "Wrist"),
            ("HANDS", "Hands"), ("WAIST", "Waist"), ("LEGS", "Legs"), ("FEET", "Feet"), ("FINGER_1", "Ring 1"), ("FINGER_2", "Ring 2"),
            ("TRINKET_1", "Trinket 1"), ("TRINKET_2", "Trinket 2"), ("MAIN_HAND", "Main Hand")
        };

        // Best first, which is the order the Gear tab counts them in.
        private static readonly (string Type, string Emoji, string Name)[] Qualities =
        {
            ("LEGENDARY", "🟠", "legendary"), ("EPIC", "🟣", "epic"), ("RARE", "🔵", "rare"),
            ("UNCOMMON", "🟢", "uncommon"), ("COMMON", "⚪", "common"), ("POOR", "🔘", "poor")
        };

        private static readonly (ForeverRosterSort Sort, char Code, string Label, string Description)[] Sorts =
        {
            (ForeverRosterSort.Rank, 'r', "Guild rank", "Guild Master first, then by level"),
            (ForeverRosterSort.Level, 'l', "Level", "Highest level first"),
            (ForeverRosterSort.Name, 'n', "Name", "A to Z"),
            (ForeverRosterSort.Class, 'c', "Class", "Grouped by class, highest level first")
        };

        public static string Line(ForeverCharacter c)
            => $"{ClassEmoji(c.Class)} **{Format.Sanitize(c.FullName)}** — {c.Class?.ToString() ?? "class not known yet"}";

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
        /// Getting started: the three setup steps with the ones already done ticked off, a pointer at the
        /// first step the caller can do right now, then every command and what to know about Forever.
        /// </summary>
        public static MessageComponent Help(string serverName, string? iconUrl, ulong userId, ForeverSetup setup)
        {
            var guild = setup.Guild;
            var hasCharacter = setup.Characters > 0;
            // Setting the guild needs Kick Members, so everyone else is told who to ask.
            var byOfficer = setup.IsOfficer ? "" : "an officer runs ";

            var steps = new (bool Done, bool CanDoNow, string Text)[]
            {
                (guild != null, setup.IsOfficer, guild != null
                    ? $"**Guild set** · {Format.Sanitize(guild.Name)} · {ForeverDisplay.Context(guild.Region, guild.Ruleset, guild.Faction)}"
                    : $"**Set this server's guild** · {byOfficer}`/forever guild set`"),
                (hasCharacter, true, hasCharacter
                    ? $"**{setup.Characters:N0} character{(setup.Characters == 1 ? "" : "s")} saved**"
                      + (setup.MainName != null ? $" · ★ {Format.Sanitize(setup.MainName)} is your main here" : "")
                    : "**Save your character** · `/forever char add`"),
                (setup.Shared > 0, hasCharacter && guild != null, setup.Shared > 0
                    ? $"**{setup.Shared:N0} on the roster** · your name shows next to {(setup.Shared == 1 ? "it" : "them")}"
                    : "**Put it on the roster** · `/forever char list`, pick it, then **Show on roster**")
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
                + "🪪 `/forever char view` · anyone's armory card: level, talents, gear, professions and recipes\n"
                + "👥 `/forever roster` · the guild's members, and who in this server plays whom\n"
                + "🛠️ `/forever craft` · what a recipe takes, what it costs and who in the guild can make it\n"
                + "💰 `/forever price` · what an item goes for on the auction houses\n"
                + "🔥 `/forever camp` · post a camp card; people join and it works out who brings what\n"
                + "⚔️ `/forever logs` · the guild's latest logs on Warcraft Logs; `/watchlogs` posts new ones in a channel\n"
                + "📜 `/forever char add` and `/forever char list` · your own characters: main, roster, remove\n"
                + "🏰 `/forever guild view` · this server's guild; officers (Kick Members) use `guild set` and `guild unset`"));
            AddDivider(card);

            card.AddComponent(new TextDisplayBuilder(
                "### Good to know\n"
                + "- Forever has no realms: a character is a first name and a surname, unique in its region.\n"
                + $"- Everything about a character comes from Blizzard's armory, which has it from level {ArmoryMinLevel} once it logs out. A younger guildmate can still be saved from the guild's roster.\n"
                + "- Saving a character is private. Your name only shows next to it once you put it on this server's roster.\n"
                + "- Prices are TradeSkillMaster's regional averages, not your own auction house."));

            if (guild != null)
            {
                card.AddComponent(new ActionRowBuilder().WithButton(
                    new ButtonBuilder("View roster", RosterId(userId, 0, ForeverRosterSort.Rank), ButtonStyle.Primary, emote: new Emoji("👥"))));
            }

            AddFooter(card, "Characters and rosters come from Blizzard's armory · nothing proves a character is yours");
            return new ComponentBuilderV2().AddComponent(card).Build();
        }

        // ===== Guild =====

        /// <param name="armory">What the armory says about the guild now; null when it didn't answer.</param>
        /// <param name="justSet">The card confirms a set, so it says what to do next.</param>
        public static MessageComponent Guild(
            ForeverGuild guild, string serverName, string? iconUrl, ulong userId, ForeverArmoryGuild? armory = null, bool justSet = false)
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
            if (armory is { MemberCount: > 0 })
                sb.Append("👥 **Members** ").Append(armory.MemberCount.ToString("N0")).Append('\n');
            else if (guild.ArmorySlug == null)
                sb.Append("🪪 **Armory** not matched yet. An officer can run `/forever guild set` again to match it.\n");
            if (justSet)
                sb.Append("\nMembers can now save their characters with `/forever char add`, and `/forever craft` shows who can make what.");
            card.AddComponent(new TextDisplayBuilder(sb.ToString().TrimEnd('\n')));

            card.AddComponent(new ActionRowBuilder().WithButton(
                new ButtonBuilder("View roster", RosterId(userId, 0, ForeverRosterSort.Rank), ButtonStyle.Primary, emote: new Emoji("👥"))));

            var footer = guild.ArmorySlug != null ? "From Blizzard's armory" : SelfReported;
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
                "An officer can set one with `/forever guild set`.\nMembers can still save their own characters with `/forever char add`."));
            AddFooter(card, HelpPointer);
            return new ComponentBuilderV2().AddComponent(card).Build();
        }

        // ===== Saved characters =====

        /// <summary>
        /// One of the caller's characters with everything they can do to it: make it their main here,
        /// put it on or take it off this server's roster, open its armory card, or remove it.
        /// </summary>
        /// <param name="verb">What just happened to it ("added"), or null when the card is only showing it.</param>
        /// <param name="canShare">It fits this server's guild, so it can go on the roster.</param>
        /// <param name="confirmRemove">Remove was pressed once; the next press deletes it.</param>
        public static MessageComponent CharacterSaved(
            ForeverSavedCharacter saved, string? verb, bool isMain, bool isShared, ulong userId, bool canShare, bool confirmRemove = false)
        {
            var c = saved.Character;
            var card = new ContainerBuilder().WithAccentColor(ClassColor(c.Class, c.Faction));

            var sb = new StringBuilder();
            sb.Append("## ").Append(ClassEmoji(c.Class)).Append(' ').Append(Heading(c.FullName));
            if (verb != null) sb.Append(" · ").Append(verb).Append(" ✅");
            sb.Append('\n');
            sb.Append(c.Class?.ToString() ?? "Class not known yet").Append(" · ").Append(ForeverDisplay.Context(c.Region, c.Ruleset, c.Faction)).Append('\n');
            switch (saved.Lookup)
            {
                case ForeverLookupStatus.Found when saved.Armory != null:
                    sb.Append(ArmoryLine(saved.Armory)).Append('\n');
                    break;
                case ForeverLookupStatus.NotFound:
                    sb.Append("Not on the armory yet. ").Append(ForeverService.NotOnArmoryHint).Append('\n');
                    break;
                case ForeverLookupStatus.Unavailable:
                    sb.Append("Blizzard's armory didn't answer just now.\n");
                    break;
            }
            card.AddComponent(new TextDisplayBuilder(sb.ToString().TrimEnd('\n')));
            AddDivider(card);
            card.AddComponent(new TextDisplayBuilder(
                (isMain ? "★ Your main in this server." : "Not your main in this server.") + "\n"
                + (isShared
                    ? "👥 On this server's roster, with your name next to it."
                    : canShare
                        ? "🔒 Private: only you can see that it's yours."
                        : "🔒 Private. This server's roster is for its own guild's region, ruleset and faction, which this character isn't.")));

            var row = new ActionRowBuilder();
            if (!isMain)
                row.WithButton(new ButtonBuilder("Make main", $"{MainButtonPrefix}~{userId}~{c.Id}", ButtonStyle.Primary, emote: new Emoji("⭐")));
            if (isShared)
                row.WithButton(new ButtonBuilder("Hide from roster", $"{HideButtonPrefix}~{userId}~{c.Id}", ButtonStyle.Secondary, emote: new Emoji("🔒")));
            else if (canShare)
                row.WithButton(new ButtonBuilder("Show on roster", $"{ShareButtonPrefix}~{userId}~{c.Id}", ButtonStyle.Success, emote: new Emoji("👥")));
            if (saved.Armory != null && CardId(userId, CardOverview, saved.Armory) is { } cardId)
                row.WithButton(new ButtonBuilder("Armory card", cardId, ButtonStyle.Secondary, emote: new Emoji("🪪")));
            row.WithButton(confirmRemove
                ? new ButtonBuilder("Really remove it", $"{RemoveButtonPrefix}~{userId}~{c.Id}~y", ButtonStyle.Danger, emote: new Emoji("🗑️"))
                : new ButtonBuilder("Remove", $"{RemoveButtonPrefix}~{userId}~{c.Id}~n", ButtonStyle.Secondary, emote: new Emoji("🗑️")));
            row.WithButton(new ButtonBuilder("Your characters", $"{ListButtonPrefix}~{userId}", ButtonStyle.Secondary, emote: new Emoji("📜")));
            card.AddComponent(row);

            AddFooter(card, confirmRemove
                ? "Removing deletes it here, as your main and from every server's roster. The character itself is untouched."
                : "From Blizzard's armory · nothing proves a character is yours");
            return new ComponentBuilderV2().AddComponent(card).Build();
        }

        private static string ArmoryLine(ForeverArmoryCharacter armory)
        {
            var line = $"[On the armory]({ForeverNames.ArmoryUrl(armory.Region, armory.Slug)}): level {armory.Level}";
            if (!string.IsNullOrWhiteSpace(armory.Race)) line += $" {Format.Sanitize(armory.Race)}";
            if (!string.IsNullOrWhiteSpace(armory.GuildName)) line += $", in {Format.Sanitize(armory.GuildName)}";
            return line;
        }

        /// <param name="note">A line to lead with, such as what was just removed.</param>
        public static MessageComponent CharacterList(
            IReadOnlyList<ForeverCharacter> characters, long? mainId, IReadOnlySet<long> sharedIds, ulong userId, string? note = null)
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
                card.AddComponent(new TextDisplayBuilder((note == null ? "" : note + "\n") + "You haven't saved any yet. Use `/forever char add`."));
                AddFooter(card, HelpPointer);
                return new ComponentBuilderV2().AddComponent(card).Build();
            }

            var sb = new StringBuilder();
            if (note != null) sb.Append(note).Append("\n\n");
            foreach (var c in characters)
            {
                var marks = (c.Id == mainId ? "★ " : "") + (sharedIds.Contains(c.Id) ? "👥 " : "");
                sb.Append(marks).Append(Line(c)).Append(" · ").Append(ForeverDisplay.Context(c.Region, c.Ruleset, c.Faction)).Append('\n');
            }
            card.AddComponent(new TextDisplayBuilder(Clip(sb.ToString().TrimEnd('\n'), MaxBodyChars)));

            var menu = new SelectMenuBuilder()
                .WithCustomId($"{ManagePrefix}~{userId}")
                .WithPlaceholder("Pick a character: main, roster, armory card, remove…")
                .WithMinValues(1)
                .WithMaxValues(1);
            foreach (var c in characters.Take(25))
            {
                menu.AddOption(
                    label: Clip(c.FullName, 100),
                    value: c.Id.ToString(),
                    description: Clip($"{c.Class?.ToString() ?? "Class not known yet"} · {ForeverDisplay.Context(c.Region, c.Ruleset, c.Faction)}", 100),
                    emote: new Emoji(ClassEmoji(c.Class)));
            }
            card.AddComponent(new ActionRowBuilder().WithSelectMenu(menu));

            AddFooter(card, "★ main in this server · 👥 on this server's roster · add another with /forever char add");
            return new ComponentBuilderV2().AddComponent(card).Build();
        }

        public static MessageComponent Member(string displayName, string? avatarUrl, IReadOnlyList<ForeverCharacter> characters, long? mainId)
        {
            var card = new ContainerBuilder().WithAccentColor(ForeverColor);
            AddHeader(card,
                $"# 👤 {Heading(displayName)}\n"
                + (characters.Count == 0
                    ? "in WoW Forever"
                    : $"**{characters.Count}** character{(characters.Count == 1 ? "" : "s")} on this server's Forever roster"),
                avatarUrl, "Member avatar");
            AddDivider(card);

            var sb = new StringBuilder();
            if (characters.Count == 0)
            {
                sb.Append("No characters on this server's roster.");
            }
            else
            {
                foreach (var c in characters)
                    sb.Append(c.Id == mainId ? "★ " : "").Append(Line(c)).Append('\n');
            }
            card.AddComponent(new TextDisplayBuilder(Clip(sb.ToString().TrimEnd('\n'), MaxBodyChars)));

            AddFooter(card, (characters.Any(c => c.Id == mainId) ? "★ main · " : "") + "Put there by the member · nothing proves a character is theirs");
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
                unlisted[0] = $"\n__On this server's roster, not in the guild on the armory__\n{unlisted[0]}";
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

            var footer = "A name after the dash put that character on the roster from `/forever char list` · ★ main";
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
                + $"characters members added · {ForeverDisplay.Context(guild.Region, guild.Ruleset, guild.Faction)}"
                + (roster.ArmoryMissed ? "\nBlizzard's armory didn't send the guild's roster, so this only shows what members put on it." : ""),
                iconUrl, "Server icon");
            AddDivider(card);

            if (pages.Count == 0)
            {
                card.AddComponent(new TextDisplayBuilder("Nobody has put a character on the roster yet. Add yours from `/forever char list`."));
                AddFooter(card, SelfReported);
                return new ComponentBuilderV2().AddComponent(card).Build();
            }

            var sb = new StringBuilder();
            foreach (var (name, member) in pages[page])
                sb.Append(MemberBlock(name, member));
            card.AddComponent(new TextDisplayBuilder(Clip(sb.ToString().TrimEnd('\n'), MaxBodyChars)));

            AddPager(card, userId, page, pages.Count, ForeverRosterSort.Rank);
            AddFooter(card, "★ main · " + SelfReported);
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

        public static bool IsProfessions(string? view) => view != null && view.EndsWith(CardProfessions, StringComparison.Ordinal);

        /// <summary>The profession whose recipes a view code asks for ("k197", or "rk197" from the roster), or null for any other view.</summary>
        public static int? RecipesProfession(string? view)
        {
            if (view == null) return null;
            var code = IsFromRoster(view) ? view[FromRoster.Length..] : view;
            return code.StartsWith(CardRecipes, StringComparison.Ordinal)
                   && int.TryParse(code[CardRecipes.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0
                ? id
                : null;
        }

        /// <summary>The tab a view code asks for. Anything the card doesn't know is the Overview.</summary>
        public static string Tab(string? view)
            => IsGear(view) ? CardGear
                : RecipesProfession(view) is { } profession ? CardRecipes + profession.ToString(CultureInfo.InvariantCulture)
                : IsProfessions(view) ? CardProfessions
                : CardOverview;

        public static bool IsFromRoster(string? view) => view != null && view.StartsWith(FromRoster, StringComparison.Ordinal);

        /// <param name="view">"o", "g", "p" or "k" with a profession id, led by "r" when the card was opened from the roster.</param>
        public static MessageComponent Card(ForeverArmoryCard card, string view, ulong userId)
        {
            var c = card.Character;
            var tab = Tab(view);
            // A profession the character has dropped since the menu was drawn falls back to the tab it came from.
            var recipesOf = RecipesProfession(tab) is { } wanted ? card.Professions.FirstOrDefault(p => p.Id == wanted && p.Known is { Count: > 0 }) : null;
            if (RecipesProfession(tab) != null) tab = CardProfessions;
            var gear = tab == CardGear;
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

            container.AddComponent(CardButtons(userId, c, tab, fromRoster, card.Professions.Count > 0));
            AddDivider(container);
            var body = gear ? GearText(card)
                : recipesOf != null ? RecipesText(recipesOf)
                : tab == CardProfessions ? ProfessionsText(card.Professions)
                : OverviewText(card);
            container.AddComponent(new TextDisplayBuilder(Clip(body, MaxBodyChars)));
            if (tab == CardProfessions && RecipeMenu(userId, c, fromRoster, card.Professions, recipesOf) is { } recipeMenu)
                container.AddComponent(new ActionRowBuilder().WithSelectMenu(recipeMenu));
            if (gear && card.RenderUrl != null)
                container.AddComponent(new MediaGalleryBuilder().AddItem(card.RenderUrl, $"{c.FullName.Trim()} in game"));

            var footer = "From Blizzard's armory · updates when the character logs out";
            if (gear && body.Contains("](" + ForeverWowhead.Site, StringComparison.Ordinal))
                footer += " · item names open Wowhead";
            // The Overview only links the primary professions; the Professions tab links them all.
            if (!gear && (recipesOf != null
                    ? ForeverProfessions.WowheadUrl(recipesOf.Id) != null
                    : card.Professions.Any(p => (p.IsPrimary || tab == CardProfessions) && ForeverProfessions.WowheadUrl(p.Id) != null)))
                footer += " · profession names open Wowhead";
            AddFooter(container, footer);
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
            AppendProfessions(sb, card.Professions);
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

        // ===== Professions =====

        private const int ShortBar = 10;
        private const int LongBar = 20;

        /// <summary>
        /// The Overview's take: a short bar for each primary profession, with the secondary ones on
        /// one small line. The Professions tab has the rest.
        /// </summary>
        private static void AppendProfessions(StringBuilder sb, IReadOnlyList<ForeverArmoryProfession> professions)
        {
            if (professions.Count == 0) return;
            var scale = SkillScale(professions);
            var primaries = professions.Where(p => p.IsPrimary).ToList();
            var others = string.Join(" · ", professions.Where(p => !p.IsPrimary)
                .Select(p => $"{ForeverProfessions.Emoji(p.Id)} {Format.Sanitize(p.Name)} {SkillText(p)}"));

            if (primaries.Count == 0)
            {
                sb.Append("🧰 **Professions** ").Append(others).Append('\n');
                return;
            }

            sb.Append("🧰 **Professions**\n");
            foreach (var p in primaries)
            {
                sb.Append(ForeverProfessions.Emoji(p.Id)).Append(' ').Append(ProfessionName(p))
                    .Append(" `").Append(SkillBar(p, scale, ShortBar)).Append("` ").Append(SkillText(p)).Append('\n');
            }
            if (others.Length > 0) sb.Append("-# ").Append(others).Append('\n');
        }

        /// <summary>The Professions tab: every profession with its rank, recipes known and a bar of the whole climb.</summary>
        private static string ProfessionsText(IReadOnlyList<ForeverArmoryProfession> professions)
        {
            if (professions.Count == 0) return "*The armory has no professions for this character right now.*";

            var scale = SkillScale(professions);
            var sb = new StringBuilder();
            foreach (var group in new[] { (Title: "Primary", Primary: true), (Title: "Secondary", Primary: false) })
            {
                var members = professions.Where(p => p.IsPrimary == group.Primary).ToList();
                if (members.Count == 0) continue;

                sb.Append("### ").Append(group.Title).Append('\n');
                foreach (var p in members)
                {
                    sb.Append(ForeverProfessions.Emoji(p.Id)).Append(" **").Append(ProfessionName(p)).Append("**");
                    if (ForeverProfessions.Rank(p.MaxSkill) is { } rank) sb.Append(" · ").Append(rank);
                    if (p.Recipes > 0) sb.Append($" · {p.Recipes:N0} recipe{(p.Recipes == 1 ? "" : "s")}");
                    sb.Append('\n');

                    sb.Append('`').Append(SkillBar(p, scale, LongBar)).Append("` **").Append(p.Skill).Append("**");
                    if (p.MaxSkill > 0) sb.Append(" / ").Append(p.MaxSkill);
                    if (p.Skill >= scale) sb.Append(" · maxed");
                    else if (p.MaxSkill > 0 && p.Skill >= p.MaxSkill && ForeverProfessions.NextRank(p.MaxSkill) is { } next)
                        sb.Append(" · at the cap, ").Append(next).Append(" is next");
                    sb.Append('\n');
                }
            }
            sb.Append("-# █ skill · ▒ still to earn in the trained rank · ░ ranks not trained yet");
            return sb.ToString();
        }

        /// <summary>One profession's recipes by name, A to Z, as many as the card holds.</summary>
        private static string RecipesText(ForeverArmoryProfession profession)
        {
            var names = (profession.Known ?? Array.Empty<ForeverKnownRecipe>())
                .Select(r => r.Name.Replace('\r', ' ').Replace('\n', ' ').Trim())
                .Where(n => n.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var sb = new StringBuilder();
            sb.Append("### ").Append(ForeverProfessions.Emoji(profession.Id)).Append(' ').Append(ProfessionName(profession))
                .Append($" · {names.Count:N0} recipe{(names.Count == 1 ? "" : "s")}\n");
            sb.Append("-# ").Append(SkillText(profession));
            if (ForeverProfessions.Rank(profession.MaxSkill) is { } rank) sb.Append(" · ").Append(rank);
            sb.Append(" · `/forever craft` shows what one takes and who else makes it\n");

            // Room for the "and N more" line, so the list is never cut mid-name.
            const int room = MaxBodyChars - 40;
            var shown = 0;
            foreach (var name in names)
            {
                var piece = (shown == 0 ? "" : " · ") + Format.Sanitize(name);
                if (sb.Length + piece.Length > room) break;
                sb.Append(piece);
                shown++;
            }
            if (shown < names.Count) sb.Append($"\n-# …and {names.Count - shown:N0} more");
            return sb.ToString();
        }

        /// <summary>
        /// The menu under the Professions tab: one entry per profession the armory listed recipes for,
        /// and the way back to the skill bars. Null when there is nothing to list or the id wouldn't fit.
        /// </summary>
        private static SelectMenuBuilder? RecipeMenu(
            ulong userId, ForeverArmoryCharacter c, bool fromRoster, IReadOnlyList<ForeverArmoryProfession> professions, ForeverArmoryProfession? shown)
        {
            var listed = professions.Where(p => p.Id is > 0 && p.Known is { Count: > 0 }).DistinctBy(p => p.Id).Take(24).ToList();
            var id = $"{CardMenuPrefix}~{userId}~{(fromRoster ? FromRoster : "n")}~{(int)c.Region}~{c.FirstName}~{c.Surname}";
            if (listed.Count == 0 || id.Length > 100 || CardId(userId, CardOverview, c) == null) return null;

            var menu = new SelectMenuBuilder()
                .WithCustomId(id)
                .WithPlaceholder("List a profession's recipes…")
                .WithMinValues(1)
                .WithMaxValues(1);
            if (shown != null)
                menu.AddOption("Skill bars", CardProfessions, "Back to every profession's skill", new Emoji(ForeverProfessions.DefaultEmoji));
            foreach (var p in listed)
            {
                menu.AddOption(
                    label: Clip($"{p.Name} recipes", 100),
                    value: CardRecipes + p.Id!.Value.ToString(CultureInfo.InvariantCulture),
                    description: $"{p.Known!.Count:N0} known",
                    emote: new Emoji(ForeverProfessions.Emoji(p.Id)),
                    isDefault: shown != null && p.Id == shown.Id);
            }
            return menu;
        }

        /// <summary>The profession's name, linked to its Wowhead page when the bot knows it has one.</summary>
        private static string ProfessionName(ForeverArmoryProfession p)
            => ForeverProfessions.WowheadUrl(p.Id) is { } url ? $"[{Format.Sanitize(p.Name)}]({url})" : Format.Sanitize(p.Name);

        private static string SkillText(ForeverArmoryProfession p) => p.MaxSkill > 0 ? $"{p.Skill}/{p.MaxSkill}" : p.Skill.ToString();

        /// <summary>What a full bar stands for: Artisan's cap, or more if the armory reports a higher one.</summary>
        private static int SkillScale(IEnumerable<ForeverArmoryProfession> professions)
            => professions.Aggregate(ForeverProfessions.MaxSkill, (scale, p) => Math.Max(scale, Math.Max(p.Skill, p.MaxSkill)));

        /// <summary>
        /// One bar for the whole climb: solid for skill earned, medium for what is left in the rank the
        /// character trained, light for ranks not trained yet. Any skill at all shows at least one block.
        /// </summary>
        private static string SkillBar(ForeverArmoryProfession p, int scale, int width)
        {
            var skill = Math.Max(p.Skill, 0);
            var filled = (int)Math.Min((long)skill * width / scale, width);
            if (skill > 0 && filled == 0) filled = 1;
            // With no cap from the armory, or none left to earn, nothing is drawn as still to earn.
            var trained = p.MaxSkill > skill
                ? (int)Math.Clamp((long)Math.Ceiling(p.MaxSkill * (double)width / scale), filled, width)
                : filled;
            return new string('█', filled) + new string('▒', trained - filled) + new string('░', width - trained);
        }

        // ===== Gear =====

        private const int MaxSlotLabel = 12;

        /// <summary>
        /// The Gear tab: item level, sets and a count by quality, then every item on one line under
        /// Armor, Jewelry or Weapons. Item names link to Wowhead unless that would not fit.
        /// </summary>
        private static string GearText(ForeverArmoryCard card)
        {
            var linked = GearText(card, links: true);
            return linked.Length <= MaxBodyChars ? linked : GearText(card, links: false);
        }

        private static string GearText(ForeverArmoryCard card, bool links)
        {
            var sb = new StringBuilder();
            if (card.Character.ItemLevel is > 0) sb.Append("🛡️ **Item level** ").Append(card.Character.ItemLevel).Append('\n');
            if (card.Gear.Count == 0)
                return sb.Append("*The armory has no gear for this character right now.*").ToString();

            foreach (var set in card.Sets ?? Array.Empty<ForeverArmorySet>())
            {
                sb.Append("🧩 **Set** ").Append(Linked(set.Name, links ? ForeverWowhead.ItemSet(set.Id) : null));
                if (set.Pieces > 0) sb.Append(' ').Append(set.Equipped).Append('/').Append(set.Pieces);
                if (set.Bonuses > 0) sb.Append($" · {set.ActiveBonuses} of {set.Bonuses} bonuses active");
                sb.Append('\n');
            }

            var counts = Qualities
                .Select(q => (q.Emoji, q.Name, Count: card.Gear.Count(i => string.Equals(i.Quality, q.Type, StringComparison.OrdinalIgnoreCase))))
                .Where(q => q.Count > 0)
                .Select(q => $"{q.Emoji} {q.Count} {q.Name}")
                .ToList();
            if (counts.Count > 0) sb.Append("-# ").Append(string.Join(" · ", counts)).Append('\n');

            // One width for every group, so the slot and item level columns line up down the whole card.
            var slotWidth = card.Gear.Max(i => SlotLabel(i).Length);
            var levelWidth = card.Gear.Max(i => i.ItemLevel is > 0 ? i.ItemLevel.Value.ToString().Length : 0);

            var known = GearGroups.SelectMany(g => g.Slots).ToHashSet();
            var groups = GearGroups
                .Select(g => (g.Title, Items: card.Gear.Where(i => g.Slots.Contains(i.Slot)).OrderBy(i => Array.IndexOf(g.Slots, i.Slot)).ToList()))
                .Append((Title: "Other", Items: card.Gear.Where(i => !known.Contains(i.Slot)).ToList()))
                .Where(g => g.Items.Count > 0);
            foreach (var (title, items) in groups)
            {
                sb.Append("### ").Append(title).Append('\n');
                foreach (var item in items)
                {
                    sb.Append('`').Append(SlotLabel(item).PadRight(slotWidth));
                    if (levelWidth > 0) sb.Append(' ').Append((item.ItemLevel is > 0 ? item.ItemLevel.Value.ToString() : "–").PadLeft(levelWidth));
                    sb.Append("` ").Append(QualityEmoji(item.Quality)).Append(' ')
                        .Append(Linked(item.Name, links ? ForeverWowhead.Item(item.Id) : null)).Append('\n');

                    var enchants = (item.Enchants ?? Array.Empty<string>()).Where(e => !string.IsNullOrWhiteSpace(e)).ToList();
                    if (enchants.Count > 0)
                        sb.Append("-# ↳ ✨ ").Append(string.Join(" · ", enchants.Select(e => Format.Sanitize(e.Trim())))).Append('\n');
                }
            }

            var worn = card.Gear.Select(i => i.Slot).ToHashSet();
            var empty = SlotsToFill.Where(slot => !worn.Contains(slot.Slot)).Select(slot => slot.Name).ToList();
            if (empty.Count > 0) sb.Append("-# Empty slots: ").Append(string.Join(" · ", empty)).Append('\n');
            return sb.ToString().TrimEnd('\n');
        }

        private static string QualityEmoji(string? quality)
        {
            var known = Array.FindIndex(Qualities, q => string.Equals(q.Type, quality, StringComparison.OrdinalIgnoreCase));
            return known >= 0 ? Qualities[known].Emoji : "⚪";
        }

        /// <summary>The slot as the armory names it, kept short and free of anything that would end the code span it sits in.</summary>
        private static string SlotLabel(ForeverArmoryItem item)
        {
            var label = (string.IsNullOrWhiteSpace(item.SlotName)
                    ? CultureInfo.InvariantCulture.TextInfo.ToTitleCase(item.Slot.Replace('_', ' ').ToLowerInvariant())
                    : item.SlotName)
                .Replace('`', '\'').Replace('\r', ' ').Replace('\n', ' ').Trim();
            return label.Length == 0 ? "Item" : label.Length > MaxSlotLabel ? label[..MaxSlotLabel] : label;
        }

        /// <summary>A name as a link. A name with a square bracket in it stays plain, because it would end the link early.</summary>
        private static string Linked(string name, string? url)
            => url == null || name.Contains('[') || name.Contains(']') ? Format.Sanitize(name) : $"[{Format.Sanitize(name)}]({url})";

        /// <summary>
        /// The view tabs (Professions only when the character has any), the way back to the roster when
        /// the card came from it, and a link to the armory: five buttons at most, which is all a row holds.
        /// </summary>
        private static ActionRowBuilder CardButtons(ulong userId, ForeverArmoryCharacter c, string tab, bool fromRoster, bool hasProfessions)
        {
            var row = new ActionRowBuilder();
            var origin = fromRoster ? FromRoster : "";
            var tabs = new List<(string Code, string Label, string Emoji)> { (CardOverview, "Overview", "📋"), (CardGear, "Gear", "🛡️") };
            if (hasProfessions) tabs.Add((CardProfessions, "Professions", ForeverProfessions.DefaultEmoji));

            // A name the bot wouldn't accept back, or an id past Discord's limit, just loses the tabs.
            if (CardId(userId, origin + CardOverview, c) != null)
            {
                foreach (var (code, label, emoji) in tabs)
                {
                    row.WithButton(new ButtonBuilder(label, CardId(userId, origin + code, c),
                        code == tab ? ButtonStyle.Success : ButtonStyle.Primary, emote: new Emoji(emoji)));
                }
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

        /// <summary>Every block of text on a card that came back from Discord, wherever it is nested.</summary>
        public static IEnumerable<string> Texts(IEnumerable<IMessageComponent>? components)
        {
            foreach (var component in components ?? Enumerable.Empty<IMessageComponent>())
            {
                if (component is TextDisplayComponent text && text.Content != null) yield return text.Content;
                IEnumerable<IMessageComponent>? children = component switch
                {
                    ContainerComponent container => container.Components,
                    SectionComponent section => section.Components,
                    _ => null
                };
                foreach (var nested in Texts(children)) yield return nested;
            }
        }

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
