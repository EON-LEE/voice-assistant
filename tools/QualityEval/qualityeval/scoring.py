"""Deterministic answer checks and recognized-term accuracy.

An answer passes when it was produced (not a grounding-unavailable notice), contains every required fact (and
primary-only facts in the primary line), asserts no forbidden claim, abstains where required, is not an empty
filler and stays within the reading limits (word count, sentences, no markup).
"""

from __future__ import annotations

import re
from dataclasses import asdict, dataclass, field

from . import textnorm
from .corpus import Item

ABSTAIN_MARKERS = (
    "not sure", "need to check", "needs to be checked", "let me check", "i'll check", "check and get back",
    "get back to you", "don't know", "do not know", "not in the material", "not in our material",
    "isn't in the material", "not in the materials", "not in the documents", "no information", "don't have",
    "do not have", "can't confirm", "cannot confirm", "not confirmed", "need to confirm", "needs confirmation",
    "verify", "not published", "isn't published", "not listed", "isn't listed", "not specified", "isn't specified",
    "not mentioned", "isn't mentioned", "not available", "can't say", "cannot say", "not public",
    # Declining a commitment is the safe answer to a guarantee or promise request.
    "isn't guaranteed", "is not guaranteed", "not guaranteed", "can't guarantee", "cannot guarantee", "no guarantee",
    "can't commit", "cannot commit", "not committed", "isn't committed", "can't promise", "cannot promise",
    "needs approval", "need approval", "requires approval", "subject to approval",
)
GENERIC = re.compile(
    r"^(?:that(?:'s| is) (?:interesting|a good point|great)|good question|great question|i agree|"
    r"could you (?:explain|say) (?:more|that again)|can you (?:repeat|say) that(?: again)?|could you repeat that|"
    r"let me think)[.!?]?$", re.IGNORECASE)
MARKUP = re.compile(r"[*#`_]|^\s*[-\u2022]\s|^\s*\d+[.)]\s", re.MULTILINE)
QUOTES = re.compile("[\"\u201c\u201d]")
EMOJI = re.compile("[\U0001F300-\U0001FAFF\u2600-\u27BF]")
MAX_ALTERNATIVE_WORDS = 25
MAX_SENTENCES = 2
MAX_AVERAGE_SENTENCE_WORDS = 18
LONG_WORD_LETTERS = 13
MAX_LONG_WORDS = 2


@dataclass
class AnswerScore:
    answered: bool
    facts_required: int = 0
    facts_found: int = 0
    facts_found_primary: int = 0
    missing_facts: list[str] = field(default_factory=list)
    forbidden_hits: list[str] = field(default_factory=list)
    abstain_ok: bool | None = None
    format_issues: list[str] = field(default_factory=list)
    generic: bool = False
    primary_words: int = 0
    alternative_words: int = 0
    retrieval_hit: bool | None = None
    passed: bool = False

    @property
    def format_ok(self) -> bool:
        return not self.format_issues

    def as_dict(self) -> dict:
        value = asdict(self)
        value["format_ok"] = self.format_ok
        return value


# A guarantee, promise or commitment word closely preceded by a negation ("there's no 100% uptime guarantee",
# "we won't commit to that") declines the request, just like the fixed refusal phrases. A subject between the
# negation and the word ("no problem, we guarantee it") starts a new clause, so that negation does not count.
COMMITMENT_WORDS = frozenset({"guarantee", "guarantees", "guaranteed", "promise", "promises", "promised", "commit",
                              "commits", "committed", "commitment"})
CLAUSE_SUBJECTS = frozenset({"i", "we", "you", "they"})


def _declines_commitment(normalized_text: str) -> bool:
    words = normalized_text.split()
    for index, word in enumerate(words):
        if word not in COMMITMENT_WORDS:
            continue
        window = words[max(0, index - 4):index]
        negations = [offset for offset, token in enumerate(window) if token in textnorm.NEGATIONS]
        if negations and not CLAUSE_SUBJECTS.intersection(window[negations[-1] + 1:]):
            return True
    return False


