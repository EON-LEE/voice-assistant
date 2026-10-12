# Meeting benchmark measurement tools

Python 3.10+ standard library only. This directory prepares approved **local**
licensed media and analyzes operator-collected browser event traces. It does
not log in, download content, start a browser, call Azure, upload audio, or claim
a latency improvement. Nothing overwrites existing outputs by default.

## Correct the input before comparing deployments

The earlier pipeline used `-ss <start> -i <full-video> -t <duration+3> -af apad=3`.
That output bound does **not** trim the input before padding: following source
speech can occupy the supposed silence. Do not compare its old numbers with
new corrected input and call that an optimization.

The approved FOSDEM manifest contains the exact recording URL, source SHA256,
CC-BY-2.0-BE license URL, attribution and source intervals **54-70**, **331-360**,
**355-398** seconds. Published automatic captions are explicitly
**not human-verified** and no reference text is invented. All `speechEnd`
annotations are null. A cut at 70 seconds is a clip boundary, not proof that
the speaker stopped there.

Run from repository root (PowerShell; use your actual approved paths):

```powershell
$files = 'C:\Users\eonlee.REDMOND\.copilot\session-state\94af7cb9-a5ba-44f9-baac-b89831b37a39\files'
python .\tools\MeetingBenchmark\benchmark.py prepare `
  --manifest .\tools\MeetingBenchmark\manifest.fosdem.json `
  --source "$files\fosdem-jmap-panel.av1.webm" `
  --ffmpeg "$files\media-tools\imageio_ffmpeg\binaries\ffmpeg-win-x86_64-v7.1.exe" `
  --output "$files\corrected-clips-NEW"
```

The output directory must not exist, and its parent must already exist.
An explicit existing FFmpeg path is mandatory: no PATH resolution, package
install or download. Each process is capped at 300 seconds by default
(`--timeout`, range 1-1800). At most 20 clips of at most 120 seconds are accepted
from a source no larger than 4 GiB. A hash mismatch stops before decoding.
Audio extraction retains source sample coordinates. Video uses FFmpeg's explicit
accurate-seek decoding (discarding preroll, not packet-copy seeking) so long
AV1 recordings do not require decoding every earlier video frame.

Outputs are `meeting-introduction.wav/.mp4`, `technical-question.wav/.mp4`,
`multi-speaker-followup.wav/.mp4`, and `clips.json`. The metadata maps filenames,
hashes, sample intervals, total durations, source attribution, tool version/hash
and exact command argument arrays. Clip-relative cut boundaries are **16, 29,
43 seconds**; total durations including silence are **19, 32, 46 seconds**.
Temporary paths in the recorded argument arrays identify intermediate steps;
regeneration uses the same filter pipeline with new temporary paths. Hash the
**actual generated final MP4 bytes**, not the original recording, in each run's
comparison profile.

The pipeline:

1. Bound decoding with input `-t <source end>` **before `-i`**, so an AV1 panel
   recording is not decoded to EOF after the selected excerpt. This is not the
   old incorrect output-duration-only trim. Resample source audio to 16 kHz, **atrim exact start/end samples before any
   padding**, then reset audio PTS. Validate the resulting uncompressed mono
   PCM16 sample count; a too-short source is an error, not padded into success.
2. Append exactly **48,000 zero samples** in Python. Read the PCM back and check
   every padding sample. The production path requires peak **0**; the helper
   can test explicitly requested tiny encoding tolerances of at most 8/32768,
   never speech-scale tolerances.
3. Video explicitly enables input `-ss <start> -accurate_seek -t <duration>`
   (decoded preroll discarded), then `trim=0:duration,setpts=PTS-STARTPTS` **before** `fps=25` and
   `tpad`. VFR cadence can leave one missing frame after conversion; a bounded
   one-frame last-frame clone followed by `trim=end_frame=<content frames>`
   aligns the content to the output grid. Only then append 75 cloned frames.
   No video frame after the requested source end is selected.
