"""Load and validate the committed synthetic corpus and its evaluation items.

Only corpora declared ``"classification": "synthetic"`` and ``"fictional": true`` whose every document carries the
manifest's synthetic marker are accepted; anything else is refused before it can be uploaded or evaluated.
"""

from __future__ import annotations

import hashlib
import json
import re
from dataclasses import dataclass
from pathlib import Path

from . import textnorm
from .privacy import CORPUS_DIR, PrivacyError, inside

CATEGORIES = ("fact", "number", "asr-term", "multi-turn", "not-covered", "commitment", "general")
SPEAKERS = ("A", "B")
MIN_ITEMS = 30
DEFAULT_MAX_WORDS = 24
_ID = re.compile(r"^[a-z0-9][a-z0-9-]{2,63}$")


@dataclass(frozen=True)
class Document:
    id: str
    title: str
    file: str
    text: str


@dataclass(frozen=True)
class Corpus:
    name: str
    company: str
    directory: Path
    documents: tuple[Document, ...]
    sha256: str

    def document(self, document_id: str) -> Document:
        for document in self.documents:
            if document.id == document_id:
                return document
        raise KeyError(document_id)

    @property
    def titles(self) -> dict[str, str]:
        return {document.id: document.title for document in self.documents}


@dataclass(frozen=True)
class Turn:
    speaker: str
    text: str


@dataclass(frozen=True)
class Fact:
    id: str
    any_of: tuple[str, ...]
    primary: bool = False


@dataclass(frozen=True)
class KeyTerm:
    term: str
    variants: tuple[str, ...]


@dataclass(frozen=True)
class Item:
    id: str
    category: str
    context: tuple[Turn, ...]
    target: Turn
    materials_required: bool
    expected_sources: tuple[str, ...]
    required_facts: tuple[Fact, ...]
    forbidden_claims: tuple[str, ...]
    forbidden_patterns: tuple[str, ...]
    abstain_required: bool
    abstain_any_of: tuple[str, ...]
    key_terms: tuple[KeyTerm, ...]
    max_words: int = DEFAULT_MAX_WORDS

    @property
    def utterances(self) -> tuple[Turn, ...]:
        return (*self.context, self.target)


@dataclass(frozen=True)
class ItemSet:
    items: tuple[Item, ...]
    sha256: str

    def select(self, ids: list[str] | None = None, categories: list[str] | None = None) -> list[Item]:
        chosen = list(self.items)
        if ids:
            unknown = sorted(set(ids) - {item.id for item in chosen})
            if unknown:
                raise ValueError(f"Unknown item ids: {', '.join(unknown)}")
            chosen = [item for item in chosen if item.id in set(ids)]
        if categories:
            unknown = sorted(set(categories) - set(CATEGORIES))
            if unknown:
                raise ValueError(f"Unknown categories: {', '.join(unknown)}")
            chosen = [item for item in chosen if item.category in set(categories)]
        if not chosen:
            raise ValueError("The selection contains no items.")
        return chosen


