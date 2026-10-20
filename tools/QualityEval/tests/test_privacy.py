import json
import unittest

from qualityeval import privacy
from qualityeval.corpus import load_corpus, load_items
from qualityeval.privacy import (CORPUS_DIR, DEFAULT_CACHE_DIR, DEFAULT_OUTPUT_DIR, REPO_ROOT, PrivacyError,
                                 check_output_path, safe_write_text)

from .helpers import scratch_dir


class OutputBoundaryTests(unittest.TestCase):
    def test_repository_paths_outside_the_ignored_folders_are_refused(self):
        self.assertIsNotNone(REPO_ROOT)
        for path in (REPO_ROOT / "results.json", CORPUS_DIR / "results.json", CORPUS_DIR / "docs" / "new.md",
                     privacy.TOOL_DIR / "results.json", REPO_ROOT / "tests" / "acceptance-results" / "eval.json"):
            with self.assertRaises(PrivacyError, msg=str(path)):
                check_output_path(path)

    def test_ignored_output_and_cache_folders_are_allowed(self):
        self.assertEqual(check_output_path(DEFAULT_OUTPUT_DIR / "run" / "results.json"),
                         (DEFAULT_OUTPUT_DIR / "run" / "results.json").resolve())
        check_output_path(DEFAULT_CACHE_DIR / "abc.pcm")
        check_output_path(DEFAULT_OUTPUT_DIR)

    def test_paths_outside_the_repository_are_allowed(self):
        outside = REPO_ROOT.parent / "quality-eval-results" / "results.json"
        self.assertEqual(check_output_path(outside), outside.resolve())

    def test_tracked_lookalike_folder_is_refused_when_not_ignored(self):
        with self.assertRaises(PrivacyError):
            check_output_path(CORPUS_DIR / "notes.json", allowed_dirs=(CORPUS_DIR,))

    def test_safe_write_refuses_overwrite_by_default(self):
        target = scratch_dir(self) / "result.json"
        safe_write_text(target, "{}")
        with self.assertRaises(FileExistsError):
            safe_write_text(target, "{}")
        safe_write_text(target, "[]", overwrite=True)
        self.assertEqual(target.read_text(encoding="utf-8"), "[]")


class SyntheticCorpusGuardTests(unittest.TestCase):
    def make_corpus(self, manifest_changes=None, document_text=None, items_changes=None, crlf=False):
        """Copy the corpus once, applying changes in memory, so no freshly written file is rewritten."""
        directory = scratch_dir(self) / "corpus"
        manifest = json.loads((CORPUS_DIR / "corpus.json").read_text(encoding="utf-8"))
        changed_manifest = dict(manifest, **(manifest_changes or {}))
        items = json.loads((CORPUS_DIR / "items.json").read_text(encoding="utf-8"))
        items.update(items_changes or {})
        (directory / "docs").mkdir(parents=True)
        (directory / "corpus.json").write_text(json.dumps(changed_manifest), encoding="utf-8")
        (directory / "items.json").write_text(json.dumps(items), encoding="utf-8")
        for index, entry in enumerate(manifest["documents"]):
            data = (CORPUS_DIR / entry["file"]).read_bytes().replace(b"\r\n", b"\n")
            if index == 0 and document_text is not None:
                data = document_text.encode("utf-8")
            (directory / entry["file"]).write_bytes(data.replace(b"\n", b"\r\n") if crlf else data)
        return directory

    def test_committed_corpus_is_accepted(self):
        corpus = load_corpus()
        self.assertEqual(len(corpus.documents), 7)
        self.assertEqual(load_corpus(self.make_corpus()).sha256, corpus.sha256)

    def test_non_synthetic_corpus_is_refused(self):
        for changes in ({"classification": "confidential"}, {"classification": "private"}, {"fictional": False}):
            with self.assertRaises(PrivacyError):
                load_corpus(self.make_corpus(changes))

    def test_document_without_marker_is_refused(self):
        with self.assertRaises(PrivacyError):
            load_corpus(self.make_corpus(document_text="# Real quarterly plan\nRevenue grew."))

    def test_document_outside_the_corpus_folder_is_refused(self):
        manifest = json.loads((CORPUS_DIR / "corpus.json").read_text(encoding="utf-8"))
        documents = manifest["documents"]
        documents[0] = dict(documents[0], file="../../README.md")
        with self.assertRaises(PrivacyError):
            load_corpus(self.make_corpus({"documents": documents}))

    def test_non_synthetic_items_are_refused(self):
        directory = self.make_corpus(items_changes={"classification": "confidential"})
        with self.assertRaises(PrivacyError):
            load_items(directory / "items.json")

    def test_hash_ignores_line_ending_conversion(self):
        self.assertEqual(load_corpus(self.make_corpus(crlf=True)).sha256, load_corpus().sha256)


if __name__ == "__main__":
    unittest.main()