4. Mux H.264 video and **lossless FLAC audio** into MP4, using the authoritative
   WAV as the only audio input. Decode the final MP4 audio back to PCM and
   require byte-for-byte equality with the WAV, including all three silent
   seconds. Decode/count video frames and require the exact expected duration.

Clip boundaries must lie on the 25 fps grid (40 ms). Audio cuts are exact in
the FFmpeg-decoded/resampled source sample timeline; source video frame cadence
and the bounded final-frame hold mean a visual boundary is only frame-precise,
not a human acoustic annotation. No lossy AAC ringing is waived as silence.
**Browser H.264/FLAC MP4 support must be verified by the operator on the actual
capture browser.** Successful FFmpeg decoding is not a browser compatibility
claim. If another encoding is required, retain the canonical WAV and independently
verify the alternate final file's decoded padding before benchmarking it.

For an optional human annotation, use:

```json
"speechEnd": {
  "kind": "human-annotated",
  "sourceSeconds": 69.52,
  "provenance": "Operator/alignment method and reviewed source version"
}
```

That value must be within the source interval. It is not inferred by this tool,
and the example is not an annotation for the FOSDEM recording.

## Browser event contract

Store raw events/transcripts only in operator-approved artifacts **outside this
repository**. One JSON object per run:

```json
{
  "schemaVersion": 1,
  "runId": "intro-baseline-attempt-01",
  "variant": "baseline",
  "questionId": "meeting-introduction",
  "profile": {
    "inputSha256": "<64 lowercase hex of final captured clip>",
    "contextSha256": "<64 lowercase hex of exact initial context>",
    "mode": "Azure",
    "capture": "native-tab",
    "model": "<actual deployment and version>",
    "rankConfiguration": {"semantic": "meeting-semantic", "minimum": 2.0},
    "warmState": "unknown"
  },
  "variantSettings": {"endSilenceMs": 700, "replyMode": "forced-search"},
  "reference": {"humanVerified": false, "text": null, "provenance": "Unverified automatic captions"},
  "events": []
}
```

`variant` is `baseline` or `optimized`. The exact input/context/provider/capture/
model/ranker/warm-state profile must match for a comparison. `warmState` is
`warm`, `cold`, or **unknown**; never infer identity/provider reuse from success
or elapsed wall time. Explicit optimization knobs belong in `variantSettings`,
outside the fixed-profile fingerprint. This is a measurement convention, not
permission to hide a changed model, knowledge corpus, capture path or input.
Use separate reports for separate clips/questions and quality-only mixed modes.

Every event has `type` and floating-point `atMs` from **one browser window's
`performance.now()` time origin**. Do not mix Node, UTC, server or another
window's monotonic values. Monotonic order is validated, never repaired by
sorting. If a video callback crosses an IPC bridge before the app stamps
`clip.end`, explicitly describe that scheduling/IPC delay in `provenance`.
That measurement is cut-boundary-observation latency, not acoustic-end latency.

| Event | Required additional fields |
| --- | --- |
| `clip.end` | `questionId`, `provenance`; at the content cut, not the end of the three-second pad |
| `speech.end` | Optional: `questionId`, `annotationKind: "human-annotated"`, `provenance`; same clock mapping of an actual reviewed annotation |
| `transcript.partial` / `transcript.final` | `turnId`, positive increasing `revision`, replacement `text` |
| `response.started` | `turnId`, `responseId` |
| `response.delta` | `turnId`, `responseId`, appended `text` |
| `response.completed` | `turnId`, `responseId`, complete replacement `text` |
| `response.cancelled` | `turnId`, `responseId` |
| `error` | Preserve safe `code`; no credentials, tickets or socket URL |
| `question.marker` | `questionId`, explicitly selected `turnId`, `responseId`, `finalRevision`, `selectionProvenance` |
| `run.end` | `status: "success"`, `"failed"` or `"cancelled"` |

`question.marker` may be recorded after the selected response completes and
before `run.end`; it selects an exact final revision, **not the timestamp at
which the marker was written**. Do not bind the question merely to the last
final `"And."` in a multi-speaker clip. Record the operator/algorithm's concept/
context selection criteria, retain all final events and unsuccessful attempts,
and apply the **same selection rules** to both variants. Post-hoc selection can
bias a benchmark even if the selected IDs are valid; this tool cannot verify
question meaning or the operator's criteria.

