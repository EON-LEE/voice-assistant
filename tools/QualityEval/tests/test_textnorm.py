import unittest

from qualityeval import textnorm


class NormalizationTests(unittest.TestCase):
    def test_numbers_currency_dates_and_times_share_one_form(self):
        self.assertEqual(textnorm.normalize("$1,950 per month"), "usd 1950 per month")
        self.assertEqual(textnorm.normalize("1,950 US dollars"), "1950 us dollars")
        self.assertEqual(textnorm.normalize("99.9 percent"), "99.9 %")
        self.assertEqual(textnorm.normalize("99.9%"), "99.9 %")
        self.assertEqual(textnorm.normalize("January 14th, 2026."), "january 14 2026")
        self.assertEqual(textnorm.normalize("02:00 a.m. UTC"), "2:00 am utc")
        self.assertEqual(textnorm.normalize("ORN-4"), "orn 4")
        self.assertEqual(textnorm.normalize("1,950,000"), "1950000")
        self.assertEqual(textnorm.normalize("U.S. data centers."), "u s data centers")

    def test_possessives_split_but_contractions_keep_negation(self):
        self.assertEqual(textnorm.normalize("Raghunathan's role"), "raghunathan 's role")
        self.assertIn("can't", textnorm.tokens("We can't guarantee that."))

    def test_phrase_matching_respects_token_boundaries(self):
        text = textnorm.normalize("The Growth plan is $1,950 a month.")
        self.assertTrue(textnorm.contains_phrase(text, "1,950"))
        self.assertTrue(textnorm.contains_phrase(text, "growth plan"))
        self.assertFalse(textnorm.contains_phrase(text, "195"))
        self.assertFalse(textnorm.contains_phrase(text, ""))

    def test_negated_claims_are_not_assertions(self):
        self.assertFalse(textnorm.asserted_phrase(textnorm.normalize("We can't guarantee 100% uptime."), "guarantee 100%"))
        self.assertTrue(textnorm.asserted_phrase(textnorm.normalize("Yes, we guarantee 100% uptime."), "guarantee 100%"))
        self.assertTrue(textnorm.asserted_phrase(
            textnorm.normalize("We don't charge setup, but we guarantee 100% uptime."), "guarantee 100%"))
        self.assertFalse(textnorm.asserted_pattern(textnorm.normalize("No plan offers 100 percent uptime."), r"\b100 %"))
        self.assertTrue(textnorm.asserted_pattern(textnorm.normalize("It is 100 percent."), r"\b100 %"))

    def test_containment_measures_shared_content_words(self):
        self.assertEqual(textnorm.containment("which plan fits", "So which plan fits that?"), 1.0)
        self.assertEqual(textnorm.containment("yeah okay", "anything"), 0.0)


if __name__ == "__main__":
    unittest.main()
