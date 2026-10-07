# In-person English Coach for Windows

A native .NET 8 WPF app whose **main live surface is the overlay itself**: recognized English/Korean conversation above one or two English/Korean suggestions and document sources. Login, microphone consent and materials remain in a secondary settings window, accessed by right-clicking the overlay (or the keyboard context-menu key). It is **not a Teams client or bot**. Meeting mode captures the selected physical microphone only; it never captures speaker/output loopback, records audio, posts messages, advances slides, or installs global keyboard hooks.

## Run

Open `VoiceAssistant.Desktop.sln` if present, or build and launch from the repository root in PowerShell:

```powershell
$env:DOTNET_ROOT = 'C:\path\to\dotnet'
$env:PATH = "$env:DOTNET_ROOT;$env:PATH"
dotnet build .\src\VoiceAssistant.Desktop\VoiceAssistant.Desktop.csproj -c Release
dotnet run --project .\src\VoiceAssistant.Desktop\VoiceAssistant.Desktop.csproj
dotnet test .\tests\VoiceAssistant.Desktop.Tests\VoiceAssistant.Desktop.Tests.csproj -c Release
```

To inspect the overlay and separate practice window without sign-in or hardware, open **Microphone settings / My materials** from the overlay's right-click menu, then click **Offline demo preview**, or launch the explicit offline profile:

```powershell
dotnet run --project .\src\VoiceAssistant.Desktop\VoiceAssistant.Desktop.csproj -- --demo
```

For the portable published executable, run `VoiceAssistant.Desktop.exe --demo`. The separate preview window uses canned meeting/practice text and synthetic meeting frames only; it makes no network requests, opens no microphone, and does not use Azure speech, Search, or TTS. Starting the meeting preview shows a synthetic final question and then a canned reply automatically; **Generate reply again** replays it. Sign-in, microphone, consent, and refresh controls stay disabled before and after the preview runs. Every preview surface is labeled **OFFLINE DEMO**. It does not alter the signed-in production window, and a failed live connection never switches to the preview.

For a self-contained Windows build, publish with the repository's `scripts\desktop\Publish.ps1` when available, or use:

```powershell
dotnet publish .\src\VoiceAssistant.Desktop\VoiceAssistant.Desktop.csproj -c Release -r win-x64 --self-contained true `
  -o "$env:USERPROFILE\.copilot\session-state\<session-id>\files\desktop-publish"
