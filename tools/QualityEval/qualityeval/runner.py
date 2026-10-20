"""Plan and execute an evaluation run against a Live Coach server, then write aggregate results."""

from __future__ import annotations

import asyncio
import datetime as dt
import hashlib
import json
import os
import time
from collections import Counter
from dataclasses import dataclass, field
from pathlib import Path
from typing import Callable, Mapping

from . import SCHEMA_VERSION, TOOL_NAME, TOOL_VERSION
from .audio import build_timeline
from .azureauth import AuthError
from .corpus import Corpus, Item, ItemSet
from .httpapi import ApiError, LiveCoachApi
from .identity import FAKE_OWNER, IdentityError, guard, object_id
from .judge import Judge, JudgeError
from .privacy import safe_mkdir, safe_write_text
from .report import RunRecord, aggregate
from .scoring import score_answer, term_results
from .session import Limits, run_session
from .wsclient import HandshakeError, WebSocketClient

ABORT_AFTER_FAILURES = 3


class RunError(RuntimeError):
    pass


@dataclass
class RunConfig:
    items: list[Item]
    accents: list[str]
    options: dict
    output_dir: Path
    repeats: int = 3
    pace: float = 1.0
    lead_ms: int = 400
    gap_ms: int = 700
    between_runs_s: float = 0.5
    samples: int = 2000
    seed: int = 20261020
    write_details: bool = False
    allow_materials_upload: bool = False
    cleanup_materials: bool = False
    materials_settle_s: float = 10.0
    limits: Limits = field(default_factory=Limits)


@dataclass
class Credentials:
    bearer: str | None
    source: str


def plan(items: list[Item], accents: list[str], repeats: int) -> list[tuple[Item, str, int]]:
    """Every repeat rotates the accent per item, so with 3 accents and 3 repeats each item is heard in each accent."""
    return [(item, accents[(index + repeat) % len(accents)], repeat)
            for repeat in range(repeats) for index, item in enumerate(items)]


def resolve_credentials(api: LiveCoachApi, config: dict, environ: Mapping[str, str] = os.environ) -> Credentials:
    if config["mode"] == "Fake":
        return Credentials(None, "fake")
    bearer = environ.get("EVAL_BEARER_TOKEN", "").strip()
    if bearer:
        return Credentials(bearer, "bearer")
    username, password = environ.get("EVAL_DEMO_USERNAME", ""), environ.get("EVAL_DEMO_PASSWORD", "")
    if config.get("login") == "demo" and username and password:
        return Credentials(api.demo_login(username, password), "demo")
    raise RunError("Set EVAL_BEARER_TOKEN (a delegated Meeting.Access token of the synthetic evaluation identity) or, "
                   "on a demo-login server, EVAL_DEMO_USERNAME and EVAL_DEMO_PASSWORD.")


def identity_hash(credentials: Credentials, environ: Mapping[str, str] = os.environ) -> str:
    oid = FAKE_OWNER if credentials.bearer is None else object_id(credentials.bearer)
    if oid is None:
        raise IdentityError("The token carries no oid claim; cannot verify the evaluation identity.")
    return guard(oid, environ)


def sync_materials(api: LiveCoachApi, bearer: str | None, corpus: Corpus, *, upload: bool) -> dict:
    """Replace the corpus documents (matched by exact title) in the identity's materials, or verify they exist."""
    titles = set(corpus.titles.values())
    existing = api.list_materials(bearer)
    if not upload:
        present = {document.get("title") for document in existing}
        return {"uploaded": False, "documents": len(titles & present), "verified": titles <= present}
    replaced = 0
    for document in existing:
        if document.get("title") in titles:
            api.delete_material(bearer, str(document["id"]))
            replaced += 1
    for document in corpus.documents:
        api.upload_material(bearer, document.title, document.text)
    present = {document.get("title") for document in api.list_materials(bearer)}
    return {"uploaded": True, "documents": len(corpus.documents), "replaced": replaced, "verified": titles <= present}


