# Live Coach transparent web overlay (Windows)

A small WPF app that hosts the deployed web overlay (`/live.html`) in Microsoft Edge WebView2 and makes the
window background see-through. Screen, features and network connection are the web page's own; this host adds only:

- Borderless, always-on-top, resizable window with a transparent background (`WebView2CompositionControl`).
- Injected CSS for the hosted page only: transparent page background, panel alpha (92/80/65/50 %, default 80 %),
  drag by the header, themed scrollbars, PiP item hidden.
- Extra right-click items: **투명도 바꾸기**, **창 최소화**, **프로그램 종료**.
- Microphone permission is granted only to the hosted page origin; other permissions are denied.
  Navigation of the overlay to another origin is blocked (sign-in uses its own popup window).
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

## Real meeting-audio test

`--fake-mic --audio-file <16 kHz mono .wav>` feeds a recording into the page's microphone once (no loop).
`tests/VoiceAssistant.Web.E2E/real-meeting-audio.mjs <outDir> <seconds>` then starts live (demo login if asked),
records the overlay every second and writes `real-meeting-report.json` plus a final screenshot.

Recommended free source: the AMI Meeting Corpus (University of Edinburgh, CC BY 4.0), real face-to-face English
meetings. The `Array1-01` far-field room microphone resembles a laptop hearing people across a table, e.g.
`https://groups.inf.ed.ac.uk/ami/AMICorpusMirror/amicorpus/ES2002a/audio/ES2002a.Array1-01.wav`.
Keep downloaded audio outside the repository and credit "AMI Meeting Corpus, University of Edinburgh, CC BY 4.0".
The recording is sent to the configured Azure service like live microphone audio.
