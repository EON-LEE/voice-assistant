import copy
import json
import math
import unittest

from measurement import (
    EventCollector, analyze_run, cohort_key, distribution, first_sentence_end, summarize, word_error_rate,
)


def profile():
    return {"inputSha256": "a" * 64, "contextSha256": "b" * 64, "mode": "Azure",
            "capture": "native-tab", "model": "deployment/version",
            "rankConfiguration": {"semantic": "meeting-semantic", "minimum": 2.0}, "warmState": "unknown"}


def fixture(run_id="one", variant="baseline"):
    return {
        "schemaVersion": 1, "runId": run_id, "questionId": "stable-question", "variant": variant,
        "profile": profile(), "reference": {"humanVerified": False, "text": None},
        "events": [
            {"type": "transcript.partial", "atMs": 80, "turnId": "t1", "revision": 1, "text": "The full"},
            {"type": "clip.end", "atMs": 100, "questionId": "stable-question", "provenance": "test-only same-window marker; IPC delay included"},
            {"type": "transcript.final", "atMs": 120, "turnId": "t1", "revision": 2, "text": "The full selected question"},
            {"type": "response.started", "atMs": 130, "turnId": "t1", "responseId": "r1"},
            {"type": "response.delta", "atMs": 150, "turnId": "t1", "responseId": "r1", "text": "We should"},
            {"type": "response.delta", "atMs": 175, "turnId": "t1", "responseId": "r1", "text": " review this."},
            {"type": "response.delta", "atMs": 200, "turnId": "t1", "responseId": "r1", "text": " Next"},
            {"type": "response.completed", "atMs": 250, "turnId": "t1", "responseId": "r1", "text": "We should review this. Next we can discuss it."},
            {"type": "question.marker", "atMs": 260, "questionId": "stable-question", "turnId": "t1",
             "responseId": "r1", "finalRevision": 2, "selectionProvenance": "Explicit full question selection, not last final"},
            {"type": "run.end", "atMs": 270, "status": "success"},
        ],
    }


