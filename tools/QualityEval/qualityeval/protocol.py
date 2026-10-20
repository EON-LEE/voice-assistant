"""Live Coach WebSocket protocol v1 (contracts/websocket-v1.md): start message and strict event parsing.

``live_ui_options`` mirrors ``sessionOptions()`` in src/VoiceAssistant.Web/src/live.ts so an evaluation exercises the
same server path as the Live overlay. ``parse_event`` mirrors protocol.ts and additionally accepts the optional
``primaryDone: true`` flag on ``response.delta``; an empty delta text is valid only together with that flag.
"""

from __future__ import annotations

import json
from typing import Any

RESPONSE_MODES = ("balanced", "grounded", "conversation")
GROUNDING = ("disabled", "grounded", "unavailable", "no_matches")
ROUTES = ("transcript", "profile", "knowledge")
MAX_EVENT_BYTES = 65536
MAX_TEXT = 32768
START_LIMIT = 32768
STOP_MESSAGE = {"type": "session.stop"}


class ProtocolError(ValueError):
    """A server event that violates the v1 contract."""

    def __init__(self, message: str, kind: str = "invalid_event"):
        super().__init__(message)
        self.kind = kind


def live_ui_options(response_mode: str = "grounded", reply_korean: bool = False, topic: str = "",
                    phrases: list[str] | tuple[str, ...] = ()) -> dict[str, Any]:
    """The options the Live page sends: semantic segmentation, a 1000 ms settle window and 1100 ms silence."""
    if response_mode not in RESPONSE_MODES:
        raise ValueError(f"responseMode must be one of {', '.join(RESPONSE_MODES)}")
    topic = topic.strip()
    if len(topic) > 300 or any(ord(character) < 32 for character in topic):
        raise ValueError("topic must be at most 300 characters without control characters")
    cleaned = [phrase.strip() for phrase in phrases]
    if len(cleaned) > 40 or any(not phrase or len(phrase) > 64 for phrase in cleaned) or sum(map(len, cleaned)) > 2048:
        raise ValueError("phrases: at most 40 nonblank terms of at most 64 characters, 2048 characters in total")
    return {
        "responseMode": response_mode,
        "profile": {"name": "", "role": "", "project": ""},
        "profileConfirmed": False,
        "topic": topic,
        "phrases": cleaned,
        "endSilenceMs": 1100,
        "semanticSegmentation": True,
        "replySettleMs": 1000,
        "replyKorean": bool(reply_korean),
    }


def start_message(options: dict[str, Any] | None) -> str:
    message: dict[str, Any] = {
        "type": "session.start", "protocolVersion": 1,
        "audio": {"encoding": "pcm_s16le", "sampleRate": 16000, "channels": 1},
    }
    if options is not None:
        message["options"] = options
    text = json.dumps(message, ensure_ascii=False, separators=(",", ":"))
    if len(text.encode("utf-8")) > START_LIMIT:
        raise ValueError("session.start exceeds the 32 KiB limit")
    return text


def _text(event: dict, key: str, *, allow_empty: bool = True) -> str:
    value = event.get(key)
    if not isinstance(value, str) or len(value) > MAX_TEXT:
        raise ProtocolError(f"Invalid {key} in {event.get('type')} event.")
    if not allow_empty and not value:
        raise ProtocolError(f"Empty {key} in {event.get('type')} event.")
    return value


def _optional_bool(event: dict, key: str) -> bool | None:
    value = event.get(key)
    if value is not None and not isinstance(value, bool):
        raise ProtocolError(f"Invalid {key} flag in {event.get('type')} event.")
    return value


