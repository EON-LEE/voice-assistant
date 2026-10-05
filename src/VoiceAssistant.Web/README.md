# Browser meeting assistant

Installation-free TypeScript/Vite frontend for the same-origin Azure-hosted API. End users open a URL; no Windows app, extension, or download is needed. Tab capture never requests microphone permission; the explicit in-person microphone mode does. The preserved desktop project is not a dependency.

## Viewport overlay, floating window and practice

The meeting viewport is a single glass-like floating card, not a scrolling document: a small handle and control strip, **one internally scrollable conversation area**, and a fixed compact action bar. No hero/title block or long footer occupies the meeting surface. The strip contains round Start/Stop, elapsed timer, Meeting / Practice chips, status and Settings/Float/Opacity icons. English question, Korean meaning, streamed suggestion/listen controls and aligned pronunciation occupy the middle; transcript history is a collapsed disclosure. A pinned answer remains separate. The card fills the viewport; **Compact** optionally narrows it to about 420px when snapping the browser beside Teams. The separate materials page keeps its own layout. No third-party brand, wording, imagery, logo or assets are included.

**Settings** opens a sheet over the card (closed on load) containing connection/source selection, sign-in, consent, Pause, profile/topic/terms/response mode, silence and session details, the new-tab materials link and privacy/help. The round control starts the default audio demo in one click. A Live Start that needs sign-in/consent opens Settings instead of silently doing nothing; invalid/unconfirmed profile input leaves the sheet visibly editable and displays the error there. Settings never changes active immutable options. P still toggles meeting pause outside editable/control fields. Practice selection opens a separate setup sheet; after Start it closes so the question, hint, answer/feedback and controls stay compact. Setup can be reopened without stopping. Sheets trap focus, make background controls inert, close with Escape and restore focus. Errors and streaming content use live regions and text nodes.

### Float and opacity

On supported desktop Chrome/Edge, **Float** calls `documentPictureInPicture.requestWindow({width:460,height:680})` from the user click. It **moves the existing card DOM, never clones it**, into the browser-managed always-on-top/resizable PiP window, copies stylesheet links/style elements and the base URL, and preserves handlers, sockets, media streams, reply/practice state, pause and pins. Close PiP (or its return icon) to move the same card back; focus returns to Float. Keep the original tab open: closing/navigating it ends the application and releases capture. Failure is visible; unsupported browsers show a disabled Float control with an explanation and can use Compact/manual snapping instead.

**Browser limitation:** PiP is always on top, but cannot be transparent through to other applications or desktop pixels. Glass/opacity affects the card against its browser page/window background only. The handle is decorative; move/resize using the OS/browser window chrome. This is not a native desktop overlay and has no custom OS focus/position privileges.

Opacity controls card alpha from **35% to 100%, default 80%** through `--overlay-alpha`. Only this numeric UI preference is stored in `localStorage["voice-assistant.overlay-opacity"]`, and only after changing the slider. Compact, connection choices, profile, transcripts, answers, auth tokens and practice history are **not persisted**. Bad/out-of-range values use 80%; denied storage keeps the current tab's choice and reports the limitation. Text has dark backing surfaces for contrast even at minimum alpha. `prefers-reduced-transparency: reduce` and forced-colors use an opaque/no-blur fallback; reduced-motion disables sheet animation.

Element access is scoped to the moved card, including practice and keyboard handlers, not the now-empty opener document. The coalesced render clock registers timers with the card's **current owner window**. After the 50ms batching window it races that window's rAF against a 16ms timeout fallback (one paint only), so absent/throttled rAF cannot strand an update. Moving/restoring flushes and cancels old-window handles; elapsed-time updates rebind to the visible owner. Audio/WS processing stays event-driven in the opener with the existing bounded worklet; no clone/reconnect or WebSocket worker rewrite is used. Visible PiP avoids reliance on the hidden tab's render timers, but browser/OS tab suspension or power policies can still interrupt a session; this is not a guarantee against arbitrary background freezing.

PiP DOM movement, same-node restoration, copied styles, focus, elapsed/render fallback and practice/meeting handlers are covered by an explicit **mock Document PiP window** in headless Chromium. This is not proof of real desktop always-on-top behavior, OS resizing, screen-sharing capture of PiP, or platform-specific permission gestures. Verify those manually in supported Chrome/Edge with consented audio. Use headphones for read-aloud and avoid sharing a display containing private controls.

### Korean assist and listening

