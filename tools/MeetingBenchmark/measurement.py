"""Content-free measurement with explicit question selection and one monotonic clock."""
from __future__ import annotations

import hashlib
import json
import math
import re
import statistics
from collections import Counter

MIN_P50 = 5
MIN_P95 = 20
PROFILE_KEYS = {"inputSha256", "contextSha256", "mode", "capture", "model", "rankConfiguration", "warmState"}
EVENT_TYPES = {
    "question.marker", "clip.end", "speech.end", "transcript.partial", "transcript.final",
    "response.started", "response.delta", "response.completed", "response.cancelled", "error", "run.end",
}
ABBREVIATIONS = {
    "mr", "mrs", "ms", "dr", "prof", "sr", "jr", "st", "vs", "etc", "e.g", "i.e",
    "u.s", "u.k", "a.m", "p.m", "no", "fig", "inc", "ltd", "dept", "approx",
}
WORDS = re.compile(r"[^\W_]+(?:['\u2019][^\W_]+)*", re.UNICODE)


def finite(value, field: str) -> float:
    if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value):
        raise ValueError(f"{field} must be finite numeric milliseconds")
    return float(value)


def identifier(value, name: str) -> str:
    if not isinstance(value, str) or not value or len(value) > 200:
        raise ValueError(f"Invalid {name}")
    return value


def cohort_key(profile: dict) -> str:
    if not isinstance(profile, dict) or set(profile) != PROFILE_KEYS:
        raise ValueError("Profile must specify exactly the documented comparison fields")
    for name in ("inputSha256", "contextSha256"):
        if not isinstance(profile[name], str) or not re.fullmatch(r"[a-f0-9]{64}", profile[name]):
            raise ValueError(f"Invalid {name}")
    for name in ("mode", "capture", "model"):
        identifier(profile[name], name)
    if profile["mode"] not in ("Azure", "Fake"):
        raise ValueError("Provider mode must be explicit Azure or Fake")
    if profile["warmState"] not in ("warm", "cold", "unknown"):
        raise ValueError("Warm state must be warm, cold or unknown; never infer it")
    if not isinstance(profile["rankConfiguration"], dict) or not profile["rankConfiguration"]:
        raise ValueError("Explicit rank configuration required")
    encoded = json.dumps(profile, sort_keys=True, separators=(",", ":"), allow_nan=False)
    return hashlib.sha256(encoded.encode()).hexdigest()


def first_sentence_end(text: str, completed: bool = False) -> int | None:
    """Conservative English sentence boundary, not a semantic answer-quality score."""
    for match in re.finditer(r"[.!?]", text):
        position = match.start()
        char = text[position]
        if char == ".":
            if (position and text[position - 1] == ".") or (position + 1 < len(text) and text[position + 1] == "."):
                continue
            if position and text[position - 1].isdigit() and position + 1 < len(text) and text[position + 1].isdigit():
                continue
            token_match = re.search(r"([A-Za-z.]+|\d+)$", text[:position])
            token = token_match.group(1) if token_match else ""
            if token.lower() in ABBREVIATIONS or re.fullmatch(r"[A-Za-z]", token) or token.isdigit():
                continue
        end = position + 1
        while end < len(text) and text[end] in "\"'\u201d\u2019)]":
            end += 1
        suffix = text[end:]
        # At a streaming buffer edge, punctuation might be an unfinished decimal/abbreviation.
        if not suffix.strip():
            if not completed:
                continue
        elif not suffix[0].isspace() or not re.match(r"\s+[\"'(\u201c]*[A-Za-z]", suffix):
            continue
        words = WORDS.findall(text[:end])
        if len(words) >= 2 or (len(words) == 1 and words[0].lower() in {"yes", "no", "thanks", "agreed", "okay"}):
            return end
    return None


def word_error_rate(reference: str, actual: str) -> dict:
    expected = [w.lower().replace("\u2019", "'") for w in WORDS.findall(reference)]
    observed = [w.lower().replace("\u2019", "'") for w in WORDS.findall(actual)]
    if not expected:
        raise ValueError("Human reference must contain words")
    previous = list(range(len(observed) + 1))
    for i, left in enumerate(expected, 1):
        row = [i]
        for j, right in enumerate(observed, 1):
            row.append(min(row[-1] + 1, previous[j] + 1, previous[j - 1] + (left != right)))
        previous = row
    return {"status": "measured", "wordErrors": previous[-1], "referenceWords": len(expected),
            "wordErrorRate": previous[-1] / len(expected), "scope": "selected-final"}


