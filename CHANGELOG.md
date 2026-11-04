# Changelog

## [Unreleased]

### WoW Forever

- Added `/forever guild set|view|unset|roster`. Officers set the server's Forever guild (name, region, ruleset, faction) separately from Retail and Classic, and turn the member roster on or off.
- Added `/forever char add|list|edit|remove|main`. Characters have a first name and a surname with no realm, belong to the member rather than the server, and have a main per server. Nothing is written to the Retail character table or sent to Blizzard, Raider.IO or WarcraftLogs.
- Added `/forever roster view|share|unshare`. Characters stay private until shared. The roster shows only current members' characters that match the guild, and leaving a server removes your shares there.
- `/help` has a WoW Forever category and shows the option names people actually type.

### Raid Recap

- `/raid-recap` now follows the `/char` WarcraftLogs cards: guild header with the server icon, emoji view buttons, parse dots, compact numbers, `m:ss` durations and a single footer.
- Every report view has a **How to read** button. Interpretation notes moved there, so the cards are shorter.
- `/char` and `/raid-recap` share one parse palette. `/char` now shows pink for 99 and gold for exactly 100.
- Select menus show plain names instead of markdown-escaped ones.
- Added `/raid-recap-live`: a public card that is posted when the guild's raid log goes live and updates in place until the raid ends. It shows pull results, the first deaths of the last wipe and the top performers of the last kill by spec and class, never by name. **Open my recap** gives each viewer the private detail.
- Added `/raid-recap-rollout` for the bot owner: off, the owner's servers only, or everyone.
- The live card names the top damage and healing players of the latest kill. Deaths stay by spec and class.
- The live card calls out a new best pull, states the killing blow that most often started a wipe, and adds a raid line to each kill with average parses, speed and execution.
- The final card adds the typical gap between pulls and the players with the most top-three finishes.
- Each of the latest kill's top damage and healing players shows their two biggest targets, with amount and share.
- The live card shows kicks and dispels for the latest pull: how many, the top kickers and dispellers, and what was dispelled most.
- Two raids in one night each get a card. A zone change, a newer log, or a night appended to the same log starts a new card, and the previous card closes as Raid ended unless a second team is still raiding. Two people logging the same raid get one card. Mythic+ and other non-raid fights no longer appear on the card, and a card ends after an hour without a raid pull.
- Live card discovery asks WarcraftLogs for the guild's newest 10 reports instead of 100.
- `/raid-recap-rollout Status` shows the live card watcher's health: cards by state, the last sweep, the last error, WarcraftLogs points used this hour and the latest card events with reasons. The same is served at `GET /api/raid-recap-live/health` for the web admin page, and every card end or stop is logged with its reason.
- A stopped live card picks up again with its raid's next pull: after a WarcraftLogs outage, after an officer turns the card off and on, or when the rollout covers the server again. A deleted card stays gone unless an officer turns the card on again.
- The private recap's Bosses view and *Still progressing* card now show a pull strip: boss health left on each pull, in order.
- The live card can be rolled out to selected servers: `/raid-recap-rollout` has a *Selected servers and mine* mode, and `/raid-recap-select` (owner server only) previews and selects the most active servers (a New log in the last 7 days, ranked by logs posted, each confirmed with a Warcraft Logs report this week), or adds or removes one by ID, up to 25. Selecting a server turns the card on in its `/watchlogs` channel and posts a one-time message there on how to turn it off. A server that turned the card off stays off.
- `/raid-recap-live state:Off` is saved even in a server that never had the card on.

### Commands

- Bot-owner commands are registered only in the owner's server (`ownerGuild` in config, defaults to the home server), not globally. This frees 34 of Discord's 100 global command slots, and other servers no longer see them in their `/` menu.
- `wow-list-duplicates`, `wow-cleanup-duplicates` and `wow-cleanup-null-slugs` are owner-only. They act on every server's guild links but only needed Administrator in the server they were run from.
- Tests check every command against Discord's limits (100 top-level commands, 25 subcommands, options and choices, name and description lengths, 8000 characters per command), and startup logs an error if the global list is over 100.

## [v3.2.7] - 2026-08-23

### Raider.IO Insights

- Added M+ Coach, Run Review, scoped Rivals, season-aware Score Goals, and Talents views to `/char`.
- Added an ephemeral **My Insights** shortcut to `/keys` and Live Raid information to `/ginfo`.
- Hardened Raider.IO requests with bounded retries, safe caching, origin-checked links, attribution, and secret-safe logging.

### Crafting and Discord Reliability

- Modernized crafting cards and interactions with Discord Components V2.
- Made crafting claim, profession, crafted, completed, cancel, unclaim, and expiration transitions concurrency-safe.
- Added character-management pagination, prompt interaction acknowledgement, bounded component content, and explicit mention controls.

## [v3.0.0] - Changes since v2.3.7

### New Commands

**`/char`** - Unified character lookup combining Raider.IO, Armory, and WarcraftLogs
- Tabbed views: Overview, Gear, Logs, M+, PvP, Achievements
- Individual boss parses with direct fight links
- Rank display (#11/7,279 format)
- Save characters with `/setchar`, autocomplete from history

**`/top10`** - Server and guild rankings revamp
- Boss autocomplete search
- Interactive DPS/HPS and difficulty toggles
- Boss navigation buttons
- Server vs Guild scope toggle

**`/realm-watch`** - Realm status notifications
- Get alerts when realms go up/down
- Channel or DM delivery

**`/housing-collection`** - Decor collection tracker
- Progress bar and collection stats
- Browse missing items with pagination
- Wowhead links and item details

### Renamed Commands

- **`/donate`** → **`/support-ninjabot`**

### Enhancements

- **Help** - Paginated with First/Prev/Next/Last buttons
- **Polls** - "View Voters" button on non-anonymous polls
- **Greetings** - Separate toggles for welcome and goodbye messages (`/toggle-greetings`, `/toggle-partings`)
- **Word Filter** - Detects leet speak, accented characters, and other obfuscation; now available to server admins (was bot-owner only)
- **Log Monitoring** - Smarter checking intervals based on guild activity

### Bug Fixes

- Fixed missing bosses in encounter dropdown
- Fixed boss dropdown showing wrong order
- Fixed realm watch autocomplete not finding subscriptions
- Fixed crash when guild roster is empty
- Fixed `/top10` title saying "Top 10" when fewer players available