In Live/local Fake mode, the client requests `/api/assist/enrich` for a final question and **only after `response.completed`** for a reply. English text is rendered first using the existing 50ms-coalesced delta path; no translation or pronunciation request blocks it. A newer turn cancels both enrichment lanes; a newer response invalidates the previous reply lane. Late results are ignored even if the network ignores abort. Optional enrichment errors/malformed data leave plain English visible without an error-shaped replacement. Inputs longer than the contract's 600-character assist limit are left in English.

Korean meaning appears below each English line. Reply pronunciation chunks stack the exact English words above their Hangul reading and wrap on narrow screens. The client validates chunk order, punctuation, 1–4 words/chunk, 40-chunk maximum, Hangul-only reading rules, and Korean translation bounds; invalid data is omitted. **Hangul reading is approximate**, not a phonetic assessment. Only explicit local Fake config permits the contract's `[fake-ko]` test prefix. The normal Audio demo remains scripted and makes no assist/API/auth/capture calls.

**Click to listen / 듣기** sends `/api/assist/speak` with `voice:"coach"` and normal/slow rate. Audio is never automatically played for meeting suggestions. One shared audio controller cancels older fetches/clips, caps response bytes, uses the actual MIME type (MP3; WAV only for local Fake), and revokes its Blob URL on completion/error/replacement/Stop/unload. Loading, playing, blocked-autoplay, rate-limit and retry states are visible. Reading a meeting reply pauses capture to avoid feeding playback back into microphone/system audio; the completed reply remains visible, and the user explicitly resumes with Pause. Speech is limited to 400 characters; longer replies are still readable but cannot be synthesized from the listen control. Fake speech is a silent test clip, not a real voice-quality demonstration.

### Practice flow

Select Practice, sign in if required, choose Sales meeting / Interview / Presentation / Custom, difficulty 1–3 and 1–12 questions (default 5). Custom requires a description. **Use my materials** defaults on; **Smart suggestion** defaults on but only requests a hint on click. No personal scenario facts are prefilled. Consent is required before Start practice opens a microphone.

Start opens the existing bounded microphone/AudioWorklet/socket pipeline with `options.transcribeOnly:true` and conversation mode. The recognition-only channel never generates an automatic reply or performs retrieval. It remains paused while the partner question/audio or a hint is playing. An existing `transcribe_only` error remains nonfatal for this channel as required by `practice-v1.md`; the UI never sends `response.request` during practice.

The stateless HTTP flow is:

1. `/api/practice/turn` receives the chosen settings and in-memory alternating history; the opening request has empty history.
2. The returned English question is shown, Korean enrichment loads independently, and a partner voice clip is attempted **only after the user started practice**. If playback is blocked/unavailable, the visible Listen to question button retries. **Answer by voice** stops any playback, resumes the microphone and begins collecting recognized finals; it is an explicit gesture, never a surprise recording transition.
3. **Done answering** joins unique final transcript segments (maximum 800 characters); while a partial is still arriving it stays disabled. **Skip question** sends an empty answer. No guessed/unfinished transcript is submitted and long answers are not silently truncated.
4. `/api/practice/feedback` shows corrected English and easier English (each can be listened to), Korean feedback/points and clarity 1–5. This is feedback about recognized wording, **not pronunciation, accent, intelligence or a grade of the person**.
5. Next question sends the bounded updated history. When the server returns `done:true`, `/api/practice/summary` shows Korean strengths/improvements and reusable English/Korean phrases with listen buttons; microphone tracks are released. Stop / restart clears the entire round.

History, answers, feedback and hints live only in memory, never localStorage/sessionStorage. Switching modes, Stop, source end, or unload cancels pending work and audio; there is no persistence or automatic reconnect. Ordinary mic system-mute warnings remain visible via the existing source lifecycle. Practice uses the browser-default microphone; meeting mode retains the selectable input device. Only one mode captures at a time.

Failed practice requests are explicit and retryable without duplicating history; 429 `Retry-After` is honored for turn/feedback/summary retries, hints and speech. Stop/restart remains available while requests are pending. Hint failures do not block submitting an otherwise valid answer. Requests are bounded at 16KiB and all outputs are schema-checked: unknown fields, wrong types, unexpected turn/done order, invalid Korean/chunks, excess source/point/phrase counts and overlong English fail closed. Extremely long full-round histories can hit the 16KiB contract cap: restart with shorter answers rather than silently discarding history. Sources are inert titles, never uploaded instructions.

### Coach validation and limitations

`npm test` now runs the existing Node test suite (`npm run test:core`) followed by **Vitest** (`npm run test:coach`). Coach Vitest cases live in `tests/VoiceAssistant.Web.Tests/coach`; they cover strict clients, pronunciation rendering using jsdom, stale/cancelled enrichment, single-clip Blob cleanup, blocked playback, practice history/skip/retry/summary and recognition-only session behavior. Tests make no real identity-provider/Speech/OpenAI calls.

