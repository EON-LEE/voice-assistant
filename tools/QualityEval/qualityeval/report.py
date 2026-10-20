"""Aggregate run records into the results document: numbers only, no audio, transcripts or reply text."""

from __future__ import annotations

import statistics
from collections import Counter, defaultdict
from dataclasses import dataclass

from .corpus import Item
from .scoring import AnswerScore
from .session import STAGES, SessionOutcome
from .stats import P50_MIN_SAMPLES, latency_summary, rate_summary

QUALITY_METRICS = ("answeredRate", "passRate", "factRecall", "factRecallPrimary", "forbiddenRate", "abstainRate",
                   "formatOkRate", "genericRate", "retrievalHitRate", "termAccuracy")


@dataclass
class RunRecord:
    item: Item
    accent: str
    repeat: int
    outcome: SessionOutcome
    score: AnswerScore
    terms: list[tuple[str, bool]]
    judge: dict | None = None


def _median(values: list[float]) -> float | None:
    return round(statistics.median(values), 1) if values else None


def _p50(values: list[float]) -> float | None:
    return round(statistics.median(values), 1) if len(values) >= P50_MIN_SAMPLES else None


def _mean_of_means(groups: dict[str, list[float]]) -> float | None:
    means = [statistics.fmean(values) for values in groups.values() if values]
    return round(statistics.fmean(means), 4) if means else None


def _collect(records: list[RunRecord]) -> tuple[dict, dict, dict]:
    latency: dict[str, dict[str, list[float]]] = {stage: defaultdict(list) for stage in STAGES}
    rates: dict[str, dict[str, list[float]]] = {metric: defaultdict(list) for metric in QUALITY_METRICS}
    judge: dict[str, dict[str, list[float]]] = {"usefulness": defaultdict(list), "support": defaultdict(list)}
    for record in records:
        item_id, score, answered = record.item.id, record.score, record.outcome.answered
        rates["answeredRate"][item_id].append(float(answered))
        rates["passRate"][item_id].append(float(answered and score.passed))
        if record.item.required_facts:
            rates["factRecall"][item_id].append(score.facts_found / score.facts_required)
            rates["factRecallPrimary"][item_id].append(score.facts_found_primary / score.facts_required)
        for term, recognized in record.terms:
            rates["termAccuracy"][item_id].append(float(recognized))
        if not answered:
            continue
        for stage in STAGES:
            value = record.outcome.timings.get(stage)
            if value is not None:
                latency[stage][item_id].append(value)
        rates["forbiddenRate"][item_id].append(float(bool(score.forbidden_hits)))
        rates["formatOkRate"][item_id].append(float(score.format_ok))
        rates["genericRate"][item_id].append(float(score.generic))
        if record.item.abstain_required:
            rates["abstainRate"][item_id].append(float(bool(score.abstain_ok)))
        if record.item.materials_required:
            rates["retrievalHitRate"][item_id].append(float(bool(score.retrieval_hit)))
        if record.judge and "usefulness" in record.judge:
            judge["usefulness"][item_id].append(float(record.judge["usefulness"]))
            judge["support"][item_id].append(float(record.judge["support"]))
    return latency, rates, judge


def _breakdown(records: list[RunRecord], key) -> dict:
    groups: dict[str, list[RunRecord]] = defaultdict(list)
    for record in records:
        groups[key(record)].append(record)
    result = {}
    for name in sorted(groups):
        group = groups[name]
        latency, rates, _ = _collect(group)
        terms = [recognized for record in group for _, recognized in record.terms]
        result[name] = {
            "items": len({record.item.id for record in group}), "runs": len(group),
            "answeredRate": _mean_of_means(rates["answeredRate"]), "passRate": _mean_of_means(rates["passRate"]),
            "factRecall": _mean_of_means(rates["factRecall"]),
            "termAccuracyMicro": round(sum(terms) / len(terms), 4) if terms else None,
            "speechEndToFinalP50": _p50([v for values in latency["speechEndToFinal"].values() for v in values]),
            "speechEndToFirstDeltaP50": _p50([v for values in latency["speechEndToFirstDelta"].values() for v in values]),
            "speechEndToCompletedP50": _p50([v for values in latency["speechEndToCompleted"].values() for v in values]),
        }
    return result


