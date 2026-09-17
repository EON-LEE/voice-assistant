# Windows meeting English reply assistant

.NET 8 WPF Windows client. It only displays suggested replies: no TTS, Teams posting, microphone input, file recording, or external actions. Audio and transcript/reply state are memory-only. Do not use with real participants without the appropriate consent.

## Build and offline demo

On Windows with the .NET 8 SDK:

```powershell
dotnet build .\src\VoiceAssistant.Desktop\VoiceAssistant.Desktop.csproj
dotnet run --project .\src\VoiceAssistant.Desktop\VoiceAssistant.Desktop.csproj
dotnet test .\tests\VoiceAssistant.Desktop.Tests\VoiceAssistant.Desktop.Tests.csproj
```

The shipped settings select **Demo**, which never connects to a backend or opens a capture device. Nothing starts at launch. Click **Start session**, wait for the sample final transcript, and click **Suggest a short reply**. Demo uses synthetic silence and explicitly canned protocol events; a live failure never falls back to it.

**Pin snapshot** copies the current answer and its references. Further deltas, new answers, cancellation, stop, and reconnect cannot replace that snapshot. New answers occupy their own panel. **Unpin** explicitly clears it. Always-on-top is off by default.

## Configuration

`appsettings.json` is copied alongside the application. All settings are nonsecret. Edit the source settings before building, or supply a separate nonsecret JSON file:

```powershell
dotnet run --project .\src\VoiceAssistant.Desktop\VoiceAssistant.Desktop.csproj -- --settings C:\config\meeting-client.json
```

| Setting | Meaning |
| --- | --- |
| `Mode` | `Demo`, `Development`, or `Production`; also selectable in the UI |
| `Endpoint` | Exact WebSocket endpoint ending in `/api/meeting`, with no query, credentials, or fragment |
| `Authority` | HTTPS Entra authority host, e.g. `https://login.microsoftonline.com`; no tenant path |
| `TenantId` | Directory tenant GUID |
| `ClientId` | Desktop public-client app-registration GUID, not the API app registration |
| `Scope` | Delegated scope exposed by the backend, e.g. `api://<API-APP-ID>/Meeting.Access` |

Production requires `wss://` and valid, non-placeholder Entra settings. Register the desktop app as a **mobile and desktop public client** with `http://localhost` as a redirect URI, grant its delegated API permission, and configure the backend's audience/tenant/scope to match. MSAL uses the system browser and sends the acquired access token only in the WebSocket upgrade's `Authorization: Bearer` header. There is no client secret, token query parameter, persisted token cache, or custom certificate validation. The current implementation creates an in-memory MSAL client per session; browser SSO may reduce repeat prompts. Token expiry or service disconnect ends the meeting session rather than silently reconnecting.

Development permits unauthenticated `ws://` only to loopback addresses (`localhost`, `127.0.0.1`, `[::1]`; secure loopback WSS is also accepted). Use it only with the backend's explicit fake/development mode. Check **Development: use synthetic silence** to exercise a real local WebSocket without opening any audio device.

For live capture, select the same **output** speaker/headset that Teams uses, check the consent box, then Start. Consent is reset after each session. The source opens only after `session.ready`. Loopback captures **all applications** playing to that endpoint, not just Teams. No microphone endpoint is offered or accepted.

## Protocol v1

Connect to `/api/meeting`; the first text frame is:

```json
{"type":"session.start","protocolVersion":1,"audio":{"encoding":"pcm_s16le","sampleRate":16000,"channels":1}}
```

Only after `session.ready`, send binary signed little-endian PCM16, 16,000 Hz, mono. Frames are at most 640 bytes (20 ms); a capture callback may leave a smaller even-length frame. No WAV header is sent.

Client commands are `response.request`, `response.cancel`, and `session.stop`, each a text JSON object with a `type` property. Pause drops queued/new audio locally, waits for an already-in-flight audio frame, and sends `response.cancel`. Audio already received by the server cannot be recalled. Stop attempts `session.stop` before cancelling socket reads, disconnects, and releases capture resources. Reconnection always requires an explicit new Start.

The client accepts the coordinated server contract without extensions:

