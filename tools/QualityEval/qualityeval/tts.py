"""Azure AI Speech text-to-speech (REST, Microsoft Entra auth) with a git-ignored on-disk cache.

Audio is requested as raw 16 kHz 16-bit mono PCM, exactly what the meeting protocol streams. Synthesis only happens
when explicitly allowed (it is billable); otherwise the cache must already contain every clip.
"""

from __future__ import annotations

import hashlib
import json
import os
import time
from dataclasses import dataclass
from pathlib import Path
from typing import Callable, Mapping
from xml.sax.saxutils import escape

from .audio import Clip, clip_from_pcm, tone_clip
from .httpapi import Transport, http_request
from .privacy import DEFAULT_CACHE_DIR, safe_write_bytes

OUTPUT_FORMAT = "raw-16khz-16bit-mono-pcm"
DEFAULT_ACCENTS = ("en-US", "en-GB", "en-IN")
DEFAULT_VOICES: dict[str, dict[str, str]] = {
    "en-US": {"A": "en-US-GuyNeural", "B": "en-US-AriaNeural"},
    "en-GB": {"A": "en-GB-RyanNeural", "B": "en-GB-SoniaNeural"},
    "en-IN": {"A": "en-IN-PrabhatNeural", "B": "en-IN-NeerjaNeural"},
    "en-AU": {"A": "en-AU-WilliamNeural", "B": "en-AU-NatashaNeural"},
}


class TtsError(RuntimeError):
    pass


@dataclass(frozen=True)
class SpeechEndpoint:
    url: str
    resource_id: str | None


def endpoint_from_env(environ: Mapping[str, str] = os.environ) -> SpeechEndpoint:
    """EVAL_SPEECH_ENDPOINT (resource custom domain) or EVAL_SPEECH_REGION, plus EVAL_SPEECH_RESOURCE_ID."""
    endpoint = environ.get("EVAL_SPEECH_ENDPOINT", "").strip()
    region = environ.get("EVAL_SPEECH_REGION", "").strip()
    resource_id = environ.get("EVAL_SPEECH_RESOURCE_ID", "").strip() or None
    if endpoint:
        if not endpoint.startswith("https://"):
            raise TtsError("EVAL_SPEECH_ENDPOINT must be the https:// custom-domain endpoint of the Speech resource.")
        return SpeechEndpoint(endpoint.rstrip("/") + "/tts/cognitiveservices/v1", resource_id)
    if region:
        if not region.isalnum():
            raise TtsError("EVAL_SPEECH_REGION must be an Azure region name such as koreacentral.")
        if not resource_id:
            raise TtsError("Regional TTS with Microsoft Entra auth also needs EVAL_SPEECH_RESOURCE_ID.")
        return SpeechEndpoint(f"https://{region}.tts.speech.microsoft.com/cognitiveservices/v1", resource_id)
    raise TtsError("Set EVAL_SPEECH_ENDPOINT (or EVAL_SPEECH_REGION with EVAL_SPEECH_RESOURCE_ID) to synthesize audio.")


def ssml(voice: str, text: str) -> str:
    locale = "-".join(voice.split("-")[:2])
    return (f"<speak version='1.0' xml:lang='{escape(locale)}'><voice name='{escape(voice, {chr(39): '&apos;'})}'>"
            f"{escape(text, {chr(39): '&apos;', chr(34): '&quot;'})}</voice></speak>")