def cleanup_materials(api: LiveCoachApi, bearer: str | None, corpus: Corpus) -> int:
    """Delete only documents whose titles exactly match the synthetic corpus titles."""
    titles = set(corpus.titles.values())
    removed = 0
    for document in api.list_materials(bearer):
        if document.get("title") in titles:
            api.delete_material(bearer, str(document["id"]))
            removed += 1
    return removed


def _short_hash(value: str) -> str:
    return hashlib.sha256(value.encode("utf-8")).hexdigest()[:16]


def _progress(number: int, total: int, record: RunRecord) -> str:
    timings = record.outcome.timings
    first = timings.get("speechEndToFirstDelta")
    done = timings.get("speechEndToCompleted")
    verdict = "pass" if record.score.passed else ("fail" if record.outcome.answered else "-")
    latency = f"first {first:.0f} ms, done {done:.0f} ms" if first is not None and done is not None else "no timing"
    return (f"[{number}/{total}] {record.item.id} {record.accent} r{record.repeat + 1}: {record.outcome.status}, "
            f"{latency}, {verdict}")


async def _execute(api: LiveCoachApi, credentials: Credentials, config: RunConfig, planned, timelines: Mapping,
                   titles: Mapping[str, str], log: Callable[[str], None]) -> list[RunRecord]:
    records: list[RunRecord] = []
    failures = 0

    async def connect() -> WebSocketClient:
        ticket = None
        if credentials.bearer is not None:
            try:
                ticket = await asyncio.to_thread(api.ticket, credentials.bearer)
            except ApiError as error:
                raise HandshakeError(error.status) from error
        return await WebSocketClient.connect(api.websocket_url(ticket), origin=api.origin)

    for number, (item, accent, repeat) in enumerate(planned, 1):
        outcome = await run_session(connect, timelines[item.id, accent], config.options, pace=config.pace,
                                    limits=config.limits, details=config.write_details)
        response = outcome.response or {}
        score = score_answer(item, response.get("primary", ""), response.get("alternative", ""),
                             response.get("grounding"), response.get("sources", []),
                             [titles[source] for source in item.expected_sources if source in titles],
                             answered=outcome.answered)
        record = RunRecord(item, accent, repeat, outcome, score, term_results(item, outcome.segment_finals))
        records.append(record)
        log(_progress(number, len(planned), record))
        broken = outcome.status.startswith(("handshake_", "start_error", "transport_error"))
        failures = failures + 1 if broken else 0
        if failures >= ABORT_AFTER_FAILURES:
            log("Stopping: the server rejected or dropped several consecutive sessions.")
            break
        if number < len(planned):
            await asyncio.sleep(config.between_runs_s)
    return records


def _details_lines(records: list[RunRecord]) -> str:
    lines = []
    for record in records:
        outcome = record.outcome
        lines.append(json.dumps({
            "item": record.item.id, "category": record.item.category, "accent": record.accent,
            "repeat": record.repeat + 1, "status": outcome.status, "timingsMs": outcome.timings,
            "finalsBySegment": outcome.segment_finals, "response": outcome.response, "score": record.score.as_dict(),
            "terms": record.terms, "judge": record.judge, "errorCodes": outcome.error_codes,
            "anomalies": outcome.anomalies, "events": [{"tMs": t, **event} for t, event in outcome.events],
        }, ensure_ascii=False))
    return "\n".join(lines) + "\n"