The Playwright 1.61.1 `coach.spec.js` mocks assist/practice HTTP routes and uses an isolated synthetic Chromium microphone for a full round. It checks streamed English before enrichment, stale/error omission, speaker clicks, hints, feedback, summary, 429 retry, stop cleanup and 390px overflow. Set `VOICE_ASSISTANT_COACH_SCREENSHOTS` to an absolute directory outside the repository to capture original meeting/practice desktop and mobile screens from those fixtures. Screens are visibly local Fake examples, not real coaching acceptance evidence. An additional real-local-Fake integration test is explicit opt-in with `VOICE_ASSISTANT_PRACTICE_E2E=1` after the backend endpoints land (alongside existing backend test flags).

This frontend does not choose Azure neural voice names; those are the backend contract/runtime responsibility. No live voice/translation quality, tenant sign-in, physical-room recording, or Azure practice semantics are claimed from mocked/offline tests. Float uses the browser's Document PiP API where supported; it is not a native OS overlay. Enrichment requests do not block English rendering, but no live latency improvement is asserted.

`overlay.spec.js` adds sheet focus/Escape/consent prompting, opacity persistence and denied storage, Compact, unsupported Float, reduced-transparency/forced-colors, same-DOM PiP move/return with a deliberately stalled rAF, retained practice state, and viewport bounds. `overlay-helpers.js` updates existing browser interactions to open Settings before operating relocated controls without weakening existing assertions. Vitest adds numeric preference and owner-window/fallback tests; `testTimeout`/`hookTimeout` are 20 seconds for cold jsdom startup. `VOICE_ASSISTANT_OVERLAY_SCREENSHOTS=<absolute session directory>` writes original meeting/practice/settings screenshots at 1100×800, 460×680 and 390×844; these are mock/Fake fixtures, not live meeting evidence.

## Build and run

Developer prerequisites: Node 22.12+ (validated with 22.23.2), npm, modern Chromium browser for screen/tab audio support.

```powershell
Set-Location .\src\VoiceAssistant.Web
npm ci
npm run typecheck
npm test
npm run build
npm run dev
```

Vite serves `http://127.0.0.1:5173` and proxies `/api` (including WebSockets) to `http://localhost:5080`, preserving the incoming Host header. Serve `dist` from the ASP.NET API's same-origin static root in Azure; no Vite development server is needed in production. The frontend contains no cloud secrets or build-time tenant config.

The production build is multi-page: `dist/index.html` is the meeting UI and `dist/materials.html` is the dedicated materials UI. Vite emits their shared JS/CSS dependencies under `dist/assets`. The API's existing `UseDefaultFiles` / `UseStaticFiles` serves both without new routes. **`dotnet publish` alone does not build or copy frontend files**: the existing API Dockerfile builds Vite and copies the complete `dist` into the published `wwwroot`. For local published-output validation, build the web, run `dotnet publish .\src\VoiceAssistant.Api -c Release -o <isolated-output>`, then copy the complete web `dist` into `<isolated-output>\wwwroot`, matching that Docker step. Do not copy only `index.html` or only one page's bundle.

For isolated local integration runs set `VOICE_ASSISTANT_DEV_API=http://localhost:5084` before starting Vite; the default stays unchanged. `/health` is proxied to the same target for local E2E readiness checks. This is development-server configuration only, not production routing.

The page starts stopped in clearly labelled **Audio demo**. One click on **Start demo** plays the bundled original English recording through the browser, then automatically displays its scripted transcript and streaming example reply. No sign-in, microphone, screen picker or backend AI call is used. Stop/pause control the recording too. Playback failure is explicit, never a pretend successful demo. Select **Live** explicitly to use Azure; no failed live connection ever switches to Demo.

The sample says "Project Lumen needs a brief update by Friday." It is the original
synthetic recording documented in `tests/VoiceAssistant.Web.E2E/fixtures`, bundled
at `src/assets/demo-original.wav`. The demo transcript is predefined, not actual
speech recognition; only the sample input is played, never the suggested answer.
Static app/audio files are fetched normally, but no credentials or recorded
audio are sent to a backend.

## Optional meeting context and answer modes

Live and local synthetic modes expose **Meeting context and answer settings**. No personal value is prepopulated or inferred from sign-in, WorkIQ, or directory data. All values stay in page/session memory (no localStorage/sessionStorage); they are sent in `session.start.options` only when Start succeeds. Reload clears them. The demo ignores these fields and retains its one-click audible, zero-API/auth/capture behavior.

