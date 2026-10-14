"""Synthetic event/clock model only: does NOT parse or execute real LogQL/Grafana.

GenerationTests pins exact LogQL bytes. This model extracts its line filters and
windows, then illustrates intended event-count, last-marker and pending semantics.
Live Loki execution and Grafana state transitions remain separate acceptance gates.
"""
import json
from decimal import Decimal
import re
import unittest

from rules import build_artifact


def sample(rule, events, now):
    """Model A's numeric sample, or None for an absent auth-state vector."""
    query = rule["data"][0]["model"]["expr"]
    scope = re.search(r'container="([^"]+)"', query)
    window = re.search(r'\[(\d+)([mh])\]', query)
    assert scope is not None and window is not None
    container = scope.group(1)
    amount, unit = window.groups()
    lookback = int(amount) * {"m": 60, "h": 3600}[unit]
    selected = [(ts, line) for ts, source, line, _level in events
                if source == container and now - lookback < ts <= now]
    if rule["labels"]["signal"] == "wcl-auth":
        # Interpret only the two reviewed expression shapes, including the old
        # broken expression for regression RED. This is not a LogQL parser.
        atoms = list(dict.fromkeys(re.findall(r'max\(max_over_time\(.+?\[24h\]\)\)', query)))
        assert len(atoms) == 2
        failure, recovery = atoms
        comparison = re.search(r'(>=|>) bool', query)
        assert comparison is not None
        operator = comparison.group(1)
        old = f"{failure} {operator} bool ({recovery} or vector(0))"
        repaired = f"({failure} or ({recovery} * 0)) {operator} bool ({recovery} or vector(0))"
        assert query in (old, repaired)
        markers = list(dict.fromkeys(re.findall(r'\|= ("[^"]+")', query)))
        unavailable, recovered = [json.loads(marker) for marker in markers]
        def timestamp(ts):
            # Decimal fixtures retain source precision; unwrap returns float64.
            if "| unixEpochNanos }}" in query:
                return float(int(Decimal(str(ts)) * 1_000_000_000))
            assert "| unixEpoch }}" in query
            return float(int(ts))
        failures = [timestamp(ts) for ts, line in selected if unavailable in line]
        successes = [timestamp(ts) for ts, line in selected if recovered in line]
        left = max(failures) if failures else (0 if query == repaired and successes else None)
        right = max(successes, default=0)
        if left is None:
            return None
        return int(left >= right if operator == ">=" else left > right)
    include_match = re.search(r'\|~ ("[^"]+")', query)
    exclude_match = re.search(r'!~ ("[^"]+")', query)
    assert include_match is not None and exclude_match is not None
    include = json.loads(include_match.group(1))
    exclude = json.loads(exclude_match.group(1))
    return sum(bool(re.search(include, line)) and not re.search(exclude, line)
               for _ts, line in selected)


def state_at(rule, timeline):
    """Model condition/pending and KeepLast; not Grafana's persisted state engine."""
    condition_match = re.fullmatch(r'\$A (>=|>) (\d+)',
                                   rule["data"][1]["model"]["expression"])
    assert condition_match is not None
    operator, threshold = condition_match.groups()
    threshold = int(threshold)
    pending = int(rule["for"][:-1]) * {"s": 1, "m": 60}[rule["for"][-1]]
    state, since = "normal", None
    for now, value in timeline:
        if value is None and rule["noDataState"] == "KeepLast":
            continue
        condition = value is not None and (value >= threshold if operator == ">=" else value > threshold)
        if not condition:
            state, since = "normal", None
        else:
            since = now if since is None else since
            state = "alerting" if now - since >= pending else "pending"
    return state


