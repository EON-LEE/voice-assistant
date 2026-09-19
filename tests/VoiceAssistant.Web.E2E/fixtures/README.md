# Original speech test fixture

`original-project.wav` is offline text-to-speech generated specifically for this repository from this wholly original sentence:

> Project Lumen needs a brief update by Friday.

No real meeting, private conversation, third-party video, downloaded recording, or copyrighted script is used. The fictional project has no real-world referent. The sentence and generated test recording are contributed for use, modification, and redistribution as part of this project's tests, without additional restrictions. No voice-model/software files are redistributed.

The generator uses the locally installed Windows `System.Speech` synthesizer and Microsoft Zira Desktop. Run `Generate-OriginalSpeech.ps1` in an environment permitting local PowerShell scripts; do not change OS execution policy to run it. The committed small WAV is portable for Linux/Windows tests and avoids needing SAPI in CI. Regeneration may vary with the installed voice version; metadata includes the actual SHA256 for that generation.

Format: canonical RIFF PCM signed little-endian 16-bit, mono, 16,000 Hz, with exactly 16,000 zero samples appended after synthesis. The metadata's `synthesisEndSample` is the exclusive end of generated output, **not** ground-truth semantic speech end. `speechEndSample` is deliberately null because SAPI does not expose sample-aligned end-of-speech ground truth. Consumers must report speech-end latency as unavailable, not infer it from STT or silence. `approvedForLiveUse` marks only that this original synthetic nonprivate fixture is suitable test input; it does not authorize cloud resources, authentication, or a billable run.

The browser test decodes and plays this file with an actual HTMLAudioElement, uses `captureStream()` in the isolated test's `getDisplayMedia` mock, processes it through the native AudioWorklet, and uploads PCM to the real **local Fake** API. This verifies media processing and transport, **not Azure transcription accuracy or semantic understanding**. Playback is routed through a zero-gain test graph so it does not audibly play through the user's speakers. No real screen/tab/microphone permission is requested.
