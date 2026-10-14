"""Offline NinjaBot alert artifact generator (stdlib only)."""
import argparse
import json
from pathlib import Path
import re
import sys

DEFAULT_FOLDER = "cfdlg9jvphpfkd"
DEFAULT_LOKI = "afdlfkc5oc1s0e"
GROUP = "ninjabot-health"
SERVICES = {"core": "/ninjabot", "helpers": "/ninjabot-helpers", "web": "/ninjabot-web-prod"}
ERROR_PATTERN = "( ERR]| FTL]|^fail:|^crit:)"
FATAL_PATTERN = "( FTL]|^crit:|(?i:unhandled exception))"
EXCLUDE_PATTERN = "Shard [0-9]+ disconnected:"


def build_artifact(folder_uid=DEFAULT_FOLDER, loki_uid=DEFAULT_LOKI, receiver=None, enable=False):
    for value in (folder_uid, loki_uid):
        if not isinstance(value, str) or not re.fullmatch(r"[A-Za-z0-9_-]{1,40}", value):
            raise ValueError("folder and datasource UIDs must be 1-40 ASCII letters, digits, _ or -")
    if receiver is not None and (not isinstance(receiver, str) or not receiver
                                 or receiver != receiver.strip() or len(receiver) > 200
                                 or any(ord(c) < 32 or ord(c) == 127 for c in receiver)):
        raise ValueError("receiver must be a nonempty, unpadded name without control characters")
    if enable and receiver is None:
        raise ValueError("--enable requires --receiver (an explicitly approved existing contact point)")
    rules = []

    def add(uid, title, service, signal, severity, expr, threshold, pending, summary, recovery,
            no_data="OK", lookback=600):
        rule = {
            "uid": uid, "title": title, "folderUID": folder_uid, "ruleGroup": GROUP,
            "condition": "B", "for": pending, "noDataState": no_data,
            "execErrState": "Alerting", "isPaused": not enable,
            "labels": {"application": "ninjabot", "service": service,
                       "signal": signal, "severity": severity},
            "annotations": {"summary": summary, "recovery": recovery},
            "data": [
                {"refId": "A", "queryType": "instant", "datasourceUid": loki_uid,
                 "relativeTimeRange": {"from": lookback, "to": 0},
                 "model": {"refId": "A", "expr": expr, "queryType": "instant",
                           "instant": True, "range": False, "editorMode": "code",
                           "datasource": {"type": "loki", "uid": loki_uid},
                           "intervalMs": 1000, "maxDataPoints": 43200}},
                {"refId": "B", "queryType": "", "datasourceUid": "__expr__",
                 "relativeTimeRange": {"from": 0, "to": 0},
                 "model": {"refId": "B", "type": "math", "expression": threshold,
                           "datasource": {"type": "__expr__", "uid": "__expr__"},
                           "intervalMs": 1000, "maxDataPoints": 43200}},
            ],
        }
        if receiver is not None:
            rule["notification_settings"] = {
                "receiver": receiver, "group_by": ["alertname"], "group_wait": "30s",
                "group_interval": "5m", "repeat_interval": "4h"}
        rules.append(rule)

    for service, container in SERVICES.items():
        for signal, pattern, pending, threshold, severity in (
            ("errors", ERROR_PATTERN, "5m", "$A >= 5", "warning"),
            ("fatal", FATAL_PATTERN, "0s", "$A > 0", "critical"),
        ):
            expr = (f'sum(count_over_time({{container="{container}"}} |~ "{pattern}" '
                    f'!~ "{EXCLUDE_PATTERN}" [10m])) or vector(0)')
            summary = (f"NinjaBot {service}: at least 5 error lines in 10 minutes, sustained for 5 minutes."
                       if signal == "errors" else
                       f"NinjaBot {service}: fatal, critical or unhandled-exception signal in the last 10 minutes.")
            add(f"nb-{service}-{signal}", f"NinjaBot {service} {signal}", service, signal,
                severity, expr, threshold, pending, summary,
                "Error signal cleared; this does not prove application health.")

    def latest(container, marker):
        return ('max(max_over_time({container="' + container + '"} |= "[WCL Auth] state='
                + marker + '" | label_format event_ts="{{ __timestamp__ | unixEpochNanos }}" '
                '| unwrap event_ts | __error__="" [24h]))')

    for service, uid in (("core", "nb-wcl-auth"), ("helpers", "nb-helpers-wcl-auth")):
        failure = latest(SERVICES[service], "unavailable")
        recovery = latest(SERVICES[service], "recovered")
        # Recovery alone creates zero; no markers leave an absent vector (KeepLast).
        # Float64 unwrap is not exact nanoseconds; a timestamp tie stays unavailable.
        expr = f"({failure} or ({recovery} * 0)) >= bool ({recovery} or vector(0))"
        add(uid, f"NinjaBot {service} WCL authentication unavailable", service, "wcl-auth", "warning",
            expr, "$A > 0", "2m",
            f"NinjaBot {service} WCL authentication remains unavailable after recovery attempts.",
            "An authenticated WCL API success was observed with no newer or tied unavailable marker in the lookback.",
            no_data="KeepLast", lookback=86400)
    return {"formatVersion": 1,
            "group": {"folderUID": folder_uid, "name": GROUP, "intervalSeconds": 60},
            "rules": rules}


