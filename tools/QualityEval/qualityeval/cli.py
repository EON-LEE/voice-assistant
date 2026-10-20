"""Command line: validate, synthesize, materials, run, rescore, compare and summarize."""

from __future__ import annotations

import argparse
import json
import os
import sys
from pathlib import Path

from . import TOOL_VERSION
from .azureauth import AuthError, token_provider
from .compare import Thresholds, compare, render
from .corpus import CATEGORIES, load_corpus, load_items, validate
from .httpapi import ApiError, LiveCoachApi
from .identity import IdentityError
from .judge import Judge, JudgeError, config_from_env
from .privacy import CORPUS_DIR, DEFAULT_CACHE_DIR, DEFAULT_OUTPUT_DIR, PrivacyError, check_output_path, safe_write_text
from .protocol import RESPONSE_MODES, live_ui_options
from .report import QUALITY_METRICS
from .rescore import rescore
from .runner import RunConfig, RunError, cleanup_materials, execute, identity_hash, resolve_credentials, sync_materials
from .session import Limits
from .tts import DEFAULT_ACCENTS, DEFAULT_VOICES, AzureTts, ToneAudio, TtsAudio, TtsCache, TtsError, endpoint_from_env

EXIT_OK, EXIT_FAIL, EXIT_BLOCKED = 0, 1, 2


def _csv(value: str) -> list[str]:
    return [part.strip() for part in value.split(",") if part.strip()]


def _load(args, items_path: str | None = None) -> tuple:
    corpus = load_corpus(args.corpus_dir)
    items = load_items(items_path or Path(args.corpus_dir) / "items.json")
    problems = validate(corpus, items)
    if problems:
        raise ValueError("Corpus/items validation failed:\n" + "\n".join(problems))
    return corpus, items


def _voices(path: str | None) -> dict:
    if not path:
        return DEFAULT_VOICES
    with open(path, encoding="utf-8") as handle:
        return json.load(handle)


def _api(args) -> LiveCoachApi:
    api = LiveCoachApi(args.base_url, origin=args.origin)
    if not api.loopback and not args.allow_remote:
        raise PrivacyError("The base URL is not loopback. Add --allow-remote to send synthetic audio and text to "
                           "that deployed service (billable); never point this at production with a real identity.")
    return api


def _audio(args, selected_items, accents):
    if args.audio_source == "synthetic-tone":
        return ToneAudio()
    voices = _voices(args.voices)
    missing = [accent for accent in accents if accent not in voices]
    if missing:
        raise TtsError(f"No voices configured for: {', '.join(missing)}")
    synthesizer = None
    if args.allow_tts:
        synthesizer = AzureTts(endpoint_from_env(), token_provider("EVAL_SPEECH_TOKEN"))
    return TtsAudio(TtsCache(args.cache_dir), voices, synthesizer)


def cmd_validate(args) -> int:
    corpus, items = _load(args)
    print(f"PASS: {len(corpus.documents)} synthetic documents ({corpus.sha256[:12]}), {len(items.items)} items "
          f"({items.sha256[:12]}) across {len(CATEGORIES)} categories.")
    return EXIT_OK


def cmd_synthesize(args) -> int:
    if not args.allow_tts:
        raise PrivacyError("Synthesis calls Azure AI Speech (billable). Add --allow-tts to proceed.")
    corpus, item_set = _load(args)
    items = item_set.select(_csv(args.items) if args.items else None, _csv(args.categories) if args.categories else None)
    accents = _csv(args.accents)
    audio = _audio(args, items, accents)
    clips = {(turn.text, turn.speaker, accent) for item in items for turn in item.utterances for accent in accents}
    for text, speaker, accent in sorted(clips):
        audio.clip(text, speaker, accent)
    print(f"Cached {len(clips)} clips ({audio.synthesized} newly synthesized) in {Path(args.cache_dir).resolve()}.")
    return EXIT_OK


def cmd_materials(args) -> int:
    corpus, _ = _load(args)
    api = _api(args)
    credentials = resolve_credentials(api, api.client_config())
    print(f"Identity oid sha256 {identity_hash(credentials)[:16]} ({credentials.source}).")
    if args.action == "list":
        titles = set(corpus.titles.values())
        documents = api.list_materials(credentials.bearer)
        print(f"{len(documents)} documents; {len([d for d in documents if d.get('title') in titles])} match the synthetic corpus.")
    elif args.action == "upload":
        if not args.allow_materials_upload:
            raise PrivacyError("Uploading changes the identity's materials. Add --allow-materials-upload.")
        print(json.dumps(sync_materials(api, credentials.bearer, corpus, upload=True)))
    else:
        print(f"Removed {cleanup_materials(api, credentials.bearer, corpus)} synthetic documents.")
    return EXIT_OK


