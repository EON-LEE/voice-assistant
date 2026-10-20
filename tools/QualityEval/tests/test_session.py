import asyncio
import struct
import unittest

from qualityeval.audio import FRAME_BYTES, Clip, build_timeline, clip_from_pcm, silence, speech_bounds, tone_clip
from qualityeval.session import Limits, SessionTracker
from qualityeval.wsclient import (OP_BINARY, OP_CLOSE, OP_TEXT, WebSocketError, accept_key, apply_mask, encode_frame,
                                  read_frame)

S = 1_000_000_000


def timeline(*texts: str):
    return build_timeline([("A", text, tone_clip(text)) for text in texts], lead_ms=400, gap_ms=700)


def feed(tracker: SessionTracker, line) -> None:
    for t, event in line:
        tracker.event(t, event)


def send_all(tracker: SessionTracker, frames: int, start: int = 0, step: int = 20_000_000) -> int:
    for index in range(frames):
        tracker.frame_sent(index, start + index * step)
    return start + frames * step


class AudioTests(unittest.TestCase):
    def test_timeline_marks_segments_and_pads_frames(self):
        line = timeline("We want a pilot.", "Which plan fits?")
        self.assertEqual(len(line.pcm) % FRAME_BYTES, 0)
        first, target = line.segments
        self.assertEqual((first.role, target.role), ("context", "target"))
        self.assertEqual(first.start_sample, 6400)
        self.assertEqual(target.start_sample, first.end_sample + 11200)
        self.assertGreater(target.speech_end_frame, first.speech_end_frame)
        self.assertEqual(line.frame(line.frames + 5), bytes(FRAME_BYTES))

    def test_speech_bounds_ignore_digital_silence(self):
        tone = tone_clip("hello there")
        pcm = silence(100) + tone.pcm + silence(250)
        start, end = speech_bounds(pcm)
        self.assertLess(abs(start - 1600), 160)
        self.assertLessEqual(abs(end - (1600 + tone.samples)), 400)
        self.assertEqual(speech_bounds(silence(100)), (0, 0))
        with self.assertRaises(ValueError):
            clip_from_pcm(silence(100))


