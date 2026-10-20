import json
import unittest

from qualityeval.protocol import ProtocolError, live_ui_options, parse_event, start_message


class StartMessageTests(unittest.TestCase):
    def test_live_ui_defaults_match_the_overlay(self):
        options = live_ui_options()
        self.assertEqual(options, {
            "responseMode": "grounded", "profile": {"name": "", "role": "", "project": ""}, "profileConfirmed": False,
            "topic": "", "phrases": [], "endSilenceMs": 1100, "semanticSegmentation": True, "replySettleMs": 1000,
            "replyKorean": False})
        message = json.loads(start_message(options))
        self.assertEqual(message["type"], "session.start")
        self.assertEqual(message["protocolVersion"], 1)
        self.assertEqual(message["audio"], {"encoding": "pcm_s16le", "sampleRate": 16000, "channels": 1})

    def test_invalid_options_are_rejected_before_sending(self):
        with self.assertRaises(ValueError):
            live_ui_options("everything")
        with self.assertRaises(ValueError):
            live_ui_options(phrases=["x" * 65])
        with self.assertRaises(ValueError):
            start_message({"topic": "x" * 40000})


class ParseEventTests(unittest.TestCase):
    def parse(self, **event):
        return parse_event(json.dumps(event))

    def test_delta_with_primary_done_may_be_empty(self):
        event = self.parse(type="response.delta", responseId="r", turnId="t", text="", primaryDone=True)
        self.assertEqual(event, {"type": "response.delta", "turnId": "t", "responseId": "r", "text": "", "primaryDone": True})
        with_text = self.parse(type="response.delta", responseId="r", turnId="t", text=" owner.", primaryDone=True)
        self.assertEqual(with_text["text"], " owner.")
        plain = self.parse(type="response.delta", responseId="r", turnId="t", text="Hello")
        self.assertNotIn("primaryDone", plain)

    def test_empty_delta_without_primary_done_is_a_protocol_error(self):
        with self.assertRaises(ProtocolError) as error:
            self.parse(type="response.delta", responseId="r", turnId="t", text="")
        self.assertEqual(error.exception.kind, "empty_delta")
        for flag in (False, "true", 1):
            with self.assertRaises(ProtocolError) as invalid:
                self.parse(type="response.delta", responseId="r", turnId="t", text="", primaryDone=flag)
            self.assertEqual(invalid.exception.kind, "invalid_primary_done")

    def test_completion_validation(self):
        completed = self.parse(type="response.completed", responseId="r", turnId="t", text="One.", suggestions=["One.", "Two."],
                               sources=[{"title": "Doc", "url": "https://x.invalid/1", "updatedAt": None}],
                               grounding="grounded", responseRoute="knowledge", retrievalPrefetched=True, respondNow=False)
        self.assertEqual(completed["suggestions"], ["One.", "Two."])
        self.assertFalse(completed["respondNow"])
        legacy = self.parse(type="response.completed", responseId="r", turnId="t", text="Only.", sources=[])
        self.assertEqual(legacy["suggestions"], ["Only."])
        for broken in ({"suggestions": ["Other.", "Two."]}, {"grounding": "maybe"}, {"responseRoute": "web"},
                       {"sources": [{"title": 1, "url": "u"}]}, {"suggestions": []}):
            event = {"type": "response.completed", "responseId": "r", "turnId": "t", "text": "One.", "sources": []}
            event.update(broken)
            with self.assertRaises(ProtocolError):
                parse_event(json.dumps(event))

    def test_other_events(self):
        self.assertEqual(self.parse(type="session.ready", protocolVersion=1, sessionUpdate=True),
                         {"type": "session.ready", "sessionUpdate": True})
        self.assertEqual(self.parse(type="transcript.final", turnId="t", revision=2, text="Hi?")["text"], "Hi?")
        self.assertEqual(self.parse(type="error", code="grounding_unavailable", message="m", retryable=True)["code"],
                         "grounding_unavailable")
        self.assertTrue(self.parse(type="response.korean", responseId="r", turnId="t", index=1, failed=True)["failed"])
        self.assertEqual(self.parse(type="session.updated", responseMode="conversation", replyKorean=False)["responseMode"],
                         "conversation")
        for broken in ({"type": "transcript.final", "turnId": "t", "revision": -1, "text": ""},
                       {"type": "transcript.final", "turnId": "t", "revision": True, "text": ""},
                       {"type": "error", "code": "x", "message": "m"},
                       {"type": "response.started", "turnId": "t"},
                       {"type": "response.korean", "responseId": "r", "turnId": "t", "index": 2, "korean": "x"},
                       {"type": "session.ready", "protocolVersion": 2}):
            with self.assertRaises(ProtocolError):
                parse_event(json.dumps(broken))

    def test_unknown_and_malformed_messages(self):
        with self.assertRaises(ProtocolError) as unknown:
            self.parse(type="response.surprise", turnId="t", responseId="r")
        self.assertEqual(unknown.exception.kind, "unknown_event")
        for raw in ("not json", "[]", b"\xff", "x" * 70000):
            with self.assertRaises(ProtocolError):
                parse_event(raw)


if __name__ == "__main__":
    unittest.main()