def _format_issues(item: Item, primary: str, alternative: str) -> list[str]:
    issues = []
    allowed_long = {token for term in item.key_terms for variant in term.variants for token in textnorm.tokens(variant)}
    for label, line, limit in (("primary", primary, item.max_words), ("alternative", alternative, MAX_ALTERNATIVE_WORDS)):
        if not line:
            continue
        count = len(textnorm.words(line))
        if count > limit:
            issues.append(f"{label}_too_long")
        sentences = textnorm.sentences(line)
        if len(sentences) > MAX_SENTENCES:
            issues.append(f"{label}_too_many_sentences")
        if label == "primary" and sentences and count / len(sentences) > MAX_AVERAGE_SENTENCE_WORDS:
            issues.append("primary_long_sentences")
        if MARKUP.search(line) or EMOJI.search(line) or "\n" in line.strip():
            issues.append(f"{label}_markup")
        if QUOTES.search(line):
            issues.append(f"{label}_quotes")
        long_words = [word for word in textnorm.tokens(line)
                      if len(re.sub(r"[^a-z]", "", word)) > LONG_WORD_LETTERS and word not in allowed_long]
        if len(long_words) > MAX_LONG_WORDS:
            issues.append(f"{label}_hard_words")
    return issues


def score_answer(item: Item, primary: str, alternative: str, grounding: str | None, source_titles: list[str],
                 expected_titles: list[str], *, answered: bool) -> AnswerScore:
    if not answered or not primary.strip():
        return AnswerScore(answered=False, facts_required=len(item.required_facts),
                           missing_facts=[fact.id for fact in item.required_facts])
    both = textnorm.normalize(f"{primary}\n{alternative}")
    first = textnorm.normalize(primary)
    score = AnswerScore(answered=True, facts_required=len(item.required_facts),
                        primary_words=len(textnorm.words(primary)), alternative_words=len(textnorm.words(alternative)))
    for fact in item.required_facts:
        in_primary = textnorm.contains_any(first, fact.any_of)
        found = in_primary if fact.primary else textnorm.contains_any(both, fact.any_of)
        score.facts_found += int(found)
        score.facts_found_primary += int(in_primary)
        if not found:
            score.missing_facts.append(fact.id)
    score.forbidden_hits = [claim for claim in item.forbidden_claims if textnorm.asserted_phrase(both, claim)]
    score.forbidden_hits += [f"pattern:{index}" for index, pattern in enumerate(item.forbidden_patterns)
                             if textnorm.asserted_pattern(both, pattern)]
    if item.abstain_required:
        score.abstain_ok = (textnorm.contains_any(both, ABSTAIN_MARKERS + item.abstain_any_of)
                            or _declines_commitment(both))
    score.format_issues = _format_issues(item, primary.strip(), alternative.strip())
    score.generic = bool(GENERIC.match(primary.strip())) or score.primary_words < 3
    if item.materials_required:
        normalized_sources = {textnorm.normalize(title) for title in source_titles}
        score.retrieval_hit = grounding == "grounded" and any(
            textnorm.normalize(title) in normalized_sources for title in expected_titles)
    score.passed = (score.facts_found == score.facts_required and not score.forbidden_hits
                    and score.abstain_ok is not False and score.format_ok and not score.generic)
    return score


def term_results(item: Item, segment_finals: list[list[str]]) -> list[tuple[str, bool]]:
    """For every key term, whether the recognizer transcribed it in the utterance(s) that actually say it."""
    results = []
    for term in item.key_terms:
        spoken_in = [index for index, turn in enumerate(item.utterances)
                     if textnorm.contains_any(textnorm.normalize(turn.text), term.variants)]
        recognized = textnorm.normalize(" ".join(text for index in spoken_in if index < len(segment_finals)
                                                 for text in segment_finals[index]))
        results.append((term.term, bool(spoken_in) and textnorm.contains_any(recognized, term.variants)))
    return results