def parse_event(data: str | bytes) -> dict[str, Any]:
    """Validate one server text message and return a normalized event dictionary."""
    if isinstance(data, bytes):
        if len(data) > MAX_EVENT_BYTES:
            raise ProtocolError("Server message exceeds 64 KiB.")
        try:
            data = data.decode("utf-8")
        except UnicodeDecodeError as error:
            raise ProtocolError("Server message is not UTF-8.") from error
    if not isinstance(data, str) or len(data.encode("utf-8")) > MAX_EVENT_BYTES:
        raise ProtocolError("Server message must be text of at most 64 KiB.")
    try:
        event = json.loads(data)
    except json.JSONDecodeError as error:
        raise ProtocolError("Server message is not JSON.") from error
    if not isinstance(event, dict):
        raise ProtocolError("Server event must be a JSON object.")
    kind = _text(event, "type", allow_empty=False)
    if kind == "session.ready":
        if event.get("protocolVersion", 1) != 1:
            raise ProtocolError("Unsupported protocol version.")
        ready: dict[str, Any] = {"type": kind}
        for flag in ("replyKorean", "sessionUpdate"):
            if _optional_bool(event, flag):
                ready[flag] = True
        return ready
    if kind == "session.updated":
        mode = _text(event, "responseMode")
        if mode not in RESPONSE_MODES or not isinstance(event.get("replyKorean"), bool):
            raise ProtocolError("Invalid session.updated event.")
        return {"type": kind, "responseMode": mode, "replyKorean": event["replyKorean"]}
    if kind == "error":
        if not isinstance(event.get("retryable"), bool):
            raise ProtocolError("Invalid retryable error flag.")
        return {"type": kind, "code": _text(event, "code", allow_empty=False), "message": _text(event, "message"),
                "retryable": event["retryable"]}
    turn_id = _text(event, "turnId", allow_empty=False)
    if kind in ("transcript.partial", "transcript.final"):
        revision = event.get("revision")
        if not isinstance(revision, int) or isinstance(revision, bool) or revision < 0:
            raise ProtocolError("Invalid transcript revision.")
        return {"type": kind, "turnId": turn_id, "revision": revision, "text": _text(event, "text")}
    response_id = _text(event, "responseId", allow_empty=False)
    if kind in ("response.started", "response.cancelled"):
        return {"type": kind, "turnId": turn_id, "responseId": response_id}
    if kind == "response.delta":
        text = _text(event, "text")
        primary_done = event.get("primaryDone")
        if primary_done is not None and primary_done is not True:
            raise ProtocolError("primaryDone must be true when present.", "invalid_primary_done")
        if not text and primary_done is not True:
            raise ProtocolError("Empty response.delta text is only valid with primaryDone: true.", "empty_delta")
        delta = {"type": kind, "turnId": turn_id, "responseId": response_id, "text": text}
        if primary_done:
            delta["primaryDone"] = True
        return delta
    if kind == "response.korean":
        index = event.get("index")
        if index not in (0, 1) or isinstance(index, bool):
            raise ProtocolError("Invalid reply line index.")
        if "failed" in event:
            if event["failed"] is not True or "korean" in event:
                raise ProtocolError("Invalid translation failure.")
            return {"type": kind, "turnId": turn_id, "responseId": response_id, "index": index, "failed": True}
        korean = _text(event, "korean")
        if not korean.strip() or len(korean) > 400:
            raise ProtocolError("Invalid korean text.")
        return {"type": kind, "turnId": turn_id, "responseId": response_id, "index": index, "korean": korean}
    if kind == "response.completed":
        sources = event.get("sources")
        if not isinstance(sources, list) or len(sources) > 20:
            raise ProtocolError("Invalid response sources.")
        parsed_sources = []
        for source in sources:
            if not isinstance(source, dict) or not isinstance(source.get("title"), str) or \
                    not isinstance(source.get("url"), str) or \
                    (source.get("updatedAt") is not None and not isinstance(source.get("updatedAt"), str)):
                raise ProtocolError("Invalid source fields.")
            parsed_sources.append({"title": source["title"], "url": source["url"], "updatedAt": source.get("updatedAt")})
        grounding = event.get("grounding")
        if grounding is not None and grounding not in GROUNDING:
            raise ProtocolError("Invalid grounding status.")
        route = event.get("responseRoute")
        if route is not None and route not in ROUTES:
            raise ProtocolError("Invalid response route.")
        text = _text(event, "text")
        suggestions = event.get("suggestions")
        if suggestions is not None:
            if not isinstance(suggestions, list) or not 1 <= len(suggestions) <= 2 or any(
                    not isinstance(value, str) or not value.strip() or len(value) > 8000 for value in suggestions):
                raise ProtocolError("Invalid response suggestions.")
            if suggestions[0] != text:
                raise ProtocolError("The primary suggestion must match response text.")
        completed = {
            "type": kind, "turnId": turn_id, "responseId": response_id, "text": text, "sources": parsed_sources,
            "grounding": grounding, "responseRoute": route,
            "retrievalPrefetched": _optional_bool(event, "retrievalPrefetched"),
            "suggestions": list(suggestions) if suggestions is not None else [text],
        }
        respond_now = _optional_bool(event, "respondNow")
        if respond_now is not None:
            completed["respondNow"] = respond_now
        return completed
    raise ProtocolError(f"Unsupported server event: {kind}", "unknown_event")