| Setting | Behavior and bounds |
| --- | --- |
| Answer mode | `balanced` (new UI default): context first, knowledge when needed; `grounded`: always search approved knowledge; `conversation`: conversation/context only, no Search |
| Profile | Optional `name` up to 100, `role` up to 160, `project` up to 300 characters |
| Profile confirmation | Required for any nonempty profile; editing any profile field unchecks confirmation. No details are treated as confirmed until the user checks it again |
| Meeting topic | Optional, up to 300 characters; not a verified personal fact |
| Recognition terms | One per line; up to 40 nonempty terms, 64 characters each, 2048 combined characters after trimming; not confirmed facts |
| End-of-turn silence | 450, 500 (new UI default), 700, or 1000 ms; shorter values may split natural pauses, longer values tolerate pauses but finalize later |

Input is trimmed, validated without silently truncating, and copied before capture setup. The complete JSON startup payload is checked against the API's 32 KiB UTF-8 limit before capture; maximum Korean/escaped-string context is tested without truncation. Invalid/unconfirmed inputs show an error before any capture/socket connection. The fieldset is disabled while a session starts/runs; Stop makes it editable for the next session. Settings do not promise a particular response latency or fabricate available knowledge/profile integrations.

New web sessions explicitly send:

```json
{
  "type": "session.start",
  "protocolVersion": 1,
  "audio": { "encoding": "pcm_s16le", "sampleRate": 16000, "channels": 1 },
  "options": {
    "responseMode": "balanced",
    "profile": { "name": "", "role": "", "project": "" },
    "profileConfirmed": false,
    "topic": "",
    "phrases": [],
    "endSilenceMs": 500
  }
}
```

The shared API accepts integer silence 350..1500; the UI deliberately offers the four presets above. Clients omitting `options` retain the server's legacy grounded/700 behavior. The explicit demo has no options-dependent behavior and no backend connection.

## Browser capture constraints and consent

For remote meetings use **Teams in a browser tab** where possible. After selecting Live, choose **Meeting tab / system audio**, sign in (Azure only), check participant permission, then click **Share meeting audio**. Select the Teams tab and enable **Share tab audio** in the browser/OS prompt. The application invokes `getDisplayMedia({ video: true, audio: true })` only from that click, and creates/resumes `AudioContext` in the same gesture.

The browser requires `video: true` to offer display audio; video is not attached to a video element, inspected, uploaded, recorded, or retained by this app. All video and audio tracks are stopped on Stop, session failure, permission/setup failure, source `ended`/audio `mute`, and page unload. A share granted after a cancelled/pending picker is also immediately stopped. Browsers do not let websites dismiss the picker themselves; dismiss an outstanding picker if you stop while it is open.

Audio availability depends on the browser, OS, selected surface, and user's audio-sharing option. **Teams desktop/system audio is not guaranteed.** Window sharing frequently supplies no audio. A stream without audio fails visibly and releases all tracks; there is no microphone fallback. Screen/system audio can include other applications. No browser permission can bypass participant consent or meeting policy.

The live source converts only audio tracks with a fixed-memory AudioWorklet: mono downmix, 127-tap low-pass FIR anti-alias filter, continuous fractional resampling to 16 kHz, clipped signed little-endian PCM16. It sends 640-byte/20ms frames without a WAV header, only after `session.ready`. Video is never sent. Fifty recyclable transferable buffers cap the worklet queue at one second (32,000 bytes), tolerating short UI-thread stalls without losing samples; no growing per-sample arrays are used. Worklet starvation and WebSocket `bufferedAmount` exceeding 32,000 bytes still stop visibly rather than accumulating unbounded audio or silently losing speech. Buffer capacity is not a fixed delay: frames are sent immediately while the UI thread is responsive.

Pause while speaking drops audio locally and cancels suggestions. Epoch-tagged worklet frames prevent already-queued pre-pause buffers being uploaded after resume; DSP history is reset to avoid retaining paused speech. Pause leaves the shared tracks open; **Stop** releases them. Already-uploaded audio cannot be recalled. Browser throttling/suspension can interrupt capture; keep the page available. No automatic reconnect, recording, Teams posting, or external source-link navigation is performed. Read-aloud is explicit-click only in meetings; practice partner playback is attempted only after starting a practice round.

## In-person use

