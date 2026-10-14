#!/usr/bin/env python3
"""Normalize extracted presentations, expand scenario plans, and score meeting-assistant replies."""

from __future__ import annotations

import argparse
import json
import re
import statistics
from pathlib import Path
from typing import Any

SPACE = re.compile(r"[ \t]+")
NUMBER = re.compile(r"(?<!\w)(?:[$€£])?\d+(?:[.,]\d+)*(?:\s?(?:%|million|billion|k|m|ms|s|sec|seconds?|years?))?", re.I)
NEGATION = re.compile(r"\b(?:no|not|never|cannot|can't|won't|without|unknown|unavailable)\b", re.I)
QUESTION = re.compile(r"(?:\?|^\s*(?:what|how|why|when|where|who|which|can|could|would|will|do|does|did|is|are|should|may|might)\b)", re.I)
GENERIC = re.compile(r"^(?:that(?:'s| is) (?:interesting|a good point)|i agree|good question|let(?:'s| us) discuss|could you explain more)[.!]?$", re.I)
REPO_ROOT = Path(__file__).resolve().parents[2]
PUBLISHABLE = {"public", "synthetic"}


def read_json(path: str) -> Any:
    return json.loads(Path(path).read_text(encoding="utf-8-sig"))


def ensure_publish_boundary(path: str, classification: str) -> None:
    target = Path(path).resolve()
    if classification not in PUBLISHABLE and target.is_relative_to(REPO_ROOT):
        raise PermissionError(
            "Confidential/private output must stay outside the repository. "
            "Use the session artifact directory or another private path."
        )


def write_json(path: str, value: Any, classification: str) -> None:
    ensure_publish_boundary(path, classification)
    target = Path(path)
    if target.exists():
        raise FileExistsError(f"Choose a new output path; output is not overwritten: {target}")
    target.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def clean(value: str) -> str:
    lines = [SPACE.sub(" ", line).strip() for line in value.replace("\r", "\n").splitlines()]
    return "\n".join(line for line in lines if line)


def normalize(extracted: dict[str, Any], classification: str) -> dict[str, Any]:
    slides = []
    for slide in extracted["slides"]:
        text = clean(slide.get("text", ""))
        notes = clean(slide.get("notes", ""))
        slides.append({"number": slide["number"], "text": text, "notes": notes})
    return {
        "schemaVersion": 1,
        "classification": classification,
        "sourceName": extracted["sourceName"],
        "sourceFormat": extracted["sourceFormat"],
        "slideCount": len(slides),
        "slides": slides,
        "plainText": "\n\n".join(f"[Slide {s['number']}]\n{s['text']}" for s in slides if s["text"]),
    }


def expand(plan: dict[str, Any]) -> dict[str, Any]:
    classification = plan.get("classification")
    if classification not in {"confidential", "private", *PUBLISHABLE}:
        raise ValueError("plan classification must be confidential, private, public, or synthetic")
    facts = {item["id"]: item for item in plan["facts"]}
    unknowns = {item["id"]: item for item in plan["unknowns"]}
    scenarios = []
    for item in plan["scenarios"]:
        required_ids = set(item.get("requiredFactIds", item["expectedFactIds"]))
        expected = [dict(facts[fact_id], required=fact_id in required_ids) for fact_id in item["expectedFactIds"]]
        required_abstain = set(item.get("requiredAbstainIds", item["unknownIds"]))
        must_abstain = [dict(unknowns[unknown_id], required=unknown_id in required_abstain) for unknown_id in item["unknownIds"]]
        scenarios.append(
            {
                "id": item["id"],
                "category": item["category"],
                "title": item["title"],
                "script": item["script"],
                "expectedFacts": expected,
                "answerHints": item["answerHints"],
                "mustAbstain": must_abstain,
                "preserve": item.get("preserve", {}),
                "maxFirstResponseMs": item.get("maxFirstResponseMs", plan.get("maxFirstResponseMs", 1000)),
            }
        )
    return {
        "schemaVersion": 1,
        "classification": classification,
        "name": plan["name"],
        "source": plan["source"],
        "scenarios": scenarios,
    }


def validate_suite(suite: dict[str, Any]) -> list[str]:
    errors: list[str] = []
    required_categories = {
        "executive-qa",
        "skeptical-customer",
        "technical-methodology",
        "product-value",
        "evidence-privacy",
        "rollout-next-step",
        "unknown-answer",
    }
    scenarios = suite.get("scenarios", [])
    present = {scenario.get("category") for scenario in scenarios}
    for category in sorted(required_categories - present):
        errors.append(f"missing category: {category}")
    ids: set[str] = set()
    for scenario in scenarios:
        identifier = scenario.get("id", "")
        if not identifier or identifier in ids:
            errors.append(f"invalid or duplicate scenario id: {identifier!r}")
        ids.add(identifier)
        for field in ("script", "expectedFacts", "answerHints", "mustAbstain"):
            if not scenario.get(field):
                errors.append(f"{identifier}: {field} must not be empty")
        if not any(turn.get("speaker") == "customer" and QUESTION.search(turn.get("text", "")) for turn in scenario.get("script", [])):
            errors.append(f"{identifier}: script needs a customer question")
    return errors