class EventCollector:
    """Adapter for browser observations; caller supplies the SAME browser performance.now clock."""
    def __init__(self, run_id: str, question_id: str, variant: str, profile: dict):
        identifier(run_id, "runId")
        identifier(question_id, "questionId")
        if variant not in ("baseline", "optimized"):
            raise ValueError("Unknown variant")
        cohort_key(profile)
        self.run = {"schemaVersion": 1, "runId": run_id, "questionId": question_id, "variant": variant,
                    "profile": profile, "reference": {"humanVerified": False, "text": None},
                    "events": []}

    def add(self, event: dict) -> None:
        if len(self.run["events"]) >= 50000:
            raise ValueError("Bounded event limit exceeded")
        timestamp = finite(event.get("atMs"), "atMs")
        if timestamp < 0 or (self.run["events"] and timestamp < self.run["events"][-1]["atMs"]):
            raise ValueError("Events must be monotonic within a run; sorting cannot repair mixed clocks")
        if event.get("type") not in EVENT_TYPES:
            raise ValueError("Unknown event type")
        if self.run["events"] and self.run["events"][-1]["type"] == "run.end":
            raise ValueError("No events permitted after run.end")
        self.run["events"].append(dict(event))


def analyze_run(run: dict) -> dict:
    if run.get("schemaVersion") != 1:
        raise ValueError("Unsupported event schema")
    collector = EventCollector(run.get("runId"), run.get("questionId"), run.get("variant"), run.get("profile"))
    events = run.get("events")
    if not isinstance(events, list):
        raise ValueError("Events must be a list")
    for event in events:
        collector.add(event)
    result = {
        "runId": run["runId"], "questionId": run["questionId"], "variant": run["variant"],
        "cohort": cohort_key(run["profile"]), "outcome": "unknown", "issues": [],
        "eventCount": len(events), "cancelledResponses": sum(e["type"] == "response.cancelled" for e in events),
        "errorEvents": sum(e["type"] == "error" for e in events), "ignoredStaleEvents": 0,
        "latencyMs": {}, "accuracy": {"status": "unknown", "reason": "No verified selected-final reference"},
        "clipBoundary": {"observed": False}, "speechEnd": {"observed": False},
    }
    markers = [e for e in events if e["type"] == "question.marker" and e.get("questionId") == run["questionId"]]
    endings = [e for e in events if e["type"] == "run.end"]
    if not endings:
        result["issues"].append("missing-run-end")
    elif endings[0].get("status") not in ("success", "failed", "cancelled"):
        raise ValueError("Invalid run.end status")
    if len(markers) != 1:
        result["issues"].append("missing-or-ambiguous-question-marker")
        result["outcome"] = endings[0]["status"] if endings and endings[0]["status"] != "success" else "unknown"
        return result
    marker = markers[0]
    turn = identifier(marker.get("turnId"), "marker.turnId")
    response_id = identifier(marker.get("responseId"), "marker.responseId")
    revision = marker.get("finalRevision")
    if isinstance(revision, bool) or not isinstance(revision, int) or revision < 1:
        raise ValueError("Explicit question finalRevision required")
    if not isinstance(marker.get("selectionProvenance"), str) or not marker["selectionProvenance"].strip():
        raise ValueError("Explicit question-selection provenance is required; never select last final implicitly")
    selected_final = None
    partial = None
    revisions = {}
    response_started = False
    response_terminal = None
    text = ""
    first_text = first_sentence = completion = None
    boundaries = {}
    for event in events:
        kind, timestamp = event["type"], event["atMs"]
        if kind in ("clip.end", "speech.end") and event.get("questionId") == run["questionId"]:
            if kind in boundaries:
                raise ValueError("Ambiguous repeated timing boundary")
            if kind == "speech.end" and (event.get("annotationKind") != "human-annotated" or not event.get("provenance")):
                raise ValueError("speech.end requires human annotation provenance")
            if kind == "clip.end" and not event.get("provenance"):
                raise ValueError("clip.end must describe observation/IPC timing provenance")
            boundaries[kind] = timestamp
            result["clipBoundary" if kind == "clip.end" else "speechEnd"] = {
                "observed": True, "provenance": event["provenance"],
                "isAcousticEnd": kind == "speech.end",
            }
        if kind in ("transcript.partial", "transcript.final"):
            event_turn = identifier(event.get("turnId"), "turnId")
            rev = event.get("revision")
            if isinstance(rev, bool) or not isinstance(rev, int) or rev < 1:
                raise ValueError("Transcript revision must be a positive integer")
            if not isinstance(event.get("text"), str):
                raise ValueError("Transcript text must be a string")
            if rev <= revisions.get(event_turn, 0):
                result["ignoredStaleEvents"] += 1
                continue
            revisions[event_turn] = rev
            if event_turn == turn:
                if kind == "transcript.partial" and partial is None and event["text"].strip():
                    partial = timestamp
                if kind == "transcript.final" and rev == revision:
                    selected_final = event
        if kind.startswith("response.") and event.get("responseId") == response_id:
            if event.get("turnId") != turn:
                raise ValueError("Selected response ID is attached to a different turn")
            if response_terminal is not None:
                result["ignoredStaleEvents"] += 1
                continue
            if kind == "response.started":
                if response_started:
                    raise ValueError("Duplicate selected response start")
                response_started = True
                result["responseStartedAtMs"] = timestamp
                continue
            if not response_started:
                raise ValueError("Selected response data before response.started")
            if kind == "response.cancelled":
                response_terminal = "cancelled"
                continue
            if not isinstance(event.get("text"), str):
                raise ValueError("Response text must be a string")
            if kind == "response.delta":
                text += event["text"]
            elif kind == "response.completed":
                text = event["text"]
                completion = timestamp
                response_terminal = "completed"
            if first_text is None and text.strip():
                first_text = timestamp
            if first_sentence is None and first_sentence_end(text, kind == "response.completed") is not None:
                first_sentence = timestamp
    if selected_final is None:
        result["issues"].append("selected-question-final-not-found")
    if response_terminal != "completed":
        result["issues"].append("selected-response-not-completed")
    if response_terminal == "completed" and not text.strip():
        result["issues"].append("empty-selected-response")
    if selected_final and result.get("responseStartedAtMs", selected_final["atMs"]) < selected_final["atMs"]:
        raise ValueError("Selected response starts before its explicit finalized question")
    anchors = {"clipBoundary": boundaries.get("clip.end"), "annotatedSpeechEnd": boundaries.get("speech.end"),
               "selectedFinal": selected_final["atMs"] if selected_final else None}
    targets = {"firstText": first_text, "firstReadableSentence": first_sentence, "completed": completion,
               "firstSttPartial": partial, "selectedSttFinal": anchors["selectedFinal"]}
    for anchor_name, anchor in anchors.items():
        for target_name, target in targets.items():
            result["latencyMs"][f"{anchor_name}To{target_name[0].upper() + target_name[1:]}"] = (
                None if anchor is None or target is None else target - anchor
            )
    if endings and endings[0]["status"] in ("failed", "cancelled"):
        result["outcome"] = endings[0]["status"]
    elif result["errorEvents"]:
        result["outcome"] = "failed"
    elif response_terminal == "cancelled":
        result["outcome"] = "cancelled"
    elif not result["issues"] and endings and response_terminal == "completed":
        result["outcome"] = "success"
    reference = run.get("reference") or {}
    if reference.get("humanVerified") is True and reference.get("scope") == "selected-final" and reference.get("provenance") and selected_final:
        if not isinstance(reference.get("text"), str):
            raise ValueError("Verified reference requires explicit text")
        result["accuracy"] = word_error_rate(reference["text"], selected_final["text"])
    # Absolute timestamps and all transcript/response/reference text remain operator-local.
    result.pop("responseStartedAtMs", None)
    return result