Select **Live → Audio source → Microphone (in-person)**, sign in, inform the participants under your company/customer policy, check the room-audio permission checkbox, then click **Start microphone**. Internet is required for Azure processing. Test the microphone first: the input-level meter should respond after Start. Place the laptop/phone near the speaker without blocking its microphone. Room acoustics, noise and browser support affect recognition; this does not identify individual speakers or guarantee a particular latency.

This explicit choice requests `getUserMedia({audio:{channelCount:1,echoCancellation:true,noiseSuppression:true,autoGainControl:true,deviceId?:{exact:...}},video:false})` from the Start click. It never requests screen capture and never falls back to another source. After permission, the device list shows `enumerateDevices()` audio-input labels; Stop before selecting a different device for the next session. Selection stays in page memory only. Permission denial, no device, a busy device, disconnection and device removal produce actionable errors; missing devices are not silently replaced.

The microphone reuses the same fixed-memory Worklet, PCM16/16kHz conversion, 50-frame capacity, ready-gated WebSocket and one-use-ticket auth as tab capture. A parallel 256-sample AnalyserNode updates the level meter at 10Hz without allocating per-frame sample arrays or changing PCM transmission. **Pause while speaking** drops audio and cancels generation; press **P** when not typing in an input, textarea, select, editable area or button to toggle it. Stop releases all tracks and closes the context (the browser microphone indicator should turn off). A system/browser `mute` displays a warning, drops microphone frames and keeps the session; `unmute` clears the warning and resumes unless the user remains paused. An ended track stops the session instead. Permission never starts automatically on page load, mode selection or device selection.

The server controls session duration (`Session:MaxMinutes`). Its exact fatal `session_time_limit` event produces **Session time limit reached – click Start to continue** after cleanup. Other normal server closes are shown as ended sessions, abnormal closes as unexpected disconnects; normal closure alone is not falsely classified as a time limit. A new Start always requires fresh capture consent.

## My meeting materials

Open **Manage my meeting materials** from the meeting page, or visit **`/materials.html`** directly before travelling. The compact meeting-page link opens a **new tab** with `rel="noopener"`; there is no inline upload card and a running meeting is never navigated away. The dedicated page includes a Back to the meeting page link, signed-out/sign-in state, large drop zone, file/folder pickers, notes, upload table, indexed-document table, quotas and preparation help. No microphone or screen-capture code runs on that page.

Sign in separately in the materials tab; tokens are held only in that tab's memory, not shared with the meeting tab or persisted. The localhost-only Fake backend is explicitly labelled and uses its fixed fake owner without a token. Production calls the existing MSAL token flow and adds `Authorization: Bearer` only to `/api/knowledge` or a validated document-ID path; redirects to other origins are refused. A 401 prompts sign-in rather than falling back to Fake. Document ownership comes only from the server's authenticated identity; no owner ID is sent.

Select multiple files or a folder (`webkitdirectory`), or drag/drop files/folders. Selection shows ready/skipped counts before **Upload selected files**. Filtering excludes hidden directories/files, `node_modules`, `.git`, `bin`, `obj`, `dist`, `build`, `.venv`, `__pycache__`, unsupported extensions, files above 5 MiB, and binary/non-UTF-8 content masquerading as text. Supported DOCX/PPTX/PDF containers are sent to the server for bounded extraction; the client does not parse them. Drag-folder traversal stops at 2,000 entries and a selection/queue is capped at 300 files. No ignored file is uploaded. See `contracts/knowledge-v1.md` for the exact extension list and extraction/ACL guarantees.

Alternatively paste notes with a title (1–200 characters; notes up to 400,000 characters). Uploads use at most **two concurrent requests**, including notes. The per-file table shows name, size, state, safe details and individual Retry/Cancel actions; there is no fabricated byte-percentage meter. **Retry failed only** never replays successful or cancelled items, and a row's Retry retries only that failed row. **Cancel uploads** aborts active client requests and cancels queued items; an already accepted server upload may still complete, so refresh and delete if necessary. Clear finished entries releases their in-memory file/text references. Uploading in the materials tab is allowed while a meeting runs in its original tab and does not pause its audio.

The indexed table displays titles, chunk counts and dates with keyboard-accessible per-item Delete and select-all/delete-selected controls. **Every deletion asks for confirmation** of the number of documents and deletion of stored text/embeddings. Bulk deletes run sequentially, stop on a failure and refresh the actual list; they do not pretend all documents were deleted. The usage meters show documents/chunks versus server limits. Changes are refreshed after successful uploads/deletes. Error codes map to safe messages (too large, unsupported type, no extractable text, quota, busy, unavailable, or sign-in required); raw server/cloud errors and file contents are not logged or displayed as diagnostics. Reference sources whose host is `my-materials.invalid` render the document title with **My meeting materials**, never that placeholder URL or an automatic navigation link.

