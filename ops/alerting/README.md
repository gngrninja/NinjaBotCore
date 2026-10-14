# NinjaBot alert rules (offline artifacts)

**Approved delivery: private Telegram via `NinjaBot Telegram`
(UID `ninjabot-telegram`). All shipped rules remain paused and unrouted.**
The operator verified the contact point by readback and Grafana reported a
successful Telegram notification test. An isolated synthetic rule also reached
firing then inactive; Telegram notification counters advanced from zero to one
to two with no failure counter, and the test rule was removed. Human receipt is
a separate check. Email was abandoned because SMTP is disabled;
the unused email contact point was removed. Do not configure SMTP or reuse the
default placeholder. The normal Grafana service-account token is read-only;
provisioning requires separately authorized administrator access. This directory
contains no credentials, network client, apply command, or application sender.
Existing `bot-alerts` rules and the global notification policy are not modified.

Deployment record (2026-10-14): all eight rules were provisioned and read back in
the dedicated group at a 60-second interval. The six general error/fatal rules
were enabled with the verified Telegram receiver. Both WCL-auth rules remain
paused pending application deployment and marker verification. The shipped
artifact intentionally stays paused/unrouted; it is not an export of live state.

## Generate and validate

Python 3 standard library only; run from the repository root:

```sh
python3 -B -m unittest discover -s ops/alerting -p 'test_*.py' -v
python3 -B ops/alerting/rules.py generate
python3 -B ops/alerting/rules.py validate ops/alerting/paused-rules.json
# Validate freshly generated stdout without writing any file:
python3 -B ops/alerting/rules.py generate | python3 -B ops/alerting/rules.py validate -
```

`paused-rules.json` is the actual default generator output, not a live Grafana
export. To regenerate that artifact, save `generate` stdout to that path. Optional
`--folder-uid` and `--loki-uid` select the target; defaults are the discovered Bot
Alerts folder `cfdlg9jvphpfkd` and Loki `afdlfkc5oc1s0e`. Group and rule UIDs are
fixed. Invalid identifiers are rejected, not normalized.

The manifest is **not** a Grafana file-provisioning YAML/JSON export:

- `formatVersion: 1`: this local artifact contract.
- `group`: folder UID, name `ninjabot-health`, evaluation interval **60 seconds**.
- `rules`: eight individual Grafana alert-rule provisioning API request bodies.
  Each has an instant Loki query `A` and a math condition `B`.

The validator checks this exact generator contract, including expressions,
thresholds, labels, annotations, uniform pause/routing settings, scope, and UIDs.
It rejects unknown fields, duplicates, malformed shapes, altered policies and
unsafe edits. It is **not** a general Grafana export/schema validator and does not
certify server compatibility, receiver existence, approval, or delivery. Server
readbacks may add fields; compare the intended fields separately.

## Signals

| Rule UIDs | Scope | Trigger | Pending |
| --- | --- | --- | --- |
| `nb-core-errors`, `nb-helpers-errors`, `nb-web-errors` | One exact container per rule | At least 5 matching error **lines** in rolling 10m | 5m |
| `nb-core-fatal`, `nb-helpers-fatal`, `nb-web-fatal` | One exact container per rule | At least 1 fatal/critical/unhandled-exception line in rolling 10m | 0s |
| `nb-wcl-auth`, `nb-helpers-wcl-auth` | Separate Core and Helpers instances | Latest unavailable marker newer than or tied with latest authenticated recovery within 24h | 2m |

Exact containers: Core `/ninjabot`, Helpers `/ninjabot-helpers`, Web
`/ninjabot-web-prod`. Every query aggregates away stream labels, so there is one
stable instance per rule, independent of `detected_level` or stream rotation.
Labels are fixed application/service/signal/severity; annotations are fixed text.
No log excerpts, user/report identifiers, report content, exception bodies, dynamic
log labels, or provider URLs are copied into alert annotations.

General errors recognize Serilog ` ERR]`/` FTL]` and line-leading ASP.NET
`fail:`/`crit:`. Fatal rules recognize ` FTL]`, leading `crit:`, and case-insensitive
`unhandled exception`. Both exclude `Shard [0-9]+ disconnected:` chatter. Other
logging formats are not implied covered. Fatal has no pending delay, but evaluation
cadence and notification grouping still add latency. A burst can trigger both a
fatal and sustained-error rule; there is no cross-rule inhibition.

General/fatal queries use zero fallback and `noDataState=OK`; rolling-window expiry
means **“error signal cleared,” not “application healthy.”** They do not detect
stopped containers, lost log shipping or missing heartbeats. Counts are lines, not
deduplicated incidents. `execErrState=Alerting` on all rules surfaces evaluation
failures rather than silently reporting recovery; verify the platform's rendered
error notification before approving delivery.

### WCL explicit state and its limits

The application contract is:

- Emit `[WCL Auth] state=unavailable reason=<fixed>` only after unrecovered auth
  failure, with reminders bounded to no more than one per five minutes.
- Emit `[WCL Auth] state=recovered` only after authenticated API success, not merely
  OAuth token acquisition. Transparently recovered first-401 retries need not emit
  unavailable and must not create an outage alert.

Each service query extracts log timestamps via `unixEpochNanos` and takes the
global maximum for each marker independently **within that service**. Let `F` be
the latest unavailable timestamp and `R` the latest recovered timestamp:

```text
(F or (R * 0)) >= bool (R or vector(0))
```

| Markers within 24h | Numeric sample / behavior |
| --- | --- |
| Unavailable only | 1; outage condition |
| Recovered only, including after old failure expired | 0; explicitly clears retained outage |
| Both | 1 if unavailable is newer or tied; otherwise 0 |
| Neither | Absent vector; `noDataState=KeepLast` |

**Missing unavailable gets zero only when an explicit recovery exists.** There is
no unconditional left-hand zero fallback: `KeepLast` preserves a previously
evaluated outage if all markers age out, instead of manufacturing recovery after
24h. A fresh rule with no markers has no outage evidence. Sparse requests/reminders
do not resolve an outage between calls. Core keeps UID `nb-wcl-auth`; Helpers has
its own UID `nb-helpers-wcl-auth`, fixed service label and exact source selector.

This is log-derived state, not a durable auth-health database:

- Loki retention must cover the requested lookback. A lost/expired failure marker
  cannot reconstruct an outage after rule recreation or loss of Grafana's retained
  state. Server restart persistence must be verified, not assumed.
- Explicit recovery can clear a retained outage even after its failure expired.
  If both markers disappear before evaluation, there is still no new evidence;
  retained state may require independent operator reconciliation. Never inject
  fake production markers or silently clear on missing data.
- `unixEpochNanos` is converted by `unwrap` to float64, **not exact nanoseconds**.
  At contemporary epoch values numeric resolution is approximately submicrosecond
  (256ns spacing near the synthetic test epoch), and grows with epoch magnitude.
  Representably distinct events in the same second retain their order. Exact or
  float-rounded ties conservatively remain unavailable (`>=`): a genuinely later
  recovery within one rounding bin cannot clear until a distinct later recovery
  is observed or the failure expires while recovery remains visible.
- The parent operator queried all eight final expressions through live Loki:
  all succeeded; current error/fatal signals were zero and both auth expressions
  returned absent vectors because the new markers are not deployed. This proves
  syntax/no-marker behavior, not positive auth-transition behavior or delivery.
- Marker timestamp/ingestion ordering, loss, simultaneous processes, and lookback
  boundaries are not transactional guarantees. Healthy API success proves that
  observation only, not all WCL features or the entire application are healthy.

## Provisioning and delivery gates (operator-owned; pending)

1. Run offline tests and validate the manifest. Query **each exact generated A
   expression** through live Loki, including zero-data and positive controls.
   Check Grafana's instant/math expression evaluation and accepted `KeepLast`
   spelling on the actual version. Offline Python models do not execute LogQL.
2. Read existing rule UIDs and group first. On a UID collision, stop unless the
   exact target is proven owned by this artifact. Preserve both legacy rules,
   other groups and the global notification policy.
3. Provision only these eight **paused** rule bodies through the Grafana alert-rule
   provisioning API. Set the dedicated group's interval to 60 seconds without
   replacing a shared group. Read back every exact UID, expression, pending time,
   no-data/error state and pause status, plus the dedicated group's interval.
4. Verify authorized administrator access and the intended `NinjaBot Telegram`
   contact point (UID `ninjabot-telegram`). Its destination is the approved private
   chat, never the default placeholder. Keep auth rules paused until the matching
   application markers are deployed and verified. Creating paused rules is not
   alert delivery.
5. Verify the exact existing contact point and approved destination by readback.
   Generate a routed but still-paused manifest with `--receiver 'NinjaBot Telegram'`.
   This adds **per-rule** `notification_settings`, never a global policy replacement:
   group by alert name, wait 30s, group interval 5m, repeat 4h.
6. Only after explicit enable approval, add **both** `--receiver '<exact name>'`
   and `--enable`. `--enable` without a receiver is rejected. The generator cannot
   verify that an arbitrary name really exists; that is the operator gate. The
   validator rejects enabled artifacts unless passed `--allow-enabled`.
7. Under separately approved test scope, verify firing and resolved delivery with
   sanitized synthetic data in an isolated test source/rule, then verify the
   intended production route. Read back the exact settings and confirm actual
   receipt. Do not induce a real WCL outage or post synthetic markers into the
   production Core or Helpers streams. Keep test receipt evidence distinct from query/schema
   validation. The offline generator itself never sends notifications.

Rollback is pause-first on these exact UIDs, followed by readback. Deleting them
loses their alert state and is not an equivalent safe pause; do not remove legacy
rules or change the root receiver as part of rollback.

## Offline evidence boundary

`test_rules.py` executes the real CLI, pins exact generated expressions and API
fields, and tests routing/enablement and validator rejection. `test_semantics.py`
uses synthetic event/time fixtures and Python regexes for the limited generated
patterns: threshold edges, chatter exclusion, service scope, fatal singletons,
sparse auth failures, different stream levels, per-service marker isolation,
explicit recovery after failure expiry, no-marker KeepLast, new failure after
recovery, same-second ordering, reset limitations and float-rounded timestamp
ties. Its model is **not a LogQL parser, Loki
server, or Grafana state machine**. Local GREEN does not prove live provisioning,
retention, state persistence, notification templates, receiver existence or actual
alert delivery. Those gates remain with the operator.
