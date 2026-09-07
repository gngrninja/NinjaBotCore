# `/raid-recap` — manual retail raid recap

## Use

- `/raid-recap` — resolve this Discord server's associated WoW guild by **server ID**, then choose a recent report.
- `/raid-recap guild:"Area 52, Example Guild, us"` — use an explicit `realm, guild[, region]`; the default override region is `us`.
- `/raid-recap report:"https://www.warcraftlogs.com/reports/AbCdEfGh12345678"` — open a report directly. An exact 16-character report code also works. This example code is synthetic.

Choose either a guild override or a report, not both. Missing or ambiguous associations direct the user to `/setguild` or an explicit target. Guild lookup is read-only and does not depend on the Discord server's display name. Retail HTTPS hosts `warcraftlogs.com` and `www.warcraftlogs.com` are accepted; other game hosts, arbitrary URLs, credentials in URLs, malformed codes, and whitespace repairs are rejected before lookup.

The panel is private to the invoking user. Use a **normal server text channel**; DMs and threads are intentionally unsupported in this slice. You and the bot must retain guild membership and View Channel access. Existing `/logs`, `/watchlogs`, and monitoring behavior are unchanged. There is no background watcher, polling, configuration write, AI inference, or LLM dependency.

## Views

- **Reports:** up to 100 recent guild-associated reports, newest first; 25 options per page. Labels show title/date, descriptions show zone and recorded span. Supply a direct URL for older reports. Discovery fetches metadata only, never every report's fights/tables.
- **Overview:** deterministic synopsis, distinct cleared encounter/difficulty groups, kills/wipes/live-or-unknown counts, and most-pulled unresolved encounter. Provider-reported boss health is explicitly not encounter completion.
- **Bosses:** encounter + difficulty selector with paging, attempt/kill/wipe counts, fastest known kill, best known wipe remaining health, and links to the latest attempts. The report link provides the complete attempt history.
- **Damage / Healing:** choose one completed boss kill (paged). Sources are ordered by that kill's returned total divided by the **whole fight's elapsed seconds**. These are not cross-boss averages, parse percentiles, active-time rankings, or player-quality judgements. Source rows are paged ten at a time. Wipes, trash, and in-progress/unknown-completion fights never enter performance tables. No kills produces a useful progression recap, not an empty ranking error.
- **Refresh:** on demand only; metadata may remain cached for 30 seconds. Every report view includes its original as-of timestamp and “may still update.” `Report.endTime` is a last-event timestamp, never proof that raiding is finished.
- **Share overview:** a distinct, explicit action, disabled until current Send Messages + View Channel permissions for both actor and bot are established. It publishes a read-only CV2 overview only to the originating channel, with no private controls. Permissions are checked again at dispatch. A session can attempt sharing only once. An uncertain send is **not retried**; the panel tells the user to check the channel. Reopening a command is a new explicit action, not an automatic retry.

## Data and safety contracts

Fights are grouped by encounter ID **and** difficulty; encounter ID zero is excluded as trash. `inProgress` and `kill` must be actual booleans to classify a completed outcome. Missing/invalid health, totals, and durations remain unknown. No phase-progress inference or historical v1 percentage conversion is used.

Table requests explicitly set `viewBy: Source`, `DamageDone` or `Healing`, `killType: Kills`, one `fightIDs` entry, and that fight's relative millisecond start/end range. No nested pet totals or overheal are added to the provider's source totals. A returned code/revision/end-time mismatch rejects the table and asks for a refreshed snapshot rather than caching it under an old scope.

The existing WarcraftLogsV2Client OAuth and rate-limit execution path is retained. The recap-specific adapter fails closed on GraphQL errors (including partial-data responses), unavailable reports, and unrecognized table shapes. It does not retry failed WCL HTTP requests on its own.

Caches are process-local, bounded to 128 entries **including in-flight work**, and single-flight per key. Guild discovery/report metadata have a 30-second TTL; performance tables have a two-minute TTL. Table keys include validated report code, revision, last-event time, snapshot acquisition time, selected fight/range, and metric. Live in-flight cache entries are not evicted to start duplicate fetches. Cache saturation returns a bounded busy response.

Sessions are capped at 256, last ten minutes, and use random opaque tokens bound to actor/server/channel. Controls include a generation; stale selections fail closed with reopen guidance. Complete asynchronous transitions, including response edits and sharing, are serialized. Membership is checked before provider access and before protected rendering. Restarting the process invalidates all sessions. Untrusted WCL text is flattened, mention-neutralized, Markdown-escaped, and bounded after escaping; generated links use validated report identities. CV2 edits clear content/embeds, set the CV2 flag, and suppress mentions. Public sends use `RetryMode.AlwaysFail` and a bounded request timeout.

## Verification and remaining release gate

Unit/transport/interaction tests use **explicitly synthetic** report/table fixtures and mocked HTTP/Discord boundaries. They execute the concrete Discord.Net module registration and handlers, CV2 builders, scoped guild-ID query, cache/sessions, GraphQL request construction, error paths, and single-kill calculations. They do not start a real bot, contact a production database, or publish to Discord. Full .NET 9 Release verification runs from fresh disposable source copies, with separate core/helper TRX files.

**Authenticated live WCL acceptance is still required before release.** No current live API schema introspection or captured report was available. Archived official documentation confirms the report/fight fields and nullability; the following primary forum material supports table syntax and report-relative ranges:

- [Maintainer explanation of table time ranges](https://forums.combatlogforums.com/t/api-v2-issues-with-table-from-report/10722)
- [Source-view DamageDone query example](https://forums.combatlogforums.com/t/api-damagedone-and-rankings-for-all-encounters/16005)
- [Report documentation](https://www.warcraftlogs.com/v2-api-docs/warcraft/report.doc.html)
- [ReportFight documentation](https://www.warcraftlogs.com/v2-api-docs/warcraft/reportfight.doc.html)

The parser's expected `table.data.entries[].name/total` structure, WCL Healing total's effective-healing semantics, exact current v2 `bossPercentage` scale, and pet attribution must be checked against an authorized real report. Synthetic GREEN tests are **not** certification of those provider semantics. Out-of-range health is unknown, not heuristically divided; nested pets and overheal are never summed. Until this staging check, regard those display semantics as an explicit integration assumption, not a verified live-provider result. Verify both an ordinary completed report and a live/wipe-only report, compare one kill's Damage/Healing against WCL, and inspect public/private CV2 rendering without enabling any watcher.
