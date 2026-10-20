"""Rescore a finished run offline with the current scorer and items.

details.jsonl keeps everything the deterministic checks read: the recognized finals per utterance and the selected
reply with its sources. Rescoring rebuilds every run record from it, scores it again through the runner's scoring path
and aggregates the records with the source run's bootstrap settings, so runs measured under an older scorer or items
file can be compared under one scorer. Statuses, timings and the other outcome data are taken over unchanged; the
response counts, which details.jsonl does not store, are recovered by replaying its recorded events. Advisory judge
grades are carried over as recorded. Nothing leaves the machine: no network, Azure or TTS.
"""

from __future__ import annotations

import datetime as dt
import json
import os
import re
from collections import Counter
from pathlib import Path
from typing import Callable

from . import SCHEMA_VERSION, TOOL_NAME, TOOL_VERSION
from .corpus import Corpus, ItemSet
from .privacy import safe_mkdir, safe_write_text
from .report import aggregate
from .runner import _details_lines, score_run
from .scoring import scorer_sha256
from .session import STAGES, SessionOutcome

DETAILS_FILE = "details.jsonl"
RESULTS_FILE = "results.json"
# results.json keys that describe the run; report.aggregate recomputes every other key from the run records.
RUN_KEYS = ("schemaVersion", "tool", "classification", "createdAt", "target", "identity", "corpus", "items", "scorer",
            "session", "audio", "materials", "runs", "statistics")
SOURCE_KEYS = ("sourceRunId", "sourceTool", "sourceItemsSha256", "sourceScorerSha256")
LINE_FIELDS = (("item", str), ("accent", str), ("repeat", int), ("status", str), ("timingsMs", dict),
               ("finalsBySegment", list), ("response", (dict, type(None))), ("judge", (dict, type(None))),
               ("errorCodes", list), ("anomalies", list), ("events", list))
RESPONSE_KEYS = ("primary", "alternative", "grounding", "route", "prefetched", "sources", "primaryDoneSeen",
                 "streamMatchesPrimary")


class RescoreError(ValueError):
    """The run cannot be rescored: missing, malformed or inconsistent input."""


def locate(source: str | Path) -> tuple[Path, Path]:
    """The details.jsonl of a run folder (or the file itself) and the results.json beside it."""
    path = Path(source)
    details = path / DETAILS_FILE if path.is_dir() else path
    if not details.is_file():
        raise RescoreError(f"{details} does not exist; rescoring needs a run made with --write-details.")
    if details.suffix != ".jsonl":
        raise RescoreError(f"Pass a run folder or its {DETAILS_FILE}, not {details.name}.")
    results = details.with_name(RESULTS_FILE)
    if not results.is_file():
        raise RescoreError(f"No {RESULTS_FILE} next to {details.name}; rescoring takes the run's settings from it.")
    return details, results


def _read_results(path: Path) -> dict:
    try:
        with open(path, encoding="utf-8-sig") as handle:
            results = json.load(handle)
    except json.JSONDecodeError as error:
        raise RescoreError(f"{path.name} is not valid JSON ({error.msg}, line {error.lineno}).") from error
    if not isinstance(results, dict) or results.get("classification") != "synthetic" or \
            not isinstance(results.get("tool"), dict) or results["tool"].get("name") != TOOL_NAME:
        raise RescoreError(f"{path.name} is not a synthetic {TOOL_NAME} results file.")
    if results.get("schemaVersion") != SCHEMA_VERSION:
        raise RescoreError(f"{path.name} has schema version {results.get('schemaVersion')!r}; this tool reads "
                           f"{SCHEMA_VERSION}.")
    for block, key, kind in (("corpus", "sha256", str), ("items", "selectedIds", list), ("runs", "completed", int),
                             ("statistics", "bootstrapSamples", int), ("statistics", "seed", int)):
        value = results[block].get(key) if isinstance(results.get(block), dict) else None
        if not isinstance(value, kind) or isinstance(value, bool):
            raise RescoreError(f"{path.name} lacks a valid {block}.{key}.")
    if not all(isinstance(item_id, str) for item_id in results["items"]["selectedIds"]):
        raise RescoreError(f"{path.name} lacks a valid items.selectedIds.")
    return results