class MeasurementTests(unittest.TestCase):
    def test_distinct_first_text_and_sentence(self):
        result = analyze_run(fixture())
        self.assertEqual(result["outcome"], "success")
        self.assertEqual(result["latencyMs"]["selectedFinalToFirstText"], 30)
        self.assertEqual(result["latencyMs"]["selectedFinalToFirstReadableSentence"], 80)
        self.assertEqual(result["latencyMs"]["clipBoundaryToCompleted"], 150)

    def test_no_acoustic_guess(self):
        result = analyze_run(fixture())
        self.assertIsNone(result["latencyMs"]["annotatedSpeechEndToFirstText"])
        self.assertFalse(result["speechEnd"]["observed"])
        self.assertFalse(result["clipBoundary"]["isAcousticEnd"])

    def test_annotated_end_separate(self):
        run = fixture()
        run["events"].insert(1, {"type": "speech.end", "atMs": 90, "questionId": run["questionId"],
                                "annotationKind": "human-annotated", "provenance": "Human alignment reference"})
        result = analyze_run(run)
        self.assertEqual(result["latencyMs"]["annotatedSpeechEndToFirstText"], 60)
        self.assertEqual(result["latencyMs"]["clipBoundaryToFirstText"], 50)

    def test_last_and_fragment_not_question(self):
        run = fixture()
        run["events"].insert(8, {"type": "transcript.final", "atMs": 255, "turnId": "t2", "revision": 1, "text": "And."})
        result = analyze_run(run)
        self.assertEqual(result["latencyMs"]["selectedFinalToFirstText"], 30)

    def test_reference_unknown_with_captions(self):
        run = fixture()
        run["reference"] = {"humanVerified": False, "scope": "selected-final", "text": "The full selected question", "provenance": "Automatic captions"}
        self.assertEqual(analyze_run(run)["accuracy"]["status"], "unknown")

    def test_verified_selected_reference(self):
        run = fixture()
        run["reference"] = {"humanVerified": True, "scope": "selected-final", "text": "The full selected question", "provenance": "Human-aligned"}
        self.assertEqual(analyze_run(run)["accuracy"]["wordErrorRate"], 0)

    def test_wrong_reference_scope_unknown(self):
        run = fixture()
        run["reference"] = {"humanVerified": True, "scope": "whole-clip", "text": "Text", "provenance": "Human"}
        self.assertEqual(analyze_run(run)["accuracy"]["status"], "unknown")

    def test_whitespace_delta_not_first_text(self):
        run = fixture()
        run["events"][4]["text"] = " "
        self.assertEqual(analyze_run(run)["latencyMs"]["selectedFinalToFirstText"], 55)

    def test_completion_without_delta(self):
        run = fixture()
        run["events"] = [e for e in run["events"] if e["type"] != "response.delta"]
        result = analyze_run(run)
        self.assertEqual(result["latencyMs"]["selectedFinalToFirstText"], 130)
        self.assertEqual(result["latencyMs"]["selectedFinalToFirstReadableSentence"], 130)

    def test_selected_cancelled_then_stale_completion(self):
        run = fixture()
        run["events"].insert(7, {"type": "response.cancelled", "atMs": 220, "turnId": "t1", "responseId": "r1"})
        result = analyze_run(run)
        self.assertEqual(result["outcome"], "cancelled")
        self.assertIsNone(result["latencyMs"]["selectedFinalToCompleted"])
        self.assertEqual(result["ignoredStaleEvents"], 1)

    def test_stale_delta_after_completion(self):
        run = fixture()
        run["events"].insert(8, {"type": "response.delta", "atMs": 255, "turnId": "t1", "responseId": "r1", "text": "ignored"})
        self.assertEqual(analyze_run(run)["ignoredStaleEvents"], 1)

    def test_old_transcript_revision_ignored(self):
        run = fixture()
        run["events"].insert(3, {"type": "transcript.final", "atMs": 125, "turnId": "t1", "revision": 1, "text": "old"})
        self.assertEqual(analyze_run(run)["ignoredStaleEvents"], 1)

    def test_other_response_does_not_move_metrics(self):
        run = fixture()
        run["events"].insert(3, {"type": "response.delta", "atMs": 125, "turnId": "t0", "responseId": "obsolete", "text": "old"})
        self.assertEqual(analyze_run(run)["latencyMs"]["selectedFinalToFirstText"], 30)

    def test_missing_marker_unknown_not_drop(self):
        run = fixture()
        run["events"] = [e for e in run["events"] if e["type"] != "question.marker"]
        result = summarize([run])
        self.assertEqual(result["variants"]["baseline"]["outcomes"], {"unknown": 1})
        self.assertEqual(result["attempts"], 1)

    def test_missing_end_unknown(self):
        run = fixture()
        run["events"].pop()
        self.assertEqual(analyze_run(run)["outcome"], "unknown")

    def test_error_does_not_become_success(self):
        run = fixture()
        run["events"].insert(8, {"type": "error", "atMs": 255, "code": "grounding_unavailable"})
        self.assertEqual(analyze_run(run)["outcome"], "failed")

    def test_failed_attempts_in_denominator(self):
        runs = [fixture(str(i)) for i in range(5)]
        runs[1]["events"][-1]["status"] = "failed"
        runs[2]["events"][-1]["status"] = "cancelled"
        runs[3]["events"].pop()
        runs[4]["events"][1]["atMs"] = -1
        result = summarize(runs)
        cohort = result["variants"]["baseline"]
        self.assertEqual(cohort["attempts"], 5)
        self.assertEqual(cohort["successRate"], .2)
        self.assertEqual(cohort["outcomes"], {"success": 1, "failed": 1, "cancelled": 1, "unknown": 1, "invalid": 1})
        metric = cohort["metrics"]["selectedFinalToFirstText"]
        self.assertEqual(metric["samples"], 1)
        self.assertEqual(metric["allAttemptDenominator"], 5)
        self.assertIsNone(metric["p95"])

    def test_content_free_export(self):
        encoded = json.dumps(summarize([fixture()]))
        self.assertNotIn("The full selected question", encoded)
        self.assertNotIn("We should review", encoded)
        self.assertIn('"qualityOrAccuracyPassed": null', encoded)

    def test_variant_settings_do_not_change_cohort(self):
        a, b = fixture(), fixture("two", "optimized")
        a["variantSettings"] = {"endSilenceMs": 700}
        b["variantSettings"] = {"endSilenceMs": 500}
        self.assertEqual(summarize([a, b])["attempts"], 2)

    def test_rank_object_order_not_cohort_difference(self):
        a, b = profile(), profile()
        b["rankConfiguration"] = {"minimum": 2.0, "semantic": "meeting-semantic"}
        self.assertEqual(cohort_key(a), cohort_key(b))

    def test_duplicate_ids_rejected(self):
        with self.assertRaisesRegex(ValueError, "Duplicate"):
            summarize([fixture(), fixture()])

    def test_mixed_questions_rejected(self):
        b = fixture("two")
        b["questionId"] = "different-question"
        with self.assertRaisesRegex(ValueError, "Different questions"):
            summarize([fixture(), b])

    def test_negative_clip_latency_kept(self):
        run = fixture()
        boundary = run["events"].pop(1)
        boundary["atMs"] = 255
        run["events"].insert(7, boundary)
        self.assertEqual(analyze_run(run)["latencyMs"]["clipBoundaryToFirstText"], -105)

    def test_explicit_profile_unknown_warm(self):
        self.assertTrue(cohort_key(profile()))

    def test_empty_runs_rejected(self):
        with self.assertRaises(ValueError):
            summarize([])

    def test_readable_sentence_may_remain_unknown(self):
        run = fixture()
        for event in run["events"]:
            if event["type"].startswith("response.") and "text" in event:
                event["text"] = "incomplete fragment"
        self.assertIsNone(analyze_run(run)["latencyMs"]["selectedFinalToFirstReadableSentence"])

    def test_fractional_milliseconds_preserved(self):
        run = fixture()
        run["events"][4]["atMs"] = 150.125
        self.assertEqual(analyze_run(run)["latencyMs"]["selectedFinalToFirstText"], 30.125)

    def test_counter_no_success_label_for_n3(self):
        result = summarize([fixture(str(i)) for i in range(3)])
        self.assertIsNone(result["variants"]["baseline"]["metrics"]["selectedFinalToFirstText"]["p50"])
        self.assertIsNone(result["latencyTargetAchieved"])

    def test_word_error_rate_can_exceed_one(self):
        self.assertEqual(word_error_rate("one", "one two three")["wordErrorRate"], 2)

    def test_empty_reference_rejected(self):
        with self.assertRaises(ValueError):
            word_error_rate("...", "text")


