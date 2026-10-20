import hashlib
import json
import unittest

from qualityeval.audio import tone_clip
from qualityeval.azureauth import AuthError
from qualityeval.corpus import load_corpus, load_items
from qualityeval.httpapi import LiveCoachApi
from qualityeval.identity import IdentityError
from qualityeval.protocol import live_ui_options
from qualityeval.runner import RunConfig, RunError, execute, plan
from qualityeval.session import Limits
from qualityeval.tts import ToneAudio, TtsAudio, TtsCache, TtsError

from .fakeserver import OID, FakeServer
from .helpers import scratch_dir

ENV = {"EVAL_DEMO_USERNAME": "eval-user", "EVAL_DEMO_PASSWORD": "eval-pass"}
REPLIES = {
    "security questionnaires": ("Joaquin Halloway handles security questionnaires.", "He also answers audits.", "grounded",
                                [{"title": "Kestrova team and roles", "url": "https://my-materials.invalid/1", "updatedAt": None}]),
    "which plan fits": ("The Growth plan fits, at $1,950 per month.", "It covers up to 400 sensors.", "grounded",
                        [{"title": "Kestrova Pulse pricing tiers", "url": "https://my-materials.invalid/2", "updatedAt": None}]),
}


def scripted_reply(text: str):
    for key, reply in REPLIES.items():
        if key in text.lower():
            return reply
    return ("Let's agree on the next step.", "", "no_matches", [])


class NeutralToneAudio(ToneAudio):
    """Tone audio under a neutral name, so the Azure-mode fake server accepts it."""
    name = "test-audio"