def execute(api: LiveCoachApi, corpus: Corpus, item_set: ItemSet, config: RunConfig, audio, *,
            judge: Judge | None = None, log: Callable[[str], None] = print,
            environ: Mapping[str, str] = os.environ) -> tuple[dict, Path]:
    client_config = api.client_config()
    if client_config["mode"] == "Azure" and getattr(audio, "name", "") == "synthetic-tone":
        raise RunError("synthetic-tone audio carries no words; use it only against a local Fake-mode API.")
    credentials = resolve_credentials(api, client_config, environ)
    oid_hash = identity_hash(credentials, environ)
    log(f"Server mode {client_config['mode']}, login {credentials.source}; identity oid sha256 {oid_hash[:16]}")
    planned = plan(config.items, config.accents, config.repeats)
    timelines = {}
    for item, accent, _ in planned:
        if (item.id, accent) not in timelines:
            utterances = [(turn.speaker, turn.text, audio.clip(turn.text, turn.speaker, accent)) for turn in item.utterances]
            timelines[item.id, accent] = build_timeline(utterances, lead_ms=config.lead_ms, gap_ms=config.gap_ms)
    if config.allow_materials_upload:
        log(f"Uploading the synthetic corpus ({len(corpus.documents)} documents) to the identity above.")
    materials = sync_materials(api, credentials.bearer, corpus, upload=config.allow_materials_upload)
    if not materials["verified"] and any(item.materials_required for item in config.items):
        log("Warning: the synthetic corpus is not present in this identity's materials; grounded items will fail. "
            "Use --allow-materials-upload.")
    if materials["uploaded"] and config.materials_settle_s > 0:
        time.sleep(config.materials_settle_s)
    try:
        records = asyncio.run(_execute(api, credentials, config, planned, timelines, corpus.titles, log))
    finally:
        if config.cleanup_materials:
            log(f"Removed {cleanup_materials(api, credentials.bearer, corpus)} synthetic documents from materials.")
    if judge is not None:
        for record in records:
            if record.outcome.answered and record.outcome.response:
                try:
                    record.judge = judge.grade(record.item, corpus, record.outcome.response["primary"],
                                               record.outcome.response["alternative"])
                except (JudgeError, AuthError, OSError) as error:
                    record.judge = {"error": type(error).__name__}
    meta = {
        "schemaVersion": SCHEMA_VERSION, "tool": {"name": TOOL_NAME, "version": TOOL_VERSION},
        "classification": "synthetic", "createdAt": dt.datetime.now(dt.timezone.utc).isoformat(timespec="seconds"),
        "target": {"mode": client_config["mode"], "login": credentials.source, "originSha256": _short_hash(api.origin),
                   "loopback": api.loopback},
        "identity": {"oidSha256Prefix": oid_hash[:16]},
        "corpus": {"name": corpus.name, "sha256": corpus.sha256, "documents": len(corpus.documents)},
        "items": {"sha256": item_set.sha256, "selected": len(config.items),
                  "selectedIds": sorted(item.id for item in config.items),
                  "byCategory": dict(sorted(Counter(item.category for item in config.items).items()))},
        "session": {"protocolVersion": 1, "options": config.options, "pace": config.pace},
        "audio": {"source": getattr(audio, "name", "unknown"), "accents": list(config.accents),
                  "voices": {accent: dict(audio.voices[accent]) for accent in config.accents}
                  if hasattr(audio, "voices") else None,
                  "leadMs": config.lead_ms, "gapMs": config.gap_ms, "frameMs": 20},
        "materials": materials,
        "runs": {"planned": len(planned), "completed": len(records), "repeats": config.repeats,
                 "complete": len(records) == len(planned)},
        "statistics": {"bootstrapSamples": config.samples, "seed": config.seed, "ciLevel": 0.95, "clusters": "item",
                       "p50MinSamples": 5, "p95MinSamples": 20,
                       "latencyAnchor": "send time of the 20 ms frame carrying the target question's last speech sample"},
    }
    results = aggregate(records, meta, samples=config.samples, seed=config.seed)
    stamp = dt.datetime.now(dt.timezone.utc).strftime("run-%Y%m%dT%H%M%SZ")
    run_dir = safe_mkdir(config.output_dir / f"{stamp}-{os.urandom(3).hex()}")
    results_path = safe_write_text(run_dir / "results.json", json.dumps(results, indent=2, ensure_ascii=False) + "\n")
    if config.write_details:
        safe_write_text(run_dir / "details.jsonl", _details_lines(records))
    return results, results_path