def add_invalid(name, mutation):
    def test(self):
        run = fixture()
        mutation(run)
        with self.assertRaises((ValueError, TypeError, KeyError)):
            analyze_run(run)
    setattr(MeasurementTests, "test_invalid_" + name, test)


for name, change in {
    "version": lambda r: r.update(schemaVersion=3),
    "nonmonotonic": lambda r: r["events"][4].update(atMs=20),
    "negative_clock": lambda r: r["events"][0].update(atMs=-1),
    "nan_clock": lambda r: r["events"][0].update(atMs=float("nan")),
    "inf_clock": lambda r: r["events"][0].update(atMs=float("inf")),
    "bool_clock": lambda r: r["events"][0].update(atMs=True),
    "string_clock": lambda r: r["events"][0].update(atMs="80"),
    "unknown_event": lambda r: r["events"][0].update(type="mystery"),
    "after_run_end": lambda r: r["events"].append({"type": "error", "atMs": 300}),
    "missing_turn": lambda r: r["events"][8].pop("turnId"),
    "missing_response": lambda r: r["events"][8].pop("responseId"),
    "missing_provenance": lambda r: r["events"][8].pop("selectionProvenance"),
    "revision_bool": lambda r: r["events"][8].update(finalRevision=True),
    "revision_zero": lambda r: r["events"][8].update(finalRevision=0),
    "transcript_revision": lambda r: r["events"][0].update(revision=-1),
    "transcript_text": lambda r: r["events"][0].update(text=None),
    "response_turn_mismatch": lambda r: r["events"][4].update(turnId="wrong"),
    "response_missing_start": lambda r: r["events"].pop(3),
    "response_text": lambda r: r["events"][4].update(text=5),
    "run_status": lambda r: r["events"][-1].update(status="PASS"),
    "clip_provenance": lambda r: r["events"][1].pop("provenance"),
    "acoustic_guess": lambda r: r["events"][1].update(type="speech.end", annotationKind="clip-cut"),
}.items():
    add_invalid(name, change)


for field, value in {
    "inputSha256": "c" * 64, "contextSha256": "d" * 64, "mode": "Fake", "capture": "microphone",
    "model": "other/version", "rankConfiguration": {"semantic": "other", "minimum": 3}, "warmState": "cold",
}.items():
    def test(self, field=field, value=value):
        other = fixture("two", "optimized")
        other["profile"][field] = value
        with self.assertRaisesRegex(ValueError, "Mixed cohorts"):
            summarize([fixture(), other])
    setattr(MeasurementTests, "test_mixed_profile_" + field, test)


for n in (0, 1, 2, 3, 4, 5, 6, 10, 19, 20, 21, 40):
    def test(self, n=n):
        result = distribution(list(range(n)))
        self.assertEqual(result["samples"], n)
        self.assertEqual(result["p50"], (n - 1) / 2 if n >= 5 else None)
        self.assertEqual(result["p95"], math.ceil(n * .95) - 1 if n >= 20 else None)
    setattr(MeasurementTests, f"test_percentile_sample_threshold_{n}", test)


for name, text, complete, expected in [
    ("simple_complete", "We agree.", True, 9),
    ("stream_end_wait", "We agree.", False, None),
    ("next_sentence_confirms", "We agree. Next", False, 9),
    ("dr_abbrev", "Dr. Smith", False, None),
    ("dr_sentence", "Dr. Smith agrees.", True, 17),
    ("mr_abbrev", "Mr. Jones", True, None),
    ("decimal", "It costs 3.14 dollars", False, None),
    ("decimal_sentence", "It costs 3.14 dollars.", True, 22),
    ("initial", "A. Smith", True, None),
    ("acronym", "The U.S. team", False, None),
    ("enumeration", "1. Review", False, None),
    ("ellipsis", "We might...", True, None),
    ("empty", "", True, None),
    ("yes", "Yes.", True, 4),
    ("fragment", "And.", True, None),
    ("question", "Can we proceed?", True, 15),
    ("exclamation", "Well done!", True, 10),
    ("domain", "Visit example.com for details", False, None),
    ("closing_quote", 'He said "go now." Next', False, 17),
    ("two_sentences", "We agree. We proceed.", True, 9),
]:
    def test(self, text=text, complete=complete, expected=expected):
        self.assertEqual(first_sentence_end(text, complete), expected)
    setattr(MeasurementTests, "test_sentence_" + name, test)


if __name__ == "__main__":
    unittest.main()
