# voice-assistant

An Azure-hosted, installation-free web meeting assistant that listens to shared English meeting audio
and displays short English replies for the user to read aloud. It does not speak,
send Teams messages, or make commitments on the user's behalf.

## Architecture

```text
Teams tab/system audio -> browser audio sharing -> PCM audio over WebSocket
  -> Azure Speech -> conversation context + optional Azure AI Search
  -> Azure OpenAI streaming reply -> browser reply panel
```

Open the web app and explicitly start sharing audio through the browser's
screen-sharing chooser. Teams in an Edge/Chrome tab with **Share tab audio** is
the recommended first path. Capturing a desktop Teams application's audio
requires browser/OS support for system audio; sharing a window alone does not
guarantee an audio track. System audio can include other applications. No
installer or browser extension is required.

The browser needs HTTPS (or localhost for development), a user gesture, and
permission to share. The cloud server cannot independently listen to a PC.
Captured video is not sent to the server; only shared audio is processed.
If the selected source contains no audio track, the app must explain this and
stop rather than silently use the microphone.

The initial implementation separates streaming speech recognition from text
generation. Foundry-hosted models provide English replies, while Azure AI Search
provides optional document grounding. Voice Live and multi-tool Foundry agents
are possible later alternatives, not prerequisites for displaying text replies.

## Components

| Component | Location | Responsibility |
| --- | --- | --- |
| Browser app | `src/VoiceAssistant.Web` | Audio-sharing consent, transcription, readable replies |
| ASP.NET Core API | `src/VoiceAssistant.Api` | Authentication, audio recognition, retrieval, streaming generation |
| Wire contract | `contracts` | Versioned client/server message definitions |
| Azure infrastructure | `infra` | Parameterized deployment and prerequisites |
| Component tests | `tests` | Offline provider, protocol, and state tests |
| Optional legacy desktop | `src/VoiceAssistant.Desktop` | Preserved prototype; not required for the web product |

The browser, API, and infrastructure components are integrated from parallel
worktrees. Passing offline tests is not evidence of a successful Azure
deployment or a live Teams meeting test.

## Build and test

Contributors need Node.js 22 and the .NET 8 SDK, not only the .NET runtime.
End users need only a supported browser. The preserved optional desktop
prototype and its tests additionally require Windows.

To build and test the API and browser app:

```powershell
.\scripts\test-local.ps1
$env:VOICE_ASSISTANT_BACKEND_E2E = '1'
.\scripts\test-web.ps1 -InstallBrowsers
```

Start the browser development server:

```powershell
Set-Location .\src\VoiceAssistant.Web
npm ci
npm run dev -- --host 127.0.0.1 --port 5173
```

Open `http://127.0.0.1:5173`. The explicit offline demo does not use meeting
audio or Azure. Live and local synthetic modes additionally need the API;
see each component's configuration guide.

An isolated SDK can be selected without changing machine-wide settings:

```powershell
.\scripts\test-local.ps1 -Dotnet 'C:\path\to\dotnet.exe'
```

The .NET script builds/tests the API by default; use `-IncludeDesktop` to include
the preserved Windows prototype. The web script builds the browser bundle and
runs unit and Chromium tests. CI performs the same checks. Follow your organization's PowerShell execution
policy; do not weaken machine-wide policy to run these commands.

After starting the API in its explicitly configured, loopback-only fake-provider
mode, test the actual WebSocket transport without capturing any meeting:

```powershell
.\scripts\test-websocket.ps1 -Endpoint 'ws://127.0.0.1:5080/api/meeting'
```

This smoke test sends a synthetic PCM tone followed by silence and requires a final transcript and
a consistent streamed reply from the deterministic fake provider. It is not a
speech-recognition accuracy, live grounding, or cloud latency test. Fake mode
must never silently replace a failed Azure connection.

## What the tests establish

The browser suite covers explicit consent, sharing rejection, missing audio,
source-ended and disconnected cleanup, answer pinning, and a narrow viewport.
With `VOICE_ASSISTANT_BACKEND_E2E=1`, it also starts the real local API and sends
synthetic audio through the browser's native AudioContext/AudioWorklet and a real
WebSocket. No microphone, actual screen selection, or meeting is captured.

Testing the published bundle against an already running local server:

```powershell
$env:VOICE_ASSISTANT_WEB_URL = 'http://localhost:5081'
$env:VOICE_ASSISTANT_BACKEND_E2E = '1'
$env:VOICE_ASSISTANT_API_EXTERNAL = '1'
Set-Location .\tests\VoiceAssistant.Web.E2E
npm test
```

These checks prove transport and lifecycle behavior, not Azure speech accuracy.
For a real meeting-like acceptance run, configure an authenticated Azure
deployment, open a permitted public English video in a separate browser tab,
choose **Live**, sign in, grant consent, and share that tab **with audio**. Verify
that the transcript follows the actual spoken words and that the English answer
responds to them. Confirm Stop releases sharing. Record latency separately;
never count a Fake or Demo reply as a successful Azure inference. Do not save
the video's audio or a full transcript in the repository.