class SyntheticSemanticsTests(unittest.TestCase):
    def setUp(self):
        self.rules = {r["uid"]: r for r in build_artifact()["rules"]}
        self.error = self.rules["nb-core-errors"]
        self.fatal = self.rules["nb-core-fatal"]
        self.auth = self.rules["nb-wcl-auth"]

    def test_realistic_levels_not_overescaped_words(self):
        lines = ["[12:00:00 ERR] failed", "[12:00:00 FTL] stopped", "fail: failure", "crit: critical"]
        events = [(100, "/ninjabot", line, "unknown") for line in lines]
        # Old escaping does not recognize the actual abbreviation. Synthetic only.
        self.assertFalse(re.search(r"\\berror\\b|\\bfatal\\b", lines[0]))
        self.assertEqual(sample(self.error, events, 100), 4)

    def test_filters_discard_other_containers_noise_and_old_events(self):
        events = [(100, "/ninjabot", "[12:00 ERR] kept", "unknown"),
                  (100, "/ninjabot-helpers", "[12:00 ERR] other", "error"),
                  (100, "/ninjabot-evil", "[12:00 ERR] other", "error"),
                  (100, "/ninjabot", "[12:00 ERR] Shard 12 disconnected: normal", "error"),
                  (100, "/ninjabot", "[12:00 INF] informational", "info"),
                  (-600, "/ninjabot", "[12:00 ERR] stale", "error")]
        self.assertEqual(sample(self.error, events, 100), 1)
        self.assertEqual(sample(self.error, [], 100), 0)

    def test_five_line_boundary_pending_and_aged_clear(self):
        four = [(100, "/ninjabot", "[12:00 ERR] synthetic", "error")] * 4
        five = four + [(100, "/ninjabot", "[12:00 ERR] synthetic", "error")]
        self.assertEqual(state_at(self.error, [(0, 4), (300, 4)]), "normal")
        self.assertEqual(state_at(self.error, [(100, sample(self.error, five, 100)),
                                               (399, sample(self.error, five, 399))]), "pending")
        self.assertEqual(state_at(self.error, [(100, 5), (400, 5)]), "alerting")
        self.assertEqual(state_at(self.error, [(100, 5), (400, 5),
                                               (701, sample(self.error, five, 701))]), "normal")
        self.assertEqual(state_at(self.error, [(100, 5), (399, 4), (400, 5)]), "pending")

    def test_singleton_fatal_has_no_pending(self):
        for line in ("[12:00 FTL] stopped", "crit: stopped", "Unhandled exception. SyntheticException"):
            with self.subTest(line=line):
                value = sample(self.fatal, [(100, "/ninjabot", line, "unknown")], 100)
                self.assertEqual(value, 1)
                self.assertEqual(state_at(self.fatal, [(100, value)]), "alerting")
        self.assertEqual(sample(self.fatal, [(100, "/ninjabot", "[12:00 ERR] ordinary", "error")], 100), 0)

    def test_auth_different_stream_levels_and_explicit_recovery(self):
        events = [(100, "/ninjabot", "[WCL Auth] state=unavailable reason=unauthorized", "error")]
        self.assertEqual(sample(self.auth, events, 100), 1)
        self.assertEqual(state_at(self.auth, [(100, 1), (219, 1)]), "pending")
        self.assertEqual(state_at(self.auth, [(100, 1), (220, 1)]), "alerting")
        events.append((230, "/ninjabot", "[WCL Auth] state=recovered", "info"))
        self.assertEqual(sample(self.auth, events, 230), 0)
        self.assertEqual(state_at(self.auth, [(100, 1), (220, 1), (230, 0)]), "normal")
        events.append((240, "/ninjabot", "[WCL Auth] state=unavailable reason=unauthorized", "unknown"))
        self.assertEqual(sample(self.auth, events, 240), 1)

    def test_transparent_recovery_no_unavailable_does_not_alert(self):
        events = [(100, "/ninjabot", "[WCL Auth] state=recovered", "info")]
        self.assertEqual(sample(self.auth, events, 100), 0)
        self.assertEqual(state_at(self.auth, [(100, sample(self.auth, events, 100))]), "normal")
        self.assertIsNone(sample(self.auth, [], 100))

    def test_expired_failure_cannot_auto_resolve_outage(self):
        events = [(100, "/ninjabot", "[WCL Auth] state=unavailable reason=unauthorized", "error")]
        self.assertEqual(sample(self.auth, events, 86499), 1)
        self.assertIsNone(sample(self.auth, events, 86501))
        self.assertEqual(state_at(self.auth, [(100, 1), (220, 1), (86501, None)]), "alerting")
        # A new evaluator without retained state cannot reconstruct this old outage.
        self.assertEqual(state_at(self.auth, [(86501, None)]), "normal")

    def test_expired_failure_then_explicit_recovery_clears_retained_outage(self):
        events = [(100, "/ninjabot", "[WCL Auth] state=unavailable", "error")]
        events.append((86502, "/ninjabot", "[WCL Auth] state=recovered", "info"))
        self.assertEqual(sample(self.auth, events, 86502), 0)
        timeline = [(t, sample(self.auth, events, t)) for t in (100, 220, 86501, 86502)]
        self.assertEqual(state_at(self.auth, timeline[:-1]), "alerting")
        self.assertEqual(state_at(self.auth, timeline), "normal")

    def test_no_markers_keeps_last_but_cannot_reconstruct_lost_state(self):
        self.assertIsNone(sample(self.auth, [], 100))
        self.assertEqual(state_at(self.auth, [(100, 1), (220, 1),
                                            (86501, sample(self.auth, [], 86501))]), "alerting")
        self.assertEqual(state_at(self.auth, [(86501, sample(self.auth, [], 86501))]), "normal")

    def test_auth_markers_are_isolated_by_source_service(self):
        for uid, source, other in (("nb-wcl-auth", "/ninjabot", "/ninjabot-helpers"),
                                    ("nb-helpers-wcl-auth", "/ninjabot-helpers", "/ninjabot")):
            with self.subTest(uid=uid):
                self.assertIn(uid, self.rules)
                rule = self.rules[uid]
                events = [(100, source, "[WCL Auth] state=unavailable", "error"),
                          (110, other, "[WCL Auth] state=recovered", "info")]
                self.assertEqual(sample(rule, events, 110), 1)
                events.append((120, source, "[WCL Auth] state=recovered", "info"))
                self.assertEqual(sample(rule, events, 120), 0)
                events.append((130, other, "[WCL Auth] state=unavailable", "error"))
                self.assertEqual(sample(rule, events, 130), 0)

    def test_sparse_reminders_do_not_resolve_between_requests(self):
        events = [(100, "/ninjabot", "[WCL Auth] state=unavailable reason=unauthorized", "error"),
                  (1600, "/ninjabot", "[WCL Auth] state=unavailable reason=unauthorized", "error")]
        self.assertEqual(sample(self.auth, events, 1599), 1)
        self.assertEqual(sample(self.auth, events, 1600), 1)
        self.assertEqual(sample(self.auth, events, 72000), 1)

    def test_identical_timestamp_tie_conservatively_retains_unavailable(self):
        events = [(100, "/ninjabot", "[WCL Auth] state=recovered", "info"),
                  (100, "/ninjabot", "[WCL Auth] state=unavailable reason=unauthorized", "error")]
        self.assertEqual(sample(self.auth, events, 100), 1)

    def test_same_second_ordering_and_new_failure_after_recovery(self):
        # Contemporary epoch, 1024ns spacing: distinct even after float64 unwrap.
        base = Decimal("1791849600.000000000")
        delta = Decimal("0.000001024")
        events = [(base, "/ninjabot", "[WCL Auth] state=unavailable", "error")]
        self.assertEqual(sample(self.auth, events, base), 1)
        events.append((base + delta, "/ninjabot", "[WCL Auth] state=recovered", "info"))
        self.assertEqual(sample(self.auth, events, base + delta), 0)
        events.append((base + delta * 2, "/ninjabot", "[WCL Auth] state=unavailable", "error"))
        self.assertEqual(sample(self.auth, events, base + delta * 2), 1)

    def test_float64_subresolution_recovery_tie_does_not_clear(self):
        base = Decimal("1791849600.000000000")
        later = base + Decimal("0.000000001")
        self.assertEqual(float(base * 1_000_000_000), float(later * 1_000_000_000))
        events = [(base, "/ninjabot", "[WCL Auth] state=unavailable", "error"),
                  (later, "/ninjabot", "[WCL Auth] state=recovered", "info")]
        self.assertEqual(sample(self.auth, events, later), 1)


if __name__ == "__main__":
    unittest.main()
