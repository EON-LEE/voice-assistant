# voice-assistant

An Azure-hosted, installation-free web meeting assistant that listens to shared English meeting audio
and displays short English replies for the user to read aloud. It does not speak,
send Teams messages, or make commitments on the user's behalf.

Deployed application: https://voice-web.gentlesky-d6ba12c8.koreacentral.azurecontainerapps.io

The deployed API uses the real Azure provider; the UI deliberately starts in a
labeled **Audio demo** until **Live** is selected. **Start demo** plays an original
English recording and automatically displays a scripted transcript and reply,
without sign-in or audio capture. It is not live AI. Sign-in must use an account in
the subscription's configured Entra tenant. A Windows work account from another
tenant is not automatically a member. Operator Azure PowerShell authentication
and interactive browser authentication are separate checks; neither authorizes
bypassing the other.

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

To execute the API, browser, and infrastructure checks as one fail-closed run,
write a new evidence report outside the repository:

```powershell
.\scripts\verify-release.ps1 -BicepPath 'C:\tools\bicep.exe' `
  -ReportPath 'C:\existing-artifact-folder\verification.json' -InstallBrowsers
```

The report records the source revision, uncommitted-change flag, stage results,
and durations without meeting content or credentials. Reports are not
overwritten. Local success is explicitly `LOCAL_PASS_LIVE_NOT_VERIFIED`, never a
claim of working cloud inference. `-RequireLive` makes the command fail as
`BLOCKED` after passing local checks because this local suite does not exercise
the actual authenticated Azure deployment or a user's tab-sharing selection.

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

As of September 22, 2026:

| Verification | Result |
| --- | --- |
| API Release build and automated tests | 72 passed; includes actual SDK serialization, finite-input lifecycle, semantic score and personal-context policy regressions |
| Live-provider probe unit tests | 83 passed; service, bounded cleanup, transcript-reference and redaction checks |
| Browser production build and unit tests | 39 passed, including audible-demo and bounded main-thread-stall handling |
| Chromium lifecycle and real local API transport | 22 local browser tests passed, including a 350ms main-thread stall without dropping the session |
| Infrastructure | Compiled and offline-tested, including semantic metadata migration; actual ARM deployment and ETag-guarded index readback succeeded |
| Real Azure Speech and OpenAI | Final managed-identity Linux run submitted all 219 frames / 140160 PCM bytes; 6 partial events, 1 final transcript and 34 streamed reply deltas |
| Deployed HTTPS boundary | UI and health return 200, client configuration selects Azure, anonymous ticket creation returns 401 |
| Browser sign-in | Entra account selection reached; Windows work account rejected by the separate subscription tenant; full owner-account sign-in not verified |
| Authorized knowledge ingestion | Separate managed identity ingested one original fictional fixture through Blob Private Link; actual Search ACL filters returned 1 authorized chunk and 0 for an unlisted principal; 1536-dimensional vectors are non-retrievable |
| Post-ingestion state | Job maintenance/create-index flags reset to false; web restored to Running; Storage public networking remains Disabled |
| Native Linux runtime identity | The bounded Speech/OpenAI acceptance job succeeded using the web runtime's managed identity; this does not establish interactive browser authorization |
| Reference-recognition acceptance | Original 8-word fixture recognized as 8 words with 0 word edits (WER 0), verified from retained numeric evidence and the execution's immutable image digest |
| External-video live acceptance | NASA public-domain Apollo clip, 00:07-00:18.2, shared with real `getDisplayMedia` tab audio and production worklet/session components into the genuinely authenticated deployed Azure API; 2 real final transcripts and a context-relevant streamed reply, with no unrelated sources |
| Native tab capture versus manual interaction | Actual Chromium tab audio verified with automatic selection of only the isolated NASA test tab; manual sharing-picker clicks, YouTube-site specifics, interactive tenant login and meeting p50/p95 remain unverified |

The local preview uses explicit Fake/Demo behavior. The deployed Azure services
and real-provider probe do not. An earlier cloud reference run timed out. A
reproduced finite-input ordering defect was corrected: the probe now explicitly
closes input and waits for terminal recognition instead of waiting for a final
transcript before ever signaling end-of-input. Startup, playback, drain and
cleanup have bounded waits; actual completed writes are counted. The subsequent
reference-checked cloud run succeeded; no claim is made that every historical
timeout had the same cause.

The final cleanup-reviewed run took 5.876 seconds overall, with 2.155 seconds from final transcription
to first model text, including probe drain/cleanup. Probe timing includes its
WAV tail and Speech shutdown; the fixture has no aligned speech-end ground
truth, so speech-end latency fields remain null rather than being guessed.
Three bounded text-only
calls on one client observed a cold first delta of 7.297 seconds (including
5.308 seconds acquiring a credential), then warm first deltas of 0.864 and
1.773 seconds. These are small-sample observations, not a meeting p50/p95 SLA.

Application users do not need a Windows installer. Interactive tenant sign-in,
audio-sharing consent, approved knowledge sources, and measured meeting latency
remain distinct acceptance gates. No company documents or actual meeting audio
were used for the recorded automated tests.

The deployed knowledge currently contains **only a visibly fictional acceptance
document**, not the user's real work knowledge. Ingestion is manual, private,
and disarmed after completion. Importing actual work documents requires selecting
approved sources and preserving their authorization; no Microsoft 365 crawler
or Work IQ connector has been enabled.

The external-video test used NASA's
[KSC-04-S-00294 original video](https://images-assets.nasa.gov/video/ksc_080504_apollo/ksc_080504_apollo~mobile.mp4).
Azure recognized the narration about July 20, 1969 and humans making history on
the Moon, then generated a reply about that event. Captions were reference
material only and were never supplied as transcription or model output.
The test used a genuine delegated operator token and a real one-use server
ticket, with temporary operator consent removed afterward. An authentication
adapter replaces only interactive sign-in. The final run invokes real
`getDisplayMedia` in an isolated headed browser: Chromium automatically selects
the named NASA test tab, and the returned stream has `displaySurface: browser`
and one actual shared audio track. No media stream, server response,
transcription, retrieval or model response is substituted. Manual picker clicks
and the user's interactive tenant login are not covered.

The first run exposed an unrelated knowledge candidate. Hybrid nearest-neighbor
results are now semantically reranked and filtered before they enter the prompt
or source list. Actual Azure scores in the bounded acceptance set were 2.983 for
the fictional Lighthouse question, 0.904 for the Moon narration, and 1.351 for
its date fragment. With the documented 2.0 threshold, the relevant document was
retained and both unrelated candidates were rejected; an unlisted principal
received zero candidates. Replaying the same video produced `no_matches`,
`sources: []` and a genuine transcript-only model reply, not a fixed refusal.
Ranker failure or missing scores never silently reuse unfiltered candidates.
This threshold requires evaluation on approved real sources; a relevance score
is not proof that every answer statement is supported by a returned document.

## Real English meeting replay

The [FOSDEM 2025 JMAP panel](https://fosdem.org/2025/schedule/event/fosdem-2025-6822-panel-discussion-5-years-of-jmap-experiences-and-outlook/)
provided authentic questions, accented English and a speaker change, rather than
NASA narration or synthesized conversation. The source recording is licensed
CC BY 2.0 Belgium; speaker attribution, exact intervals, preparation and results
are preserved in [the acceptance record](tests/acceptance-results/fosdem-jmap-2026-09-22.json).

| Segment | Actual result | Final transcription to first reply |
| --- | --- | --- |
| 00:54-01:10, introductions | Recognized the request; asked for clarification without inventing the user's background | 1.50 s |
| 05:31-06:00, adoption benefits | Recognized the technical question; reply was overly cautious and asked for clarification | 1.30 s |
| 05:55-06:38, moderator to participant | Preserved faster-sync/fewer-calls context and produced a relevant English response | 1.27 s |

All three final runs used real tab audio, actual Azure services, and no injected
transcript or canned model response. They ran in an isolated Linux browser after
Windows automation instability; this is not evidence that all Windows/network
stalls are solved. A subsequent audit found that the claimed appended silence
was actually a continuation of the source recording. These historical clip
intervals are therefore nominal, and the timings must not be treated as
question-end latency or compared directly with corrected clips. The original
run values and the correction are preserved together in the acceptance record.
No speech was synthesized. An operator-only authentication
adapter and automatic selection of the named tab replace interactive login and
manual picker clicks only; temporary grants were removed afterward.

Earlier runs exposed two defects: the 160ms worklet pool was too small for short
UI stalls, and the model invented personal project history when asked to
introduce the user. Both were addressed and the same scenarios rerun. Remaining
limitations are explicit: some protocol names are mistranscribed, the technical
answer can be unnecessarily tentative, and these three excerpts do not establish
long-meeting reliability, acoustic-end latency, or p95 performance.

## References

- [Windows loopback recording](https://learn.microsoft.com/en-us/windows/win32/coreaudio/loopback-recording)
- [Azure Voice Live API](https://learn.microsoft.com/en-us/azure/ai-services/speech-service/voice-live-how-to)
- [Azure AI Search security filtering](https://learn.microsoft.com/en-us/azure/search/search-security-trimming-for-azure-search)