The Python `EventCollector.add(event)` supports the same schema for integrations.
It accepts no more than 50,000 events/run and rejects events after `run.end`.
It does not instrument the app or inject transcripts.

## Analysis and interpretation

```powershell
python .\tools\MeetingBenchmark\benchmark.py analyze `
  --runs C:\approved\baseline-01.json C:\approved\optimized-01.json `
  --output C:\approved\comparison-NEW.json
```

The output file must not exist. Summary output excludes transcript, response
and reference text; labels/provenance must also be content-free operator IDs,
not pasted meeting content. Keep raw licensed material in the operator's
approved location and never commit it simply to make the analysis reproducible.

Metrics separately anchor to the selected question final, the observed clip cut,
and (only when present) the annotated acoustic end. Missing anchors are null.
Negative cut-relative values remain signed: a reply received before an arbitrary
cut is not clamped to zero. Raw browser timestamps retain sub-millisecond
precision; precision does not imply equivalent timing accuracy.

**First text** is the first non-whitespace rendered response content (or a
completion-only reply). **First complete readable sentence** is deliberately
conservative: accumulate deltas, require `.?!` with a plausible following
sentence or terminal completion, exclude common abbreviations, initials,
decimals, enumerations, domains and ellipses, and require at least two words
(except short conventional responses such as `Yes.`). Closing quotes are
included. Punctuation at a streaming edge is provisional until more text or
completion arrives. This can overestimate sentence readiness; it is not
linguistic correctness, factuality or answer usefulness. Unrecognized languages,
unlisted abbreviations and fragments need human review. No punctuation is
manufactured for incomplete replies.

Only the explicitly selected turn/response contributes latency; stale transcript
revisions and deltas after terminal cancellation/completion are ignored and
counted. A selected cancelled response never becomes successful due to a late
completion. A recorded error makes that attempt failed. Incomplete, failed,
cancelled and invalid event streams remain in all-attempt denominators; only
successful attempts with an observed metric contribute that metric's samples.
Absent `run.end` or missing question binding is unknown, not a fake pass.

P50 is withheld below **5 metric samples** and p95 below **20**, per variant,
per identical cohort. P95 uses nearest rank; median uses the usual midpoint
rule. These are conservative reporting thresholds, not evidence that 20 samples
establish a population percentile or SLA. Every distribution includes sample
counts and all-attempt/successful-attempt denominators. No n=3 p95 or claim that
a latency target was achieved is produced. `latencyTargetAchieved` and
`qualityOrAccuracyPassed` remain null.

Accuracy remains **unknown** for absent references, automatic captions or
references with the wrong scope. Optional numerical word-error rate requires
`humanVerified: true`, `scope: "selected-final"`, nonempty `text` and explicit
human `provenance`. WER normalizes case/apostrophes and ignores punctuation;
it can exceed 1 with many insertions. It evaluates only that explicitly selected
final transcript, not the entire meeting or reply quality. A zero WER is not
automatically a quality PASS.

## Deterministic and media tests

```powershell
python -m unittest discover -s .\tools\MeetingBenchmark -p "test_*.py" -v

# Enables the original deterministic tone/video integration test (no Azure):
$env:MEETING_BENCHMARK_FFMPEG = "$files\media-tools\imageio_ffmpeg\binaries\ffmpeg-win-x86_64-v7.1.exe"
python -m unittest discover -s .\tools\MeetingBenchmark -p "test_*.py" -v
```

The suite contains more than 100 independent deterministic cases covering
time boundaries, stale/cancelled events, question selection, sentence parsing,
sample thresholds, mixed cohorts, source approval, overwrite refusal and
silence verification. The generated-source test makes the source *louder*
immediately after the selected interval, then proves that none of it enters
the 48,000-zero tail and that final MP4 audio equals the canonical PCM.
The `prepare` command runs the same validation on each actual FOSDEM clip.
These are measurement-correctness tests, **not 100 live performance trials**.
