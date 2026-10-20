import statistics
import unittest

from qualityeval import stats


class StatisticsTests(unittest.TestCase):
    def test_nearest_rank_percentiles(self):
        values = list(range(1, 21))
        self.assertEqual(stats.nearest_rank(values, 0.95), 19)
        self.assertEqual(stats.nearest_rank(values, 0.5), 10)
        self.assertEqual(stats.nearest_rank([7.0], 0.95), 7.0)

    def test_percentiles_are_withheld_below_minimum_samples(self):
        small = stats.latency_summary({"a": [100.0, 110.0], "b": [120.0]}, samples=500, seed=1)
        self.assertIsNone(small["p50"])
        self.assertIsNone(small["p95"])
        self.assertEqual(len(small["withheld"]), 2)
        medium = stats.latency_summary({str(i): [100.0 + i] for i in range(10)}, samples=500, seed=1)
        self.assertEqual(medium["p50"], 104.5)
        self.assertIsNone(medium["p95"])
        large = stats.latency_summary({str(i): [100.0 + i, 200.0 + i] for i in range(15)}, samples=500, seed=1)
        self.assertEqual(large["n"], 30)
        self.assertEqual(large["p95"], stats.nearest_rank([v for i in range(15) for v in (100.0 + i, 200.0 + i)], 0.95))

    def test_bootstrap_is_deterministic_and_brackets_the_estimate(self):
        clusters = {f"item-{i}": [float(i % 2)] * 3 for i in range(30)}
        first = stats.rate_summary(clusters, samples=1000, seed=7)
        second = stats.rate_summary(clusters, samples=1000, seed=7)
        self.assertEqual(first, second)
        self.assertAlmostEqual(first["value"], 0.5)
        low, high = first["ci95"]
        self.assertLess(low, 0.5)
        self.assertGreater(high, 0.5)
        self.assertGreater(low, 0.25)
        self.assertLess(high, 0.75)

    def test_constant_items_give_a_degenerate_interval(self):
        result = stats.rate_summary({f"i{n}": [1.0, 1.0] for n in range(10)}, samples=500, seed=3)
        self.assertEqual(result["ci95"], [1.0, 1.0])

    def test_question_level_mean_weighs_items_equally(self):
        result = stats.rate_summary({"many": [1.0] * 9, "few": [0.0]}, samples=500, seed=3)
        self.assertEqual(result["value"], 0.5)
        self.assertEqual(result["runs"], 10)

    def test_cluster_bootstrap_resamples_whole_items(self):
        clusters = {"slow": [1000.0] * 20, "fast": [100.0] * 20}
        interval = stats.cluster_bootstrap(clusters, statistics.median, samples=2000, seed=11)
        self.assertEqual(interval, [100.0, 1000.0])
        self.assertIsNone(stats.cluster_bootstrap({"only": [1.0]}, statistics.median, samples=500, seed=1))


if __name__ == "__main__":
    unittest.main()