def cmd_run(args) -> int:
    corpus, item_set = _load(args)
    items = item_set.select(_csv(args.items) if args.items else None, _csv(args.categories) if args.categories else None)
    accents = _csv(args.accents)
    if not accents or args.repeats < 1:
        raise ValueError("Use at least one accent and one repeat.")
    check_output_path(args.output_dir)
    api = _api(args)
    judge = None
    if args.judge:
        judge = Judge(config_from_env(), token_provider("EVAL_JUDGE_TOKEN"))
    options = live_ui_options(args.response_mode, args.reply_korean, args.topic, _csv(args.phrases) if args.phrases else [])
    config = RunConfig(
        items=items, accents=accents, options=options, output_dir=Path(args.output_dir), repeats=args.repeats,
        pace=args.pace, lead_ms=args.lead_ms, gap_ms=args.gap_ms, between_runs_s=args.between_runs_ms / 1000,
        samples=args.bootstrap, seed=args.seed, write_details=args.write_details,
        allow_materials_upload=args.allow_materials_upload, cleanup_materials=args.cleanup_materials,
        materials_settle_s=args.materials_settle_seconds, limits=Limits(run_s=args.run_timeout_seconds))
    results, path = execute(api, corpus, item_set, config, _audio(args, items, accents), judge=judge)
    print(summary(results))
    print(f"Results (aggregates only): {path}")
    return EXIT_OK if results["runs"]["complete"] else EXIT_FAIL


def summary(results: dict) -> str:
    quality, latency = results["quality"], results["latencyMs"]

    def rate(name: str) -> str:
        metric = quality[name]
        if metric["value"] is None:
            return f"{name} n/a"
        interval = f" [{metric['ci95'][0]:.2f}-{metric['ci95'][1]:.2f}]" if metric.get("ci95") else ""
        return f"{name} {metric['value']:.2f}{interval}"

    def stage(name: str) -> str:
        metric = latency[name]
        return f"{name} n={metric['n']} p50={metric['p50']} p95={metric['p95']}"

    lines = [f"Runs {results['runs']['completed']}/{results['runs']['planned']}, outcomes {results['outcomes']}",
             "Quality: " + ", ".join(rate(name) for name in ("answeredRate", "passRate", "factRecall", "forbiddenRate",
                                                             "abstainRate", "retrievalHitRate", "termAccuracy")),
             "Latency (ms): " + "; ".join(stage(name) for name in ("speechEndToFinal", "speechEndToFirstDelta",
                                                                   "speechEndToPrimaryDone", "speechEndToCompleted"))]
    if results["judge"]["enabled"]:
        lines.append(f"Judge (advisory): usefulness {results['judge']['usefulness']['value']}, "
                     f"support {results['judge']['support']['value']} (0-2)")
    if results["protocolAnomalies"]:
        lines.append(f"Protocol anomalies: {results['protocolAnomalies']}")
    return "\n".join(lines)


def cmd_rescore(args) -> int:
    check_output_path(args.output_dir)
    corpus, items = _load(args, args.items_file)
    results, before, path = rescore(args.source, corpus, items, Path(args.output_dir))

    def value(document: dict, name: str):
        return ((document.get("quality") or {}).get(name) or {}).get("value")

    print(summary(results))
    changes = [f"{name} {value(before, name)} -> {value(results, name)}" for name in QUALITY_METRICS
               if value(before, name) != value(results, name)]
    print("Changed by rescoring: " + (", ".join(changes) if changes else "no quality metric"))
    print(f"Rescored {len(results['perItem'])} items, {results['runs']['completed']} runs of "
          f"{results['rescore']['sourceRunId']} (items {items.sha256[:12]}): {path}")
    return EXIT_OK


def cmd_compare(args) -> int:
    if args.report:
        check_output_path(args.report)
    with open(args.baseline, encoding="utf-8") as handle:
        baseline = json.load(handle)
    with open(args.candidate, encoding="utf-8") as handle:
        candidate = json.load(handle)
    report = compare(baseline, candidate, Thresholds(args.max_p95_regression, args.max_p50_regression,
                                                     args.min_latency_delta_ms, args.gate_judge, args.allow_mismatch))
    print(render(report))
    if args.report:
        safe_write_text(args.report, json.dumps(report, indent=2) + "\n")
    return report["exitCode"]


def cmd_summarize(args) -> int:
    with open(args.results, encoding="utf-8") as handle:
        print(summary(json.load(handle)))
    return EXIT_OK


