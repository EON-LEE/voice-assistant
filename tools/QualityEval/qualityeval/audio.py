"""PCM16 mono 16 kHz audio: utterance clips, speech bounds and the per-item streaming timeline."""

from __future__ import annotations

import math
import sys
from array import array
from dataclasses import dataclass

SAMPLE_RATE = 16000
SAMPLE_BYTES = 2
FRAME_SAMPLES = 320
FRAME_BYTES = FRAME_SAMPLES * SAMPLE_BYTES
SILENCE_FRAME = bytes(FRAME_BYTES)
SPEECH_THRESHOLD = 200


@dataclass(frozen=True)
class Clip:
    """One synthesized utterance; speech_start/speech_end are sample offsets inside the clip (end exclusive)."""
    pcm: bytes
    speech_start: int
    speech_end: int

    @property
    def samples(self) -> int:
        return len(self.pcm) // SAMPLE_BYTES


@dataclass(frozen=True)
class Segment:
    index: int
    role: str
    speaker: str
    text: str
    start_sample: int
    speech_start_sample: int
    speech_end_sample: int
    end_sample: int

    @property
    def first_frame(self) -> int:
        return self.start_sample // FRAME_SAMPLES

    @property
    def speech_start_frame(self) -> int:
        return self.speech_start_sample // FRAME_SAMPLES

    @property
    def speech_end_frame(self) -> int:
        """Frame that carries the last speech sample."""
        return max(self.speech_start_sample, self.speech_end_sample - 1) // FRAME_SAMPLES


@dataclass(frozen=True)
class Timeline:
    pcm: bytes
    segments: tuple[Segment, ...]

    @property
    def frames(self) -> int:
        return len(self.pcm) // FRAME_BYTES

    def frame(self, index: int) -> bytes:
        if index >= self.frames:
            return SILENCE_FRAME
        return self.pcm[index * FRAME_BYTES:(index + 1) * FRAME_BYTES]

    @property
    def target(self) -> Segment:
        return self.segments[-1]


def _samples(pcm: bytes) -> array:
    values = array("h")
    values.frombytes(pcm[:len(pcm) - len(pcm) % SAMPLE_BYTES])
    if sys.byteorder == "big":
        values.byteswap()
    return values


def _pcm(values: array) -> bytes:
    if sys.byteorder == "big":
        values = array("h", values)
        values.byteswap()
    return values.tobytes()


def silence(milliseconds: int) -> bytes:
    return bytes(round(milliseconds * SAMPLE_RATE / 1000) * SAMPLE_BYTES)


def speech_bounds(pcm: bytes, threshold: int = SPEECH_THRESHOLD) -> tuple[int, int]:
    """First and one-past-last sample whose magnitude reaches the threshold; (0, 0) for silence."""
    values = _samples(pcm)
    start = next((index for index, value in enumerate(values) if abs(value) >= threshold), None)
    if start is None:
        return 0, 0
    end = next(index for index in range(len(values) - 1, -1, -1) if abs(values[index]) >= threshold) + 1
    return start, end


def clip_from_pcm(pcm: bytes) -> Clip:
    if len(pcm) % SAMPLE_BYTES:
        pcm = pcm[:-1]
    start, end = speech_bounds(pcm)
    if end <= start:
        raise ValueError("The audio clip contains no speech above the detection threshold.")
    return Clip(pcm, start, end)


def tone_clip(text: str) -> Clip:
    """Deterministic nonzero placeholder 'speech' for offline Fake-mode runs (about 0.33 s per word)."""
    word_count = max(1, len([word for word in text.split() if any(character.isalnum() for character in word)]))
    count = int(SAMPLE_RATE * max(0.6, 0.33 * word_count))
    fade = int(SAMPLE_RATE * 0.01)
    values = array("h", bytes(count * SAMPLE_BYTES))
    for index in range(count):
        envelope = min(1.0, index / fade, (count - 1 - index) / fade)
        phase = 2 * math.pi * index / SAMPLE_RATE
        sample = 0.18 * math.sin(phase * 180) + 0.08 * math.sin(phase * 360) + 0.04 * math.sin(phase * 540)
        value = int(32767 * envelope * sample * (0.75 + 0.25 * math.sin(phase * 3)))
        values[index] = value if value != 0 else (1 if index % 2 else -1)
    pcm = _pcm(values)
    return Clip(pcm, 0, count)


def build_timeline(utterances: list[tuple[str, str, Clip]], *, lead_ms: int = 400, gap_ms: int = 700) -> Timeline:
    """Concatenate (speaker, text, clip) utterances; the last one is the target question."""
    if not utterances:
        raise ValueError("A timeline needs at least one utterance.")
    pcm = bytearray(silence(lead_ms))
    segments = []
    for index, (speaker, text, clip) in enumerate(utterances):
        start = len(pcm) // SAMPLE_BYTES
        pcm += clip.pcm
        segments.append(Segment(
            index=index, role="target" if index == len(utterances) - 1 else "context", speaker=speaker, text=text,
            start_sample=start, speech_start_sample=start + clip.speech_start, speech_end_sample=start + clip.speech_end,
            end_sample=start + clip.samples))
        if index < len(utterances) - 1:
            pcm += silence(gap_ms)
    remainder = len(pcm) % FRAME_BYTES
    if remainder:
        pcm += bytes(FRAME_BYTES - remainder)
    return Timeline(bytes(pcm), tuple(segments))
