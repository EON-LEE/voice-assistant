import contextlib
import io
import json
import re
import shutil
import unittest
import uuid
from dataclasses import replace

from qualityeval import cli
from qualityeval.audio import build_timeline, tone_clip
from qualityeval.compare import compare, compatibility
from qualityeval.corpus import ItemSet, load_corpus, load_items
from qualityeval.httpapi import LiveCoachApi
from qualityeval.privacy import CORPUS_DIR, DEFAULT_OUTPUT_DIR, REPO_ROOT
from qualityeval.protocol import live_ui_options
from qualityeval.rescore import RescoreError, rescore, response_counts
from qualityeval.runner import RunConfig, execute, plan
from qualityeval.scoring import scorer_sha256
from qualityeval.session import Limits, SessionTracker
from qualityeval.tts import ToneAudio

from .fakeserver import FakeServer
from .helpers import scratch_dir

ENV = {"EVAL_DEMO_USERNAME": "eval-user", "EVAL_DEMO_PASSWORD": "eval-pass"}
UPTIME = "commit-uptime-100"


def source(title: str) -> dict:
    return {"title": title, "url": "https://my-materials.invalid/doc", "updatedAt": None}


def scripted_reply(text: str):
    lowered = text.lower()
    if "security questionnaires" in lowered:
        return ("Joaquin Halloway handles security questionnaires.", "He also answers audit requests.", "grounded",
                [source("Kestrova team and roles")])
    if "which plan fits" in lowered:
        return ("The Growth plan fits, at $1,950 per month.", "It covers up to 400 sensors.", "grounded",
                [source("Kestrova Pulse pricing tiers")])
    return ("No, 100% uptime isn't offered on any plan.", "The Enterprise SLA is 99.9% monthly uptime.", "grounded",
            [source("Kestrova support and service levels")])


class SyntheticAudio(ToneAudio):
    """Tone audio under a neutral name, so the Azure-mode fake server accepts it."""
    name = "test-audio"


def run_cli(*argv: str) -> tuple[int, str, str]:
    out, err = io.StringIO(), io.StringIO()
    with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
        code = cli.main(list(argv))
    return code, out.getvalue(), err.getvalue()


class RescoreTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        root = DEFAULT_OUTPUT_DIR / f"_test-{uuid.uuid4().hex[:12]}"
        cls.addClassCleanup(shutil.rmtree, root, True)
        cls.corpus, cls.items = load_corpus(), load_items()
        selected = cls.items.select(["fact-security-contact", "multi-plan-fit", UPTIME])
        accents = ["en-US", "en-GB"]
        transcripts = [[turn.text for turn in item.utterances] for item, _, _ in plan(selected, accents, 2)]
        config = RunConfig(items=selected, accents=accents, options=live_ui_options(), output_dir=root, repeats=2,
                           pace=0.05, between_runs_s=0, samples=200, write_details=True, allow_materials_upload=True,
                           cleanup_materials=True, materials_settle_s=0,
                           limits=Limits(ready_s=5, final_s=3, start_s=3, complete_s=5, grace_s=0.3, run_s=30))
        with FakeServer(transcripts=transcripts, reply=scripted_reply) as server:
            _, path = execute(LiveCoachApi(server.base_url), cls.corpus, cls.items, config, SyntheticAudio(),
                              log=lambda line: None, environ=ENV)
        cls.run_dir = path.parent
        cls.results = json.loads(path.read_text(encoding="utf-8"))

    def setUp(self):
        self.logs: list[str] = []

    def lines(self) -> list[str]:
        return (self.run_dir / "details.jsonl").read_text(encoding="utf-8").splitlines()

    def copy_run(self, lines: list[str] | None = None, results: dict | None = None, *, details: bool = True,
                 with_results: bool = True):
        folder = scratch_dir(self) / self.run_dir.name
        folder.mkdir(parents=True)
        if details:
            text = "\n".join(self.lines() if lines is None else lines) + "\n"
            (folder / "details.jsonl").write_text(text, encoding="utf-8")
        if with_results:
            text = json.dumps(self.results if results is None else results)
            (folder / "results.json").write_text(text, encoding="utf-8")
        return folder

    def test_unchanged_scorer_and_items_reproduce_the_run(self):
        source_files = {name: (self.run_dir / name).read_bytes() for name in ("results.json", "details.jsonl")}
        results, before, path = rescore(self.run_dir, self.corpus, self.items, scratch_dir(self), log=self.logs.append)
        self.assertEqual(self.results["scorer"], {"sha256": scorer_sha256()})
        self.assertEqual(before, self.results)
        self.assertEqual({key: value for key, value in results.items() if key != "rescore"}, self.results)
        self.assertEqual(results["quality"]["passRate"]["value"], 1.0)
        self.assertEqual(results["responses"]["targetResponses"], 6)
        self.assertEqual(json.loads(path.read_text(encoding="utf-8")), results)
        self.assertEqual((path.parent / "details.jsonl").read_bytes(), source_files["details.jsonl"])
        self.assertEqual({name: (self.run_dir / name).read_bytes() for name in source_files}, source_files)
        self.assertTrue(path.parent.name.startswith(f"{self.run_dir.name}-rescored-"))
        provenance = results["rescore"]
        self.assertEqual((provenance["sourceRunId"], provenance["sourceTool"]),
                         (self.run_dir.name, self.results["tool"]))
        self.assertEqual((provenance["itemsSha256"], provenance["sourceItemsSha256"]), (self.items.sha256,) * 2)
        self.assertEqual((provenance["scorerSha256"], provenance["sourceScorerSha256"]), (scorer_sha256(),) * 2)
        self.assertRegex(provenance["rescoredAt"], r"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\+00:00$")
        self.assertEqual(self.logs, [])

    def test_a_changed_variant_changes_only_that_items_scores(self):
        uptime = next(item for item in self.items.items if item.id == UPTIME)
        narrow = replace(uptime, abstain_any_of=tuple(value for value in uptime.abstain_any_of
                                                      if value != "isn't offered"))
        items = ItemSet(tuple(narrow if item.id == UPTIME else item for item in self.items.items), "narrow-items")
        results, before, path = rescore(self.run_dir, self.corpus, items, scratch_dir(self), log=self.logs.append)
        old = {entry["id"]: entry for entry in before["perItem"]}
        new = {entry["id"]: entry for entry in results["perItem"]}
        self.assertEqual((old[UPTIME]["passRate"], old[UPTIME]["abstainRate"]), (1.0, 1.0))
        self.assertEqual((new[UPTIME]["passRate"], new[UPTIME]["abstainRate"]), (0.0, 0.0))
        unchanged = {key: value for key, value in old[UPTIME].items() if key not in ("passRate", "abstainRate")}
        self.assertEqual({key: value for key, value in new[UPTIME].items() if key in unchanged}, unchanged)
        self.assertEqual({key: value for key, value in new.items() if key != UPTIME},
                         {key: value for key, value in old.items() if key != UPTIME})
        self.assertEqual(results["quality"]["passRate"]["value"], round(2 / 3, 4))
        self.assertEqual(results["quality"]["abstainRate"]["value"], 0.0)
        self.assertEqual(results["quality"]["factRecall"], before["quality"]["factRecall"])
        for key in ("latencyMs", "outcomes", "responses", "errorCodes", "protocolAnomalies", "judge", "runs", "corpus",
                    "session", "audio", "createdAt"):
            self.assertEqual(results[key], before[key], key)
        self.assertEqual((results["items"]["sha256"], results["rescore"]["sourceItemsSha256"]),
                         ("narrow-items", self.items.sha256))
        rewritten = (path.parent / "details.jsonl").read_text(encoding="utf-8").splitlines()
        for original, line in zip(map(json.loads, self.lines()), map(json.loads, rewritten)):
            self.assertEqual(line["score"] != original["score"], line["item"] == UPTIME, line["item"])
            self.assertEqual({key: value for key, value in line.items() if key != "score"},
                             {key: value for key, value in original.items() if key != "score"})

    def test_rescored_results_pass_compare_compatibility(self):
        first, _, first_path = rescore(self.run_dir, self.corpus, self.items, scratch_dir(self), log=self.logs.append)
        second, _, second_path = rescore(first_path.parent, self.corpus, self.items, scratch_dir(self),
                                         log=self.logs.append)
        self.assertEqual(second["rescore"]["sourceRunId"], self.run_dir.name)
        self.assertEqual(compatibility(first, second), [])
        self.assertEqual((compare(first, second)["status"], compare(first, second)["exitCode"]), ("pass", 0))
        code, out, err = run_cli("compare", str(first_path), str(second_path))
        self.assertEqual((code, out.splitlines()[0]), (0, "Comparison: PASS"), err)
        self.assertEqual(compatibility(self.results, first), [])
        other, _, _ = rescore(self.run_dir, self.corpus, ItemSet(self.items.items, "other-items"), scratch_dir(self),
                              log=self.logs.append)
        self.assertEqual(compatibility(first, other), ["items differs"])
        legacy = {key: value for key, value in self.results.items() if key != "scorer"}
        self.assertEqual(compatibility(legacy, self.results), [])
        self.assertEqual(compatibility(legacy, first), ["scorer differs"])
        self.assertEqual(compatibility(first, dict(second, scorer={"sha256": "0" * 64})), ["scorer differs"])

    def test_rescore_command_scores_with_another_items_file_and_reports_changes(self):
        items_file = scratch_dir(self) / "items.json"
        items_file.parent.mkdir(parents=True)
        data = json.loads((CORPUS_DIR / "items.json").read_text(encoding="utf-8"))
        next(raw for raw in data["items"] if raw["id"] == UPTIME)["abstainAnyOf"].remove("isn't offered")
        items_file.write_text(json.dumps(data), encoding="utf-8")
        output = scratch_dir(self)
        code, out, err = run_cli("rescore", str(self.run_dir / "details.jsonl"), "--items", str(items_file),
                                 "--output-dir", str(output))
        self.assertEqual(code, 0, err)
        self.assertIn("Changed by rescoring: passRate 1.0 -> 0.6667, abstainRate 1.0 -> 0.0", out)
        written = [path for path in output.glob("*/results.json")]
        self.assertEqual(len(written), 1)
        self.assertEqual(json.loads(written[0].read_text(encoding="utf-8"))["items"]["sha256"],
                         load_items(items_file).sha256)
        self.assertIn(str(written[0]), out)
        code, _, err = run_cli("rescore", str(self.run_dir), "--output-dir", str(REPO_ROOT / "tools"))
        self.assertEqual(code, 2)
        self.assertIn("inside the repository", err)

    def test_judge_grades_are_carried_over_unchanged(self):
        lines = [json.loads(text) for text in self.lines()]
        lines[0]["judge"], lines[1]["judge"] = {"usefulness": 2, "support": 1}, {"error": "JudgeError"}
        results, _, path = rescore(self.copy_run([json.dumps(line) for line in lines]), self.corpus, self.items,
                                   scratch_dir(self), log=self.logs.append)
        self.assertEqual((results["judge"]["enabled"], results["judge"]["judged"], results["judge"]["failed"]),
                         (True, 1, 1))
        self.assertEqual(results["judge"]["usefulness"]["value"], 2.0)
        rewritten = (path.parent / "details.jsonl").read_text(encoding="utf-8").splitlines()
        self.assertEqual([json.loads(text)["judge"] for text in rewritten[:3]],
                         [lines[0]["judge"], lines[1]["judge"], None])

    def test_invalid_input_fails_clearly_without_writing(self):
        lines = self.lines()

        def edit(index: int, *, drop: str = "", **changes) -> list[str]:
            line = {key: value for key, value in json.loads(lines[index]).items() if key != drop}
            return lines[:index] + [json.dumps({**line, **changes})] + lines[index + 1:]

        missing_item = ItemSet(tuple(item for item in self.items.items if item.id != "multi-plan-fit"), "fewer-items")
        cases = [
            (self.copy_run(details=False), self.items,
             "does not exist; rescoring needs a run made with --write-details"),
            (self.copy_run() / "results.json", self.items, "Pass a run folder or its details.jsonl, not results.json"),
            (self.copy_run(with_results=False), self.items, "No results.json next to details.jsonl"),
            (self.copy_run(results=dict(self.results, classification="private")), self.items,
             "results.json is not a synthetic QualityEval results file"),
            (self.copy_run(), missing_item, "The items file lacks multi-plan-fit, which the run selected"),
            (self.copy_run(lines[:2] + ["{not json"] + lines[3:]), self.items,
             "details.jsonl line 3 is not valid JSON"),
            (self.copy_run(edit(0, item="general-weekend")), self.items,
             "line 1 has item 'general-weekend', which the run did not select"),
            (self.copy_run(edit(0, finalsBySegment=[])), self.items,
             "line 1 recorded 0 turns for fact-security-contact, which now has 1; its dialogue changed"),
            (self.copy_run(edit(1, drop="events")), self.items, "line 2 lacks a valid events"),
            (self.copy_run(edit(1, events=[])), self.items, "line 2 has a reply but no recorded events"),
            (self.copy_run(edit(2, timingsMs={"speechEndToFinal": "fast"})), self.items,
             "line 3 has a timing that is not a number"),
            (self.copy_run(lines + lines[:1]), self.items, "line 7 repeats fact-security-contact repeat 1"),
            (self.copy_run(lines[:-1]), self.items, "details.jsonl holds 5 runs, but results.json reports 6 completed"),
        ]
        output = scratch_dir(self)
        for path, items, message in cases:
            with self.subTest(message=message):
                with self.assertRaisesRegex(RescoreError, re.escape(message)):
                    rescore(path, self.corpus, items, output, log=self.logs.append)
        self.assertFalse(output.exists())
        code, out, err = run_cli("rescore", str(cases[0][0]), "--output-dir", str(output))
        self.assertEqual((code, out), (1, ""))
        self.assertIn("FAILED: ", err)
        self.assertIn("--write-details", err)

    def test_response_counts_replay_what_the_session_tracker_counted(self):
        texts = ("What does the Starter plan cost?", "And the Enterprise plan?")
        timeline = build_timeline([("A", text, tone_clip(text)) for text in texts])
        tracker = SessionTracker(timeline.segments)
        for index in range(timeline.frames):
            tracker.frame_sent(index, (index + 1) * 20_000_000)
        context, target = (tracker.sent_speech_end[segment.index] + 500_000_000 for segment in timeline.segments)
        events = [
            (context, {"type": "transcript.final", "turnId": "c", "revision": 1, "text": texts[0]}),
            (context + 1, {"type": "response.started", "responseId": "r1", "turnId": "c"}),
            (context + 2, {"type": "response.korean", "responseId": "r1", "turnId": "c", "index": 0, "korean": "k"}),
            (target, {"type": "transcript.final", "turnId": "t", "revision": 1, "text": texts[1]}),
            (target + 1, {"type": "response.cancelled", "responseId": "r1", "turnId": "c"}),
            (target + 2, {"type": "response.started", "responseId": "r2", "turnId": "t"}),
            (target + 3, {"type": "response.started", "responseId": "r2", "turnId": "t"}),
            (target + 4, {"type": "response.korean", "responseId": "r9", "turnId": "t", "index": 0, "korean": "k"}),
            (target + 5, {"type": "response.completed", "responseId": "r2", "turnId": "t",
                          "text": "It starts at $7,500.", "sources": []}),
        ]
        for t, event in events:
            tracker.event(t, event)
        outcome = tracker.outcome(details=True)
        names = ("target_responses", "context_responses", "cancelled_responses", "korean_events")
        counts = {name: getattr(outcome, name) for name in names}
        self.assertEqual(counts, dict.fromkeys(names, 1))
        recorded = [{"tMs": t, **event} for t, event in outcome.events]
        self.assertEqual(response_counts(recorded, outcome.segment_finals), counts)


if __name__ == "__main__":
    unittest.main()
