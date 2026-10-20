"""Text normalization and phrase matching shared by scoring, term accuracy and corpus validation.

Matching is token-based on a normalized form so that "$1,950", "1950 dollars" and "USD 1950" all contain the
token "1950", "99.9 percent" equals "99.9%", "02:00 a.m." equals "2:00 am" and "ORN-4" equals "ORN 4".
"""

from __future__ import annotations

import re
import unicodedata
from typing import Iterable

NEGATIONS = frozenset({
    "not", "no", "never", "cannot", "can't", "cant", "won't", "wont", "don't", "dont", "doesn't", "doesnt",
    "isn't", "isnt", "aren't", "arent", "wasn't", "wasnt", "weren't", "didn't", "didnt", "shouldn't",
    "wouldn't", "couldn't", "haven't", "hasn't", "without", "unable", "nor", "neither",
})

STOPWORDS = frozenset({
    "a", "an", "the", "and", "or", "but", "so", "to", "of", "in", "on", "at", "for", "with", "by", "from", "is",
    "are", "was", "were", "be", "been", "it", "its", "it's", "this", "that", "these", "those", "we", "you", "i",
    "our", "your", "us", "they", "them", "do", "does", "did", "can", "could", "would", "will", "what", "which",
    "who", "how", "when", "where", "why", "if", "then", "there", "here", "about", "as", "yeah", "okay", "ok",
    "um", "uh", "mm", "hmm", "right", "well", "just", "also", "any", "some",
})

_TRANSLATE = str.maketrans({
    "\u2019": "'", "\u2018": "'", "\u201c": '"', "\u201d": '"', "\u2013": "-", "\u2014": "-", "\u2011": "-",
    "\u00a0": " ",
})


def normalize(text: str) -> str:
    """Lowercased, punctuation-light form used for every comparison in this tool."""
    value = unicodedata.normalize("NFKC", text or "").lower().translate(_TRANSLATE)
    value = value.replace("$", " usd ").replace("\u20ac", " eur ").replace("\u00a3", " gbp ")
    value = re.sub(r"(?<=\d),(?=\d{3}(?:\D|$))", "", value)
    value = re.sub(r"\b(\d+)(?:st|nd|rd|th)\b", r"\1", value)
    value = re.sub(r"\b([ap])\.\s?m\b\.?", r"\1m", value)
    value = re.sub(r"\b0(\d):(\d\d)\b", r"\1:\2", value)
    value = re.sub(r"\bper\s?cent\b", "%", value)
    value = re.sub(r"(?<=[a-z0-9])[-/](?=[a-z0-9])", " ", value)
    value = re.sub(r"[^a-z0-9%.:' ]+", " ", value)
    value = re.sub(r"(?<!\d)[.:]|[.:](?!\d)", " ", value)
    value = re.sub(r"(?<![a-z0-9])'|'(?![a-z0-9])", " ", value)
    value = re.sub(r"(?<=[a-z0-9])'s\b", " 's", value)
    value = value.replace("%", " % ")
    return " ".join(value.split())


def tokens(text: str) -> list[str]:
    return normalize(text).split()


def contains_phrase(normalized_text: str, phrase: str) -> bool:
    """True when the normalized phrase occurs on token boundaries of already normalized text."""
    target = normalize(phrase)
    return bool(target) and f" {target} " in f" {normalized_text} "


def contains_any(normalized_text: str, phrases: Iterable[str]) -> bool:
    return any(contains_phrase(normalized_text, phrase) for phrase in phrases)


def phrase_positions(text_tokens: list[str], phrase: str) -> list[int]:
    needle = normalize(phrase).split()
    if not needle:
        return []
    size = len(needle)
    return [index for index in range(len(text_tokens) - size + 1) if text_tokens[index:index + size] == needle]


def negated(text_tokens: list[str], index: int, window: int = 4) -> bool:
    """A claim preceded closely by a negation ("can't guarantee") is not an assertion of that claim."""
    return any(token in NEGATIONS for token in text_tokens[max(0, index - window):index])


def asserted_phrase(normalized_text: str, phrase: str) -> bool:
    """True when the phrase occurs at least once without a preceding negation."""
    text_tokens = normalized_text.split()
    return any(not negated(text_tokens, index) for index in phrase_positions(text_tokens, phrase))


def asserted_pattern(normalized_text: str, pattern: str) -> bool:
    """Regex variant of asserted_phrase, evaluated on the normalized text."""
    text_tokens = normalized_text.split()
    for match in re.finditer(pattern, normalized_text):
        index = len(normalized_text[:match.start()].split())
        if not negated(text_tokens, index):
            return True
    return False


def content_tokens(text: str) -> set[str]:
    return {token for token in tokens(text) if token not in STOPWORDS and (len(token) > 1 or token.isdigit())}


def containment(part: str, whole: str) -> float:
    """Share of the part's content words that also occur in the whole (0..1)."""
    part_tokens = content_tokens(part)
    if not part_tokens:
        return 0.0
    return len(part_tokens & content_tokens(whole)) / len(part_tokens)


def words(text: str) -> list[str]:
    return [word for word in (text or "").split() if re.search(r"[A-Za-z0-9]", word)]


def sentences(text: str) -> list[str]:
    return [part for part in re.split(r"(?<=[.!?])\s+", (text or "").strip()) if part.strip()]
