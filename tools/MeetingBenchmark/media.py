"""Approved local media preparation. No downloads, implicit FFmpeg, or cloud calls."""
from __future__ import annotations

import hashlib
import json
import math
import re
import subprocess
import tempfile
import wave
from array import array
from decimal import Decimal
from pathlib import Path

SAMPLE_RATE = 16000
FPS = 25
PADDING_SECONDS = 3
MAX_SOURCE_BYTES = 4 * 1024**3
MAX_CLIP_SECONDS = 120


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def number(value, name: str) -> Decimal:
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        raise ValueError(f"{name} must be a finite JSON number")
    result = Decimal(str(value))
    if not result.is_finite():
        raise ValueError(f"{name} must be finite")
    return result


def validate_manifest(manifest: dict) -> None:
    if manifest.get("schemaVersion") != 1:
        raise ValueError("Unsupported manifest version")
    source = manifest.get("source", {})
    for key in ("url", "license", "licenseUrl", "title"):
        if not isinstance(source.get(key), str) or not source[key].strip():
            raise ValueError(f"Missing source {key}")
    for key in ("url", "licenseUrl"):
        if not source[key].startswith("https://"):
            raise ValueError("Source and license URLs must be HTTPS")
    if not re.fullmatch(r"[a-f0-9]{64}", source.get("sha256", "")):
        raise ValueError("An explicit source SHA256 is required")
    if not isinstance(source.get("attribution"), list) or not source["attribution"] or any(
        not isinstance(item, str) or not item.strip() for item in source["attribution"]
    ):
        raise ValueError("Explicit attribution required")
    clips = manifest.get("clips")
    if not isinstance(clips, list) or not 1 <= len(clips) <= 20:
        raise ValueError("Manifest requires 1-20 clips")
    seen = set()
    for clip in clips:
        clip_id = clip.get("id", "")
        if not re.fullmatch(r"[a-z0-9][a-z0-9-]{0,63}", clip_id) or clip_id in seen:
            raise ValueError("Clip IDs must be unique safe lowercase names")
        seen.add(clip_id)
        start = number(clip.get("startSeconds"), "startSeconds")
        end = number(clip.get("endSeconds"), "endSeconds")
        if start < 0 or not 0 < end - start <= MAX_CLIP_SECONDS:
            raise ValueError("Clip interval must be positive, at most 120 seconds")
        for boundary in (start, end):
            if boundary * FPS != (boundary * FPS).to_integral_value():
                raise ValueError("Clip boundaries must align to the 25fps output grid (40ms)")
        annotation = clip.get("speechEnd")
        if annotation is not None:
            if annotation.get("kind") != "human-annotated" or not annotation.get("provenance"):
                raise ValueError("Speech end requires explicit human annotation provenance")
            timestamp = number(annotation.get("sourceSeconds"), "speechEnd.sourceSeconds")
            if not start <= timestamp <= end:
                raise ValueError("Annotated speech end must lie within the source interval")


def pcm_info(path: Path) -> tuple[bytes, int]:
    with wave.open(str(path), "rb") as stream:
        if (stream.getnchannels(), stream.getsampwidth(), stream.getframerate(), stream.getcomptype()) != (
            1, 2, SAMPLE_RATE, "NONE"
        ):
            raise ValueError("Expected uncompressed mono PCM16 at 16000Hz")
        frames = stream.getnframes()
        data = stream.readframes(frames)
    if len(data) != frames * 2:
        raise ValueError("Truncated PCM data")
    return data, frames


def verify_tail(path: Path, content_frames: int, tolerance: int = 0) -> dict:
    if isinstance(tolerance, bool) or not isinstance(tolerance, int) or not 0 <= tolerance <= 8:
        raise ValueError("PCM silence tolerance must be an integer from 0 through 8")
    data, frames = pcm_info(path)
    if frames != content_frames + SAMPLE_RATE * PADDING_SECONDS:
        raise ValueError("Output is not exactly source interval plus three seconds")
    tail = array("h")
    tail.frombytes(data[content_frames * 2:])
    import sys
    if sys.byteorder != "little":
        tail.byteswap()
    peak = max(abs(sample) for sample in tail)
    if peak > tolerance:
        raise ValueError("Trailing PCM is not silent; possible source leakage or encoding artifacts")
    return {
        "sampleRate": SAMPLE_RATE, "totalSamples": frames, "contentSamples": content_frames,
        "paddingSamples": len(tail), "durationSeconds": frames / SAMPLE_RATE,
        "tailPeakPcm16": peak, "allowedTailPeakPcm16": tolerance,
        "paddingVerified": True,
    }


