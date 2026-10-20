"""Percentiles and question-level (cluster) bootstrap confidence intervals.

Runs of the same item are correlated (same question, different accent/repeat), so intervals resample whole items
with replacement. Like tools/MeetingBenchmark, p50 is withheld below 5 samples and p95 below 20.
"""

from __future__ import annotations

import math
import random
import statistics
from typing import Callable, Mapping, Sequence

P50_MIN_SAMPLES = 5
P95_MIN_SAMPLES = 20
CI_LEVEL = 0.95


def nearest_rank(values: Sequence[float], quantile: float) -> float:
    ordered = sorted(values)
    if not ordered:
        raise ValueError("No values.")
    rank = max(1, math.ceil(quantile * len(ordered)))
    return ordered[rank - 1]


def quantile_interpolated(ordered: Sequence[float], quantile: float) -> float:
    position = (len(ordered) - 1) * quantile
    lower = math.floor(position)
    upper = min(lower + 1, len(ordered) - 1)
    return ordered[lower] + (ordered[upper] - ordered[lower]) * (position - lower)


def cluster_bootstrap(clusters: Mapping[str, Sequence[float]], statistic: Callable[[list[float]], float | None],
                      *, samples: int, seed: int) -> list[float] | None:
    """Percentile 95% interval of a statistic computed over values pooled from items resampled with replacement."""
    keys = sorted(key for key, values in clusters.items() if values)
    if len(keys) < 2 or samples < 100:
        return None
    rng = random.Random(seed)
    estimates = []
    for _ in range(samples):
        pooled: list[float] = []
        for _ in keys:
            pooled.extend(clusters[keys[rng.randrange(len(keys))]])
        value = statistic(pooled)
        if value is not None:
            estimates.append(value)
    if len(estimates) < samples * 0.9:
        return None
    estimates.sort()
    alpha = (1 - CI_LEVEL) / 2
    return [round(quantile_interpolated(estimates, alpha), 4), round(quantile_interpolated(estimates, 1 - alpha), 4)]


def latency_summary(per_item: Mapping[str, Sequence[float]], *, samples: int, seed: int) -> dict:
    clusters = {key: [value for value in values if value is not None] for key, values in per_item.items()}
    pooled = [value for values in clusters.values() for value in values]
    summary: dict = {"n": len(pooled), "items": sum(1 for values in clusters.values() if values),
                     "p50": None, "p95": None, "p50Ci95": None, "p95Ci95": None,
                     "mean": round(statistics.fmean(pooled), 1) if pooled else None,
                     "min": min(pooled) if pooled else None, "max": max(pooled) if pooled else None, "withheld": []}
    if len(pooled) >= P50_MIN_SAMPLES:
        summary["p50"] = round(statistics.median(pooled), 1)
        summary["p50Ci95"] = cluster_bootstrap(
            clusters, lambda values: statistics.median(values) if len(values) >= P50_MIN_SAMPLES else None,
            samples=samples, seed=seed)
    else:
        summary["withheld"].append(f"p50 needs at least {P50_MIN_SAMPLES} samples")
    if len(pooled) >= P95_MIN_SAMPLES:
        summary["p95"] = round(nearest_rank(pooled, 0.95), 1)
        summary["p95Ci95"] = cluster_bootstrap(
            clusters, lambda values: nearest_rank(values, 0.95) if len(values) >= P95_MIN_SAMPLES else None,
            samples=samples, seed=seed + 1)
    else:
        summary["withheld"].append(f"p95 needs at least {P95_MIN_SAMPLES} samples")
    return summary


def rate_summary(per_item: Mapping[str, Sequence[float]], *, samples: int, seed: int) -> dict:
    """Question-level mean: average the per-item means so every question weighs the same."""
    means = {key: statistics.fmean(values) for key, values in per_item.items() if values}
    runs = sum(len(values) for values in per_item.values())
    if not means:
        return {"value": None, "ci95": None, "items": 0, "runs": runs}
    item_means = {key: [value] for key, value in means.items()}
    return {
        "value": round(statistics.fmean(means.values()), 4),
        "ci95": cluster_bootstrap(item_means, lambda values: statistics.fmean(values) if values else None,
                                  samples=samples, seed=seed),
        "items": len(means), "runs": runs,
    }