```

The shipped `appsettings.json` points to the configured Azure service. Opening the app shows only the overlay and does not sign in or capture audio. Right-click for secondary settings. There are no separate login buttons: starting Live or requesting materials/practice connects the configured Microsoft account when needed, before accessing protected resources. After the first successful authentication, the official MSAL persistence extension retains the cache across app restarts and portable-folder updates, encrypted with Windows DPAPI for the current Windows user. Tokens are acquired and refreshed silently when possible. Microsoft may still require interactive authentication after consent revocation, MFA/sign-in-frequency policies or account changes; persistence cannot bypass those policies. Cache persistence errors are surfaced instead of silently falling back to plaintext. Closing the secondary settings window returns to the overlay; **Exit application** in the overlay menu ends sessions and releases resources.

The cache is outside the portable distribution at
`%LOCALAPPDATA%\VoiceAssistant\Identity\<tenant-id>\<client-id>\msal-cache.bin`.
Never copy it into the repository, include it in a portable archive or share it.
Tenant/client-specific directories keep different app identities separate.

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

1. Select an active **physical microphone** in secondary settings. There are no login buttons or consent/translation checkboxes in the visible meeting flow. **Start Live** explicitly identifies microphone audio transmission to Azure and requires participant permission; it does not start on launch. Start first uses the encrypted persistent account cache and requests Microsoft authentication only when necessary, before capturing audio. API authentication and private-document owner filtering are not disabled.
2. Review the topic and optional profile values. **Grounded (always search my materials)** is the default; it retrieves only from the signed-in account's authorized Azure AI Search documents. Balanced and Conversation are explicit alternatives.
3. Select **Start meeting overlay**. Microphone capture begins only after the server accepts the session; the secondary settings window hides and the overlay remains the main surface.
4. The fixed-width 550×860 overlay (height capped at the Windows work area) stays on top, has a translucent dark background and opaque text, and moves from its header. **Overlay v2** in the header distinguishes this build from older portable folders. The latest transcript has a highlighted left-edge bubble, prior speech is muted, each answer has a numbered label, and a grounding badge accompanies the answer heading. The Korean meaning stays below each English option. There are **no visible buttons, resize grip, close/expand controls, or opacity slider**. Its right-click menu provides setup, start, pause/resume, regenerate, translation retry, stop and application exit. There are no global hotkeys that can steal slide-navigation keys.
5. Recognized speech and its Korean translation appear in the upper independent conversation scroller. The central answer cards show one primary English suggestion and, when supplied by the server, one alternative with its own Korean meaning. Korean display is mandatory for the live overlay. English streams without waiting for translation; completed options translate concurrently, keyed to response/option so late translations cannot overwrite another answer. Loading and failure states occupy the Korean area instead of leaving it blank; **Retry Korean translation** is available from the menu. Owner-authorized source names and grounding state stay below the cards.
6. The last completed suggestion remains visible while a new utterance or regeneration is pending, on pause/cancel, and after stop, with its original question identified as previous/current. A new session clears it. Generated suggestions are never added to recognized transcript history. Before the first answer the card shows an honest waiting state, not an invented suggestion. Menu actions and consent remain explicit; the overlay does not auto-start capture.

The native client uses a 500 ms end-of-speech silence threshold for faster turn completion; this can split a speaker's thought sooner than the 700 ms server default. Increase it in `appsettings.json` or a supplied settings file if your speakers pause mid-sentence.

Conversation context is on by default. The API combines bounded recent recognized
utterances with bounded excerpts of earlier actual speech for follow-up retrieval
and answer generation. The excerpt buffer is not a semantic summary or a complete
meeting archive; old details can be omitted. Retrieval prefetch and final retrieval
use the same contextual query without an extra model call to rewrite each question.
One model stream produces the primary reply and an optional alternative; existing
servers without `suggestions` still display their single `text` reply.

Pause discards queued and new audio locally and cancels the active suggestion. Stop sends `session.stop`, closes the socket, and releases the WASAPI capture device. The API enforces its session duration and per-user concurrency limits. Captured audio is not written to disk; Azure still processes audio/text submitted during an active session under the service's data policy.

Grounding is shown distinctly: `grounded` means owner-authorized matching sources were retrieved; `no_matches` means the answer is transcript-only; `unavailable` means Search failed and must not be treated as a successful grounded answer; `disabled` means the selected route did not use Search. Source titles, URLs, and timestamps are inert text, not automatic links or a guarantee that every answer claim is supported.

## Separate practice window

Practice cannot run while meeting capture owns the microphone. Select a scenario, difficulty, optional topic and question count; then confirm practice-specific microphone consent and choose a physical microphone.

**Use my uploaded materials (Azure AI Search)** is enabled by default in live
practice. Sign in with the same account that uploaded the documents, and check
the **My materials** tab before starting. Both partner questions and optional
reply suggestions request owner-authorized material grounding. Matching source
titles and grounding status appear in the conversation; no match is not proof
that the documents were used. You can explicitly turn material use off.

Portable means **no installation**, not no internet. Personal-material AI
practice requires internet and Entra sign-in because the documents and inference
are in Azure. Offline preview is only a fixed UI sample, not personal-material
practice; its material checkbox is off and disabled. No local RAG or anonymous
access is introduced.

**Start practice** opens an authenticated `transcribeOnly` meeting socket. It does not request or generate meeting replies and does not run Search on microphone audio. Each partner question is displayed first. **Listen to question** explicitly requests Azure TTS and holds microphone upload paused. Select **Answer by voice** to send the user's speech for recognition; partial text is marked as partial and only finalized recognition enables **Done with final answer**. **Skip this question** submits empty-answer feedback. Optional reply suggestions include grounding status and source titles. Feedback includes corrected/easier English, Korean coaching, clarity of recognized words (not a person-rating or pronunciation score), and an approximate Korean reading guide. **Next question** continues the server contract through the final summary. Retry and Stop are available for provider errors and session cleanup.

Practice audio is never interpreted while partner TTS is playing. Recognition is sent only while the answer state is active. Stop cancels pending requests, stops playback, closes the recognition socket, clears in-memory round data, and releases the microphone.

## Personal materials

The **My materials** tab lists, uploads, and deletes documents using the signed-in account's `/api/knowledge` routes. Upload supports the file types and limits documented by the API; the client limits files to 5 MiB before upload. Notes are UTF-8 text. Azure AI Search remains the sole retrieval/grounding service—there is no local RAG or guessed operator identity. Materials ingestion, indexing, owner filtering, semantic ranking, and source metadata remain server-controlled.

## Implementation and verification limits

The settings window owns session lifecycle and delegates all feature entry points
to the same account-connection routine. Translation requests share one
nonempty-Korean-response validator; per-turn and per-option ownership remains
separate. The overlay renders session status, conversation and suggestions
independently so status updates do not overwrite reply text or translations.

The desktop tests cover protocol event parsing and grounding metadata, ticket request bearer/Origin headers and URL construction, grounded/500 ms start-option serialization, PCM conversion, bounded streaming/lifecycle behavior, and WPF's no-capture startup plus separated conversation/suggestion presentation and fixed translucent overlay. The optional external-backend test is skipped unless explicitly configured.

The application builds and tests locally, and the API has separate live Azure/Search/practice acceptance evidence, but the desktop itself has **not** completed a live meeting from its native sign-in through the physical microphone. A connected physical microphone, participant consent, interactive native Microsoft sign-in, and manual verification of transparency/topmost/drag/focus behavior over PowerPoint are still required for those claims. Synthetic tests are not proof of those live behaviors.