def _read_lines(path: Path) -> list[tuple[int, object]]:
    lines = []
    with open(path, encoding="utf-8-sig") as handle:
        for number, text in enumerate(handle, 1):
            if not text.strip():
                continue
            try:
                lines.append((number, json.loads(text)))
            except json.JSONDecodeError as error:
                raise RescoreError(f"{path.name} line {number} is not valid JSON ({error.msg}).") from error
    if not lines:
        raise RescoreError(f"{path.name} holds no runs.")
    return lines


def _number(value: object) -> bool:
    return isinstance(value, (int, float)) and not isinstance(value, bool)


def _strings(value: object) -> bool:
    return isinstance(value, list) and all(isinstance(entry, str) for entry in value)


def _line_problem(line: object) -> str | None:
    """Why a details line cannot be rebuilt into a run record, or None when it can."""
    if not isinstance(line, dict):
        return "is not a JSON object"
    for key, kinds in LINE_FIELDS:
        if key not in line or not isinstance(line[key], kinds) or isinstance(line[key], bool):
            return f"lacks a valid {key}"
    response, judge, events = line["response"], line["judge"], line["events"]
    if line["repeat"] < 1 or not line["status"]:
        return "lacks a valid repeat or status"
    if not all(value is None or _number(value) for value in line["timingsMs"].values()):
        return "has a timing that is not a number"
    if not all(_strings(texts) for texts in line["finalsBySegment"]):
        return "has finals that are not lists of strings"
    if not _strings(line["errorCodes"]) or not _strings(line["anomalies"]):
        return "has error codes or anomalies that are not strings"
    if response is not None and (any(key not in response for key in RESPONSE_KEYS)
                                 or not isinstance(response["primary"], str)
                                 or not isinstance(response["alternative"], str)
                                 or not _strings(response["sources"])):
        return "has an incomplete response"
    if judge is not None and "usefulness" in judge and not (_number(judge["usefulness"])
                                                            and _number(judge.get("support"))):
        return "has an invalid judge grade"
    if not all(isinstance(event, dict) and isinstance(event.get("type"), str) and _number(event.get("tMs"))
               for event in events):
        return "has events without a type or time"
    if response is not None and not events:
        return "has a reply but no recorded events"
    return None


def _selection_problem(line: dict, items: dict, selected: set[str], seen: set[tuple[str, int]]) -> str | None:
    """Why a well-formed details line does not fit the run's selection and the current items, or None."""
    item = items.get(line["item"]) if line["item"] in selected else None
    if item is None:
        return f"has item {line['item']!r}, which the run did not select"
    if len(line["finalsBySegment"]) != len(item.utterances):
        return (f"recorded {len(line['finalsBySegment'])} turns for {item.id}, which now has "
                f"{len(item.utterances)}; its dialogue changed")
    if (item.id, line["repeat"]) in seen:
        return f"repeats {item.id} repeat {line['repeat']}"
    return None


def response_counts(events: list[dict], finals_by_segment: list[list[str]]) -> dict[str, int]:
    """Replay recorded events into the response counts SessionTracker kept but details.jsonl does not store.

    Finals are matched to their segment in event order (finalsBySegment lists each segment's finals in arrival order),
    which recovers the turn-to-segment binding, and with it which responses answered the target question.
    """
    pending = [list(texts) for texts in finals_by_segment]
    turn_segment: dict[object, int] = {}
    started: dict[object, object] = {}
    cancelled: set[object] = set()
    korean = 0
    for event in events:
        kind = event["type"]
        if kind == "transcript.final":
            text = event.get("text")
            segment = next((index for index, texts in enumerate(pending) if texts and texts[0] == text), None)
            if segment is not None:
                pending[segment].pop(0)
                turn_segment[event.get("turnId")] = segment
        elif kind == "response.started":
            started.setdefault(event.get("responseId"), event.get("turnId"))
        elif kind == "response.cancelled" and event.get("responseId") in started:
            cancelled.add(event.get("responseId"))
        elif kind == "response.korean" and event.get("responseId") in started:
            korean += 1
    target = len(finals_by_segment) - 1
    bound = sum(1 for turn_id in started.values() if turn_segment.get(turn_id) == target)
    return {"target_responses": bound, "context_responses": len(started) - bound,
            "cancelled_responses": len(cancelled), "korean_events": korean}


