import contextlib
import io
import json
import unittest

from qualityeval import cli
from qualityeval.compare import compare
from qualityeval.privacy import REPO_ROOT

from .helpers import scratch_dir


def run(*argv: str) -> tuple[int, str, str]:
    out, err = io.StringIO(), io.StringIO()
    with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
        code = cli.main(list(argv))
    return code, out.getvalue(), err.getvalue()


class CliTests(unittest.TestCase):
    def test_validate_passes_for_the_committed_corpus(self):
        code, out, _ = run("validate")
        self.assertEqual(code, 0)
        self.assertIn("36 items", out)

    def test_remote_servers_tts_and_uploads_need_explicit_flags(self):
        code, _, err = run("run", "--base-url", "https://staging.example.invalid", "--audio-source", "synthetic-tone")
        self.assertEqual(code, 2)
        self.assertIn("--allow-remote", err)
        code, _, err = run("synthesize")
        self.assertEqual(code, 2)
        self.assertIn("--allow-tts", err)

    def test_outputs_inside_the_repository_are_refused_before_any_work(self):
        code, _, err = run("run", "--base-url", "http://localhost:1", "--output-dir", str(REPO_ROOT / "tools"))
        self.assertEqual(code, 2)
        self.assertIn("inside the repository", err)
        results = scratch_dir(self) / "results.json"
        results.parent.mkdir(parents=True)
        results.write_text(json.dumps({"schemaVersion": 1}), encoding="utf-8")
        code, out, err = run("compare", str(results), str(results), "--report", str(REPO_ROOT / "compare.json"))
        self.assertEqual((code, out), (2, ""))
        self.assertFalse((REPO_ROOT / "compare.json").exists())

    def test_compare_writes_its_report_to_the_ignored_folder(self):
        folder = scratch_dir(self)
        folder.mkdir(parents=True)
        results = folder / "results.json"
        results.write_text(json.dumps({"schemaVersion": 1, "latencyMs": {}, "quality": {}, "judge": {}}), encoding="utf-8")
        code, out, _ = run("compare", str(results), str(results), "--report", str(folder / "compare.json"))
        self.assertEqual(code, 0)
        self.assertIn("PASS", out)
        self.assertEqual(json.loads((folder / "compare.json").read_text(encoding="utf-8"))["exitCode"], 0)

    def test_fake_mode_negative_timings_are_not_gated(self):
        fake = {"latencyMs": {"speechEndToFirstDelta": {"p50": -2500.0, "p95": -1200.0}}}
        worse = {"latencyMs": {"speechEndToFirstDelta": {"p50": -100.0, "p95": 400.0}}}
        report = compare(fake, worse)
        reasons = {check["reason"] for check in report["checks"] if check["metric"].startswith("latencyMs.speechEndToFirstDelta")}
        self.assertEqual(reasons, {"non-positive baseline (Fake-mode timing)"})
        self.assertEqual(report["exitCode"], 0)


if __name__ == "__main__":
    unittest.main()
