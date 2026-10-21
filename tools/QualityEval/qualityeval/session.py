"""One evaluation session: stream an item's audio timeline in real time and bind the reply to its target question.

Every server event and every sent audio frame gets a monotonic timestamp (time.perf_counter_ns). Finals are assigned
to timeline utterances by content overlap (falling back to timing), and the reply to evaluate is the last completed
response whose ``turnId`` belongs to a final of the target question. Latency is measured from the moment the frame
carrying the target's last speech sample was handed to the socket (the audio timeline knows where speech ends).
"""

from __future__ import annotations

import asyncio
import json
import time
from dataclasses import dataclass, field
from typing import Awaitable, Callable

from . import textnorm
from .audio import FRAME_SAMPLES, SAMPLE_RATE, Segment, Timeline
from .protocol import STOP_MESSAGE, ProtocolError, parse_event, start_message
from .wsclient import OP_TEXT, ConnectionClosed, HandshakeError, WebSocketClient, WebSocketError

STAGES = ("speechEndToFinal", "finalToStarted", "startedToFirstDelta", "firstDeltaToPrimaryDone",
          "primaryDoneToCompleted", "firstDeltaToCompleted", "speechEndToStarted", "speechEndToFirstDelta",
          "speechEndToPrimaryDone", "speechEndToCompleted")
NS = 1_000_000


@dataclass
class Limits:
    ready_s: float = 15.0
    final_s: float = 12.0
    start_s: float = 20.0
    complete_s: float = 35.0
    grace_s: float = 1.5
    run_s: float = 150.0


@dataclass
class Final:
    t: int
    turn_id: str
    text: str
    segment: int


@dataclass
class Trace:
    response_id: str
    turn_id: str
    started: int
    first_delta: int | None = None
    primary_done: int | None = None
    completed: int | None = None
    cancelled: int | None = None
    streamed: str = ""
    completion: dict | None = None
    errors: list[str] = field(default_factory=list)

    @property
    def finished(self) -> int | None:
        return self.completed if self.completed is not None else self.cancelled


@dataclass
class SessionOutcome:
    status: str
    timings: dict[str, float | None]
    segment_finals: list[list[str]]
    response: dict | None
    target_responses: int = 0
    context_responses: int = 0
    cancelled_responses: int = 0
    error_codes: list[str] = field(default_factory=list)
    anomalies: list[str] = field(default_factory=list)
    korean_events: int = 0
    events: list[tuple[float, dict]] = field(default_factory=list)

    @property
    def answered(self) -> bool:
        return self.status == "ok"