def canonical_sha256(value: object) -> str:
    data = json.dumps(value, sort_keys=True, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
    return hashlib.sha256(data).hexdigest()


def _read_json(path: Path) -> dict:
    with open(path, encoding="utf-8-sig") as handle:
        value = json.load(handle)
    if not isinstance(value, dict):
        raise ValueError(f"{path.name} must contain a JSON object.")
    return value


def load_corpus(directory: str | Path = CORPUS_DIR) -> Corpus:
    root = Path(directory).resolve()
    manifest = _read_json(root / "corpus.json")
    if manifest.get("classification") != "synthetic" or manifest.get("fictional") is not True:
        raise PrivacyError("Refusing a corpus that is not declared synthetic and fictional (corpus.json).")
    marker = manifest.get("marker")
    if not isinstance(marker, str) or len(marker.strip()) < 20:
        raise PrivacyError("The synthetic corpus manifest must declare the marker sentence every document carries.")
    documents = []
    seen: set[str] = set()
    for entry in manifest.get("documents", []):
        document_id, title, file = entry.get("id"), entry.get("title"), entry.get("file")
        if not isinstance(document_id, str) or not _ID.match(document_id) or document_id in seen:
            raise ValueError(f"Invalid or duplicate corpus document id: {document_id!r}")
        if not isinstance(title, str) or not 1 <= len(title.strip()) <= 200:
            raise ValueError(f"Corpus document {document_id} needs a title of 1-200 characters.")
        if not isinstance(file, str) or not file.endswith(".md"):
            raise ValueError(f"Corpus document {document_id} must reference a Markdown file.")
        path = (root / file).resolve()
        if not inside(path, root):
            raise PrivacyError(f"Corpus document {document_id} points outside the corpus directory.")
        text = path.read_text(encoding="utf-8-sig").replace("\r\n", "\n")
        if marker not in text:
            raise PrivacyError(f"Corpus document {document_id} lacks the synthetic marker; refusing to use it.")
        seen.add(document_id)
        documents.append(Document(document_id, title.strip(), file, text))
    if not 6 <= len(documents) <= 10:
        raise ValueError("The synthetic corpus must contain 6-10 documents.")
    digest = canonical_sha256({"manifest": manifest, "documents": [[d.id, d.text] for d in documents]})
    return Corpus(str(manifest.get("name", "")), str(manifest.get("company", "")), root, tuple(documents), digest)


def _strings(value: object, field: str, item_id: str, errors: list[str]) -> tuple[str, ...]:
    if value is None:
        return ()
    if not isinstance(value, list) or not all(isinstance(entry, str) and entry.strip() for entry in value):
        errors.append(f"{item_id}: {field} must be a list of nonblank strings")
        return ()
    return tuple(entry.strip() for entry in value)


def _turn(value: object, item_id: str, errors: list[str]) -> Turn | None:
    if not isinstance(value, dict) or value.get("speaker") not in SPEAKERS:
        errors.append(f"{item_id}: every turn needs speaker A or B")
        return None
    text = value.get("text")
    if not isinstance(text, str) or not 1 <= len(text.strip()) <= 300:
        errors.append(f"{item_id}: every turn needs text of 1-300 characters")
        return None
    return Turn(value["speaker"], text.strip())


def _item(raw: dict, errors: list[str]) -> Item | None:
    item_id = raw.get("id")
    if not isinstance(item_id, str) or not _ID.match(item_id):
        errors.append(f"invalid item id: {item_id!r}")
        return None
    category = raw.get("category")
    if category not in CATEGORIES:
        errors.append(f"{item_id}: unknown category {category!r}")
    context_raw = raw.get("context", [])
    if not isinstance(context_raw, list) or len(context_raw) > 6:
        errors.append(f"{item_id}: context must be a list of at most 6 turns")
        context_raw = []
    context = tuple(turn for turn in (_turn(value, item_id, errors) for value in context_raw) if turn)
    target = _turn(raw.get("target"), item_id, errors)
    facts = []
    for fact in raw.get("requiredFacts", []) or []:
        alternatives = _strings(fact.get("anyOf") if isinstance(fact, dict) else None, "requiredFacts.anyOf", item_id, errors)
        if not isinstance(fact, dict) or not isinstance(fact.get("id"), str) or not alternatives:
            errors.append(f"{item_id}: every required fact needs an id and anyOf alternatives")
            continue
        facts.append(Fact(fact["id"], alternatives, fact.get("primary") is True))
    terms = []
    for term in raw.get("keyTerms", []) or []:
        if not isinstance(term, dict) or not isinstance(term.get("term"), str):
            errors.append(f"{item_id}: every key term needs a term")
            continue
        variants = _strings(term.get("variants"), "keyTerms.variants", item_id, errors) or (term["term"],)
        terms.append(KeyTerm(term["term"], variants))
    patterns = _strings(raw.get("forbiddenPatterns"), "forbiddenPatterns", item_id, errors)
    for pattern in patterns:
        try:
            re.compile(pattern)
        except re.error as error:
            errors.append(f"{item_id}: invalid forbidden pattern ({error})")
    max_words = raw.get("maxWords", DEFAULT_MAX_WORDS)
    if not isinstance(max_words, int) or not 5 <= max_words <= 60:
        errors.append(f"{item_id}: maxWords must be an integer from 5 to 60")
        max_words = DEFAULT_MAX_WORDS
    if target is None or category not in CATEGORIES:
        return None
    return Item(
        id=item_id, category=category, context=context, target=target,
        materials_required=raw.get("materialsRequired") is True,
        expected_sources=_strings(raw.get("expectedSources"), "expectedSources", item_id, errors),
        required_facts=tuple(facts),
        forbidden_claims=_strings(raw.get("forbiddenClaims"), "forbiddenClaims", item_id, errors),
        forbidden_patterns=patterns,
        abstain_required=raw.get("abstainRequired") is True,
        abstain_any_of=_strings(raw.get("abstainAnyOf"), "abstainAnyOf", item_id, errors),
        key_terms=tuple(terms), max_words=max_words)


def load_items(path: str | Path = CORPUS_DIR / "items.json") -> ItemSet:
    data = _read_json(Path(path))
    if data.get("classification") != "synthetic":
        raise PrivacyError("Refusing evaluation items that are not declared synthetic (items.json).")
    errors: list[str] = []
    items = []
    seen: set[str] = set()
    for raw in data.get("items", []):
        if not isinstance(raw, dict):
            errors.append("every item must be an object")
            continue
        item = _item(raw, errors)
        if item is None:
            continue
        if item.id in seen:
            errors.append(f"duplicate item id: {item.id}")
        seen.add(item.id)
        items.append(item)
    if errors:
        raise ValueError("\n".join(errors))
    return ItemSet(tuple(items), canonical_sha256(data))


def validate(corpus: Corpus, item_set: ItemSet) -> list[str]:
    """Semantic checks: coverage, traceable facts, consistent sources and spoken key terms."""
    errors: list[str] = []
    items = item_set.items
    if len(items) < MIN_ITEMS:
        errors.append(f"at least {MIN_ITEMS} items are required (found {len(items)})")
    missing = sorted(set(CATEGORIES) - {item.category for item in items})
    if missing:
        errors.append(f"missing categories: {', '.join(missing)}")
    document_ids = {document.id for document in corpus.documents}
    normalized_docs = {document.id: textnorm.normalize(document.text) for document in corpus.documents}
    for item in items:
        unknown = sorted(set(item.expected_sources) - document_ids)
        if unknown:
            errors.append(f"{item.id}: unknown expected sources {', '.join(unknown)}")
        if item.materials_required:
            if not item.required_facts or not item.expected_sources:
                errors.append(f"{item.id}: materials-required items need required facts and expected sources")
            for fact in item.required_facts:
                sources = [normalized_docs[source] for source in item.expected_sources if source in normalized_docs]
                if not any(textnorm.contains_phrase(text, alternative) for text in sources for alternative in fact.any_of):
                    errors.append(f"{item.id}: fact {fact.id} is not traceable to its expected sources")
        if item.category == "not-covered" and (item.materials_required or not item.abstain_required):
            errors.append(f"{item.id}: not-covered items must not require materials and must require a safe abstention")
        if item.category == "commitment" and not (item.forbidden_claims or item.forbidden_patterns):
            errors.append(f"{item.id}: commitment traps need forbidden claims or patterns")
        if item.category == "multi-turn" and not item.context:
            errors.append(f"{item.id}: multi-turn items need preceding context turns")
        spoken = textnorm.normalize(" ".join(turn.text for turn in item.utterances))
        for term in item.key_terms:
            if not textnorm.contains_any(spoken, term.variants):
                errors.append(f"{item.id}: key term {term.term} is not spoken in the item")
    return errors