def validate_artifact(text, allow_enabled=False):
    """Validate our exact manifest contract, not arbitrary Grafana exports."""
    def unique_keys(pairs):
        result = {}
        for key, value in pairs:
            if key in result:
                raise ValueError("duplicate JSON key")
            result[key] = value
        return result

    try:
        artifact = json.loads(text, object_pairs_hook=unique_keys)
        first = artifact["rules"][0]
        paused = first["isPaused"]
        if type(paused) is not bool:
            raise ValueError("isPaused must be boolean")
        expected = build_artifact(
            artifact["group"]["folderUID"], first["data"][0]["datasourceUid"],
            first.get("notification_settings", {}).get("receiver"), not paused)
        # JSON equality distinguishes booleans/numbers and rejects NaN/Infinity.
        if json.dumps(artifact, sort_keys=True, allow_nan=False) != json.dumps(expected, sort_keys=True):
            raise ValueError("does not match the scoped generator contract")
        if not paused and not allow_enabled:
            raise ValueError("enabled rules require explicit --allow-enabled")
    except (ValueError, KeyError, IndexError, TypeError, AttributeError, RecursionError) as exc:
        raise ValueError("invalid artifact: " + str(exc)) from None
    return {"valid": True, "ruleCount": len(artifact["rules"]), "paused": paused,
            "folderUID": artifact["group"]["folderUID"], "ruleGroup": GROUP,
            "uids": [r["uid"] for r in artifact["rules"]]}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    generate = sub.add_parser("generate", help="emit a local JSON manifest; never contact Grafana")
    generate.add_argument("--folder-uid", default=DEFAULT_FOLDER)
    generate.add_argument("--loki-uid", default=DEFAULT_LOKI)
    generate.add_argument("--receiver", help="exact existing contact point name; operator must verify approval")
    generate.add_argument("--enable", action="store_true", help="explicit opt-in; also requires --receiver")
    validate = sub.add_parser("validate", help="check exact generated contract, not live server compatibility")
    validate.add_argument("path", help="manifest path, or - for stdin")
    validate.add_argument("--allow-enabled", action="store_true")
    args = parser.parse_args()
    try:
        if args.command == "generate":
            artifact = build_artifact(args.folder_uid, args.loki_uid, args.receiver, args.enable)
        else:
            try:
                text = sys.stdin.read() if args.path == "-" else Path(args.path).read_text(encoding="utf-8")
            except (OSError, UnicodeError):
                raise ValueError("invalid artifact: cannot read UTF-8 input") from None
            artifact = validate_artifact(text, args.allow_enabled)
    except ValueError as exc:
        parser.error(str(exc))
    print(json.dumps(artifact, indent=2, sort_keys=True))


if __name__ == "__main__":
    main()
