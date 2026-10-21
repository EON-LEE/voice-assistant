import copy
import unittest

from qualityeval.compare import Thresholds, compare, render


def result(**changes) -> dict:
    base = {
        "schemaVersion": 1,
        "corpus": {"sha256": "c"}, "items": {"sha256": "i", "selectedIds": ["a", "b"]},
        "session": {"options": {"responseMode": "grounded"}}, "audio": {"accents": ["en-US"], "source": "azure-tts"},
        "latencyMs": {
            "speechEndToFirstDelta": {"p50": 1800.0, "p95": 3000.0},
            "speechEndToCompleted": {"p50": 2600.0, "p95": 4200.0},
        },
        "quality": {
            "answeredRate": {"value": 0.97, "ci95": [0.93, 1.0]},
            "passRate": {"value": 0.70, "ci95": [0.60, 0.80]},
            "factRecall": {"value": 0.80, "ci95": [0.72, 0.88]},
            "abstainRate": {"value": 0.9, "ci95": [0.7, 1.0]},
            "formatOkRate": {"value": 0.95, "ci95": [0.9, 1.0]},
            "retrievalHitRate": {"value": 0.85, "ci95": [0.75, 0.93]},
            "termAccuracy": {"value": 0.6, "ci95": [0.45, 0.75]},
            "forbiddenRate": {"value": 0.05, "ci95": [0.0, 0.1]},
            "genericRate": {"value": 0.02, "ci95": [0.0, 0.06]},
        },
        "judge": {"usefulness": {"value": 1.6, "ci95": [1.45, 1.75]}, "support": {"value": 1.8, "ci95": [1.7, 1.9]}},
    }
    for path, value in changes.items():
        target = base
        keys = path.split(".")
        for key in keys[:-1]:
            target = target[key]
        target[keys[-1]] = value
    return base


