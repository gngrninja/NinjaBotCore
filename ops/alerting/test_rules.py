"""Offline contract tests; the synthetic model is NOT a LogQL engine."""
import json
from pathlib import Path
import re
import subprocess
import sys
import unittest

ROOT = Path(__file__).resolve().parent
CLI = ROOT / "rules.py"


def invoke(*args, stdin=None):
    return subprocess.run([sys.executable, "-B", str(CLI), *args], input=stdin,
                          text=True, capture_output=True, check=False)


class GenerationTests(unittest.TestCase):
    def generate(self, *args):
        result = invoke("generate", *args)
        self.assertEqual(result.returncode, 0, result.stderr)
        return json.loads(result.stdout)

    def test_default_is_eight_scoped_paused_rules(self):
        artifact = self.generate()
        self.assertEqual(set(artifact), {"formatVersion", "group", "rules"})
        self.assertEqual(artifact["formatVersion"], 1)
        self.assertEqual(artifact["group"], {
            "folderUID": "cfdlg9jvphpfkd", "name": "ninjabot-health", "intervalSeconds": 60})
        rules = artifact["rules"]
        self.assertEqual(len(rules), 8)
        self.assertEqual({r["uid"] for r in rules}, {
            "nb-core-errors", "nb-helpers-errors", "nb-web-errors",
            "nb-core-fatal", "nb-helpers-fatal", "nb-web-fatal", "nb-wcl-auth",
            "nb-helpers-wcl-auth"})
        for rule in rules:
            self.assertIs(rule["isPaused"], True)
            self.assertNotIn("notification_settings", rule)
            self.assertEqual(rule["folderUID"], artifact["group"]["folderUID"])
            self.assertEqual(rule["ruleGroup"], "ninjabot-health")
            self.assertEqual(rule["execErrState"], "Alerting")
            self.assertEqual(rule["condition"], "B")
            self.assertEqual(rule["data"][0]["datasourceUid"], "afdlfkc5oc1s0e")
            self.assertTrue(rule["data"][0]["model"]["instant"])
            self.assertEqual(rule["data"][1]["model"]["type"], "math")

    def test_error_and_fatal_queries_have_exact_scope_and_thresholds(self):
        rules = {r["uid"]: r for r in self.generate()["rules"]}
        for service, container in (("core", "/ninjabot"), ("helpers", "/ninjabot-helpers"),
                                   ("web", "/ninjabot-web-prod")):
            for kind, regex, pending, threshold in (
                ("errors", "( ERR]| FTL]|^fail:|^crit:)", "5m", "$A >= 5"),
                ("fatal", "( FTL]|^crit:|(?i:unhandled exception))", "0s", "$A > 0"),
            ):
                rule = rules[f"nb-{service}-{kind}"]
                expected = (f'sum(count_over_time({{container="{container}"}} '
                            f'|~ "{regex}" !~ "Shard [0-9]+ disconnected:" [10m])) or vector(0)')
                self.assertEqual(rule["data"][0]["model"]["expr"], expected)
                self.assertEqual(rule["data"][1]["model"]["expression"], threshold)
                self.assertEqual(rule["for"], pending)
                self.assertEqual(rule["noDataState"], "OK")
                self.assertEqual(rule["labels"]["service"], service)
                self.assertEqual(rule["annotations"]["recovery"],
                                 "Error signal cleared; this does not prove application health.")

    def test_wcl_compares_latest_markers_across_all_streams(self):
        rules = {r["uid"]: r for r in self.generate()["rules"]}
        def latest(container, marker):
            return ('max(max_over_time({container="' + container + '"} |= "[WCL Auth] state='
                    + marker + '" | label_format event_ts="{{ __timestamp__ | unixEpochNanos }}" '
                    '| unwrap event_ts | __error__="" [24h]))')
        for uid, service, container in (("nb-wcl-auth", "core", "/ninjabot"),
                                         ("nb-helpers-wcl-auth", "helpers", "/ninjabot-helpers")):
            with self.subTest(uid=uid):
                self.assertIn(uid, rules)
                rule = rules[uid]
                failure, recovery = latest(container, "unavailable"), latest(container, "recovered")
                self.assertEqual(rule["data"][0]["model"]["expr"],
                                 f"({failure} or ({recovery} * 0)) >= bool ({recovery} or vector(0))")
                self.assertEqual(rule["labels"]["service"], service)
                self.assertEqual(rule["data"][0]["relativeTimeRange"], {"from": 86400, "to": 0})
                self.assertEqual(rule["data"][1]["model"]["expression"], "$A > 0")
                self.assertEqual(rule["for"], "2m")
                self.assertEqual(rule["noDataState"], "KeepLast")
                self.assertNotIn("detected_level", json.dumps(rule))

    def test_annotations_are_fixed_no_log_or_label_templates(self):
        for rule in self.generate()["rules"]:
            self.assertEqual(set(rule["labels"]), {"application", "service", "signal", "severity"})
            for value in rule["annotations"].values():
                self.assertNotIn("{{", value)
                self.assertNotIn("http", value)
            self.assertEqual(rule["labels"]["application"], "ninjabot")

    def test_receiver_does_not_enable_implicitly(self):
        artifact = self.generate("--receiver", "approved-ops")
        for rule in artifact["rules"]:
            self.assertIs(rule["isPaused"], True)
            self.assertEqual(rule["notification_settings"], {
                "receiver": "approved-ops", "group_by": ["alertname"],
                "group_wait": "30s", "group_interval": "5m", "repeat_interval": "4h"})

    def test_enable_requires_explicit_receiver(self):
        result = invoke("generate", "--enable")
        self.assertEqual(result.returncode, 2)
        self.assertIn("--enable requires --receiver", result.stderr)
        self.assertEqual(result.stdout, "")

    def test_explicit_enable_and_receiver(self):
        artifact = self.generate("--enable", "--receiver", "approved-ops")
        self.assertTrue(all(r["isPaused"] is False for r in artifact["rules"]))

    def test_configurable_folder_and_datasource_not_group_or_uids(self):
        artifact = self.generate("--folder-uid", "test-folder", "--loki-uid", "test-loki")
        self.assertEqual(artifact["group"]["folderUID"], "test-folder")
        for rule in artifact["rules"]:
            self.assertEqual(rule["folderUID"], "test-folder")
            self.assertEqual(rule["data"][0]["datasourceUid"], "test-loki")

    def test_invalid_identifiers_and_receiver_fail_closed(self):
        for args in (("--folder-uid", "../folder"), ("--loki-uid", ""),
                     ("--receiver", ""), ("--receiver", "x\ny"),
                     ("--receiver", " padded ")):
            with self.subTest(args=args):
                result = invoke("generate", *args)
                self.assertEqual(result.returncode, 2)
                self.assertEqual(result.stdout, "")

    def test_output_is_deterministic(self):
        self.assertEqual(self.generate(), self.generate())

    def test_shipped_paused_manifest_matches_default_cli_bytes(self):
        result = invoke("generate")
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual((ROOT / "paused-rules.json").read_text(encoding="utf-8"), result.stdout)


