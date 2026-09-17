# voice-assistant

An Azure-native Windows meeting assistant that listens to English meeting audio
and displays short English replies for the user to read aloud. It does not speak,
send Teams messages, or make commitments on the user's behalf.

## Architecture

```text
Teams playback -> Windows loopback capture -> PCM audio over WebSocket
  -> Azure Speech -> conversation context + optional Azure AI Search
  -> Azure OpenAI streaming reply -> Windows reply panel
```

The Windows app captures playback from the selected output device, including a
headset. It does not need to scrape the Teams screen or join the meeting as a bot.
Device loopback can also capture other applications playing on the same device;
it must not be presented as Teams-only capture.

The initial implementation separates streaming speech recognition from text
generation. Foundry-hosted models provide English replies, while Azure AI Search
provides optional document grounding. Voice Live and multi-tool Foundry agents
are possible later alternatives, not prerequisites for displaying text replies.

## Components

| Component | Location | Responsibility |
| --- | --- | --- |
| Windows desktop | `src/VoiceAssistant.Desktop` | Explicit audio capture, transcription, readable replies |
| ASP.NET Core API | `src/VoiceAssistant.Api` | Authentication, audio recognition, retrieval, streaming generation |
| Wire contract | `contracts` | Versioned client/server message definitions |
| Azure infrastructure | `infra` | Parameterized deployment and prerequisites |
| Component tests | `tests` | Offline provider, protocol, and state tests |

The components are being implemented in parallel worktrees. A component's
presence or passing offline tests is not evidence of a successful Azure
deployment or a live Teams meeting test.

## Build and test

Use Windows and the .NET 8 SDK, not only the .NET runtime.

To open the desktop app in its default, explicitly labeled offline demo:

```powershell
dotnet run --project .\src\VoiceAssistant.Desktop\VoiceAssistant.Desktop.csproj
```

Click **Start**, then **Suggest**. Demo mode uses no audio device and no Azure
connection. See the [desktop guide](src/VoiceAssistant.Desktop/README.md) before
switching to Development or Production mode.

```powershell
.\scripts\test-local.ps1
```

An isolated SDK can be selected without changing machine-wide settings:

```powershell
.\scripts\test-local.ps1 -Dotnet 'C:\path\to\dotnet.exe'
```

The script builds every application project and runs every test project. Windows
CI performs the same checks. Follow your organization's PowerShell execution
policy; do not weaken machine-wide policy to run these commands.

After starting the API in its explicitly configured, loopback-only fake-provider
mode, test the actual WebSocket transport without capturing any meeting:

```powershell
.\scripts\test-websocket.ps1 -Endpoint 'ws://127.0.0.1:8080/api/meeting'
```

This smoke test sends synthetic PCM silence and requires a final transcript and
a consistent streamed reply from the deterministic fake provider. It is not a
speech-recognition accuracy, live grounding, or cloud latency test. Fake mode
must never silently replace a failed Azure connection.

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

The deployment target is the user's `ME-M365CPI74210306-eonlee-1` subscription.
Model availability, quotas, roles, pricing, and permitted regions must be checked
before applying infrastructure. Infrastructure templates are not proof that
resources have been created.

Production clients use Microsoft Entra authentication and encrypted transport.
Azure service access should use managed identity; do not package long-lived
Azure keys or client secrets in the desktop app.

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

## References

- [Windows loopback recording](https://learn.microsoft.com/en-us/windows/win32/coreaudio/loopback-recording)
- [Azure Voice Live API](https://learn.microsoft.com/en-us/azure/ai-services/speech-service/voice-live-how-to)
- [Azure AI Search security filtering](https://learn.microsoft.com/en-us/azure/search/search-security-trimming-for-azure-search)