class RunnerEndToEndTests(unittest.TestCase):
    def setUp(self):
        self.corpus = load_corpus()
        self.items = load_items()
        self.selected = self.items.select(["fact-security-contact", "multi-plan-fit"])
        self.accents = ["en-US", "en-GB"]
        self.planned = plan(self.selected, self.accents, 2)
        self.transcripts = [[turn.text for turn in item.utterances] for item, _, _ in self.planned]
        self.logs: list[str] = []

    def config(self, **changes) -> RunConfig:
        values = dict(items=self.selected, accents=self.accents, options=live_ui_options(), output_dir=scratch_dir(self),
                      repeats=2, pace=0.05, between_runs_s=0, samples=200, allow_materials_upload=True,
                      cleanup_materials=True, materials_settle_s=0,
                      limits=Limits(ready_s=5, final_s=3, start_s=3, complete_s=5, grace_s=0.3, run_s=30))
        values.update(changes)
        return RunConfig(**values)

    def test_full_path_produces_aggregate_results_only(self):
        with FakeServer(transcripts=self.transcripts, reply=scripted_reply) as server:
            api = LiveCoachApi(server.base_url)
            results, path = execute(api, self.corpus, self.items, self.config(), NeutralToneAudio(), log=self.logs.append,
                                    environ=ENV)
            self.assertEqual(server.documents, {})
            self.assertEqual(server.sessions, 4)
            self.assertGreaterEqual(server.pongs, 4)
            self.assertEqual(server.stops, 4)
            self.assertEqual(server.starts[0]["options"], live_ui_options())
        self.assertEqual(results["runs"], {"planned": 4, "completed": 4, "repeats": 2, "complete": True})
        self.assertEqual(results["outcomes"], {"ok": 4})
        self.assertEqual(results["materials"], {"uploaded": True, "documents": 7, "replaced": 0, "verified": True})
        self.assertEqual(results["quality"]["passRate"]["value"], 1.0)
        self.assertEqual(results["quality"]["factRecall"]["value"], 1.0)
        self.assertEqual(results["quality"]["retrievalHitRate"]["value"], 1.0)
        self.assertEqual(results["quality"]["termAccuracy"]["micro"], 1.0)
        self.assertEqual(results["responses"]["primaryDoneSeen"], 4)
        self.assertEqual(results["responses"]["contextResponses"], 0)
        for stage in ("speechEndToFinal", "speechEndToFirstDelta", "speechEndToPrimaryDone", "speechEndToCompleted"):
            self.assertEqual(results["latencyMs"][stage]["n"], 4, stage)
            self.assertGreater(results["latencyMs"][stage]["min"], 0, stage)
        self.assertEqual(set(results["byAccent"]), set(self.accents))
        self.assertEqual(results["identity"]["oidSha256Prefix"], hashlib.sha256(OID.encode()).hexdigest()[:16])
        self.assertTrue(any(hashlib.sha256(OID.encode()).hexdigest()[:16] in line for line in self.logs))
        written = path.read_text(encoding="utf-8")
        self.assertEqual(json.loads(written)["classification"], "synthetic")
        for private in ("Halloway", "questionnaires", "Growth plan fits", "Which plan fits", OID, server.token):
            self.assertNotIn(private, written)
        self.assertFalse((path.parent / "details.jsonl").exists())

    def test_details_are_opt_in_and_stay_in_the_ignored_folder(self):
        with FakeServer(transcripts=self.transcripts, reply=scripted_reply) as server:
            _, path = execute(LiveCoachApi(server.base_url), self.corpus, self.items,
                              self.config(write_details=True), NeutralToneAudio(), log=self.logs.append, environ=ENV)
        lines = (path.parent / "details.jsonl").read_text(encoding="utf-8").splitlines()
        self.assertEqual(len(lines), 4)
        self.assertIn("Halloway", lines[0])

    def test_protocol_anomalies_are_counted_without_failing_the_run(self):
        with FakeServer(transcripts=self.transcripts, reply=scripted_reply, empty_delta_bug=True) as server:
            results, _ = execute(LiveCoachApi(server.base_url), self.corpus, self.items, self.config(), NeutralToneAudio(),
                                 log=self.logs.append, environ=ENV)
        self.assertEqual(results["protocolAnomalies"], {"empty_delta": 4})
        self.assertEqual(results["outcomes"], {"ok": 4})

    def test_forbidden_identity_is_refused_before_any_upload(self):
        environ = dict(ENV, EVAL_FORBIDDEN_OID_SHA256=hashlib.sha256(OID.encode()).hexdigest())
        with FakeServer(transcripts=self.transcripts) as server:
            with self.assertRaises(IdentityError):
                execute(LiveCoachApi(server.base_url), self.corpus, self.items, self.config(), NeutralToneAudio(),
                        log=self.logs.append, environ=environ)
            self.assertEqual((server.documents, server.sessions), ({}, 0))
        wrong = dict(ENV, EVAL_EXPECTED_OID_SHA256="0" * 64)
        with FakeServer(transcripts=self.transcripts) as server:
            with self.assertRaises(IdentityError):
                execute(LiveCoachApi(server.base_url), self.corpus, self.items, self.config(), NeutralToneAudio(),
                        log=self.logs.append, environ=wrong)

    def test_missing_credentials_and_tone_audio_against_azure_are_refused(self):
        with FakeServer() as server:
            with self.assertRaises(RunError):
                execute(LiveCoachApi(server.base_url), self.corpus, self.items, self.config(), NeutralToneAudio(),
                        log=self.logs.append, environ={})
            with self.assertRaises(RunError):
                execute(LiveCoachApi(server.base_url), self.corpus, self.items, self.config(), ToneAudio(),
                        log=self.logs.append, environ=ENV)

    def test_rejected_websockets_abort_after_repeated_failures(self):
        with FakeServer(transcripts=self.transcripts, reject_websocket=403) as server:
            results, _ = execute(LiveCoachApi(server.base_url), self.corpus, self.items, self.config(repeats=3),
                                 NeutralToneAudio(), log=self.logs.append, environ=ENV)
            self.assertEqual(server.documents, {})
        self.assertEqual(results["outcomes"], {"handshake_403": 3})
        self.assertFalse(results["runs"]["complete"])
        self.assertIsNone(results["latencyMs"]["speechEndToFirstDelta"]["p50"])

    def test_fake_mode_runs_without_credentials(self):
        with FakeServer(mode="Fake", transcripts=self.transcripts, reply=scripted_reply) as server:
            results, _ = execute(LiveCoachApi(server.base_url), self.corpus, self.items,
                                 self.config(items=self.selected[:1], repeats=1), ToneAudio(), log=self.logs.append,
                                 environ={})
        self.assertEqual(results["target"]["login"], "fake")
        self.assertEqual(results["outcomes"], {"ok": 1})
        self.assertEqual(results["audio"]["source"], "synthetic-tone")

    def test_judge_failures_are_recorded_without_losing_the_run(self):
        class BrokenJudge:
            def grade(self, *args):
                raise AuthError("no token")

        with FakeServer(mode="Fake", transcripts=self.transcripts, reply=scripted_reply) as server:
            results, path = execute(LiveCoachApi(server.base_url), self.corpus, self.items,
                                    self.config(items=self.selected[:1], repeats=1), ToneAudio(), judge=BrokenJudge(),
                                    log=self.logs.append, environ={})
        self.assertTrue(path.is_file())
        self.assertEqual((results["judge"]["enabled"], results["judge"]["judged"], results["judge"]["failed"]), (True, 0, 1))

    def test_missing_tts_audio_fails_before_touching_the_server(self):
        cache_only = TtsAudio(TtsCache(scratch_dir(self) / "empty-cache"))
        with FakeServer(transcripts=self.transcripts) as server:
            with self.assertRaises(TtsError):
                execute(LiveCoachApi(server.base_url), self.corpus, self.items, self.config(), cache_only,
                        log=self.logs.append, environ=ENV)
            self.assertEqual((server.documents, server.sessions), ({}, 0))

    def test_plan_rotates_accents_per_repeat(self):
        accents = [accent for item, accent, _ in plan(self.selected, ["en-US", "en-GB", "en-IN"], 3)
                   if item.id == "multi-plan-fit"]
        self.assertEqual(sorted(accents), ["en-GB", "en-IN", "en-US"])
        self.assertGreater(tone_clip("one two three").samples, 0)


if __name__ == "__main__":
    unittest.main()
