# Browser meeting assistant

Installation-free TypeScript/Vite frontend for the same-origin Azure-hosted API. End users open a URL; no Windows app, extension, or download is needed. Tab capture never requests microphone permission; the explicit in-person microphone mode does. The preserved desktop project is not a dependency.

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

Pause while speaking drops audio locally and cancels suggestions. Epoch-tagged worklet frames prevent already-queued pre-pause buffers being uploaded after resume; DSP history is reset to avoid retaining paused speech. Pause leaves the shared tracks open; **Stop** releases them. Already-uploaded audio cannot be recalled. Browser throttling/suspension can interrupt capture; keep the page available. No automatic reconnect, recording, TTS, Teams posting, or external source-link navigation is performed.

## In-person use

Select **Live → Audio source → Microphone (in-person)**, sign in, inform the participants under your company/customer policy, check the room-audio permission checkbox, then click **Start microphone**. Internet is required for Azure processing. Test the microphone first: the input-level meter should respond after Start. Place the laptop/phone near the speaker without blocking its microphone. Room acoustics, noise and browser support affect recognition; this does not identify individual speakers or guarantee a particular latency.

This explicit choice requests `getUserMedia({audio:{channelCount:1,echoCancellation:true,noiseSuppression:true,autoGainControl:true,deviceId?:{exact:...}},video:false})` from the Start click. It never requests screen capture and never falls back to another source. After permission, the device list shows `enumerateDevices()` audio-input labels; Stop before selecting a different device for the next session. Selection stays in page memory only. Permission denial, no device, a busy device, disconnection and device removal produce actionable errors; missing devices are not silently replaced.

The microphone reuses the same fixed-memory Worklet, PCM16/16kHz conversion, 50-frame capacity, ready-gated WebSocket and one-use-ticket auth as tab capture. A parallel 256-sample AnalyserNode updates the level meter at 10Hz without allocating per-frame sample arrays or changing PCM transmission. **Pause while speaking** drops audio and cancels generation; press **P** when not typing in an input, textarea, select, editable area or button to toggle it. Stop releases all tracks and closes the context (the browser microphone indicator should turn off). A system/browser `mute` displays a warning, drops microphone frames and keeps the session; `unmute` clears the warning and resumes unless the user remains paused. An ended track stops the session instead. Permission never starts automatically on page load, mode selection or device selection.

The server controls session duration (`Session:MaxMinutes`). Its exact fatal `session_time_limit` event produces **Session time limit reached – click Start to continue** after cleanup. Other normal server closes are shown as ended sessions, abnormal closes as unexpected disconnects; normal closure alone is not falsely classified as a time limit. A new Start always requires fresh capture consent.

## My meeting materials

The collapsible panel is disabled in Demo with an explanation. Choose Live and sign in to manage materials; the localhost-only Fake backend uses its explicitly labelled fixed fake owner without a token. Production calls the existing memory-only MSAL token flow and adds `Authorization: Bearer` only to `/api/knowledge` or a validated document-ID path; redirects to other origins are refused. A 401 prompts sign-in rather than falling back to Fake. Document ownership comes only from the server's authenticated identity; no owner ID is sent.

Select multiple files or a folder (`webkitdirectory`), or drag/drop files/folders. Selection shows ready/skipped counts before **Upload selected files**. Filtering excludes hidden directories/files, `node_modules`, `.git`, `bin`, `obj`, `dist`, `build`, `.venv`, `__pycache__`, unsupported extensions, files above 5 MiB, and binary/non-UTF-8 content masquerading as text. Supported DOCX/PPTX/PDF containers are sent to the server for bounded extraction; the client does not parse them. Drag-folder traversal stops at 2,000 entries and a selection/queue is capped at 300 files. No ignored file is uploaded. See `contracts/knowledge-v1.md` for the exact extension list and extraction/ACL guarantees.

Alternatively paste notes with a title (1–200 characters; notes up to 400,000 characters). Uploads use at most **two concurrent requests**, including notes. Per-item state is queued/uploading-and-processing/success/failure, not a fabricated byte-percentage meter. **Retry failed only** never replays successful or cancelled items. **Cancel uploads** aborts active client requests and cancels queued items; an already accepted server upload may still complete, so refresh and delete if necessary. Clear finished entries releases their in-memory file/text references. Uploading is allowed while a meeting runs and does not pause audio.