def distribution(values: list[float]) -> dict:
    ordered = sorted(finite(v, "metric") for v in values)
    n = len(ordered)
    return {
        "samples": n, "minimum": ordered[0] if n else None, "maximum": ordered[-1] if n else None,
        "p50": statistics.median(ordered) if n >= MIN_P50 else None,
        "p95": ordered[math.ceil(0.95 * n) - 1] if n >= MIN_P95 else None,
        "minimumSamplesForP50": MIN_P50, "minimumSamplesForP95": MIN_P95,
        "percentileMethod": "median for p50; nearest rank for p95; descriptive only, not SLA",
    }


def summarize(runs: list[dict]) -> dict:
    if not runs:
        raise ValueError("No runs supplied")
    fingerprints = {cohort_key(r["profile"]) for r in runs}
    if len(fingerprints) != 1:
        raise ValueError("Mixed cohorts: input/context/provider/capture/model/ranker/warm-state must match")
    if any(r.get("variant") not in ("baseline", "optimized") for r in runs):
        raise ValueError("Every attempted run requires baseline or optimized variant")
    if len({identifier(r.get("questionId"), "questionId") for r in runs}) != 1:
        raise ValueError("Different questions must be summarized in separate cohorts")
    ids = [identifier(r.get("runId"), "runId") for r in runs]
    if len(set(ids)) != len(ids):
        raise ValueError("Duplicate run IDs would inflate the sample count")
    analyses = []
    for run in runs:
        try:
            analyses.append(analyze_run(run))
        except (ValueError, TypeError, KeyError) as exc:
            analyses.append({"runId": run["runId"], "variant": run.get("variant"), "outcome": "invalid",
                             "issues": ["invalid-event-stream"], "latencyMs": {}, "accuracy": {"status": "unknown"}})
    cohorts = {}
    for variant in ("baseline", "optimized"):
        selected = [a for a in analyses if a["variant"] == variant]
        successful = [a for a in selected if a["outcome"] == "success"]
        metrics = sorted({name for a in selected for name in a["latencyMs"]})
        cohorts[variant] = {
            "attempts": len(selected), "outcomes": dict(Counter(a["outcome"] for a in selected)),
            "successRate": len(successful) / len(selected) if selected else None,
            "metrics": {name: {**distribution([a["latencyMs"][name] for a in successful if a["latencyMs"].get(name) is not None]),
                                "allAttemptDenominator": len(selected),
                                "successfulAttemptDenominator": len(successful)} for name in metrics},
        }
    return {"schemaVersion": 1, "cohort": next(iter(fingerprints)), "attempts": len(runs),
            "variants": cohorts, "runs": analyses, "latencyTargetAchieved": None,
            "qualityOrAccuracyPassed": None, "comparisonPolicy": "Variant settings may differ; every profile field must match. Cancelled/failed/invalid attempts remain in denominators."}
