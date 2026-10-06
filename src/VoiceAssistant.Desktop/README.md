# In-person English Coach for Windows

A native .NET 8 WPF app for consented, in-person meeting assistance and a separate English-practice window. It is **not a Teams client or bot**. Meeting mode captures the selected physical microphone only; it never captures speaker/output loopback, records audio, posts messages, advances slides, or installs global keyboard hooks.

## Run

Open `VoiceAssistant.Desktop.sln` if present, or build and launch from the repository root in PowerShell:

```powershell
$env:DOTNET_ROOT = 'C:\path\to\dotnet'
$env:PATH = "$env:DOTNET_ROOT;$env:PATH"
dotnet build .\src\VoiceAssistant.Desktop\VoiceAssistant.Desktop.csproj -c Release
dotnet run --project .\src\VoiceAssistant.Desktop\VoiceAssistant.Desktop.csproj
dotnet test .\tests\VoiceAssistant.Desktop.Tests\VoiceAssistant.Desktop.Tests.csproj -c Release
```

To inspect the complete main overlay and separate practice window without sign-in or hardware, click **Offline demo preview** in the main window, or launch the explicit offline profile:

```powershell
dotnet run --project .\src\VoiceAssistant.Desktop\VoiceAssistant.Desktop.csproj -- --demo
```

For the portable published executable, run `VoiceAssistant.Desktop.exe --demo`. The separate preview window uses canned meeting/practice text and synthetic meeting frames only; it makes no network requests, opens no microphone, and does not use Azure speech, Search, or TTS. Starting the meeting preview shows a synthetic final question and then a canned reply automatically; **Generate reply again** replays it. Sign-in, microphone, consent, and refresh controls stay disabled before and after the preview runs. Every preview surface is labeled **OFFLINE DEMO**. It does not alter the signed-in production window, and a failed live connection never switches to the preview.

For a self-contained Windows build, publish with the repository's `scripts\desktop\Publish.ps1` when available, or use:

```powershell
dotnet publish .\src\VoiceAssistant.Desktop\VoiceAssistant.Desktop.csproj -c Release -r win-x64 --self-contained true `
  -o "$env:USERPROFILE\.copilot\session-state\<session-id>\files\desktop-publish"
```

The shipped `appsettings.json` points to the configured Azure service. Opening the app does not sign in or capture audio. Choose **Sign in with Microsoft** for system-browser authentication, or **Use device code** to display a temporary Microsoft verification code and URL. Complete sign-in before starting a live meeting or practice round. Tokens are acquired silently from the current process's MSAL cache when possible; expired authorization requires an explicit sign-in again.

`--demo` is an explicit offline preview mode. It cannot silently replace a failed live connection.

## Native Entra and meeting connection

`appsettings.json` contains nonsecret public-client configuration: the approved tenant, desktop public-client application ID, delegated `Meeting.Access` scope, canonical allowed HTTPS origin, and WSS endpoint. The desktop app registration must remain a **native public client** with `http://localhost` redirect and delegated API permission. There is no client secret, PIN, SPA client ID, anonymous live mode, or owner-ID override.

The native client obtains an Entra access token for authenticated HTTP operations. To open a meeting socket it sends an authenticated `POST /api/session/ticket` with the configured **exact `Origin`** header, validates the short-lived ticket response, then connects to `/api/meeting?ticket=...` with that same Origin. The bearer token is not placed in the WebSocket URL or sent as a WebSocket bearer header. Tickets are one-use and bound by the API to the authenticated user's object ID and Origin. The API continues to enforce its normal delegated-scope, identity, Search ACL, and participant-session policies.

Configuration can be supplied without modifying the published settings:

```powershell
dotnet run --project .\src\VoiceAssistant.Desktop\VoiceAssistant.Desktop.csproj -- `
  --settings C:\config\meeting-client.json