class SessionTracker:
    def __init__(self, segments: tuple[Segment, ...], limits: Limits | None = None):
        self.segments = segments
        self.limits = limits or Limits()
        self.ready_ns: int | None = None
        self.sent_start: dict[int, int] = {}
        self.sent_speech_end: dict[int, int] = {}
        self.finals: list[Final] = []
        self.turn_segment: dict[str, int] = {}
        self.responses: dict[str, Trace] = {}
        self.order: list[str] = []
        self.error_codes: list[str] = []
        self.anomalies: list[str] = []
        self.korean_events = 0
        self.fatal: str | None = None
        self.closed = False
        self.transport_failed = False
        self.events: list[tuple[int, dict]] = []
        self._frames = {}
        for segment in segments:
            self._frames.setdefault(segment.first_frame, []).append(("start", segment.index))
            self._frames.setdefault(segment.speech_end_frame, []).append(("end", segment.index))

    @property
    def target(self) -> Segment:
        return self.segments[-1]

    def frame_sent(self, frame_index: int, t: int) -> None:
        for kind, index in self._frames.get(frame_index, ()):
            (self.sent_start if kind == "start" else self.sent_speech_end).setdefault(index, t)

    def anomaly(self, kind: str) -> None:
        self.anomalies.append(kind)

    def _assign(self, t: int, text: str) -> int:
        started = [segment for segment in self.segments if self.sent_start.get(segment.index, t + 1) <= t]
        if not started:
            return 0
        score, index = max((textnorm.containment(text, segment.text), segment.index) for segment in started)
        if score >= 0.34:
            return index
        assigned = {final.segment for final in self.finals}
        ended = [segment.index for segment in started
                 if self.sent_speech_end.get(segment.index, t + 1) <= t and segment.index not in assigned]
        return max(ended) if ended else started[-1].index

    def _active(self) -> Trace | None:
        if not self.order:
            return None
        latest = self.responses[self.order[-1]]
        return latest if latest.finished is None else None

    def event(self, t: int, event: dict) -> None:
        self.events.append((t, event))
        kind = event["type"]
        if kind == "session.ready":
            self.ready_ns = self.ready_ns or t
        elif kind == "transcript.final":
            segment = self._assign(t, event["text"])
            self.finals.append(Final(t, event["turnId"], event["text"], segment))
            self.turn_segment[event["turnId"]] = segment
        elif kind == "response.started":
            if event["responseId"] in self.responses:
                self.anomaly("duplicate_response_started")
                return
            self.responses[event["responseId"]] = Trace(event["responseId"], event["turnId"], t)
            self.order.append(event["responseId"])
        elif kind in ("response.delta", "response.completed", "response.cancelled", "response.korean"):
            trace = self.responses.get(event["responseId"])
            if trace is None:
                self.anomaly(f"{kind}_for_unknown_response")
                return
            if kind == "response.delta":
                if trace.finished is not None:
                    self.anomaly("delta_after_finish")
                    return
                if event["text"]:
                    trace.first_delta = trace.first_delta or t
                    trace.streamed += event["text"]
                if event.get("primaryDone"):
                    trace.primary_done = trace.primary_done or t
            elif kind == "response.completed":
                trace.completed = trace.completed or t
                trace.completion = event
            elif kind == "response.cancelled":
                trace.cancelled = trace.cancelled or t
            else:
                self.korean_events += 1
        elif kind == "error":
            self.error_codes.append(event["code"])
            active = self._active()
            if active is not None:
                active.errors.append(event["code"])
            if not event["retryable"]:
                self.fatal = event["code"]

    def target_finals(self) -> list[Final]:
        return [final for final in self.finals if final.segment == self.target.index]

    def bound(self) -> list[Trace]:
        return [self.responses[rid] for rid in self.order if self.turn_segment.get(self.responses[rid].turn_id) == self.target.index]

    def done(self, now: int) -> bool:
        if self.closed or self.fatal:
            return True
        speech_end = self.sent_speech_end.get(self.target.index)
        if speech_end is None:
            return False
        finals = self.target_finals()
        if not finals:
            return now - speech_end > self.limits.final_s * 1e9
        bound = self.bound()
        if not bound:
            return now - finals[-1].t > self.limits.start_s * 1e9
        active = [trace for trace in bound if trace.finished is None]
        if active:
            return now - active[-1].started > self.limits.complete_s * 1e9
        last = max(max(trace.finished for trace in bound), finals[-1].t)  # type: ignore[type-var]
        return now - last > self.limits.grace_s * 1e9

    def outcome(self, *, timed_out: bool = False, details: bool = False) -> SessionOutcome:
        bound = self.bound()
        completed = [trace for trace in bound if trace.completed is not None]
        selected = completed[-1] if completed else (bound[-1] if bound else None)
        finals = self.target_finals()
        speech_end = self.sent_speech_end.get(self.target.index)
        if selected is not None and selected.completion is not None:
            grounding = selected.completion.get("grounding")
            status = "grounding_unavailable" if grounding == "unavailable" or "grounding_unavailable" in selected.errors \
                else "ok"
        elif selected is not None:
            status = "response_cancelled" if selected.cancelled is not None else \
                ("response_error" if selected.errors else "response_timeout")
        elif self.transport_failed:
            status = "transport_error"
        elif self.fatal:
            status = f"start_error:{self.fatal}" if self.ready_ns is None else f"fatal:{self.fatal}"
        elif speech_end is None:
            status = "connection_closed" if self.closed else ("run_timeout" if timed_out else "incomplete")
        elif not finals:
            status = "no_target_final"
        else:
            status = "no_target_response"
        timings: dict[str, float | None] = {stage: None for stage in STAGES}
        response = None
        if selected is not None and speech_end is not None:
            final = next((item for item in reversed(finals) if item.turn_id == selected.turn_id), None)
            final_t = final.t if final else None

            def span(start: int | None, end: int | None) -> float | None:
                return None if start is None or end is None else round((end - start) / NS, 1)

            timings.update({
                "speechEndToFinal": span(speech_end, final_t), "finalToStarted": span(final_t, selected.started),
                "startedToFirstDelta": span(selected.started, selected.first_delta),
                "firstDeltaToPrimaryDone": span(selected.first_delta, selected.primary_done),
                "primaryDoneToCompleted": span(selected.primary_done, selected.completed),
                "firstDeltaToCompleted": span(selected.first_delta, selected.completed),
                "speechEndToStarted": span(speech_end, selected.started),
                "speechEndToFirstDelta": span(speech_end, selected.first_delta),
                "speechEndToPrimaryDone": span(speech_end, selected.primary_done),
                "speechEndToCompleted": span(speech_end, selected.completed),
            })
            if selected.completion is not None:
                suggestions = selected.completion.get("suggestions") or [selected.completion.get("text", "")]
                response = {
                    "primary": suggestions[0], "alternative": suggestions[1] if len(suggestions) > 1 else "",
                    "grounding": selected.completion.get("grounding"), "route": selected.completion.get("responseRoute"),
                    "prefetched": selected.completion.get("retrievalPrefetched"),
                    "sources": [source["title"] for source in selected.completion.get("sources", [])],
                    "primaryDoneSeen": selected.primary_done is not None,
                    "streamMatchesPrimary": selected.streamed.strip() == suggestions[0].strip(),
                }
        target_ids = {trace.response_id for trace in bound}
        segment_finals = [[final.text for final in self.finals if final.segment == segment.index]
                          for segment in self.segments]
        origin = self.sent_start.get(0)
        events = [(round((t - origin) / NS, 1) if origin else 0.0, event) for t, event in self.events] if details else []
        return SessionOutcome(
            status=status, timings=timings, segment_finals=segment_finals, response=response,
            target_responses=len(bound), context_responses=len([rid for rid in self.order if rid not in target_ids]),
            cancelled_responses=len([trace for trace in self.responses.values() if trace.cancelled is not None]),
            error_codes=list(self.error_codes), anomalies=list(self.anomalies), korean_events=self.korean_events,
            events=events)


