# Changelog

## [Unreleased]

### Actionable alerts

- The bot and the helpers record when Discord refuses a feature's post: New log posts, welcome and leave messages, the moderation log, realm status alerts and the word filter. Each problem says whether the channel was deleted, the bot can't see it or a permission is missing. A broken channel costs at most one database write an hour, and the next post that works resolves it.
- Owner-only commands API endpoints under `/api/admin/delivery-problems` list the problems with what Discord says now (missing permissions, who set the feature up, the server owner, a DM preview) and let the owner DM the setter or owner, re-check, turn the feature off or dismiss it. Nothing is ever DMed or turned off automatically. See `docs/actionable-alerts.md`.
- `/watchlogs`, `/toggle-greetings` and `/toggle-partings` record who turned them on.
- Moderation log and word filter permission errors are throttled warnings instead of errors, so they no longer trip the error alert.
- Departed servers' delivery problems are purged with the rest of their data.

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
- Live card settings and cards are deleted with the rest of a server's data 30 days after the bot leaves it.
- A live card waits out a WarcraftLogs sign-in outage without counting it against the card. `/raid-recap-rollout Status` says when sign-in is failing, and the health JSON adds `warcraft_logs_auth`.
- The rollout and each server's live card can be managed from the web: the commands API serves the rollout mode, the selected servers, *Preview most active*, select, add and remove for the admin page (bot owner only), and turning a server's card on or off for its dashboard. `/raid-recap-live` and the dashboard share the same rules.
- *Preview* and *Select most active* skip only the one server when Warcraft Logs reports an error about its guild, such as a guild that never logged, instead of ending the list there.
- Selecting servers runs one at a time, so two selections at once can't post the one-time message twice or pass the 25-server cap. An officer who turns the card off while the message is posting keeps it off, and one who turns it on in a channel of their own keeps that channel.
- The kill switch is checked again for each server, so turning the rollout off during *Select most active* stops the run; the rest say the rollout was switched off. A server ID of 0 is refused instead of selecting the most active servers.
- A selected server whose card is already on, but where the bot can no longer post, is skipped instead of taking a place. A server the bot has left no longer uses up a place, and the selected list says the bot left.
- The `/watchlogs` channel for the card is the one with the latest New log; a row that never had a log no longer comes first in PostgreSQL.

### Commands

- Bot-owner commands are registered only in the owner's server (`ownerGuild` in config, defaults to the home server), not globally. This frees 34 of Discord's 100 global command slots, and other servers no longer see them in their `/` menu.
- `wow-list-duplicates`, `wow-cleanup-duplicates` and `wow-cleanup-null-slugs` are owner-only. They act on every server's guild links but only needed Administrator in the server they were run from.
- Tests check every command against Discord's limits (100 top-level commands, 25 subcommands, options and choices, name and description lengths, 8000 characters per command), and startup logs an error if the global list is over 100.

### Warcraft Logs

- When WarcraftLogs rejects the bot's token, the bot and the helpers service get a new one and retry the request once. If the new token is rejected too, requests pause for 30 seconds, doubling to at most 5 minutes while it keeps failing. Each outage is logged as `[WCL Auth] state=unavailable` (at most every 5 minutes) and the recovery as `state=recovered`.
- The helpers service no longer waits out a Retry-After longer than its request deadline; it reports the real status straight away.
- A rate limit check that fails after a successful query no longer throws away that query's result.
- Grafana alert rules for errors, fatal errors and WarcraftLogs sign-in are kept offline in `ops/alerting`.

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