| Event | Fields |
| --- | --- |
| `session.ready` | `type` |
| `transcript.partial`, `transcript.final` | `turnId`, nonnegative integer `revision`, `text` |
| `response.started`, `response.cancelled` | `responseId`, `turnId` |
| `response.delta` | `responseId`, `turnId`, incremental `text` |
| `response.completed` | `responseId`, `turnId`, authoritative complete `text`, `sources: [{title,url,updatedAt}]` |
| `error` | `code`, `message`, boolean `retryable` |

`sources` must be an array (empty is valid); `updatedAt` can be null/missing. References are displayed as text, not automatically opened. Lower transcript revisions, post-final partials, obsolete turn/response events, post-completion deltas, and cancelled/paused answers are discarded. WebSocket ordering is used for new turn/response ordering; IDs are opaque and not parsed as sequence numbers. Nonretryable errors, malformed/oversized events, device failures, and disconnects are visible and terminate the session. Retryable server errors are visible without forcing a disconnect.

## Audio, limits, and lifecycle

NAudio 2.2.1 provides selected-device WASAPI loopback. The streaming converter supports float32 and PCM16/24/32 endpoints at 16-192 kHz, 1-32 channels, including extensible PCM/IEEE-float formats. It downmixes, applies a 127-tap Blackman-windowed sinc low-pass filter, resamples with continuous phase, clips, and encodes PCM16 little-endian. Partial input samples survive capture callback boundaries.

The upload queue is capped at 50 frames (at most 1 second / 32,000 bytes). Overflow explicitly stops the session rather than silently discarding speech or growing memory. Pause discards the queue. Receive messages are capped at 64 KiB; accumulated transcript/reply text at 32,768 characters; transcript history at 64 turns; remembered response IDs at 256; references at 20 per reply. Send, connect, handshake, and shutdown operations have bounded timeouts. A silent but connected socket can remain open until Stop; transport keepalive does not promise a server liveness timeout.

WASAPI may not emit callbacks when nothing plays on the selected output; the live source does not manufacture silence or switch devices automatically. Pause keeps the selected loopback source open and drops audio in memory; use Stop to release it. Transcripts before pause and already-uploaded audio may still finish processing. Selecting the wrong Teams playback device produces no useful meeting transcription.

## Tests and backend integration

Tests cover conversion output rate/endianness, fractional-rate chunk continuity, stereo downmix, signed PCM variants, alias rejection, malformed events/settings, revision/response ordering, immutable pinning, ready-gated audio creation, pause/resume, device failure, queue overflow, explicit demo, and the WPF Start/Suggest/Pin/Pause/Stop flow on an STA dispatcher. Real local Kestrel + ClientWebSocket tests verify the exact handshake, binary PCM frames, fragmented text events, completed replies, `session.stop`, message-size limits, and disconnect handling. No test performs real capture or authentication.

The opt-in external-backend smoke test is explicitly **skipped** unless a loopback fake service is provided:

```powershell
$env:VOICE_ASSISTANT_TEST_ENDPOINT = 'ws://127.0.0.1:8080/api/meeting'
dotnet test .\tests\VoiceAssistant.Desktop.Tests\VoiceAssistant.Desktop.Tests.csproj --filter Category=BackendE2E
Remove-Item Env:\VOICE_ASSISTANT_TEST_ENDPOINT
```

It uses the real transport with synthetic silence, waits for `transcript.final`, sends `response.request`, verifies nonempty `response.completed`, and stops. The fake backend must generate its fixture transcript from synthetic audio. A real speech backend will not transcribe silence and is not suitable for this test.

Live Teams playback, physical device hot-unplug, Entra sign-in, and Azure speech/AI quality require a consented manual deployment test; unit/demo runs do not claim those are validated. There is no per-Teams-process capture, durable transcript history, token refresh/reconnect loop, or microphone support.

SDK references: [NAudio WASAPI loopback](https://github.com/naudio/NAudio/blob/master/Docs/WasapiLoopbackCapture.md), [MSAL system-browser guidance](https://learn.microsoft.com/en-us/entra/msal/dotnet/acquiring-tokens/using-web-browsers).