class CompareTests(unittest.TestCase):
    def test_identical_results_pass(self):
        report = compare(result(), result())
        self.assertEqual((report["status"], report["exitCode"]), ("pass", 0))

    def test_p95_regression_above_ten_percent_fails(self):
        report = compare(result(), result(**{"latencyMs.speechEndToFirstDelta.p95": 3400.0}))
        self.assertEqual(report["exitCode"], 1)
        self.assertIn("latencyMs.speechEndToFirstDelta.p95", report["regressions"])

    def test_latency_within_threshold_or_below_absolute_floor_passes(self):
        self.assertEqual(compare(result(), result(**{"latencyMs.speechEndToFirstDelta.p95": 3290.0}))["exitCode"], 0)
        small = result(**{"latencyMs.speechEndToFirstDelta.p50": 300.0})
        self.assertEqual(compare(small, result(**{"latencyMs.speechEndToFirstDelta.p50": 380.0}))["exitCode"], 0)
        strict = Thresholds(max_p95_regression=0.05)
        self.assertEqual(compare(result(), result(**{"latencyMs.speechEndToFirstDelta.p95": 3290.0}), strict)["exitCode"], 1)

    def test_withheld_percentiles_are_skipped_not_failed(self):
        report = compare(result(**{"latencyMs.speechEndToCompleted.p95": None}), result())
        skipped = [check for check in report["checks"] if check["metric"] == "latencyMs.speechEndToCompleted.p95"]
        self.assertEqual(skipped[0]["status"], "skipped")
        self.assertEqual(report["exitCode"], 0)

    def test_quality_drop_beyond_baseline_interval_fails(self):
        within = {"quality.passRate.value": 0.62, "quality.passRate.ci95": [0.52, 0.72]}
        self.assertEqual(compare(result(), result(**within))["exitCode"], 0)
        report = compare(result(), result(**{"quality.passRate.value": 0.55, "quality.passRate.ci95": [0.45, 0.65]}))
        self.assertEqual(report["exitCode"], 1)
        self.assertEqual(report["regressions"], ["quality.passRate"])

    def test_lower_is_better_metrics_fail_on_increase(self):
        report = compare(result(), result(**{"quality.forbiddenRate.value": 0.15, "quality.forbiddenRate.ci95": [0.08, 0.22]}))
        self.assertEqual(report["regressions"], ["quality.forbiddenRate"])

    def test_one_miss_against_a_perfect_baseline_is_not_a_regression(self):
        perfect = result(**{"quality.abstainRate.value": 1.0, "quality.abstainRate.ci95": [1.0, 1.0],
                            "quality.genericRate.value": 0.0, "quality.genericRate.ci95": [0.0, 0.0]})
        one_miss = result(**{"quality.abstainRate.value": 0.9444, "quality.abstainRate.ci95": [0.8333, 1.0],
                             "quality.genericRate.value": 0.0093, "quality.genericRate.ci95": [0.0, 0.0278]})
        report = compare(perfect, one_miss)
        self.assertEqual((report["status"], report["exitCode"]), ("pass", 0))
        dropped = result(**{"quality.abstainRate.value": 0.7222, "quality.abstainRate.ci95": [0.5, 0.8889],
                            "quality.genericRate.value": 0.0741, "quality.genericRate.ci95": [0.0278, 0.1296]})
        self.assertEqual(compare(perfect, dropped)["regressions"], ["quality.abstainRate", "quality.genericRate"])

    def test_a_candidate_without_an_interval_is_judged_by_the_baseline_interval(self):
        report = compare(result(), result(**{"quality.passRate.value": 0.55, "quality.passRate.ci95": None}))
        self.assertEqual(report["regressions"], ["quality.passRate"])

    def test_one_forbidden_claim_against_a_clean_baseline_still_fails(self):
        clean = result(**{"quality.forbiddenRate.value": 0.0, "quality.forbiddenRate.ci95": [0.0, 0.0]})
        one = result(**{"quality.forbiddenRate.value": 0.0093, "quality.forbiddenRate.ci95": [0.0, 0.0278]})
        self.assertEqual(compare(clean, one)["regressions"], ["quality.forbiddenRate"])

    def test_judge_is_advisory_unless_gated(self):
        candidate = result(**{"judge.usefulness.value": 1.2, "judge.usefulness.ci95": [1.05, 1.35]})
        advisory = compare(result(), candidate)
        self.assertEqual(advisory["exitCode"], 0)
        statuses = {check["metric"]: check["status"] for check in advisory["checks"]}
        self.assertEqual(statuses["judge.usefulness"], "advisory_regression")
        self.assertEqual(compare(result(), candidate, Thresholds(gate_judge=True))["exitCode"], 1)

    def test_incompatible_inputs_exit_two_unless_allowed(self):
        report = compare(result(), result(**{"corpus.sha256": "other"}))
        self.assertEqual((report["status"], report["exitCode"]), ("incompatible", 2))
        allowed = compare(result(), result(**{"corpus.sha256": "other"}), Thresholds(allow_mismatch=True))
        self.assertEqual(allowed["exitCode"], 0)
        self.assertEqual(compare(result(), result(**{"session.options": {"responseMode": "balanced"}}))["exitCode"], 2)

    def test_scorer_fingerprints_must_match_when_both_are_recorded_or_a_result_was_rescored(self):
        first, second = result(scorer={"sha256": "s1"}), result(scorer={"sha256": "s2"})
        self.assertEqual(compare(first, second)["problems"], ["scorer differs"])
        self.assertEqual(compare(result(), first)["exitCode"], 0)
        rescored = result(scorer={"sha256": "s1"}, rescore={"sourceRunId": "run-a", "scorerSha256": "s1"})
        self.assertEqual(compare(rescored, first)["exitCode"], 0)
        self.assertEqual(compare(result(), rescored)["problems"], ["scorer differs"])
        self.assertEqual(compare(result(), rescored, Thresholds(allow_mismatch=True))["exitCode"], 0)

    def test_render_mentions_failures(self):
        text = render(compare(result(), result(**{"quality.passRate.value": 0.4, "quality.passRate.ci95": [0.3, 0.5]})))
        self.assertIn("FAIL", text)
        self.assertIn("quality.passRate", text)
        self.assertIn("candidate CI [0.3, 0.5]", text)
        self.assertEqual(copy.deepcopy(result()), result())


if __name__ == "__main__":
    unittest.main()