class ValidationTests(unittest.TestCase):
    def setUp(self):
        result = invoke("generate")
        self.assertEqual(result.returncode, 0, result.stderr)
        self.artifact = json.loads(result.stdout)

    def validate(self, artifact=None, *args):
        return invoke("validate", "-", *args,
                      stdin=json.dumps(self.artifact if artifact is None else artifact))

    def test_validate_generated_manifest(self):
        result = self.validate()
        self.assertEqual(result.returncode, 0, result.stderr)
        summary = json.loads(result.stdout)
        self.assertEqual(summary, {
            "valid": True, "ruleCount": 8, "paused": True,
            "folderUID": "cfdlg9jvphpfkd", "ruleGroup": "ninjabot-health",
            "uids": [r["uid"] for r in self.artifact["rules"]]})

    def test_validate_requires_explicit_enabled_opt_in(self):
        artifact = json.loads(invoke("generate", "--enable", "--receiver", "approved-ops").stdout)
        denied = self.validate(artifact)
        self.assertEqual(denied.returncode, 2)
        self.assertIn("--allow-enabled", denied.stderr)
        accepted = self.validate(artifact, "--allow-enabled")
        self.assertEqual(accepted.returncode, 0, accepted.stderr)
        self.assertFalse(json.loads(accepted.stdout)["paused"])

    def test_tampered_rules_are_rejected(self):
        mutations = [
            lambda a: a["rules"].pop(),
            lambda a: a["rules"].append(a["rules"][0]),
            lambda a: a["rules"][0].update(isPaused=False),
            lambda a: a["rules"][0].update(isPaused=1),
            lambda a: a["rules"][0].update(uid="legacy-rule"),
            lambda a: a["rules"][0]["annotations"].update(summary="{{ $labels.raw_log }}"),
            lambda a: a["rules"][0]["data"][0]["model"].update(expr='{container=~".+"}'),
            lambda a: a["rules"][-1].update(noDataState="OK"),
            lambda a: a["rules"][-1]["data"][0]["model"].update(expr="vector(0)"),
            lambda a: a.update(policies={"receiver": "unexpected"}),
            lambda a: a["group"].update(name="bot-alerts"),
            lambda a: a["group"].update(intervalSeconds=600),
        ]
        for mutate in mutations:
            with self.subTest(mutation=mutate):
                artifact = json.loads(json.dumps(self.artifact))
                mutate(artifact)
                result = self.validate(artifact)
                self.assertEqual(result.returncode, 2)
                self.assertIn("invalid artifact", result.stderr)
                self.assertEqual(result.stdout, "")

    def test_malformed_json_and_wrong_shapes_are_clean_errors(self):
        for text in ('{', '[]', 'null', '{}', '{"rules":null}',
                     '{"formatVersion":1,"formatVersion":1}',
                     json.dumps(self.artifact).replace('"formatVersion": 1', '"formatVersion": NaN')):
            with self.subTest(text=text[:40]):
                result = invoke("validate", "-", stdin=text)
                self.assertEqual(result.returncode, 2)
                self.assertIn("invalid artifact", result.stderr)
                self.assertNotIn("Traceback", result.stderr)
                self.assertEqual(result.stdout, "")

    def test_missing_file_is_clean_error(self):
        result = invoke("validate", str(ROOT / "does-not-exist.json"))
        self.assertEqual(result.returncode, 2)
        self.assertIn("invalid artifact", result.stderr)
        self.assertNotIn("Traceback", result.stderr)


if __name__ == "__main__":
    unittest.main()
