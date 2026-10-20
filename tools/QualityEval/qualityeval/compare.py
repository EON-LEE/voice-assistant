"""Gate: compare a candidate results file against a baseline and fail on regressions.

Latency regresses when a gated percentile grows by more than the allowed fraction AND by more than an absolute
floor (so 30 ms of noise on a 200 ms stage does not fail a build). Quality regresses when the candidate's point
estimate falls outside the baseline's question-level 95% interval in the bad direction. Judge scores are advisory
and gate only with ``gate_judge``. Results built from different corpora, items, scorers, session options or accents
are incompatible (exit 2) unless explicitly allowed. Results written before the scorer fingerprint existed carry none
and skip that check, except against a rescored result, which compares only with identically scored results.
"""

from __future__ import annotations

from dataclasses import dataclass

LATENCY_GATES = ("speechEndToFirstDelta", "speechEndToCompleted")
HIGHER_IS_BETTER = ("answeredRate", "passRate", "factRecall", "abstainRate", "formatOkRate", "retrievalHitRate",
                    "termAccuracy")
LOWER_IS_BETTER = ("forbiddenRate", "genericRate")
JUDGE_METRICS = ("usefulness", "support")
EPSILON = 1e-9


@dataclass(frozen=True)
class Thresholds:
    max_p95_regression: float = 0.10
    max_p50_regression: float = 0.10
    min_latency_delta_ms: float = 100.0
    gate_judge: bool = False
    allow_mismatch: bool = False


def _get(document: dict, *path: str):
    value = document
    for key in path:
        if not isinstance(value, dict):
            return None
        value = value.get(key)
    return value


def compatibility(baseline: dict, candidate: dict) -> list[str]:
    problems = []
    for label, path in (("corpus", ("corpus", "sha256")), ("items", ("items", "sha256")),
                        ("item selection", ("items", "selectedIds")), ("session options", ("session", "options")),
                        ("accents", ("audio", "accents")), ("audio source", ("audio", "source")),
                        ("schema", ("schemaVersion",))):
        if _get(baseline, *path) != _get(candidate, *path):
            problems.append(f"{label} differs")
    scorers = (_get(baseline, "scorer", "sha256"), _get(candidate, "scorer", "sha256"))
    rescored = _get(baseline, "rescore") is not None or _get(candidate, "rescore") is not None
    if scorers[0] != scorers[1] and (all(scorers) or rescored):
        problems.append("scorer differs")
    return problems


def compare(baseline: dict, candidate: dict, thresholds: Thresholds = Thresholds()) -> dict:
    problems = compatibility(baseline, candidate)
    report: dict = {"compatible": not problems, "problems": problems, "checks": []}
    if problems and not thresholds.allow_mismatch:
        report.update(status="incompatible", exitCode=2)
        return report
    checks = report["checks"]
    for stage in LATENCY_GATES:
        for quantile, limit in (("p95", thresholds.max_p95_regression), ("p50", thresholds.max_p50_regression)):
            base = _get(baseline, "latencyMs", stage, quantile)
            cand = _get(candidate, "latencyMs", stage, quantile)
            check = {"metric": f"latencyMs.{stage}.{quantile}", "baseline": base, "candidate": cand, "gating": True}
            if base is None or cand is None:
                check.update(status="skipped", reason="percentile withheld or missing")
            elif base <= 0:
                check.update(status="skipped", reason="non-positive baseline (Fake-mode timing)")
            else:
                delta = cand - base
                check.update(delta=round(delta, 1), change=round(delta / base, 4), limit=limit)
                regressed = delta / base > limit + EPSILON and delta > thresholds.min_latency_delta_ms
                check["status"] = "regression" if regressed else "ok"
            checks.append(check)
    for group, metrics, gating in (("quality", HIGHER_IS_BETTER, True), ("quality", LOWER_IS_BETTER, True),
                                   ("judge", JUDGE_METRICS, thresholds.gate_judge)):
        for metric in metrics:
            base = _get(baseline, group, metric) or {}
            cand = _get(candidate, group, metric) or {}
            check = {"metric": f"{group}.{metric}", "baseline": base.get("value"), "baselineCi95": base.get("ci95"),
                     "candidate": cand.get("value"), "gating": gating}
            if base.get("value") is None or cand.get("value") is None:
                check.update(status="skipped", reason="metric missing in one result")
            else:
                interval = base.get("ci95") or [base["value"], base["value"]]
                if metric in LOWER_IS_BETTER:
                    regressed = cand["value"] > interval[1] + EPSILON
                else:
                    regressed = cand["value"] < interval[0] - EPSILON
                check["status"] = ("regression" if gating else "advisory_regression") if regressed else "ok"
            checks.append(check)
    regressions = [check for check in checks if check["status"] == "regression"]
    report.update(status="regression" if regressions else "pass", exitCode=1 if regressions else 0,
                  regressions=[check["metric"] for check in regressions])
    return report


def render(report: dict) -> str:
    lines = [f"Comparison: {report['status'].upper()}"]
    lines += [f"  incompatible: {problem}" for problem in report["problems"]]
    for check in report["checks"]:
        if check["status"] == "skipped":
            lines.append(f"  [skip] {check['metric']}: {check.get('reason')}")
            continue
        mark = {"ok": "ok  ", "regression": "FAIL", "advisory_regression": "warn"}[check["status"]]
        extra = f" change {check['change']:+.1%}" if "change" in check else \
            (f" baseline CI {check['baselineCi95']}" if check.get("baselineCi95") else "")
        lines.append(f"  [{mark}] {check['metric']}: {check['baseline']} -> {check['candidate']}{extra}")
    return "\n".join(lines)
