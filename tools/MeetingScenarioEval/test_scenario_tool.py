import importlib.util
import unittest
from pathlib import Path

SPEC = importlib.util.spec_from_file_location("scenario_tool", Path(__file__).with_name("scenario_tool.py"))
tool = importlib.util.module_from_spec(SPEC)
assert SPEC.loader
SPEC.loader.exec_module(tool)


class ScenarioToolTests(unittest.TestCase):
    def test_normalize_collapses_layout_whitespace(self):
        result = tool.normalize(
            {
                "sourceName": "deck.ppt",
                "sourceFormat": "ole-binary",
                "slides": [{"number": 1, "text": "  Title \r\n\r\n  2.1   million ", "notes": ""}],
            },
            "synthetic",
        )
        self.assertEqual("Title\n2.1 million", result["slides"][0]["text"])

    def test_expansion_resolves_fact_and_unknown_ids(self):
        suite = tool.expand(
            {
                "name": "sample",
                "classification": "synthetic",
                "source": "deck",
                "facts": [{"id": "f", "text": "Fact", "matchAny": ["fact"]}],
                "unknowns": [{"id": "u", "text": "Unknown", "abstainMatchAny": ["check"], "forbiddenClaims": ["yes"]}],
                "scenarios": [
                    {
                        "id": "s",
                        "category": "executive-qa",
                        "title": "Title",
                        "script": [{"speaker": "customer", "text": "What is it?"}],
                        "expectedFactIds": ["f"],
                        "unknownIds": ["u"],
                        "answerHints": ["Hint"],
                    }
                ],
            }
        )
        self.assertEqual("Fact", suite["scenarios"][0]["expectedFacts"][0]["text"])
        self.assertTrue(suite["scenarios"][0]["expectedFacts"][0]["required"])
        self.assertEqual("Unknown", suite["scenarios"][0]["mustAbstain"][0]["text"])

    def test_expansion_supports_optional_grounding_facts(self):
        suite = tool.expand(
            {
                "name": "sample",
                "classification": "synthetic",
                "source": "deck",
                "facts": [{"id": "f", "text": "Fact", "matchAny": ["fact"]}],
                "unknowns": [{"id": "u", "text": "Unknown", "abstainMatchAny": ["check"], "forbiddenClaims": ["yes"]}],
                "scenarios": [
                    {
                        "id": "s",
                        "category": "unknown-answer",
                        "title": "Title",
                        "script": [{"speaker": "customer", "text": "What is unknown?"}],
                        "expectedFactIds": ["f"],
                        "requiredFactIds": [],
                        "unknownIds": ["u"],
                        "answerHints": ["Hint"],
                    }
                ],
            }
        )
        self.assertFalse(suite["scenarios"][0]["expectedFacts"][0]["required"])

    def test_confidential_output_is_blocked_inside_repository(self):
        output = Path(__file__).with_name("must-not-be-created.confidential.json")
        with self.assertRaises(PermissionError):
            tool.write_json(str(output), {"classification": "confidential"}, "confidential")
        self.assertFalse(output.exists())

    def test_score_flags_hallucination_generic_answer_and_latency(self):
        scenario = {
            "id": "s",
            "script": [{"speaker": "customer", "text": "Can it guarantee 95% accuracy?"}],
            "expectedFacts": [{"id": "f", "matchAny": ["2.1 million"], "required": True}],
            "mustAbstain": [
                {
                    "id": "accuracy",
                    "forbiddenClaims": ["95% accuracy"],
                    "abstainMatchAny": ["not specified", "need to verify"],
                }
            ],
            "preserve": {"tokens": ["95%"], "numbers": False},
            "maxFirstResponseMs": 1000,
        }
        row = tool.score_response(
            scenario, {"response": "That is interesting.", "firstResponseMs": 1200}
        )
        self.assertTrue(row["overGeneric"])
        self.assertFalse(row["latencyPass"])
        self.assertFalse(row["pass"])

    def test_score_accepts_equivalent_spoken_number_and_optional_unknown_guard(self):
        scenario = {
            "id": "s",
            "script": [{"speaker": "customer", "text": "Review the range."}],
            "expectedFacts": [],
            "mustAbstain": [
                {
                    "id": "guard",
                    "required": False,
                    "forbiddenClaims": ["guaranteed"],
                    "abstainMatchAny": ["not guaranteed"],
                }
            ],
            "preserve": {"anyOf": [["5–10", "five to ten"]], "numbers": False},
            "maxFirstResponseMs": 1000,
        }
        row = tool.score_response(
            scenario, {"response": "Review five to ten CEPs.", "firstResponseMs": 500}
        )
        self.assertEqual(1, row["preservedTokens"])
        self.assertEqual(0, row["unknowns"])
        self.assertTrue(row["pass"])


if __name__ == "__main__":
    unittest.main()
