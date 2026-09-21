# Browser meeting assistant

Installation-free TypeScript/Vite frontend for the same-origin Azure-hosted API. End users open a URL; no Windows app, extension, download, or microphone permission is needed. The preserved desktop project is not a dependency.

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

## Browser capture constraints and consent

Use **Teams in a browser tab** where possible. After selecting Live, sign in (Azure only), check participant permission, then click **Share meeting audio**. Select the Teams tab and enable **Share tab audio** in the browser/OS prompt. The application invokes `getDisplayMedia({ video: true, audio: true })` only from that click, and creates/resumes `AudioContext` in the same gesture.

The browser requires `video: true` to offer display audio; video is not attached to a video element, inspected, uploaded, recorded, or retained by this app. All video and audio tracks are stopped on Stop, session failure, permission/setup failure, source `ended`/audio `mute`, and page unload. A share granted after a cancelled/pending picker is also immediately stopped. Browsers do not let websites dismiss the picker themselves; dismiss an outstanding picker if you stop while it is open.

Audio availability depends on the browser, OS, selected surface, and user's audio-sharing option. **Teams desktop/system audio is not guaranteed.** Window sharing frequently supplies no audio. A stream without audio fails visibly and releases all tracks; there is no microphone fallback. Screen/system audio can include other applications. No browser permission can bypass participant consent or meeting policy.

The live source converts only audio tracks with a fixed-memory AudioWorklet: mono downmix, 127-tap low-pass FIR anti-alias filter, continuous fractional resampling to 16 kHz, clipped signed little-endian PCM16. It sends 640-byte/20ms frames without a WAV header, only after `session.ready`. Video is never sent. Fifty recyclable transferable buffers cap the worklet queue at one second (32,000 bytes), tolerating short UI-thread stalls without losing samples; no growing per-sample arrays are used. Worklet starvation and WebSocket `bufferedAmount` exceeding 32,000 bytes still stop visibly rather than accumulating unbounded audio or silently losing speech. Buffer capacity is not a fixed delay: frames are sent immediately while the UI thread is responsive.

Pause while speaking drops audio locally and cancels suggestions. Epoch-tagged worklet frames prevent already-queued pre-pause buffers being uploaded after resume; DSP history is reset to avoid retaining paused speech. Pause leaves the shared tracks open; **Stop** releases them. Already-uploaded audio cannot be recalled. Browser throttling/suspension can interrupt capture; keep the page available. No automatic reconnect, recording, TTS, Teams posting, or external source-link navigation is performed.

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

Transcript revisions cannot regress. Final text cannot be overwritten by partials. New turns invalidate old suggestions; only the active response ID receives deltas/completion. Cancel/pause suppress late answers; Pin creates an immutable text/source snapshot separate from new suggestions and survives session restarts until Unpin/reload. IDs are opaque; causal new-turn ordering relies on ordered WebSocket delivery. There is no client request ID in v1, so the server must preserve response-start ordering around cancellation/new requests.

State is bounded: 64 transcript turns, 256 remembered response IDs, 32,768 characters per transcript/reply, 20 sources, 64KiB incoming text messages. Nonretryable errors stop; retryable errors stay visible. No reconnect/replay is automatic. All meeting text is page-memory only.

## Validation

`npm test` compiles and runs Node's test runner with tests in `tests/VoiceAssistant.Web.Tests`: conversion rates/endianness, stereo/clipping/nonfinite samples, chunk continuity and anti-alias rejection, reducer revision/cancellation/stale IDs/pins, strict event parsing, ready gating, disconnect/fatal/device cleanup, no-audio/permission failure, picker cancellation and late tracks, worklet source lifecycle mocks, explicit Demo, and WebSocket backpressure. No test requests actual meeting/screen or microphone permission.

`npm run build` runs strict TypeScript typechecking and bundles both main UI and AudioWorklet. A local browser smoke checks Demo Start/Suggest/Pin/Pause/Stop. Coordinator-owned Playwright tests in a separate E2E directory cover full mocked-browser capture and actual fake-backend integration after merge.

The follow-up `media-playback.spec.js` uses the committed original offline speech fixture (140,204-byte canonical WAV; provenance/generator/metadata in `tests/VoiceAssistant.Web.E2E/fixtures`). Actual HTMLAudioElement decoding and `captureStream()` feed the native worklet and real local Fake API across **20 start/stop cycles**, checking nonzero 640-byte PCM frames, replies, all captured tracks ended, and all test/app AudioContexts closed. It additionally checks audio mute recovery by an explicit new Share, inert transcript/citation DOM text, rapid mode switching, and injected auth initialization/401 retry (no Entra traffic). Fake replies are deterministic; this is **not Azure semantic/STT validation**. `speechEndSample` is null, so no semantic speech-end latency is asserted. Audio playback uses a test-only zero-gain output.

Unit tests execute the actual worklet processor against a mocked message port with transferred buffers to verify bounded fifty-credit overflow, preservation across a 300ms UI stall, 1/2/6/32-channel samples, unsupported channel errors, and pause epochs/partial-frame discard. A browser regression also blocks the main thread for 350ms while the native worklet continues producing audio. Configuration initialization is single-flight and failures allow retry. A 401/403 ticket rejection clears the in-memory signed-in account, requiring a new explicit sign-in.

To run follow-up browser cases, start Vite and the local Fake API, set `VOICE_ASSISTANT_WEB_URL` to the Vite origin, `VOICE_ASSISTANT_BACKEND_E2E=1`, and `VOICE_ASSISTANT_API_EXTERNAL=1`, then run `npm test -- media-playback.spec.js` from `tests/VoiceAssistant.Web.E2E`. The two injected auth module tests use Vite's source-module endpoint and require `VOICE_ASSISTANT_VITE_AUTH_TESTS=1`; otherwise they are **explicitly skipped**, so published/static-bundle suites never request development source modules. All media and UI cases run against both Vite and published assets. Run twice with `--repeat-each=2` for 40 total original-media capture cycles. No real browser picker, microphone, identity provider, or cloud service is invoked.

Suggest is enabled and its command handler permits sending only when the **latest displayed turn is final**. An older final turn cannot enable generation while a newer partial turn is arriving; otherwise the backend could generate for the old turn and the stale-response filter would correctly discard that answer.

Stable automation selectors are `data-testid="mode|start|stop|suggest|pause|pin|transcript|reply|pinned-reply|status|error|consent|signin"`. Mode values are `demo`, `live`, `synthetic`. Live requires ready config and consent; Fake bypasses only Entra, never capture permission. Azure sign-in, real Teams audio support, OS permissions, throttling, and speech/AI quality require consented manual verification.

References: [getDisplayMedia](https://developer.mozilla.org/en-US/docs/Web/API/MediaDevices/getDisplayMedia), [MSAL initialization](https://learn.microsoft.com/en-us/entra/msal/javascript/browser/initialization), [MSAL caching](https://learn.microsoft.com/en-us/entra/msal/javascript/browser/caching).