class TrackerTests(unittest.TestCase):
    def test_reply_is_bound_to_the_target_question_not_older_context(self):
        line = timeline("What does the Starter plan cost?", "Sorry, I mean the Enterprise plan. What does that one start at?")
        tracker = SessionTracker(line.segments, Limits(grace_s=1))
        context, target = line.segments
        end = send_all(tracker, line.frames)
        t_context = tracker.sent_speech_end[context.index] + S // 2
        t_target = tracker.sent_speech_end[target.index] + S // 2
        feed(tracker, [
            (t_context, {"type": "transcript.final", "turnId": "c", "revision": 1, "text": "What does the Starter plan cost?"}),
            (t_context + 300 * 10**6, {"type": "response.started", "responseId": "r1", "turnId": "c"}),
            (t_target, {"type": "transcript.final", "turnId": "t", "revision": 1,
                        "text": "Sorry I mean the enterprise plan what does that one start at?"}),
            (t_target + 1 * S, {"type": "response.completed", "responseId": "r1", "turnId": "c", "text": "Starter is $480.",
                                "suggestions": ["Starter is $480."], "sources": [], "grounding": "grounded"}),
            (t_target + 1 * S + 1000, {"type": "response.started", "responseId": "r2", "turnId": "t"}),
            (t_target + 2 * S, {"type": "response.delta", "responseId": "r2", "turnId": "t", "text": "Enterprise starts"}),
            (t_target + 2 * S + 10**8, {"type": "response.delta", "responseId": "r2", "turnId": "t", "text": " at $7,500."}),
            (t_target + 2 * S + 2 * 10**8, {"type": "response.delta", "responseId": "r2", "turnId": "t", "text": "",
                                            "primaryDone": True}),
            (t_target + 3 * S, {"type": "response.completed", "responseId": "r2", "turnId": "t",
                                "text": "Enterprise starts at $7,500.", "suggestions": ["Enterprise starts at $7,500.", "Ask about SSO."],
                                "sources": [{"title": "Kestrova Pulse pricing tiers", "url": "https://x.invalid", "updatedAt": None}],
                                "grounding": "grounded", "responseRoute": "knowledge", "retrievalPrefetched": True}),
        ])
        self.assertFalse(tracker.done(t_target + 3 * S + S // 2))
        self.assertTrue(tracker.done(t_target + 5 * S))
        outcome = tracker.outcome()
        self.assertEqual(outcome.status, "ok")
        self.assertEqual(outcome.response["primary"], "Enterprise starts at $7,500.")
        self.assertEqual(outcome.response["alternative"], "Ask about SSO.")
        self.assertEqual((outcome.target_responses, outcome.context_responses), (1, 1))
        self.assertEqual(outcome.timings["speechEndToFinal"], 500.0)
        self.assertEqual(outcome.timings["finalToStarted"], 1000.0)
        self.assertEqual(outcome.timings["speechEndToFirstDelta"], 2500.0)
        self.assertEqual(outcome.timings["firstDeltaToPrimaryDone"], 200.0)
        self.assertEqual(outcome.timings["speechEndToCompleted"], 3500.0)
        self.assertTrue(outcome.response["primaryDoneSeen"])
        self.assertTrue(outcome.response["streamMatchesPrimary"])
        self.assertLess(end, t_target + 5 * S)

    def test_split_question_uses_the_latest_completed_reply(self):
        line = timeline("When is the new gateway coming out?")
        tracker = SessionTracker(line.segments)
        send_all(tracker, line.frames)
        t = tracker.sent_speech_end[0]
        feed(tracker, [
            (t - S, {"type": "transcript.final", "turnId": "a", "revision": 1, "text": "When is the new"}),
            (t - S + 10, {"type": "response.started", "responseId": "r1", "turnId": "a"}),
            (t + S // 2, {"type": "transcript.final", "turnId": "b", "revision": 1, "text": "gateway coming out?"}),
            (t + S // 2 + 5, {"type": "response.cancelled", "responseId": "r1", "turnId": "a"}),
            (t + S, {"type": "response.started", "responseId": "r2", "turnId": "b"}),
            (t + 2 * S, {"type": "response.completed", "responseId": "r2", "turnId": "b", "text": "Q2 2027, planned.",
                         "sources": []}),
        ])
        outcome = tracker.outcome()
        self.assertEqual(outcome.status, "ok")
        self.assertEqual(outcome.timings["speechEndToFinal"], 500.0)
        self.assertEqual(outcome.cancelled_responses, 1)

    def test_missing_final_and_missing_reply_time_out_with_explicit_status(self):
        line = timeline("Who owns the roadmap?")
        quiet = SessionTracker(line.segments, Limits(final_s=2))
        end = send_all(quiet, line.frames)
        self.assertFalse(quiet.done(end))
        self.assertTrue(quiet.done(end + 3 * S))
        self.assertEqual(quiet.outcome().status, "no_target_final")
        silent = SessionTracker(line.segments, Limits(start_s=2))
        end = send_all(silent, line.frames)
        silent.event(end, {"type": "transcript.final", "turnId": "t", "revision": 1, "text": "Who owns the roadmap?"})
        self.assertTrue(silent.done(end + 3 * S))
        self.assertEqual(silent.outcome().status, "no_target_response")

    def test_grounding_failure_and_fatal_errors(self):
        line = timeline("Who owns the roadmap?")
        tracker = SessionTracker(line.segments)
        end = send_all(tracker, line.frames)
        feed(tracker, [
            (end, {"type": "transcript.final", "turnId": "t", "revision": 1, "text": "Who owns the roadmap?"}),
            (end + 1, {"type": "response.started", "responseId": "r", "turnId": "t"}),
            (end + 2, {"type": "error", "code": "grounding_unavailable", "message": "m", "retryable": True}),
            (end + 3, {"type": "response.completed", "responseId": "r", "turnId": "t", "text": "Grounding is unavailable.",
                       "sources": [], "grounding": "unavailable"}),
        ])
        outcome = tracker.outcome()
        self.assertEqual(outcome.status, "grounding_unavailable")
        self.assertFalse(outcome.answered)
        self.assertEqual(outcome.error_codes, ["grounding_unavailable"])
        fatal = SessionTracker(line.segments)
        fatal.event(1, {"type": "error", "code": "invalid_start", "message": "m", "retryable": False})
        self.assertTrue(fatal.done(2))
        self.assertEqual(fatal.outcome().status, "start_error:invalid_start")

    def test_unknown_responses_and_late_deltas_are_recorded_as_anomalies(self):
        line = timeline("Who owns the roadmap?")
        tracker = SessionTracker(line.segments)
        feed(tracker, [
            (1, {"type": "response.delta", "responseId": "x", "turnId": "t", "text": "hi"}),
            (2, {"type": "response.started", "responseId": "r", "turnId": "t"}),
            (3, {"type": "response.completed", "responseId": "r", "turnId": "t", "text": "Done.", "sources": []}),
            (4, {"type": "response.delta", "responseId": "r", "turnId": "t", "text": "late"}),
        ])
        self.assertEqual(tracker.anomalies, ["response.delta_for_unknown_response", "delta_after_finish"])


class FramingTests(unittest.TestCase):
    def read(self, data: bytes, *, expect_masked: bool):
        async def go():
            reader = asyncio.StreamReader()
            reader.feed_data(data)
            reader.feed_eof()
            return await read_frame(reader, expect_masked=expect_masked)
        return asyncio.run(go())

    def test_masked_round_trip_for_all_length_encodings(self):
        for size in (0, 5, 125, 126, 640, 65535, 65536):
            payload = bytes(range(256)) * (size // 256) + bytes(range(size % 256))
            fin, opcode, data = self.read(encode_frame(OP_BINARY, payload, mask=True), expect_masked=True)
            self.assertEqual((fin, opcode, data), (True, OP_BINARY, payload))
        self.assertEqual(self.read(encode_frame(OP_TEXT, b"hi", mask=False), expect_masked=False)[2], b"hi")

    def test_protocol_violations_are_rejected(self):
        with self.assertRaises(WebSocketError):
            self.read(encode_frame(OP_TEXT, b"hi", mask=True), expect_masked=False)
        with self.assertRaises(WebSocketError):
            self.read(bytes([0xC1, 0x00]), expect_masked=False)
        with self.assertRaises(WebSocketError):
            self.read(bytes([0x08, 0x02]) + struct.pack("!H", 1000), expect_masked=False)
        self.assertEqual(self.read(encode_frame(OP_CLOSE, b"\x03\xe8", mask=False), expect_masked=False)[1], OP_CLOSE)

    def test_accept_key_and_masking(self):
        self.assertEqual(accept_key("dGhlIHNhbXBsZSBub25jZQ=="), "s3pPLMBiTxaQ9kYGzzhZRbK+xOo=")
        self.assertEqual(apply_mask(apply_mask(b"payload!", b"abcd"), b"abcd"), b"payload!")
        self.assertIsInstance(Clip(b"\x01\x00", 0, 1).samples, int)


if __name__ == "__main__":
    unittest.main()
