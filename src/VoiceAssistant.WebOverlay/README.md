# Live Coach transparent web overlay (Windows)

A small WPF app that hosts the deployed web overlay (`/live.html`) in Microsoft Edge WebView2 and makes the
window background see-through. Screen, features and network connection are the web page's own; this host adds only:

- Borderless, always-on-top, resizable window with a transparent background (`WebView2CompositionControl`).
- Injected CSS for the hosted page only: transparent page background, panel alpha (92/80/65/50 %, default 80 %),
  drag by the header, themed scrollbars, PiP item hidden.
- Extra right-click items: **투명도 바꾸기**, **창 최소화**, **프로그램 종료**.
- Microphone permission is granted only to the hosted page origin; other permission requests are denied. Screen sharing
  (`getDisplayMedia`) is not a WebView2 permission kind: the page's **탭/시스템 오디오** source opens the WebView2 share
  picker, which offers screens and windows but no browser tabs. Only **전체 화면** with **시스템 오디오 공유** carries
  audio, and system audio is the loopback of the Windows default output device; a window share has no audio. The page
  recognises the host (`window.chrome.webview`) and gives exactly these steps when a share has no audio.
- Native meeting audio for the page: **Teams 앱 소리** (only the Teams desktop app) or **PC 전체 소리**, chosen in the page's
  audio source list, without a share picker (see below).
- A main-window navigation to another origin is cancelled and an HTTPS target opens in the default browser instead.
  Pop-up windows the page opens (Microsoft sign-in, the materials link) are WebView2's own windows and are not covered by
  these navigation and permission rules.
- A fixed WebView2 profile in `%LOCALAPPDATA%\VoiceAssistant\WebOverlay\Profile` keeps the Microsoft sign-in
  between runs. Window position, size and opacity are saved in `overlay-settings.json` next to it.

Requires the Microsoft Edge WebView2 Runtime (installed with current Edge).

```powershell
dotnet publish .\src\VoiceAssistant.WebOverlay -c Release -r win-x64 --self-contained true -o <output folder>
<output folder>\LiveCoach.exe                       # production page
<output folder>\LiveCoach.exe --url http://127.0.0.1:5173/live.html --fake-mic   # local Fake API + Vite test
```

`--url` accepts HTTPS, or HTTP on loopback only. `--fake-mic` is for local testing: it uses Chromium's synthetic
microphone, enables DevTools and opens a loopback CDP port 9333 so
`tests/VoiceAssistant.Web.E2E/verify-overlay-app.mjs` can drive the hosted page. Never use it in a real meeting.

## Native Teams audio

WebView2 has no tabs to share, and "entire screen + system audio" hears only the default output device (not Teams on
a headset or communications device). So the host captures audio itself and posts it to the page
(`src/VoiceAssistant.Web/src/host-audio.ts`), which sends it through the normal meeting session. No picker is shown.

In `/live.html` (right-click → 오디오 소스 설정) these are **Teams 앱 소리** (`teams`, the default in LiveCoach; it also
replaces a share chosen earlier) and **PC 전체 소리** (`system`). The status line names the source in use. Teams not
running is a warning that clears when Teams starts; `teams-silent` and capture errors offer an inline
**PC 전체 소리로 전환**, which switches the capture without reconnecting the meeting session; `unsupported-os` and an
unexpected `stopped` end the session with Korean steps.

- **Teams** (`source: "teams"`): WASAPI process loopback of the root `ms-teams.exe` of this Windows session, on
  whichever device Teams plays to. Needs **Windows 10 build 20348 or later** (Windows 11); otherwise the host
  answers `unsupported-os` and does not fall back by itself. Teams not running: `teams-not-running`, checked again
  every 2 s; a restarted Teams is attached again. Capture failures: `error`, retried every 2 s.
- **System** (`source: "system"`, only when the page asks): loopback of the default output device, i.e. every app
  on it. Restarts on a default-device change.
- Frames: 16 kHz mono PCM16 (same filter as the browser path, `src/VoiceAssistant.Audio`), 20 ms each, continuous
  (silence while nothing is captured) so the server session stays alive.

Privacy: with Teams, only audio rendered by the Teams process (and its direct child processes) is captured, never
another app or another user's session. Audio stays in memory in a bounded 4-second queue and is never written to
disk. Frames are posted only while the document is on the trusted page origin, and capture stops on navigation.

Known limits, to verify in a real call (Teams **Settings → Devices → Make a test call** plays a voice):

- On build 26200, process loopback included the target process and its **direct children only**, not grandchildren.
- Public reports (microsoft/Windows-classic-samples#414, OBS forums) say the new Teams app's meeting audio can reach
  process loopback as **silence**. The host compares Teams' output session meters with what it captures; if Teams
  plays sound for about 6 s that arrives only as silence, it reports `error` with `code: "teams-silent"` ("…only
  silence for the Teams app. Choose system audio instead.").

Messages (JSON text; host-to-page only to the trusted origin, page-to-host only from it):

| Direction | Message |
| --- | --- |
| page → host | `{"type":"host-audio.start","source":"teams"\|"system","id":"…"}`, `{"type":"host-audio.stop","id":"…"}`, `{"type":"host-audio.ack","seq":n,"id":"…"}` |
| host → page | `{"type":"host-audio.status","id":"…","source":"…","state":"capturing\|teams-not-running\|unsupported-os\|error\|stopped","detail":"…","code":"…"}`; `detail` is English for people, the optional `code` is what the page acts on |
| host → page | `{"type":"host-audio.frame","seq":n,"pcm":"<base64>"}`: 1–5 consecutive 640-byte frames, `seq` = first frame |

`seq` restarts at 0 for each start; a gap means frames were dropped. At most 50 frames (1 s) may be unacknowledged and
at most 4 s wait in the host, oldest dropped first, so a stalled page never gets an unbounded burst. Frames go out as
soon as they exist (normally one per message); batching only happens after a UI-thread delay. The injected script
announces the feature as `window.__liveShell.hostAudio = {version: 1, sources: [...]}` ("teams" only on supported
Windows builds).

Dev-only mechanism check (plays quiet tones from child processes, then deletes them):
`$env:LIVECOACH_MANUAL_AUDIO='1'; dotnet test tests\VoiceAssistant.WebOverlay.Tests --filter Category=Manual --logger "console;verbosity=detailed"`.

## Real meeting-audio test

`--fake-mic --audio-file <16 kHz mono .wav>` feeds a recording into the page's microphone once (no loop).
`tests/VoiceAssistant.Web.E2E/real-meeting-audio.mjs <outDir> <seconds>` then starts live (demo login if asked),
records the overlay every second and writes `real-meeting-report.json` plus a final screenshot.

Recommended free source: the AMI Meeting Corpus (University of Edinburgh, CC BY 4.0), real face-to-face English
meetings. The `Array1-01` far-field room microphone resembles a laptop hearing people across a table, e.g.
`https://groups.inf.ed.ac.uk/ami/AMICorpusMirror/amicorpus/ES2002a/audio/ES2002a.Array1-01.wav`.
Keep downloaded audio outside the repository and credit "AMI Meeting Corpus, University of Edinburgh, CC BY 4.0".
The recording is sent to the configured Azure service like live microphone audio.