class AzureTts:
    def __init__(self, endpoint: SpeechEndpoint, token: Callable[[], str], *, transport: Transport = http_request,
                 timeout: float = 30.0, sleep: Callable[[float], None] = time.sleep):
        self._endpoint = endpoint
        self._token = token
        self._transport = transport
        self._timeout = timeout
        self._sleep = sleep

    def _authorization(self) -> str:
        token = self._token()
        resource_id = self._endpoint.resource_id
        return f"Bearer aad#{resource_id}#{token}" if resource_id else f"Bearer {token}"

    def synthesize(self, voice: str, text: str) -> bytes:
        body = ssml(voice, text).encode("utf-8")
        for attempt in range(5):
            headers = {"Authorization": self._authorization(), "Content-Type": "application/ssml+xml",
                       "X-Microsoft-OutputFormat": OUTPUT_FORMAT, "User-Agent": "voice-assistant-quality-eval"}
            status, data = self._transport("POST", self._endpoint.url, headers, body, self._timeout)
            if status == 200:
                if len(data) < 3200 or len(data) % 2:
                    raise TtsError("Azure TTS returned unexpected audio.")
                return data
            if status in (429, 500, 502, 503) and attempt < 4:
                self._sleep(2.0 * (attempt + 1))
                continue
            raise TtsError(f"Azure TTS failed with HTTP {status}.")
        raise TtsError("Azure TTS kept throttling.")


class TtsCache:
    def __init__(self, directory: str | Path = DEFAULT_CACHE_DIR):
        self.directory = Path(directory)

    @staticmethod
    def key(voice: str, text: str) -> str:
        return hashlib.sha256(f"v1|{OUTPUT_FORMAT}|{voice}|{text}".encode("utf-8")).hexdigest()

    def load(self, voice: str, text: str) -> Clip | None:
        key = self.key(voice, text)
        pcm_path, meta_path = self.directory / f"{key}.pcm", self.directory / f"{key}.json"
        if not pcm_path.is_file() or not meta_path.is_file():
            return None
        meta = json.loads(meta_path.read_text(encoding="utf-8"))
        pcm = pcm_path.read_bytes()
        if meta.get("samples") != len(pcm) // 2:
            return None
        return Clip(pcm, int(meta["speechStart"]), int(meta["speechEnd"]))

    def store(self, voice: str, text: str, pcm: bytes) -> Clip:
        clip = clip_from_pcm(pcm)
        key = self.key(voice, text)
        safe_write_bytes(self.directory / f"{key}.pcm", clip.pcm, overwrite=True)
        meta = {"schemaVersion": 1, "format": OUTPUT_FORMAT, "voice": voice,
                "textSha256": hashlib.sha256(text.encode("utf-8")).hexdigest(), "samples": clip.samples,
                "speechStart": clip.speech_start, "speechEnd": clip.speech_end}
        safe_write_bytes(self.directory / f"{key}.json", json.dumps(meta).encode("utf-8"), overwrite=True)
        return clip


class TtsAudio:
    """Audio source backed by the cache; synthesizes missing clips only when a synthesizer is given."""
    name = "azure-tts"

    def __init__(self, cache: TtsCache, voices: Mapping[str, Mapping[str, str]] = DEFAULT_VOICES,
                 synthesizer: AzureTts | None = None):
        self.cache = cache
        self.voices = voices
        self.synthesizer = synthesizer
        self.synthesized = 0

    def voice(self, accent: str, speaker: str) -> str:
        try:
            return self.voices[accent][speaker]
        except KeyError as error:
            raise TtsError(f"No voice configured for accent {accent} speaker {speaker}.") from error

    def clip(self, text: str, speaker: str, accent: str) -> Clip:
        voice = self.voice(accent, speaker)
        cached = self.cache.load(voice, text)
        if cached is not None:
            return cached
        if self.synthesizer is None:
            raise TtsError("A clip is missing from the TTS cache. Run 'synthesize --allow-tts' or add --allow-tts.")
        clip = self.cache.store(voice, text, self.synthesizer.synthesize(voice, text))
        self.synthesized += 1
        return clip


class ToneAudio:
    """Offline placeholder audio for Fake-mode dry runs; it carries timing, not words."""
    name = "synthetic-tone"

    def __init__(self) -> None:
        self._clips: dict[str, Clip] = {}

    def clip(self, text: str, speaker: str, accent: str) -> Clip:
        if text not in self._clips:
            self._clips[text] = tone_clip(text)
        return self._clips[text]