The list displays titles, chunk counts and dates with keyboard-accessible per-item Delete; the usage line shows documents/chunks versus server limits. Changes are refreshed after successful uploads/deletes. Error codes map to safe messages (too large, unsupported type, no extractable text, quota, busy, unavailable, or sign-in required); raw server/cloud errors and file contents are not logged or displayed as diagnostics. Reference sources whose host is `my-materials.invalid` render the document title with **My meeting materials**, never that placeholder URL or an automatic navigation link.

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

`npm run build` runs strict TypeScript typechecking and bundles both main UI and AudioWorklet. A local browser smoke checks Demo Start/Suggest/Pin/Pause/Stop. Coordinator-owned Playwright tests in a separate E2E directory cover full mocked-browser capture and actual fake-backend integration after merge.

The follow-up `media-playback.spec.js` uses the committed original offline speech fixture (140,204-byte canonical WAV; provenance/generator/metadata in `tests/VoiceAssistant.Web.E2E/fixtures`). Actual HTMLAudioElement decoding and `captureStream()` feed the native worklet and real local Fake API across **20 start/stop cycles**, checking nonzero 640-byte PCM frames, replies, all captured tracks ended, and all test/app AudioContexts closed. It additionally checks audio mute recovery by an explicit new Share, inert transcript/citation DOM text, rapid mode switching, and injected auth initialization/401 retry (no Entra traffic). Fake replies are deterministic; this is **not Azure semantic/STT validation**. `speechEndSample` is null, so no semantic speech-end latency is asserted. Audio playback uses a test-only zero-gain output.

Unit tests execute the actual worklet processor against a mocked message port with transferred buffers to verify bounded fifty-credit overflow, preservation across a 300ms UI stall, 1/2/6/32-channel samples, unsupported channel errors, and pause epochs/partial-frame discard. A browser regression also blocks the main thread for 350ms while the native worklet continues producing audio. Configuration initialization is single-flight and failures allow retry. A 401/403 ticket rejection clears the in-memory signed-in account, requiring a new explicit sign-in.

To run follow-up browser cases, start Vite and the local Fake API, set `VOICE_ASSISTANT_WEB_URL` to the Vite origin, `VOICE_ASSISTANT_BACKEND_E2E=1`, and `VOICE_ASSISTANT_API_EXTERNAL=1`, then run `npm test -- media-playback.spec.js` from `tests/VoiceAssistant.Web.E2E`. The two injected auth module tests use Vite's source-module endpoint and require `VOICE_ASSISTANT_VITE_AUTH_TESTS=1`; otherwise they are **explicitly skipped**, so published/static-bundle suites never request development source modules. All media and UI cases run against both Vite and published assets. Run twice with `--repeat-each=2` for 40 total original-media capture cycles. No real browser picker, microphone, identity provider, or cloud service is invoked.

Suggest is enabled and its command handler permits sending only when the **latest displayed turn is final**. An older final turn cannot enable generation while a newer partial turn is arriving; otherwise the backend could generate for the old turn and the stale-response filter would correctly discard that answer.

Stable automation selectors are `data-testid="mode|start|stop|suggest|pause|pin|transcript|reply|pinned-reply|status|error|consent|signin"`. Mode values are `demo`, `live`, `synthetic`. Live requires ready config and consent; Fake bypasses only Entra, never capture permission. Azure sign-in, real Teams audio support, OS permissions, throttling, and speech/AI quality require consented manual verification.

Settings selectors: `#response-mode`, `#profile-name`, `#profile-role`, `#profile-project`, `#profile-confirmed`, `#meeting-topic`, `#meeting-phrases`, `#end-silence`, under `details#meeting-options` / `fieldset#meeting-fields`. New `session-options.spec.js` covers default/personal-fact-free payloads, confirmation invalidation, session locking/reload clearing, all mode/preset payloads, invalid-term blocking with unchanged demo access, and a 200-delta rendering flood with transcript-node preservation and immediate final/pin/pause/stop behavior. Unit tests cover exact bounds, cloned start options before capture, routing metadata, and 10,000 render requests remaining one pending callback.

References: [getDisplayMedia](https://developer.mozilla.org/en-US/docs/Web/API/MediaDevices/getDisplayMedia), [MSAL initialization](https://learn.microsoft.com/en-us/entra/msal/javascript/browser/initialization), [MSAL caching](https://learn.microsoft.com/en-us/entra/msal/javascript/browser/caching).