def _outcome(line: dict) -> SessionOutcome:
    return SessionOutcome(
        status=line["status"], timings={stage: line["timingsMs"].get(stage) for stage in STAGES},
        segment_finals=line["finalsBySegment"], response=line["response"], error_codes=line["errorCodes"],
        anomalies=line["anomalies"],
        events=[(event["tMs"], {key: value for key, value in event.items() if key != "tMs"})
                for event in line["events"]],
        **response_counts(line["events"], line["finalsBySegment"]))


def rescore(source: str | Path, corpus: Corpus, item_set: ItemSet, output_dir: str | Path, *,
            log: Callable[[str], None] = print) -> tuple[dict, dict, Path]:
    """Score a finished run again with ``item_set`` and write new results and details to a new folder.

    Returns the new results, the source results and the path of the new results.json. The source is only read.
    """
    details_path, results_path = locate(source)
    before = _read_results(results_path)
    items = {item.id: item for item in item_set.items}
    selected_ids = before["items"]["selectedIds"]
    missing = sorted(set(selected_ids) - set(items))
    if missing:
        raise RescoreError(f"The items file lacks {', '.join(missing)}, which the run selected.")
    if before["corpus"]["sha256"] != corpus.sha256:
        log(f"Warning: the run used another synthetic corpus ({before['corpus']['sha256'][:12]}); expected sources "
            f"are matched by the current document titles ({corpus.sha256[:12]}).")
    records, seen = [], set()
    for number, line in _read_lines(details_path):
        problem = _line_problem(line) or _selection_problem(line, items, set(selected_ids), seen)
        if problem:
            raise RescoreError(f"{details_path.name} line {number} {problem}.")
        item = items[line["item"]]
        seen.add((item.id, line["repeat"]))
        record = score_run(item, line["accent"], line["repeat"] - 1, _outcome(line), corpus.titles)
        record.judge = line["judge"]
        records.append(record)
    if len(records) != before["runs"]["completed"]:
        raise RescoreError(f"{details_path.name} holds {len(records)} runs, but {results_path.name} reports "
                           f"{before['runs']['completed']} completed.")
    scorer = scorer_sha256()
    previous, source_scorer = before.get("rescore"), before.get("scorer")
    origin = previous if isinstance(previous, dict) else {
        "sourceRunId": details_path.parent.name, "sourceTool": before["tool"],
        "sourceItemsSha256": before["items"].get("sha256"),
        "sourceScorerSha256": source_scorer.get("sha256") if isinstance(source_scorer, dict) else None}
    selected = [items[item_id] for item_id in selected_ids]
    changed = {
        "tool": {"name": TOOL_NAME, "version": TOOL_VERSION},
        "items": {"sha256": item_set.sha256, "selected": len(selected), "selectedIds": sorted(selected_ids),
                  "byCategory": dict(sorted(Counter(item.category for item in selected).items()))},
        "scorer": {"sha256": scorer},
    }
    meta = {key: changed.get(key, before.get(key)) for key in RUN_KEYS if key in changed or key in before}
    meta["rescore"] = {**{key: origin.get(key) for key in SOURCE_KEYS}, "itemsSha256": item_set.sha256,
                       "scorerSha256": scorer,
                       "rescoredAt": dt.datetime.now(dt.timezone.utc).isoformat(timespec="seconds")}
    results = aggregate(records, meta, samples=before["statistics"]["bootstrapSamples"],
                        seed=before["statistics"]["seed"])
    name = re.sub(r"[^A-Za-z0-9._-]+", "-", str(meta["rescore"]["sourceRunId"] or "")).strip(".-")[:64] or "run"
    stamp = dt.datetime.now(dt.timezone.utc).strftime("%Y%m%dT%H%M%SZ")
    folder = safe_mkdir(Path(output_dir) / f"{name}-rescored-{stamp}-{os.urandom(3).hex()}")
    path = safe_write_text(folder / RESULTS_FILE, json.dumps(results, indent=2, ensure_ascii=False) + "\n")
    safe_write_text(folder / DETAILS_FILE, _details_lines(records))
    return results, before, path
