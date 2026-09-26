# `/raid-recap` — private Retail raid review

## Use

- `/raid-recap` — resolve this Discord server's associated WoW guild by **server ID**, then choose a recent report.
- `/raid-recap guild:"Area 52, Example Guild, us"` — explicit `realm, guild[, region]`; default region `us`.
- `/raid-recap report:"https://www.warcraftlogs.com/reports/AbCdEfGh12345678"` — direct report URL or exact 16-character code. This code is synthetic.

Choose either guild or report. Guild lookup is read-only, independent of the Discord server's display name; missing/ambiguous associations require `/setguild` or an explicit target. Only Retail HTTPS hosts `warcraftlogs.com` / `www.warcraftlogs.com` are accepted. Arbitrary origins, other game hosts, URL credentials, malformed codes and whitespace repairs are rejected before lookup.

The panel is **private to its invoking user** in a normal server text channel. DMs/threads are unsupported. Both actor and bot must retain membership and View Channel access. `/logs`, `/watchlogs`, monitoring and guild-association writers are unchanged. There is no watcher, automatic publication, database write, patch-specific ability catalog, prediction or player-quality score.

## Five main views

**Overview** summarizes cleared encounter/difficulty groups, kills, wipes and live/unknown outcomes. A most-pulled unresolved boss includes a direct first-death review link and a next step: inspect the lead-up and compare your own attempts, not blame the first player who died. `bossPercentage` is active boss health, not encounter completion.

**Bosses** separates encounter ID and difficulty. All attempts are reachable in chronological pages of ten, including short wipes and unfinished/unknown attempts. Each links to its exact fight and shows outcome, elapsed duration and active boss health. Statistics are descriptive:

- Completed combat time sums available durations of completed kills/wipes, with known/eligible sample counts. It is not the report span or “wasted time.”
- Median completed-wipe duration uses every known positive completed-wipe duration, including short wipes, and shows known/eligible `n`. Missing durations are not zeros.
- Best wipe active boss health is the minimum known wipe value; latest wipe health refers to the latest wipe even when its health is unknown. Fastest kill uses known completed-kill durations.
- No prediction, cross-boss trend or silently filtered attempts.

**Damage / Healing** selects one completed boss kill; sources are ordered by returned total divided by the **whole kill's elapsed seconds**. Wipes/trash/live/unknown outcomes are not throughput rankings. Sources are paged ten at a time. Damage owner totals already include pets in the sampled source view: nested pets are never added again. Healing is labelled **“WCL Healing table total / elapsed seconds”**, with explicit **not validated as effective healing / not a quality grade** caveats. Overheal is not added, and `totalReduced` is not substituted. These are not active-time rates, cross-fight averages, parse percentiles or player grades.

**Analysis** works on one completed kill **or wipe**. Entering from Bosses prefers that encounter/difficulty's latest completed pull, otherwise a completed pull is selected. The paged picker labels boss, difficulty, Kill/Wipe and fight ID. Paging the picker changes available options, not the analyzed pull; only explicitly selecting a pull changes scope. Four subviews occupy one row:

- **Deaths:** observed player-death events, distinct players, elapsed timestamps with millisecond precision, simultaneous first-loss ties and repeated death-event ordinals. `killingAbility` supplies the recorded killing blow; missing ability stays unknown. Only targets belonging to both the selected fight's `friendlyPlayers` and validated master-data `Player` actors are included. Pets/enemies are excluded. Missing roster is unavailable; missing identity is visibly partial. A known-empty complete result says no player deaths. Partial results report only observed events and do **not** claim a definite first loss. Inspect lead-up damage, healing, defensives and assignments in the linked WCL death view. First death/killing blow is not a cause or blame verdict; cooldown availability/preventability are not inferred.
- **Incoming:** **“WCL damage-taken table totals”** by top-level ability/source, descending. Composite parent totals are used once, without summing nested subentries or replacing `total` with `totalReduced`. Mitigation/absorb semantics are not validated as net/effective damage. Check assignments, soaks and mitigation against major sources in WCL; this is not an avoidable-damage score or an HP/opportunity denominator.
- **Interrupts:** successful observed `spellsInterrupted`, WCL-reported `spellsCompleted`, and separate `spellChannelsInterrupted`. These fields are never added together. Spell rows and returned participant `details[].id/name/total` are paginated without recursively adding nested pets. Missing/inconsistent attribution is shown as unavailable/unassigned. Completed casts are not missed assignments; not all casts are interruptible. Review cast timing and assigned rotation.
- **Dispels:** observed successful `spellsInterrupted` from WCL's nested dispel table, with attributed participants and explicit missing/unassigned attribution. Remaining applications are not failed obligations. Review timing, strategy and assignments. No coverage percentage or expected-action denominator.

All analysis subviews link to the selected fight and exact WCL view. Detail rows are paged eight at a time, including every retained participant row. Five main tabs, a pull menu, four subview controls, and separate pull/row pagination fit five action rows. Names are Unicode-aware, mention-neutralized, Markdown-escaped and bounded after escaping.

## Loading, limits and honesty

Recent report discovery is metadata-only (up to 100 reports, 25 menu options per page). Direct report URLs reach older reports. Opening reports, navigating Overview/Bosses or paging Analysis pull options does not query analysis tables/events. Death actor/roster metadata is fetched lazily with the first requested death page, not for every report.