def run_ffmpeg(executable: Path, arguments: list[str], timeout: int) -> str:
    try:
        result = subprocess.run(
            [str(executable), *arguments], capture_output=True, text=True, timeout=timeout,
            encoding="utf-8", errors="replace", check=False,
        )
    except subprocess.TimeoutExpired as exc:
        raise RuntimeError("FFmpeg exceeded bounded process timeout") from exc
    if result.returncode:
        # Media decoder errors are useful, but never echo arbitrarily large metadata.
        raise RuntimeError(f"FFmpeg failed ({result.returncode}): {result.stderr[-1500:]}")
    return result.stdout


def write_pcm(path: Path, data: bytes) -> None:
    with wave.open(str(path), "wb") as stream:
        stream.setnchannels(1)
        stream.setsampwidth(2)
        stream.setframerate(SAMPLE_RATE)
        stream.writeframes(data)


def prepare(manifest: dict, source_path: Path, ffmpeg_path: Path, output_dir: Path, timeout: int = 300) -> dict:
    validate_manifest(manifest)
    if not isinstance(timeout, int) or isinstance(timeout, bool) or not 1 <= timeout <= 1800:
        raise ValueError("FFmpeg timeout must be 1-1800 seconds per invocation")
    source_path, ffmpeg_path, output_dir = source_path.resolve(), ffmpeg_path.resolve(), output_dir.resolve()
    if not source_path.is_file() or source_path.stat().st_size > MAX_SOURCE_BYTES:
        raise ValueError("Approved local source missing or exceeds 4GiB bound")
    if not ffmpeg_path.is_file():
        raise ValueError("Explicit FFmpeg executable path is required")
    if output_dir.exists():
        raise FileExistsError("Output directory already exists; artifacts are never overwritten")
    actual_hash = sha256(source_path)
    if actual_hash != manifest["source"]["sha256"]:
        raise ValueError("Source SHA256 does not match the approved manifest")
    version = run_ffmpeg(ffmpeg_path, ["-version"], timeout).splitlines()[0]
    output_dir.mkdir(parents=False)
    created: list[Path] = []
    report = {
        "schemaVersion": 1, "source": manifest["source"], "reference": manifest.get("reference"),
        "ffmpeg": {"path": str(ffmpeg_path), "sha256": sha256(ffmpeg_path), "version": version},
        "paddingSeconds": PADDING_SECONDS, "outputVideoFps": FPS, "clips": [],
        "speechEndPolicy": "No acoustic end inferred. Null annotations stay unknown.",
        "videoBoundaryPolicy": "trim selects source frames before resampling to 25fps; source frame cadence may shift visible content within one source frame. Audio is sample-exact.",
    }
    try:
        for clip in manifest["clips"]:
            start, end = Decimal(str(clip["startSeconds"])), Decimal(str(clip["endSeconds"]))
            first, last = int(start * SAMPLE_RATE), int(end * SAMPLE_RATE)
            frames = last - first
            wav_path = output_dir / (clip["id"] + ".wav")
            video_path = output_dir / (clip["id"] + ".mp4")
            created.extend((wav_path, video_path))
            with tempfile.TemporaryDirectory(prefix="meeting-benchmark-") as temp:
                unpadded = Path(temp) / "trimmed.wav"
                extraction = [
                    "-nostdin", "-hide_banner", "-loglevel", "error", "-n", "-t", str(end), "-i", str(source_path),
                    "-map", "0:a:0", "-vn", "-af",
                    f"aresample={SAMPLE_RATE},atrim=start_sample={first}:end_sample={last},asetpts=PTS-STARTPTS",
                    "-ac", "1", "-ar", str(SAMPLE_RATE), "-c:a", "pcm_s16le", str(unpadded),
                ]
                run_ffmpeg(ffmpeg_path, extraction, timeout)
                data, extracted_frames = pcm_info(unpadded)
                if extracted_frames != frames:
                    raise ValueError("Source ended early or trim did not produce the exact requested samples")
                write_pcm(wav_path, data + bytes(PADDING_SECONDS * SAMPLE_RATE * 2))
                verification = verify_tail(wav_path, frames)
                video_filter = (
                    f"[0:v:0]trim=start=0:end={end - start},setpts=PTS-STARTPTS,"
                    f"scale=640:-2,fps={FPS},tpad=stop_mode=clone:stop=1,"
                    f"trim=end_frame={int((end - start) * FPS)},"
                    f"tpad=stop_mode=clone:stop={PADDING_SECONDS * FPS}[v]"
                )
                encoding = [
                    "-nostdin", "-hide_banner", "-loglevel", "error", "-n",
                    "-ss", str(start), "-accurate_seek", "-t", str(end - start), "-i", str(source_path),
                    "-i", str(wav_path), "-filter_complex", video_filter,
                    "-map", "[v]", "-map", "1:a:0", "-c:v", "libx264", "-preset", "fast",
                    "-pix_fmt", "yuv420p", "-c:a", "flac", "-strict", "experimental",
                    "-movflags", "+faststart", "-map_metadata", "-1", str(video_path),
                ]
                run_ffmpeg(ffmpeg_path, encoding, timeout)
                decoded = Path(temp) / "decoded.wav"
                decoding = [
                    "-nostdin", "-hide_banner", "-loglevel", "error", "-n", "-i", str(video_path),
                    "-map", "0:a:0", "-c:a", "pcm_s16le", "-ar", str(SAMPLE_RATE), "-ac", "1", str(decoded),
                ]
                run_ffmpeg(ffmpeg_path, decoding, timeout)
                decoded_pcm, _ = pcm_info(decoded)
                if decoded_pcm != pcm_info(wav_path)[0]:
                    raise ValueError("Final clip decoded audio differs from authoritative trimmed-and-padded PCM")
                verify_tail(decoded, frames)
                progress_args = [
                    "-nostdin", "-hide_banner", "-loglevel", "error", "-i", str(video_path),
                    "-map", "0:v:0", "-an", "-progress", "pipe:1", "-nostats", "-f", "null", "-",
                ]
                progress = run_ffmpeg(ffmpeg_path, progress_args, timeout)
                counts = re.findall(r"^frame=(\d+)$", progress, re.MULTILINE)
                expected_video_frames = int((end - start + PADDING_SECONDS) * FPS)
                if not counts or int(counts[-1]) != expected_video_frames:
                    raise ValueError(f"Video frame count mismatch: expected {expected_video_frames}, observed {counts[-1] if counts else 'unknown'}")
                report["clips"].append({
                    "id": clip["id"], "sourceIntervalSeconds": [float(start), float(end)],
                    "sourceSampleInterval": [first, last], "speechEnd": clip.get("speechEnd"),
                    "clipBoundaryIsSpeechEnd": False,
                    "wav": {"file": wav_path.name, "sha256": sha256(wav_path), **verification},
                    "video": {"file": video_path.name, "sha256": sha256(video_path),
                              "frames": expected_video_frames, "durationSeconds": expected_video_frames / FPS,
                              "audioCodec": "FLAC", "videoCodec": "H264", "decodedAudioMatchesWav": True,
                              "seekPolicy": "FFmpeg input accurate_seek discards preroll; trim relative 0:duration before all frame padding",
                              "browserCodecSupportVerified": False},
                    "pipeline": {"extractArguments": extraction, "videoArguments": encoding,
                                 "decodeVerificationArguments": decoding, "frameVerificationArguments": progress_args,
                                 "pcmPadding": "Append exactly 48000 signed PCM16 zero samples AFTER exact source trim"},
                })
        metadata = output_dir / "clips.json"
        created.append(metadata)
        with metadata.open("x", encoding="utf-8") as stream:
            json.dump(report, stream, indent=2, allow_nan=False)
        return report
    except BaseException:
        for file in created:
            if file.is_file():
                file.unlink()
        # Only this tool-created empty directory is removed; never recursive deletion.
        output_dir.rmdir()
        raise