```

Supported settings include `Mode`, `Endpoint`, `Origin`, `Authority`, `TenantId`, `ClientId`, `Scope`, `ResponseMode`, `Topic`, `ProfileName`, `ProfileRole`, `ProfileProject`, and `ProfileConfirmed`. Production requires WSS, a canonical HTTPS Origin, and valid Entra identifiers. A nonempty profile is sent only after the user explicitly confirms it in the meeting setup window. Never place passwords, tokens, or client secrets in settings.

## In-person meeting mode

1. Sign in, select an active **physical microphone**, and check the participant-consent box.
2. Review the topic, optional response mode, and any optional profile values. Balanced routing is the default; Grounded always retrieves from the caller's authorized materials; Conversation skips Search.
3. Select **Start meeting overlay**. Microphone capture begins only after the server accepts the session. The main window shows a persistent active status and input-level meter.
4. The separate approximately 420×260 overlay stays on top, has a transparent window/background-opacity slider, opaque high-contrast text, and can be moved from its header or resized. Its close button only hides it; use **Stop and release microphone** to end capture. There are no hotkeys that can steal PowerPoint navigation.
5. Use the main window for the full transcript, source details, optional Korean translation/pronunciation guide, answer pinning, and personal materials. Completed answers stream into the overlay. **Read answer aloud** uses the authenticated Azure Speech endpoint and memory-only playback; microphone upload pauses during playback and resumes only if it was previously active.

Pause discards queued and new audio locally and cancels the active suggestion. Stop sends `session.stop`, closes the socket, and releases the WASAPI capture device. The API enforces its session duration and per-user concurrency limits. Captured audio is not written to disk; Azure still processes audio/text submitted during an active session under the service's data policy.

Grounding is shown distinctly: `grounded` means owner-authorized matching sources were retrieved; `no_matches` means the answer is transcript-only; `unavailable` means Search failed and must not be treated as a successful grounded answer; `disabled` means the selected route did not use Search. Source titles, URLs, and timestamps are inert text, not automatic links or a guarantee that every answer claim is supported.

## Separate practice window

Practice cannot run while meeting capture owns the microphone. Select a scenario, difficulty, optional topic, question count, and optional personal-material grounding; then confirm practice-specific microphone consent and choose a physical microphone.

**Start practice** opens an authenticated `transcribeOnly` meeting socket. It does not request or generate meeting replies and does not run Search on microphone audio. Each partner question is displayed first. **Listen to question** explicitly requests Azure TTS and holds microphone upload paused. Select **Answer by voice** to send the user's speech for recognition; partial text is marked as partial and only finalized recognition enables **Done with final answer**. **Skip this question** submits empty-answer feedback. Optional reply suggestions include grounding status and source titles. Feedback includes corrected/easier English, Korean coaching, clarity of recognized words (not a person-rating or pronunciation score), and an approximate Korean reading guide. **Next question** continues the server contract through the final summary. Retry and Stop are available for provider errors and session cleanup.

Practice audio is never interpreted while partner TTS is playing. Recognition is sent only while the answer state is active. Stop cancels pending requests, stops playback, closes the recognition socket, clears in-memory round data, and releases the microphone.

## Personal materials

The **My materials** tab lists, uploads, and deletes documents using the signed-in account's `/api/knowledge` routes. Upload supports the file types and limits documented by the API; the client limits files to 5 MiB before upload. Notes are UTF-8 text. Azure AI Search remains the sole retrieval/grounding service—there is no local RAG or guessed operator identity. Materials ingestion, indexing, owner filtering, semantic ranking, and source metadata remain server-controlled.

## Implementation and verification limits

The desktop tests cover protocol event parsing and grounding metadata, ticket request bearer/Origin headers and URL construction, start-option serialization, PCM conversion, bounded streaming/lifecycle behavior, and WPF's no-capture startup plus overlay transparency/resizing properties. The optional external-backend test is skipped unless explicitly configured.

The application builds and tests locally, but this implementation session has **not** completed an interactive Entra sign-in, an actual Azure meeting/practice round, a real-room microphone recognition run, or a visual PowerPoint overlay acceptance run. A connected physical microphone, participant consent, an interactive Microsoft sign-in, live Azure access/RBAC, and manual verification of transparency/topmost/drag/resize/focus behavior are still required for those claims. Synthetic tests are not proof of those live behaviors.
