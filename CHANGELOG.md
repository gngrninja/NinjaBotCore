# Changelog

## [Unreleased]

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
- The private recap's Bosses view and *Still progressing* card now show a pull strip: boss health left on each pull, in order.

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