Prepare one topic per file and put its topic in the title. Add glossary and Q&A documents. Write key information as actual text: **text inside slide images, screenshots and diagrams is not read**; PDFs need a text layer. Legacy `.doc`/`.ppt` are unsupported (save as `.docx`/`.pptx` or a text-based PDF). Indexing is not a guarantee that every generated claim is supported by the material.

**Extracted text and search embeddings are stored in your Azure subscription's Search index until you delete them; the original file is not stored; do not upload anything you are not allowed to process in Azure.** Meeting transcript/reply text remains in page memory; uploaded material is a separate persistent server resource. Uploaded text is untrusted evidence, not instructions. This panel does not grant tenant-wide knowledge access or claim live Azure storage was tested locally.

## Agreed browser authentication contract

Live initializes from same-origin `GET /api/client-config`:

```json
{
  "clientId": "<SPA application GUID>",
  "authority": "https://login.microsoftonline.com/<tenant GUID>",
  "scope": "api://<API application GUID>/Meeting.Access",
  "mode": "Azure",
  "webSocketPath": "/api/meeting"
}
```

Register an **Entra SPA** redirect URI matching `location.origin` (the deployed HTTPS origin), and grant the delegated API scope. This is not the desktop public-client registration. MSAL Browser uses authorization-code/PKCE and an explicitly clicked sign-in popup. Both token and temporary caches use `memoryStorage`; no access/refresh token is written to localStorage, sessionStorage, cookies, or a WebSocket query. Reloading loses the application token cache (the identity provider may still have an SSO cookie).

**Materials-page popup flow:** `/materials.html` initializes its own `BrowserAuth` before enabling Sign in, then calls `loginPopup` directly from the button gesture. It intentionally uses the **same origin-root redirect URI**, not `/materials.html`, so the existing SPA registration remains valid. In installed MSAL Browser 4.x (the asynchronous-initialization model also applies to v3+), the **opener** initializes MSAL, keeps PKCE/state in memory, polls the popup until it returns to the same origin, consumes the response fragment and closes it. The root page does not need to initialize a second client or call `handleRedirectPromise` to complete this popup flow: its ordinary Demo initialization leaves `#code`/`#state` untouched, does not auto-sign-in, and does not navigate/strip the fragment. Calling a second redirect handler there could consume state owned by the opener. Do not change that callback behavior without testing popup completion.

The injected-auth browser regression opens the actual root page from a materials-page click, checks transient user activation, confirms the synthetic auth fragment survives, and verifies the opener uses memory storage and a bearer header for knowledge requests. This is a structural popup/callback test with a fake MSAL client, **not proof of live Entra sign-in, tenant consent, Conditional Access, or popup-blocker behavior**. Those still need manual authorized validation. The main-page link is `noopener`, but the MSAL login popup must retain its own opener; do not apply that link setting to MSAL popups. Popup failure leaves the page signed out with an explicit retry message. Separate tabs may benefit from the identity provider's SSO cookie but do not share application tokens.

After consent and capture preparation, the frontend acquires a token silently and sends:

```http
POST /api/session/ticket
Authorization: Bearer <access token held in memory>
```

The API returns `{ "ticket": "<opaque one-use value>", "expiresAt": "<UTC ISO timestamp>" }`. A fresh ticket is exchanged for a same-origin `wss://<origin>/api/meeting?ticket=...` connection. Tickets are scoped to the meeting endpoint, single use, and expire within 30 seconds server-side; the browser rejects expired or implausibly distant expiries. No access token goes in the query. The API must validate Origin, ticket expiry/use/scope, tenant and audience; redact tickets/query strings from access telemetry. The frontend never logs tokens/tickets.

Missing/malformed config, failed authorization, HTTP production, expired sign-in, or failed socket connection **fail closed visibly**, with capture cleanup. Interactive token renewal does not open a surprise popup during a meeting; stop and sign in again when needed. Browser sign-in/Azure services require manual tenant validation and are not exercised by offline tests.

## Fake backend integration (real WebSocket, no capture)

The API must run explicitly in `Development` with `Provider__Mode=Fake` and loopback binding/Origin/Host. Suggested backend address: `http://localhost:5080`. Its client config returns `mode: "Fake"` and the same `webSocketPath`; Entra fields are not required in Fake.