def aggregate(records: list[RunRecord], meta: dict, *, samples: int, seed: int) -> dict:
    latency, rates, judge = _collect(records)
    order: list[Item] = []
    for record in records:
        if record.item not in order:
            order.append(record.item)
    statuses = Counter(record.outcome.status for record in records)
    answered = [record for record in records if record.outcome.answered]
    term_counts: dict[str, list[int]] = defaultdict(lambda: [0, 0])
    for record in records:
        for term, recognized in record.terms:
            term_counts[term][0] += int(recognized)
            term_counts[term][1] += 1
    all_terms = [recognized for record in records for _, recognized in record.terms]
    quality = {metric: rate_summary(rates[metric], samples=samples, seed=seed + index)
               for index, metric in enumerate(QUALITY_METRICS)}
    quality["termAccuracy"]["micro"] = round(sum(all_terms) / len(all_terms), 4) if all_terms else None
    quality["termAccuracy"]["byTerm"] = {term: {"recognized": hits, "total": total}
                                         for term, (hits, total) in sorted(term_counts.items())}
    judged = sum(1 for record in records if record.judge and "usefulness" in record.judge)
    judge_failed = sum(1 for record in records if record.judge and "error" in record.judge)
    per_item = []
    for item in order:
        runs = [record for record in records if record.item.id == item.id]
        ok_runs = [record for record in runs if record.outcome.answered]
        per_item.append({
            "id": item.id, "category": item.category, "runs": len(runs), "answered": len(ok_runs),
            "passRate": round(sum(record.score.passed for record in ok_runs) / len(runs), 4),
            "factRecall": round(statistics.fmean(rates["factRecall"][item.id]), 4) if rates["factRecall"][item.id] else None,
            "forbiddenRuns": sum(1 for record in ok_runs if record.score.forbidden_hits),
            "abstainRate": round(statistics.fmean(rates["abstainRate"][item.id]), 4) if rates["abstainRate"][item.id] else None,
            "retrievalHitRate": round(statistics.fmean(rates["retrievalHitRate"][item.id]), 4)
            if rates["retrievalHitRate"][item.id] else None,
            "termAccuracy": round(statistics.fmean(rates["termAccuracy"][item.id]), 4) if rates["termAccuracy"][item.id] else None,
            "speechEndToFirstDeltaMedianMs": _median(latency["speechEndToFirstDelta"][item.id]),
            "speechEndToCompletedMedianMs": _median(latency["speechEndToCompleted"][item.id]),
            "judgeUsefulness": round(statistics.fmean(judge["usefulness"][item.id]), 3) if judge["usefulness"][item.id] else None,
            "judgeSupport": round(statistics.fmean(judge["support"][item.id]), 3) if judge["support"][item.id] else None,
            "statuses": dict(Counter(record.outcome.status for record in runs)),
        })
    results = dict(meta)
    results.update({
        "outcomes": dict(sorted(statuses.items())),
        "errorCodes": dict(sorted(Counter(code for record in records for code in record.outcome.error_codes).items())),
        "protocolAnomalies": dict(sorted(Counter(kind for record in records for kind in record.outcome.anomalies).items())),
        "responses": {
            "targetResponses": sum(record.outcome.target_responses for record in records),
            "contextResponses": sum(record.outcome.context_responses for record in records),
            "cancelledResponses": sum(record.outcome.cancelled_responses for record in records),
            "primaryDoneSeen": sum(1 for record in answered if record.outcome.response and record.outcome.response["primaryDoneSeen"]),
            "streamMatchesPrimary": sum(1 for record in answered
                                        if record.outcome.response and record.outcome.response["streamMatchesPrimary"]),
            "koreanEvents": sum(record.outcome.korean_events for record in records),
            "routes": dict(Counter(str(record.outcome.response["route"]) for record in answered if record.outcome.response)),
            "grounding": dict(Counter(str(record.outcome.response["grounding"]) for record in answered if record.outcome.response)),
            "prefetched": sum(1 for record in answered if record.outcome.response and record.outcome.response["prefetched"]),
        },
        "latencyMs": {stage: latency_summary(latency[stage], samples=samples, seed=seed + 100 + index)
                      for index, stage in enumerate(STAGES)},
        "quality": quality,
        "byCategory": _breakdown(records, lambda record: record.item.category),
        "byAccent": _breakdown(records, lambda record: record.accent),
        "perItem": per_item,
        "judge": {
            "advisory": True, "enabled": any(record.judge is not None for record in records),
            "judged": judged, "failed": judge_failed, "scale": "0-2",
            "usefulness": rate_summary(judge["usefulness"], samples=samples, seed=seed + 200),
            "support": rate_summary(judge["support"], samples=samples, seed=seed + 201),
        },
    })
    return results
