"""Shared builders for the offline tests."""

from __future__ import annotations

import shutil
import uuid
from pathlib import Path

from qualityeval.corpus import Fact, Item, KeyTerm, Turn
from qualityeval.privacy import DEFAULT_OUTPUT_DIR


def make_item(**overrides) -> Item:
    values = dict(
        id="test-item", category="fact", context=(), target=Turn("A", "Who handles security questionnaires at Kestrova?"),
        materials_required=True, expected_sources=("team",),
        required_facts=(Fact("halloway", ("Joaquin Halloway", "Halloway")),), forbidden_claims=(), forbidden_patterns=(),
        abstain_required=False, abstain_any_of=(), key_terms=(KeyTerm("Kestrova", ("kestrova",)),), max_words=24)
    values.update(overrides)
    return Item(**values)


def scratch_dir(test) -> Path:
    """A unique folder inside the git-ignored out/ directory, removed after the test."""
    path = DEFAULT_OUTPUT_DIR / f"_test-{uuid.uuid4().hex[:12]}"
    test.addCleanup(shutil.rmtree, path, True)
    return path