Select **Local fake backend · developer test only** and Start synthetic test. This developer-only option is shown only on exact loopback browser hostnames and rejects Azure config. It sends a 400ms synthetic 440Hz PCM burst followed by silence, never played through speakers, to trigger the fake backend transcript; click Suggest for its deterministic answer. On the deployed Azure site, use **Audio demo** instead to hear the recording without signing in. Live sharing against local Fake is also possible explicitly, but is labelled **LOCAL FAKE** and requires capture consent.

## Protocol and UI behavior

The v1 WebSocket protocol is unchanged: first `session.start` with `protocolVersion:1` and `{encoding:"pcm_s16le",sampleRate:16000,channels:1}`, then binary PCM only after `session.ready`. Controls send `response.request`, `response.cancel`, `session.stop`. Stop attempts the last command before closing the socket; delivery cannot be guaranteed after a network failure.

Server events: `session.ready`; `transcript.partial/final` (`turnId`, nonnegative `revision`, `text`); `response.started/cancelled` (`responseId`,`turnId`); incremental `response.delta` (`text`); authoritative `response.completed` (`text`,`sources:[{title,url,updatedAt}]`); `error` (`code`,`message`,`retryable`).

Agreed additive completed `grounding` values `disabled`, `grounded`, `unavailable`, `no_matches` are displayed explicitly, including on pinned snapshots. Omission is accepted for older v1 services and displayed as not supplied. Sources are plain text, not auto-opened links.

Optional completion `responseRoute` (`transcript`, `profile`, `knowledge`) and boolean `retrievalPrefetched` are strictly validated and displayed as plain text, including immutable pinned snapshots. Omission is accepted for older services. These are server-reported routing diagnostics, not evidence that a claim is correct; all four existing grounding states remain separate.

Transcript revisions cannot regress. Final text cannot be overwritten by partials. New turns invalidate old suggestions; only the active response ID receives deltas/completion. Cancel/pause suppress late answers; Pin creates an immutable text/source snapshot separate from new suggestions and survives session restarts until Unpin/reload. IDs are opaque; causal new-turn ordering relies on ordered WebSocket delivery. There is no client request ID in v1, so the server must preserve response-start ordering around cancellation/new requests.

State is bounded: 64 transcript turns, 256 remembered response IDs, 32,768 characters per transcript/reply, 20 sources, 64KiB incoming text messages. Nonretryable errors stop; retryable errors stay visible. No reconnect/replay is automatic. All meeting text is page-memory only.

Streaming partial/delta content updates coalesce behind **one pending 50ms render** rather than rebuilding the transcript for every reply token. Controls always reflect current state immediately; completion/final transcript, errors, pin, pause, stop, and other controls flush pending content synchronously. Reply-only deltas do not replace transcript nodes; unchanged pinned/source content is not rebuilt. Stop/unload cancel or flush pending renders so stale callbacks cannot revive cancelled text. This is bounded UI scheduling, **not** an artificial audio/network delay or a claimed service-latency improvement. The existing one-second worklet/WS capacity and immediate PCM sends are unchanged; there is no WebSocket worker rewrite.

## Validation

`npm test` compiles and runs Node's test runner with tests in `tests/VoiceAssistant.Web.Tests`: conversion rates/endianness, stereo/clipping/nonfinite samples, chunk continuity and anti-alias rejection, reducer revision/cancellation/stale IDs/pins, strict event parsing, ready gating, disconnect/fatal/device cleanup, no-audio/permission failure, picker cancellation and late tracks, worklet source lifecycle mocks, explicit Demo, and WebSocket backpressure. No test requests actual meeting/screen or microphone permission.

Microphone tests add mocked browser lifecycle/constraints, permission/busy/no-device errors, recoverable mute/unmute, device removal, grant-after-abort, pause epochs, level meter and exact time-limit handling. Materials units cover contract requests, safe errors, filtering/UTF-8, bounds, two-slot concurrency, cancellation, retry-only-failed and personal-reference formatting. `microphone.spec.js` launches only its isolated Chromium using `--use-fake-ui-for-media-stream --use-fake-device-for-media-stream --use-file-for-fake-audio-capture=<original fixture>`; it never touches a real user's microphone or opens a screen picker. The actual-Fake case uses `VOICE_ASSISTANT_BACKEND_E2E=1`. `materials.spec.js` labels its route-mocked scenarios; opt in to real localhost Fake knowledge CRUD with `VOICE_ASSISTANT_KNOWLEDGE_E2E=1` once those endpoints are running. Its injected bearer-auth case is Vite-only (`VOICE_ASSISTANT_VITE_AUTH_TESTS=1`). Keep the existing Playwright **1.61.1** lock/runtime; no browser package upgrade is required.