def parser() -> argparse.ArgumentParser:
    root = argparse.ArgumentParser(prog="quality_eval.py", description=f"Synthetic Live Coach evaluation {TOOL_VERSION}")
    root.add_argument("--corpus-dir", default=str(CORPUS_DIR), help="synthetic corpus folder (must be declared synthetic)")
    commands = root.add_subparsers(dest="command", required=True)

    commands.add_parser("validate", help="validate the synthetic corpus and items offline")

    def server(sub):
        sub.add_argument("--base-url", default=os.environ.get("EVAL_BASE_URL", ""), required=not os.environ.get("EVAL_BASE_URL"))
        sub.add_argument("--origin", help="Origin header to send when it differs from the base URL (revision labels)")
        sub.add_argument("--allow-remote", action="store_true", help="permit a non-loopback (deployed) server")
        sub.add_argument("--allow-materials-upload", action="store_true",
                         help="replace the synthetic corpus in the evaluation identity's materials")

    def audio(sub):
        sub.add_argument("--accents", default=",".join(DEFAULT_ACCENTS))
        sub.add_argument("--audio-source", choices=("azure-tts", "synthetic-tone"), default="azure-tts")
        sub.add_argument("--allow-tts", action="store_true", help="synthesize missing clips with Azure AI Speech (billable)")
        sub.add_argument("--cache-dir", default=str(DEFAULT_CACHE_DIR))
        sub.add_argument("--voices", help="JSON file mapping accent -> {A: voice, B: voice}")
        sub.add_argument("--items", help="comma-separated item ids")
        sub.add_argument("--categories", help="comma-separated categories")

    synthesize = commands.add_parser("synthesize", help="pre-fill the git-ignored TTS cache (Azure, billable)")
    audio(synthesize)

    materials = commands.add_parser("materials", help="list, upload or clean up the synthetic corpus materials")
    materials.add_argument("action", choices=("list", "upload", "cleanup"))
    server(materials)

    run = commands.add_parser("run", help="evaluate the full path through the WebSocket protocol")
    server(run)
    audio(run)
    run.add_argument("--repeats", type=int, default=3)
    run.add_argument("--response-mode", choices=RESPONSE_MODES, default="grounded")
    run.add_argument("--reply-korean", action="store_true")
    run.add_argument("--topic", default="")
    run.add_argument("--phrases", help="comma-separated Speech phrase hints (the Live UI sends none)")
    run.add_argument("--cleanup-materials", action="store_true", help="delete the synthetic corpus afterwards")
    run.add_argument("--materials-settle-seconds", type=float, default=10.0)
    run.add_argument("--output-dir", default=str(DEFAULT_OUTPUT_DIR))
    run.add_argument("--write-details", action="store_true",
                     help="also write per-run transcripts and replies (synthetic) to details.jsonl")
    run.add_argument("--judge", action="store_true", help="advisory LLM judge on a separate deployment (EVAL_JUDGE_*)")
    run.add_argument("--bootstrap", type=int, default=2000)
    run.add_argument("--seed", type=int, default=20261020)
    run.add_argument("--pace", type=float, default=1.0, help="1.0 streams audio in real time")
    run.add_argument("--lead-ms", type=int, default=400)
    run.add_argument("--gap-ms", type=int, default=700)
    run.add_argument("--between-runs-ms", type=int, default=500)
    run.add_argument("--run-timeout-seconds", type=float, default=150.0)

    rescore_parser = commands.add_parser("rescore", help="score a finished run again with the current scorer and items")
    rescore_parser.add_argument("source", help="run folder written with --write-details, or its details.jsonl")
    rescore_parser.add_argument("--items", dest="items_file",
                                help="items file to score with (default: items.json in --corpus-dir)")
    rescore_parser.add_argument("--output-dir", default=str(DEFAULT_OUTPUT_DIR),
                                help="parent of the new run folder (ignored folder or outside the repo)")

    compare_parser = commands.add_parser("compare", help="gate a candidate result against a baseline")
    compare_parser.add_argument("baseline")
    compare_parser.add_argument("candidate")
    compare_parser.add_argument("--max-p95-regression", type=float, default=0.10)
    compare_parser.add_argument("--max-p50-regression", type=float, default=0.10)
    compare_parser.add_argument("--min-latency-delta-ms", type=float, default=100.0)
    compare_parser.add_argument("--gate-judge", action="store_true", help="let advisory judge drops fail the gate")
    compare_parser.add_argument("--allow-mismatch", action="store_true", help="compare different corpora/options anyway")
    compare_parser.add_argument("--report", help="write the comparison JSON here (ignored folder or outside the repo)")

    summarize = commands.add_parser("summarize", help="print a results file")
    summarize.add_argument("results")
    return root


def main(argv: list[str] | None = None) -> int:
    args = parser().parse_args(argv)
    handlers = {"validate": cmd_validate, "synthesize": cmd_synthesize, "materials": cmd_materials, "run": cmd_run,
                "rescore": cmd_rescore, "compare": cmd_compare, "summarize": cmd_summarize}
    try:
        return handlers[args.command](args)
    except (PrivacyError, IdentityError) as error:
        print(f"BLOCKED: {error}", file=sys.stderr)
        return EXIT_BLOCKED
    except (ValueError, RunError, ApiError, TtsError, JudgeError, AuthError, OSError) as error:
        print(f"FAILED: {error}", file=sys.stderr)
        return EXIT_FAIL