## Responsible meeting use

- Capture starts only after the user explicitly chooses to listen. Follow
  meeting notice/consent requirements and organizational policies.
- Do not bypass protected audio capture or tenant restrictions.
- No microphone capture is required for playback recognition. Without a
  separate microphone path, the system does not know what the user actually
  said. Displaying a suggested answer does not mean it was spoken.
- Do not store raw audio, full transcripts, or retrieved document text in
  diagnostic logs by default.
- Prefer sharing an individual presentation/application window. An always-on-top
  reply panel may be visible when sharing the entire display; exclusion is not
  guaranteed.
- Keep a reply fixed while reading it. Treat changing partial transcripts and
  newly generated replies as separate from a pinned answer.
- Never invent personal experience, project facts, deadlines, or commitments.
  Missing evidence should produce a request to confirm, not a fabricated fact.

## Azure and knowledge prerequisites

See the [deployment and knowledge ingestion guide](infra/README.md) for the
exact parameter preparation, validation, what-if, explicit deployment, and
approved document import commands. Deployment requires an existing resource
group and registry, a pushed image digest, approved Entra API/SPA registrations,
and available model capacity. It does not silently reuse another application's
identity, documents, or deployments.

Run the infrastructure checks without signing in or uploading data:

```powershell
powershell.exe -NoProfile -File .\scripts\infra\tests\Test-Offline.ps1 `
  -BicepPath 'C:\path\to\bicep.exe' `
  -BackendSchemaPath .\contracts\search-index.json
```

The integrated checks compile Bicep, verify API/index schema compatibility,
and exercise guarded deployment switches and document ACL/version/deletion
handling with mocked Azure calls. Real ARM validation and deployment still
require authentication.

The deployment target is the user's `ME-M365CPI74210306-eonlee-1` subscription.
Model availability, quotas, roles, pricing, and permitted regions must be checked
before applying infrastructure. Infrastructure templates are not proof that
resources have been created.

Production clients use Microsoft Entra authentication and encrypted transport.
The browser uses authorization code + PKCE and keeps tokens in memory. A
same-origin authenticated endpoint issues a short-lived, one-use WebSocket
ticket; long-lived access tokens must not appear in WebSocket URLs. Azure
service access should use managed identity; never send Azure keys or client
secrets to the browser. Single-instance ticket storage requires matching
deployment/scale restrictions until shared ticket storage is introduced.

Knowledge starts with explicitly approved documents. Search results must be
filtered by the authenticated user's permissions, with title, source URL, and
freshness metadata. Original permission changes and deletions must propagate to
the retrieval layer. Retrieved documents are evidence, never instructions.

Microsoft 365/Work IQ integration is a separate capability requiring supported
APIs, licensing, tenant authorization, and source access controls. Access to an
Azure subscription does not grant access to corporate mail, Teams, or SharePoint.
Do not upload corporate content merely to make a demo work.

Cost drivers include speech time, model tokens, provisioned Search capacity, and
the API's minimum replica count. Budget alerts are not spending limits. Keeping
one API replica warm reduces cold starts but has a standing cost.

## Performance acceptance

Measure from the actual end of the speaker's utterance, not only from receipt of
the final transcription event. Initial targets, not measured guarantees:

| Metric | p50 target | p95 target |
| --- | --- | --- |
| First reply text | 1.5 seconds | 3 seconds |
| First complete readable sentence | 2 seconds | 4 seconds |

Report cold/warm connection behavior, region, retrieval mode, model deployment,
sample size, cancellations, and failures alongside latency. Offline fake-provider
results cannot establish these targets.

## Verification status

As of September 19, 2026:

| Verification | Result |
| --- | --- |
| API Release build and automated tests | 19 passed |
| Browser production build and unit tests | 24 passed |
| Chromium lifecycle and real local API transport | 10 passed; Windows published app and Linux container |
| Infrastructure compile and offline checks | 51 passed; no Azure writes |
| Azure deployment, Entra login, real Speech/OpenAI inference | Not performed; authenticated deployment prerequisites remain |
| Actual video/Teams tab capture and end-to-end latency | Not verified; do not infer this from synthetic audio tests |

The local preview uses explicit Fake/Demo behavior. A real Azure-backed test
requires a signed-in operator, authorized resource configuration, and browser
sharing consent. Application users do not need a Windows installer.

## References

- [Windows loopback recording](https://learn.microsoft.com/en-us/windows/win32/coreaudio/loopback-recording)
- [Azure Voice Live API](https://learn.microsoft.com/en-us/azure/ai-services/speech-service/voice-live-how-to)
- [Azure AI Search security filtering](https://learn.microsoft.com/en-us/azure/search/search-security-trimming-for-azure-search)