All detail requests use the fixed Retail WCL origin, variables for the exact report/fight/range, and explicit nonzero report-relative start/end times. Throughput sets `killType: Kills`, `viewBy: Source`; Analysis tables set `killType: All` and `viewBy: Ability` for DamageTaken or `Source` for Interrupts/Dispels. Utility parsers use observed nested `data.entries[].entries[]`, not a flat player table. Counts must be finite, nonnegative integers.

Death events set `dataType: Deaths`, `limit: 100`, `useActorIDs: true`, `useAbilityIDs: false`, one fight ID and its unchanged end time. The next request copies `nextPageTimestamp` exactly: no epsilon. Five pages / 500 raw event rows / a 30-second analysis deadline bound collection. A page may contain more than the requested limit (observed live); only the hard global row ceiling truncates it. A valid null cursor establishes completion, not a short page. Missing, backward, repeated or unsupported overlapping cursors, invalid rows, missing identity, errors or exhausted limits never become a complete zero. Previously valid rows may be retained as partial; snapshot drift discards the entire analysis.

Incoming is capped at 500 top-level rows. Utility is capped at 100 outer groups, 500 inspected nested rows, 100 participant entries per spell and 1,000 participant entries overall. Truncation is visible as partial. Actor metadata is capped at 2,000 entries and the selected roster at 100. Cached objects retain normalized observations and bounded names, never raw event/table JSON. Streamed HTTP response size and OAuth/HTTP cancellation are enforced by the shared WCL transport; oversized responses are unavailable/partial, never empty results.

Every detail response, including each death page, must match report code/revision/end time. These checks detect drift; there is no remote immutable-revision selector. Every panel includes its original **as-of** time and “may still update.” Report `endTime` is a last-event timestamp, not evidence that raiding is finished.

Process-local cache: 128 entries including in-flight work; guild/report metadata TTL 30 seconds, normalized detail TTL two minutes. Keys include snapshot code/revision/end/as-of, fight/range, metric and analysis options. Concurrent identical loads single-flight; active loads are not evicted to start duplicates. Failed loads clear their entries; bounded partial observations are cached for the same TTL, so immediate retries can retain the same partial result. Refresh is on demand and subject to the metadata TTL. Restart clears cache and sessions.

## Privacy and authority

Sessions: at most 256, ten-minute lifetime, random tokens bound to actor/server/channel. Generations reject stale queued controls. Transitions hold their gate through final editing; permissions/currentness are checked after provider waits. CV2 edits clear legacy content/embeds, set ComponentsV2, and suppress mentions.

**Share overview** is still the only public action. It publishes a read-only Overview to the originating channel, never Deaths, Incoming, Interrupts, Dispels, source rankings, private notices or session tokens. Sharing requires current View Channel / Send Messages rights for both participants, and fresh actor timeout/current-session checks at the actual send boundary. A timeout does not remove private analysis access. One share attempt per session; ambiguous sends are not retried. Public sends suppress mentions and use `RetryMode.AlwaysFail`. No permission or session gate is relaxed by Analysis.

## Evidence and release gates

Local tests use explicitly synthetic fixtures and offline HTTP/Discord boundaries. They exercise real service transitions, parsers, parameterized requests, CV2 payloads and actual Discord.Net module registration/handlers without logging into a gateway or sending real messages. Build/test from a fresh isolated source copy with .NET 9; package caches may be reused, source overlays may not.

Authorized September 2026 WCL schema and privacy-reduced live samples now establish current death-event shape, report-relative timestamps, roster joins, nested interrupt/dispel spell tables, incoming composite rows and sampled pet-owner source totals. A documented `limit:100` multi-page probe returned 101 then 31 events; copying its cursor unchanged exactly reproduced a separate 132-event response. That probe used multiple selected fights and ability IDs, whereas this product requests one fight and ability objects: synthetic tests still cover the product's multi-page boundaries. A tiny out-of-range limit was ignored and is **not** pagination evidence.

Live evidence is not universal semantic or deployment approval. Remaining gates:

- Healing total semantics remain unvalidated; retain the conservative label. Incoming totals do not establish mitigation/absorb/net-health or avoidability semantics.
- Inspect actual private/public Discord rendering in an authorized staging environment; offline SDK payload checks are not screenshots of the live client.
- Review provider terms on redistribution, content ownership/nonpublic sharing and cache policy before broad deployment. Local implementation does not constitute clearance.
- Final frozen-source full-suite and independent security/correctness review, CI and deployment remain separate from this local uncommitted work.

Primary references: [WCL Report](https://www.warcraftlogs.com/v2-api-docs/warcraft/report.doc.html), [ReportFight](https://www.warcraftlogs.com/v2-api-docs/warcraft/reportfight.doc.html), [maintainer on v2 table time ranges](https://forums.combatlogforums.com/t/api-v2-issues-with-table-from-report/10722), [Source-view DamageDone example](https://forums.combatlogforums.com/t/api-damagedone-and-rankings-for-all-encounters/16005). Research provenance and anonymized live evidence are retained separately in the local review artifacts. The repository's `docs/` ignore rule matches this path, but this existing file is already tracked at the baseline commit. Inventory it explicitly in review/source manifests; this task does not authorize staging.
