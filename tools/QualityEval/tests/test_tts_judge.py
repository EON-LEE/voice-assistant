import json
import unittest

from qualityeval.corpus import load_corpus, load_items
from qualityeval.judge import Judge, JudgeConfig, JudgeError, config_from_env, judge_input, parse_grade
from qualityeval.tts import AzureTts, SpeechEndpoint, TtsAudio, TtsCache, TtsError, endpoint_from_env, ssml
from qualityeval.audio import silence, tone_clip

from .helpers import scratch_dir


class Recorder:
    def __init__(self, *responses):
        self.responses = list(responses)
        self.calls = []

    def __call__(self, method, url, headers, body, timeout):
        self.calls.append((method, url, headers, body))
        return self.responses.pop(0)


class TtsTests(unittest.TestCase):
    def test_endpoint_configuration(self):
        custom = endpoint_from_env({"EVAL_SPEECH_ENDPOINT": "https://eval-speech.cognitiveservices.azure.com/",
                                    "EVAL_SPEECH_RESOURCE_ID": "/subscriptions/x/resourceGroups/y"})
        self.assertEqual(custom.url, "https://eval-speech.cognitiveservices.azure.com/tts/cognitiveservices/v1")
        regional = endpoint_from_env({"EVAL_SPEECH_REGION": "koreacentral", "EVAL_SPEECH_RESOURCE_ID": "/r"})
        self.assertEqual(regional.url, "https://koreacentral.tts.speech.microsoft.com/cognitiveservices/v1")
        for broken in ({}, {"EVAL_SPEECH_ENDPOINT": "http://insecure"}, {"EVAL_SPEECH_REGION": "koreacentral"}):
            with self.assertRaises(TtsError):
                endpoint_from_env(broken)

    def test_ssml_escapes_text_and_selects_locale(self):
        markup = ssml("en-GB-RyanNeural", "Is <ORN-4> & 'TALLIS' \"ready\"?")
        self.assertIn("xml:lang='en-GB'", markup)
        self.assertIn("&lt;ORN-4&gt; &amp; &apos;TALLIS&apos; &quot;ready&quot;?", markup)

    def test_entra_authorization_format_and_retry(self):
        audio = silence(50) + tone_clip("hello").pcm
        transport = Recorder((429, b""), (200, audio))
        tts = AzureTts(SpeechEndpoint("https://x.invalid/tts", "/subscriptions/s/resource"), lambda: "entra-token",
                       transport=transport, sleep=lambda _: None)
        self.assertEqual(tts.synthesize("en-US-GuyNeural", "Hello?"), audio)
        headers = transport.calls[-1][2]
        self.assertEqual(headers["Authorization"], "Bearer aad#/subscriptions/s/resource#entra-token")
        self.assertEqual(headers["X-Microsoft-OutputFormat"], "raw-16khz-16bit-mono-pcm")
        failing = AzureTts(SpeechEndpoint("https://x.invalid/tts", None), lambda: "t", transport=Recorder((403, b"")),
                           sleep=lambda _: None)
        with self.assertRaises(TtsError):
            failing.synthesize("en-US-GuyNeural", "Hello?")

    def test_cache_round_trip_and_cache_only_mode(self):
        cache = TtsCache(scratch_dir(self) / "tts")
        pcm = silence(100) + tone_clip("hello there").pcm + silence(200)
        stored = cache.store("en-US-GuyNeural", "Hello there?", pcm)
        self.assertLess(abs(stored.speech_start - 1600), 160)
        loaded = cache.load("en-US-GuyNeural", "Hello there?")
        self.assertEqual(loaded, stored)
        self.assertIsNone(cache.load("en-GB-RyanNeural", "Hello there?"))
        offline = TtsAudio(cache)
        self.assertEqual(offline.clip("Hello there?", "A", "en-US"), stored)
        with self.assertRaises(TtsError):
            offline.clip("Something new?", "A", "en-US")
        with self.assertRaises(TtsError):
            offline.clip("Hello there?", "A", "fr-FR")


class JudgeTests(unittest.TestCase):
    def setUp(self):
        self.corpus = load_corpus()
        self.item = next(item for item in load_items().items if item.id == "number-growth-price")

    def test_configuration_requires_a_separate_deployment(self):
        config = config_from_env({"EVAL_JUDGE_ENDPOINT": "https://judge.openai.azure.com/", "EVAL_JUDGE_DEPLOYMENT": "judge-large"})
        self.assertEqual(config.url, "https://judge.openai.azure.com/openai/deployments/judge-large/chat/completions"
                                     "?api-version=2024-10-21")
        with self.assertRaises(JudgeError):
            config_from_env({"EVAL_JUDGE_ENDPOINT": "https://judge.openai.azure.com/", "EVAL_JUDGE_DEPLOYMENT": "meeting-chat",
                             "EVAL_SUT_DEPLOYMENT": "meeting-chat"})
        with self.assertRaises(JudgeError):
            config_from_env({"EVAL_JUDGE_DEPLOYMENT": "judge"})

    def test_judge_sends_synthetic_reference_and_parses_anchored_scores(self):
        content = json.dumps({"usefulness": 2, "support": 1, "reason": "Correct price, extra claim."})
        transport = Recorder((200, json.dumps({"choices": [{"message": {"content": content}}]}).encode()))
        judge = Judge(JudgeConfig("https://judge.invalid", "judge", "2024-10-21"), lambda: "token", transport=transport)
        self.assertEqual(judge.grade(self.item, self.corpus, "Growth is $1,950 a month.", ""), {"usefulness": 2, "support": 1})
        body = json.loads(transport.calls[0][3])
        self.assertIn("anchored", body["messages"][0]["content"])
        payload = json.loads(body["messages"][1]["content"])
        self.assertEqual(payload["currentQuestion"], self.item.target.text)
        self.assertIn("1,950", payload["reference"][0])
        self.assertEqual(body["response_format"], {"type": "json_object"})

    def test_invalid_grades_are_rejected(self):
        for content in ("not json", json.dumps({"usefulness": 3, "support": 1}), json.dumps({"usefulness": True, "support": 0})):
            with self.assertRaises(JudgeError):
                parse_grade(content)
        uncovered = next(item for item in load_items().items if item.id == "notcov-revenue")
        self.assertIn("No reference document", judge_input(uncovered, self.corpus, "I'm not sure.", ""))


if __name__ == "__main__":
    unittest.main()