Connector = Callable[[], Awaitable[WebSocketClient]]

# The server accepts about 4 s of audio at once and 2x real time after that; faster audio fails with invalid_audio.
# A client that stalls (a busy machine, a throttled background task) therefore never sends more than one second of
# overdue audio at once: a longer delay moves the rest of the timeline instead. No frame is dropped.
MAX_BACKLOG_FRAMES = 50


def next_due(begin: int, index: int, interval: int, now: int) -> tuple[int, int]:
    """Return the schedule start, moved if frame ``index`` is more than MAX_BACKLOG_FRAMES late, and the frame's due time."""
    due = begin + index * interval
    late = now - due - MAX_BACKLOG_FRAMES * interval
    if late > 0:
        begin += late
        due += late
    return begin, due


async def _read(client: WebSocketClient, tracker: SessionTracker, ready: asyncio.Event,
                clock: Callable[[], int]) -> None:
    try:
        while True:
            opcode, payload = await client.receive()
            t = clock()
            if opcode != OP_TEXT:
                tracker.anomaly("binary_server_message")
                continue
            try:
                event = parse_event(payload)
            except ProtocolError as error:
                tracker.anomaly(error.kind)
                continue
            tracker.event(t, event)
            if event["type"] == "session.ready" or tracker.fatal:
                ready.set()
    except ConnectionClosed:
        tracker.closed = True
    except (WebSocketError, asyncio.IncompleteReadError, OSError):
        tracker.closed = True
        tracker.transport_failed = True
    finally:
        ready.set()


async def run_session(connect: Connector, timeline: Timeline, options: dict, *, pace: float = 1.0,
                      limits: Limits | None = None, details: bool = False,
                      clock: Callable[[], int] = time.perf_counter_ns) -> SessionOutcome:
    """Run one item: start, stream the timeline (then silence) in real time, stop, and return the outcome."""
    limits = limits or Limits()
    tracker = SessionTracker(timeline.segments, limits)
    try:
        client = await connect()
    except HandshakeError as error:
        return SessionOutcome(f"handshake_{error.status}", {stage: None for stage in STAGES},
                              [[] for _ in timeline.segments], None)
    except (OSError, asyncio.TimeoutError, WebSocketError):
        return SessionOutcome("transport_error", {stage: None for stage in STAGES}, [[] for _ in timeline.segments], None)
    ready = asyncio.Event()
    reader = asyncio.create_task(_read(client, tracker, ready, clock))
    timed_out = False
    try:
        await client.send_text(start_message(options))
        try:
            await asyncio.wait_for(ready.wait(), limits.ready_s)
        except asyncio.TimeoutError:
            tracker.fatal = tracker.fatal or "ready_timeout"
        if tracker.ready_ns is not None and not tracker.fatal and not tracker.closed:
            interval = int(FRAME_SAMPLES / SAMPLE_RATE * 1e9 * pace)
            begin = clock()
            deadline = begin + int(limits.run_s * 1e9)
            index = 0
            while not tracker.closed and not tracker.fatal:
                now = clock()
                if index >= timeline.frames and tracker.done(now):
                    break
                if now > deadline:
                    timed_out = True
                    break
                begin, due = next_due(begin, index, interval, now)
                if due > now:
                    await asyncio.sleep((due - now) / 1e9)
                await client.send_binary(timeline.frame(index))
                tracker.frame_sent(index, clock())
                index += 1
    except (ConnectionClosed, WebSocketError, OSError):
        tracker.closed = True
    finally:
        if not client.closed:
            try:
                await client.send_text(json.dumps(STOP_MESSAGE))
                await asyncio.wait_for(asyncio.shield(reader), 3.0)
            except (asyncio.TimeoutError, ConnectionClosed, WebSocketError, OSError):
                pass
        await client.close()
        if not reader.done():
            reader.cancel()
        await asyncio.gather(reader, return_exceptions=True)
    return tracker.outcome(timed_out=timed_out, details=details)