`materials-page.spec.js` adds dedicated-page tables, bulk-delete accept/cancel behavior, per-row retry, signed-out/config failure states, mobile table scrolling, and the injected popup test. The existing materials suite now targets `/materials.html`, tests the new-tab link with an active meeting, and retains reference rendering checks on the meeting page. Unit coverage adds individual retry/cancel isolation and size metadata. To include actual Fake knowledge CRUD, use both `VOICE_ASSISTANT_BACKEND_E2E=1` and `VOICE_ASSISTANT_KNOWLEDGE_E2E=1`; the existing Playwright config can start the local Fake API, or use `VOICE_ASSISTANT_API_EXTERNAL=1` for an already-running isolated published host. No live Azure call is part of these tests.

`npm run build` runs strict TypeScript typechecking and bundles both main UI and AudioWorklet. A local browser smoke checks Demo Start/Suggest/Pin/Pause/Stop. Coordinator-owned Playwright tests in a separate E2E directory cover full mocked-browser capture and actual fake-backend integration after merge.

The follow-up `media-playback.spec.js` uses the committed original offline speech fixture (140,204-byte canonical WAV; provenance/generator/metadata in `tests/VoiceAssistant.Web.E2E/fixtures`). Actual HTMLAudioElement decoding and `captureStream()` feed the native worklet and real local Fake API across **20 start/stop cycles**, checking nonzero 640-byte PCM frames, replies, all captured tracks ended, and all test/app AudioContexts closed. It additionally checks audio mute recovery by an explicit new Share, inert transcript/citation DOM text, rapid mode switching, and injected auth initialization/401 retry (no Entra traffic). Fake replies are deterministic; this is **not Azure semantic/STT validation**. `speechEndSample` is null, so no semantic speech-end latency is asserted. Audio playback uses a test-only zero-gain output.

Unit tests execute the actual worklet processor against a mocked message port with transferred buffers to verify bounded fifty-credit overflow, preservation across a 300ms UI stall, 1/2/6/32-channel samples, unsupported channel errors, and pause epochs/partial-frame discard. A browser regression also blocks the main thread for 350ms while the native worklet continues producing audio. Configuration initialization is single-flight and failures allow retry. A 401/403 ticket rejection clears the in-memory signed-in account, requiring a new explicit sign-in.

To run follow-up browser cases, start Vite and the local Fake API, set `VOICE_ASSISTANT_WEB_URL` to the Vite origin, `VOICE_ASSISTANT_BACKEND_E2E=1`, and `VOICE_ASSISTANT_API_EXTERNAL=1`, then run `npm test -- media-playback.spec.js` from `tests/VoiceAssistant.Web.E2E`. The two injected auth module tests use Vite's source-module endpoint and require `VOICE_ASSISTANT_VITE_AUTH_TESTS=1`; otherwise they are **explicitly skipped**, so published/static-bundle suites never request development source modules. All media and UI cases run against both Vite and published assets. Run twice with `--repeat-each=2` for 40 total original-media capture cycles. No real browser picker, microphone, identity provider, or cloud service is invoked.

Suggest is enabled and its command handler permits sending only when the **latest displayed turn is final**. An older final turn cannot enable generation while a newer partial turn is arriving; otherwise the backend could generate for the old turn and the stale-response filter would correctly discard that answer.

Stable automation selectors are `data-testid="mode|start|stop|suggest|pause|pin|transcript|reply|pinned-reply|status|error|consent|signin"`. Mode values are `demo`, `live`, `synthetic`. Live requires ready config and consent; Fake bypasses only Entra, never capture permission. Azure sign-in, real Teams audio support, OS permissions, throttling, and speech/AI quality require consented manual verification.

Settings selectors: `#response-mode`, `#profile-name`, `#profile-role`, `#profile-project`, `#profile-confirmed`, `#meeting-topic`, `#meeting-phrases`, `#end-silence`, under `details#meeting-options` / `fieldset#meeting-fields`. New `session-options.spec.js` covers default/personal-fact-free payloads, confirmation invalidation, session locking/reload clearing, all mode/preset payloads, invalid-term blocking with unchanged demo access, and a 200-delta rendering flood with transcript-node preservation and immediate final/pin/pause/stop behavior. Unit tests cover exact bounds, cloned start options before capture, routing metadata, and 10,000 render requests remaining one pending callback.

References: [getDisplayMedia](https://developer.mozilla.org/en-US/docs/Web/API/MediaDevices/getDisplayMedia), [MSAL initialization](https://learn.microsoft.com/en-us/entra/msal/javascript/browser/initialization), [MSAL caching](https://learn.microsoft.com/en-us/entra/msal/javascript/browser/caching).
