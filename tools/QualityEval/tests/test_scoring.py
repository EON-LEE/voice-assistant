import unittest

from qualityeval.corpus import Fact, KeyTerm, Turn, load_corpus, load_items
from qualityeval.scoring import score_answer, term_results

from .helpers import make_item

TITLES = ["Kestrova team and roles"]


class ScoringTests(unittest.TestCase):
    def test_complete_grounded_answer_passes(self):
        score = score_answer(make_item(), "Joaquin Halloway handles security questionnaires.",
                             "He also answers audit requests.", "grounded", TITLES, TITLES, answered=True)
        self.assertTrue(score.passed)
        self.assertEqual((score.facts_found, score.facts_required), (1, 1))
        self.assertTrue(score.retrieval_hit)
        self.assertTrue(score.format_ok)

    def test_missing_fact_and_unanswered_runs_fail(self):
        score = score_answer(make_item(), "I think the security team handles that.", "", "grounded", TITLES, TITLES,
                             answered=True)
        self.assertFalse(score.passed)
        self.assertEqual(score.missing_facts, ["halloway"])
        unanswered = score_answer(make_item(), "", "", None, [], TITLES, answered=False)
        self.assertFalse(unanswered.answered)
        self.assertFalse(unanswered.passed)

    def test_primary_only_facts_must_be_in_the_primary_line(self):
        item = make_item(required_facts=(Fact("price", ("7500",), primary=True),))
        late = score_answer(item, "Starter is 480 dollars a month.", "Enterprise starts at $7,500.", "grounded",
                            TITLES, TITLES, answered=True)
        self.assertFalse(late.passed)
        early = score_answer(item, "Enterprise starts at $7,500 a month.", "", "grounded", TITLES, TITLES, answered=True)
        self.assertTrue(early.passed)

    def test_forbidden_claims_are_negation_aware(self):
        item = make_item(required_facts=(), materials_required=False, expected_sources=(),
                         forbidden_claims=("guarantee 100 %",), forbidden_patterns=(r"\b\d+\s+employees\b",))
        safe = score_answer(item, "We can't guarantee 100% uptime, but Enterprise has 99.9%.", "", "grounded", [], [],
                            answered=True)
        self.assertEqual(safe.forbidden_hits, [])
        unsafe = score_answer(item, "Yes, we guarantee 100% uptime.", "We have 250 employees.", "grounded", [], [],
                              answered=True)
        self.assertEqual(unsafe.forbidden_hits, ["guarantee 100 %", "pattern:0"])
        self.assertFalse(unsafe.passed)

    def test_abstention_required_for_uncovered_questions(self):
        item = make_item(category="not-covered", required_facts=(), materials_required=False, expected_sources=(),
                         abstain_required=True, abstain_any_of=("us dollars",))
        self.assertTrue(score_answer(item, "I'm not sure, let me check that number.", "", "no_matches", [], [],
                                     answered=True).abstain_ok)
        self.assertTrue(score_answer(item, "Our list price is 1,950 US dollars a month.", "", "grounded", [], [],
                                     answered=True).passed)
        invented = score_answer(item, "It is about 1,800 euros per month.", "", "grounded", [], [], answered=True)
        self.assertFalse(invented.abstain_ok)
        self.assertFalse(invented.passed)

    def test_declining_a_commitment_counts_as_abstaining(self):
        item = make_item(category="commitment", required_facts=(Fact("sla", ("99.9 %",)),), materials_required=False,
                         expected_sources=(), abstain_required=True, abstain_any_of=("can't guarantee",))
        refusal = score_answer(item, "No, 100% uptime isn't guaranteed.", "The best plan is 99.9% monthly uptime.",
                               "grounded", [], [], answered=True)
        self.assertTrue(refusal.abstain_ok)
        self.assertTrue(refusal.passed)
        approval = score_answer(item, "That discount needs approval first.", "Our SLA stays at 99.9 percent.",
                                "grounded", [], [], answered=True)
        self.assertTrue(approval.abstain_ok)
        accepted = score_answer(item, "Yes, we will give you 100% uptime.", "Our SLA is 99.9%.", "grounded", [], [],
                                answered=True)
        self.assertFalse(accepted.abstain_ok)
        self.assertFalse(accepted.passed)

    def test_a_negated_guarantee_declines_the_commitment(self):
        item = make_item(category="commitment", required_facts=(Fact("sla", ("99.9 %",)),), materials_required=False,
                         expected_sources=(), abstain_required=True, abstain_any_of=("can't guarantee",))
        declined = score_answer(item, "No, there's no 100% uptime guarantee.", "The best plan lists a 99.9% SLA.",
                                "grounded", [], [], answered=True)
        self.assertTrue(declined.abstain_ok)
        self.assertTrue(declined.passed)
        wont = score_answer(item, "We won't commit to that date.", "The plan lists 99.9% uptime.", "grounded", [], [],
                            answered=True)
        self.assertTrue(wont.abstain_ok)
        promised = score_answer(item, "Sure, we guarantee it and our SLA is 99.9%.", "", "grounded", [], [],
                                answered=True)
        self.assertFalse(promised.abstain_ok)
        new_clause = score_answer(item, "There's no problem, we guarantee 100% uptime.", "Our SLA is 99.9%.",
                                  "grounded", [], [], answered=True)
        self.assertFalse(new_clause.abstain_ok)

    def test_unit_wording_does_not_decide_a_fact(self):
        item = make_item(required_facts=(Fact("response-time", ("30 minutes",)),), materials_required=False,
                         expected_sources=())
        hyphenated = score_answer(item, "Enterprise gets a 30-minute first response.", "", "grounded", [], [],
                                  answered=True)
        self.assertEqual(hyphenated.missing_facts, [])
        other = score_answer(item, "Enterprise gets a 3-minute first response.", "", "grounded", [], [], answered=True)
        self.assertEqual(other.missing_facts, ["response-time"])

    def test_reading_limits_markup_and_generic_fillers(self):
        item = make_item(required_facts=(), materials_required=False, expected_sources=())
        long = score_answer(item, " ".join(["word"] * 30) + ".", "", "disabled", [], [], answered=True)
        self.assertIn("primary_too_long", long.format_issues)
        markup = score_answer(item, "**Joaquin** handles it. See the list.", "", "disabled", [], [], answered=True)
        self.assertIn("primary_markup", markup.format_issues)
        sentences = score_answer(item, "One idea. Two ideas. Three ideas.", "", "disabled", [], [], answered=True)
        self.assertIn("primary_too_many_sentences", sentences.format_issues)
        quotes = score_answer(item, 'He said "yes" to that.', "", "disabled", [], [], answered=True)
        self.assertIn("primary_quotes", quotes.format_issues)
        self.assertTrue(score_answer(item, "Good question.", "", "disabled", [], [], answered=True).generic)
        self.assertTrue(score_answer(item, "Sure.", "", "disabled", [], [], answered=True).generic)

    def test_retrieval_hit_requires_grounding_and_expected_source(self):
        miss = score_answer(make_item(), "Joaquin Halloway handles them.", "", "grounded", ["Kestrova pricing"], TITLES,
                            answered=True)
        self.assertFalse(miss.retrieval_hit)
        self.assertTrue(miss.passed)
        disabled = score_answer(make_item(), "Joaquin Halloway handles them.", "", "no_matches", [], TITLES, answered=True)
        self.assertFalse(disabled.retrieval_hit)

    def test_term_accuracy_uses_the_utterance_that_says_the_term(self):
        item = make_item(context=(Turn("B", "They mostly care about SOC 2."),), target=Turn("A", "Is that report finished?"),
                         key_terms=(KeyTerm("SOC 2", ("soc 2", "sock 2")),))
        self.assertEqual(term_results(item, [["They mostly care about sock two."], ["Is that report finished?"]]),
                         [("SOC 2", False)])
        self.assertEqual(term_results(item, [["They mostly care about SOC 2."], []]), [("SOC 2", True)])
        self.assertEqual(term_results(item, [[], []]), [("SOC 2", False)])

    def test_committed_items_score_their_own_reference_answers(self):
        corpus, items = load_corpus(), load_items()
        reference = {
            "number-growth-price": "The Growth plan is $1,950 per month.",
            "number-maintenance-window": "Sundays from 02:00 to 04:00 UTC.",
            "commit-uptime-100": "No plan offers 100% uptime, but Enterprise has a 99.9% SLA.",
            "commit-discount-30": "We can't offer 30%. Paying a year ahead gives 15% off.",
            "multi-gateway-release": "The ORN-5 is targeted for Q2 2027, but that date is not committed.",
        }
        for item in items.items:
            if item.id in reference:
                titles = [corpus.document(source).title for source in item.expected_sources]
                score = score_answer(item, reference[item.id], "", "grounded", titles, titles, answered=True)
                self.assertTrue(score.passed, (item.id, score.as_dict()))


if __name__ == "__main__":
    unittest.main()
