import unittest
from collections import Counter

from qualityeval import textnorm
from qualityeval.corpus import CATEGORIES, MIN_ITEMS, ItemSet, load_corpus, load_items, validate


class CommittedCorpusTests(unittest.TestCase):
    def setUp(self):
        self.corpus = load_corpus()
        self.items = load_items()

    def test_corpus_and_items_validate(self):
        self.assertEqual(validate(self.corpus, self.items), [])
        self.assertGreaterEqual(len(self.items.items), MIN_ITEMS)
        self.assertTrue(6 <= len(self.corpus.documents) <= 10)

    def test_every_category_is_covered_with_several_items(self):
        counts = Counter(item.category for item in self.items.items)
        self.assertEqual(set(counts), set(CATEGORIES))
        self.assertTrue(all(count >= 4 for count in counts.values()), counts)

    def test_items_have_the_required_shape(self):
        for item in self.items.items:
            self.assertTrue(item.target.text.strip(), item.id)
            if item.category == "multi-turn":
                self.assertTrue(item.context, item.id)
            if item.category in ("not-covered", "general"):
                self.assertFalse(item.materials_required, item.id)
            if item.category == "commitment":
                self.assertTrue(item.forbidden_claims or item.forbidden_patterns, item.id)
        backchannels = [turn for item in self.items.items for turn in item.context
                        if textnorm.normalize(turn.text) in ("yeah", "mm hmm", "okay", "sure", "yeah agreed")]
        self.assertGreaterEqual(len(backchannels), 3)

    def test_uncovered_questions_are_really_absent_from_the_corpus(self):
        corpus_tokens = set(textnorm.tokens(" ".join(document.text for document in self.corpus.documents)))
        for word in ("employees", "employee", "revenue", "euro", "euros", "premises", "korea", "korean"):
            self.assertNotIn(word, corpus_tokens)

    def test_validation_reports_untraceable_facts_and_unspoken_terms(self):
        item = next(item for item in self.items.items if item.id == "number-growth-price")
        broken_fact = item.__class__(**{**item.__dict__, "required_facts": (item.required_facts[0].__class__("x", ("2,222",)),)})
        term = next(item for item in self.items.items if item.key_terms)
        broken_term = term.__class__(**{**term.__dict__, "key_terms": (term.key_terms[0].__class__("Zorblax", ("zorblax",)),)})
        problems = validate(self.corpus, ItemSet((broken_fact, broken_term), "x"))
        self.assertTrue(any("not traceable" in problem for problem in problems))
        self.assertTrue(any("not spoken" in problem for problem in problems))
        self.assertTrue(any("at least" in problem for problem in problems))

    def test_selection_by_id_and_category(self):
        self.assertEqual([item.id for item in self.items.select(["fact-roadmap-owner"])], ["fact-roadmap-owner"])
        self.assertTrue(all(item.category == "general" for item in self.items.select(categories=["general"])))
        with self.assertRaises(ValueError):
            self.items.select(["missing-item"])


if __name__ == "__main__":
    unittest.main()