def preserved_tokens(scenario: dict[str, Any], response: str) -> tuple[int, int]:
    preserve = scenario.get("preserve", {})
    expected = [[str(token)] for token in preserve.get("tokens", [])]
    expected.extend([[str(token) for token in group] for group in preserve.get("anyOf", [])])
    if preserve.get("numbers", True):
        expected.extend([[token] for token in NUMBER.findall(" ".join(turn["text"] for turn in scenario["script"]))])
    expected = [group for group in expected if any(group)]
    found = sum(any(token.casefold() in response.casefold() for token in group) for group in expected)
    return found, len(expected)


def score_response(scenario: dict[str, Any], result: dict[str, Any]) -> dict[str, Any]:
    response = clean(result.get("response", ""))
    alternative = clean(result.get("alternative", ""))
    assessed = "\n".join(value for value in (response, alternative) if value)
    lower = assessed.casefold().replace("’", "'")
    required = [fact for fact in scenario["expectedFacts"] if fact.get("required", True)]
    supported = sum(
        any(term.casefold() in lower for term in fact.get("matchAny", []))
        for fact in required
    )
    unsupported = [
        item["id"]
        for item in scenario["mustAbstain"]
        if any(term.casefold() in response.casefold() for term in item.get("forbiddenClaims", []))
        and not any(term.casefold() in lower for term in item.get("abstainMatchAny", []))
    ]
    required_unknowns = [item for item in scenario["mustAbstain"] if item.get("required", True)]
    generic_abstentions = (
        "not sure", "not in the material", "isn't in the material", "aren't in the material",
        "not shown", "aren't shown", "don't see", "doesn't show", "needs verification", "need verification",
    )
    abstained = sum(
        any(term.casefold() in lower for term in item.get("abstainMatchAny", []))
        or any(term in lower for term in generic_abstentions)
        for item in required_unknowns
    )
    preserved, preserve_total = preserved_tokens(scenario, assessed)
    input_text = " ".join(turn["text"] for turn in scenario["script"])
    negation_ok = not NEGATION.search(input_text) or bool(NEGATION.search(assessed))
    question_intent_ok = not QUESTION.search(input_text) or (
        not GENERIC.fullmatch(response) and len(response.split()) >= 4
    )
    first_ms = int(result.get("firstResponseMs", 2**31 - 1))
    latency_ok = first_ms <= scenario["maxFirstResponseMs"]
    generic = bool(GENERIC.fullmatch(response)) or len(response.split()) < 4
    return {
        "scenarioId": scenario["id"],
        "supportedFacts": supported,
        "requiredFacts": len(required),
        "unsupportedClaims": unsupported,
        "abstainedUnknowns": abstained,
        "unknowns": len(required_unknowns),
        "preservedTokens": preserved,
        "preserveTokens": preserve_total,
        "negationPreserved": negation_ok,
        "questionIntentPreserved": question_intent_ok,
        "overGeneric": generic,
        "firstResponseMs": first_ms,
        "latencyPass": latency_ok,
        "pass": (
            supported == len(required)
            and not unsupported
            and abstained == len(required_unknowns)
            and preserved == preserve_total
            and negation_ok
            and question_intent_ok
            and not generic
            and latency_ok
        ),
    }


def score(suite: dict[str, Any], response_doc: dict[str, Any]) -> dict[str, Any]:
    by_id = {item["scenarioId"]: item for item in response_doc["results"]}
    rows = [score_response(scenario, by_id.get(scenario["id"], {})) for scenario in suite["scenarios"]]
    latencies = [row["firstResponseMs"] for row in rows if row["firstResponseMs"] < 2**31 - 1]
    return {
        "schemaVersion": 1,
        "suite": suite["name"],
        "passed": sum(row["pass"] for row in rows),
        "total": len(rows),
        "hallucinationCount": sum(len(row["unsupportedClaims"]) for row in rows),
        "overGenericCount": sum(row["overGeneric"] for row in rows),
        "latencyPassRate": sum(row["latencyPass"] for row in rows) / len(rows) if rows else 0,
        "medianFirstResponseMs": statistics.median(latencies) if latencies else None,
        "results": rows,
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    commands = parser.add_subparsers(dest="command", required=True)
    normalize_parser = commands.add_parser("normalize")
    normalize_parser.add_argument("--input", required=True)
    normalize_parser.add_argument("--output", required=True)
    normalize_parser.add_argument(
        "--classification", required=True, choices=["confidential", "private", "public", "synthetic"]
    )
    generate_parser = commands.add_parser("generate")
    generate_parser.add_argument("--plan", required=True)
    generate_parser.add_argument("--output", required=True)
    validate_parser = commands.add_parser("validate")
    validate_parser.add_argument("--scenarios", required=True)
    score_parser = commands.add_parser("score")
    score_parser.add_argument("--scenarios", required=True)
    score_parser.add_argument("--responses", required=True)
    score_parser.add_argument("--output", required=True)
    args = parser.parse_args()

    if args.command == "normalize":
        write_json(args.output, normalize(read_json(args.input), args.classification), args.classification)
    elif args.command == "generate":
        suite = expand(read_json(args.plan))
        errors = validate_suite(suite)
        if errors:
            raise ValueError("\n".join(errors))
        write_json(args.output, suite, suite["classification"])
    elif args.command == "validate":
        errors = validate_suite(read_json(args.scenarios))
        if errors:
            print("\n".join(errors))
            return 1
        print("PASS: scenario suite is complete and valid.")
    elif args.command == "score":
        suite = read_json(args.scenarios)
        write_json(args.output, score(suite, read_json(args.responses)), suite["classification"])
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
