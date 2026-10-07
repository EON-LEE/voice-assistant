# voice-assistant

An Azure-backed English meeting coach that displays short replies for the user
to read aloud. The in-person Windows client uses a physical microphone and a
translucent topmost overlay; the existing installation-free browser client
supports shared meeting audio. Neither sends meeting messages or makes
commitments on the user's behalf. Read-aloud playback is explicitly requested.

## In-person Windows client (portable)

The Windows client's main live surface is the overlay: recognized English/Korean
conversation, one or two persistent English/Korean suggested replies, and source
information appear together. Right-click for secondary setup, login, materials,
pause, stop or exit; there are no visible expand/close/opacity controls.
It uses Entra public-client authentication
and the same Azure Speech, Azure OpenAI and owner-authorized Azure AI Search
API as the browser. This workflow does not capture Teams or system playback.
Audio requires explicit consent and Start; launching the app does not capture.
Earlier recognized conversation is used by default for follow-up retrieval and
reply generation; AI suggestions are never treated as things the user said.

Publish a self-contained **portable folder**, without an installer:

```powershell
.\scripts\desktop\Publish.ps1 -OutputDirectory C:\Apps\MeetingCoach
```

Choose a new or empty folder outside the repository, keep all companion files,
and double-click `Open-Meeting-Coach.cmd`. See
[publishing instructions](scripts/desktop/README.md) and
[native client instructions](src/VoiceAssistant.Desktop/README.md).

The native implementation is undergoing acceptance. A successful build or
offline test is not proof of interactive Entra login, physical microphone
recognition, a complete live practice round, or focus/transparency above
PowerPoint. The browser's glass styling and Picture-in-Picture do not make
PowerPoint visible through a browser window.

## Existing browser application

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
| Portable Windows client | `src/VoiceAssistant.Desktop` | Physical microphone, translucent meeting overlay and separate practice |

The browser, API, and infrastructure components are integrated from parallel
worktrees. Passing offline tests is not evidence of a successful Azure
deployment or a live Teams meeting test.

## Faster meeting responses

Before starting a **Live** session, expand **Meeting context and answer settings**.
Settings apply to that session only and are not stored in browser storage.

| Setting | Behavior |
| --- | --- |
| Balanced | Skips retrieval only for clearly supported general requests; ambiguous/private-fact requests still use authorized Search |
| Always knowledge | Searches approved references for each answer |
| Conversation only | Avoids retrieval waits; answers from the conversation and must not invent company-specific facts |
| Confirmed name, role and project | Supplies only facts you explicitly confirm; blank fields are not guessed |
| Names and terms | Adds a short Azure Speech phrase list, useful for technical acronyms |
| End-of-turn silence | Defaults to 500ms in the new web client; shorter values trade waiting time against premature segmentation |

Partial knowledge queries can be retrieved speculatively after a stable interval,
with at most three starts per utterance and one active worker. Reuse is restricted
to the same user/session/turn and exact case/whitespace-normalized final query;
numbers, dates, negation and punctuation are preserved. Obsolete results are
discarded. A failed selected prefetch is reported, and explicit retry performs a
fresh retrieval. Speculative work can incur charges even when its result is not
used. A cache miss is acceptable; unrelated evidence is not.

General answers aim to start with a useful short sentence rather than filler.
UI content updates are coalesced within 50ms while stop, pause, completion and
pin actions remain immediate. These changes do not add a fixed audio delay.
Existing clients without `session.start.options` keep the original grounded
mode and 700ms segmentation setting.

Confirmed introductions are composed from the supplied fields without calling
the model or Search. This prevents the model adding a fictional first encounter
or employment history; the UI labels that route as not model-generated. Use
concise English wording in the profile. General technical questions still use
the model, interpreting short continuations together with the preceding question.

Use [MeetingBenchmark](tools/MeetingBenchmark/README.md) for reproducible
before/after measurements. Its media tool trims before padding, verifies the
entire silent tail and hashes the final input. Its analyzer separates first
text, first complete sentence, clip-cut observation and human-annotated speech
end; missing acoustic annotations and insufficient percentile samples stay
unknown. Do not compare the invalid historical clips with corrected inputs.

### Measured first release

The [September 23 comparison](tests/acceptance-results/latency-improvements-2026-09-23.json)
uses the same corrected clips and empty context on the actual Azure deployment.
The metric below starts at the observed **clip cut**, not a human-annotated
speech end, and ends at receipt of a complete sentence, not browser paint.

| Clip | Previous always-knowledge/700ms | Conversation only/500ms |
| --- | --- | --- |
| Introduction request, no profile | 3.09 s | 2.09 s |
| General technical question | 2.16 s | 1.69 s |
| Multi-speaker explanation | 2.45 s | 1.71 s |

These are one observation per clip and variant, not p50/p95 or an SLA. The fast
mode intentionally skips document lookup; it is not appropriate for asserting
unverified project/customer facts. Balanced-mode results were mixed, and no
final response reused a prefetch in these clips, so a real prefetch speedup is
not claimed. The revised general-question prompt answered directly in the
final replay. A separate fictional-profile test produced only the three
confirmed facts, with no invented personal history. Voice Live comparison and
a larger real-meeting sample remain future validation, not completed work.

## In-person meetings and personal meeting materials

* **Microphone mode:** Live → Audio source → *Microphone (in-person)* uses the same PCM pipeline, transcript, reply, pin, pause and stop controls as tab capture. It still needs internet and sends the room audio to Azure; inform participants as your company/customer policy requires. See the Web README for the in-person checklist.
* **My meeting materials:** open **Manage my meeting materials** in a new tab, or visit `/materials.html` before your trip. Sign in separately in that tab, upload notes, meeting minutes, slides (`.pptx`), documents (`.docx`, `.pdf` text layer), text/Markdown, transcripts (`.vtt/.srt`) and code files/folders, or paste notes. Review per-file progress and indexed-document/chunk quotas, retry failed rows, and delete selected documents with confirmation. The meeting tab stays running. Only extracted text chunks and embeddings are stored (Azure AI Search, visible only to your Entra object ID) until deletion; original files are not stored. Images/diagram text are not read; legacy `.doc`/`.ppt` are unsupported. Use one topic per file, topic-bearing titles, and text-based glossary/Q&A notes. Contract: `contracts/knowledge-v1.md`.
* **Longer sessions:** the maximum session length is the deployment setting `sessionMaxMinutes` (currently 90). At the limit the app says so and you click Start again.
* **Verified (2026-10-01):** API 429 tests, web 89 unit tests and 38 browser tests (including real Chromium fake-microphone audio into the local Fake API and real upload/list/delete against it); infra checks pass. On the deployed Azure app with the operator identity: anonymous access 401, a pasted note and an uploaded Markdown file were indexed, a spoken English question (Windows SAPI synthesized, original) was transcribed and answered from the uploaded note with that note as the only reference, then after deletion no uploaded evidence was used; a legacy `.doc` was rejected. Record: `tests/acceptance-results/materials-2026-10-01.json`.
* **Reply style (tuned 2026-10-03):** replies are plain text for reading aloud: at most 2 short sentences and 25 words, simple everyday English (about B1), no markdown, lists, quotes or "You could say" prefixes, and one short line such as "I'm not sure. Let me check and get back to you." when the facts are not available. Measured on the deployed app with 6 original synthesized English questions (grounded mode, one uploaded note, deleted afterwards): mean 25.5 -> 18.7 words, longest 44 -> 23 words, no formatting artifacts before or after. This is a small sample, not a general quality guarantee. Records: `tests/acceptance-results/reply-style-before-2026-10-03.json` and `reply-style-after-2026-10-03.json`.
* **Not verified:** a physical microphone in a real room, the browser microphone against Azure (the live test streamed PCM through the real WebSocket instead), interactive tenant sign-in, `.pdf/.docx/.pptx` on Azure (covered by in-process tests only), 30–90 minute real sessions, US-region latency and large code-base uploads.

## English coach: Korean assist, read-aloud and practice rounds

The meeting page is an English meeting coach. The English reply streams exactly as before; after it completes the browser
asks `POST /api/assist/enrich` for a Korean translation of the question and the reply and a word-chunk Hangul reading of the
reply (approximate, a guide only). "Click to listen" calls `POST /api/assist/speak` (Azure Speech neural voices
`en-US-JennyNeural` for the coach and `en-US-GuyNeural` for the practice partner, 24 kHz mono mp3, normal or slow) for shadowing.
The Practice tab runs a stateless AI partner round (`/api/practice/turn|suggest|feedback|summary`) with sales, interview,
presentation or custom scenarios, optional use of your uploaded materials, Korean feedback on wording/grammar/clarity (not
pronunciation or accent) and a Korean summary. Answers use a recognition-only WebSocket (`transcribeOnly`). Nothing is stored.
Contract: `contracts/practice-v1.md`. Live evidence (deployed Korea Central, synthetic prompts only):
`tests/acceptance-results/practice-live-2026-10-04.json` plus three consecutive full passes; reply-style re-check
`reply-style-after-practice-2026-10-04.json`. Not verified: interactive tenant sign-in, a physical microphone in a room, and
Korean translation, Hangul reading or coaching quality as judged by a human.

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
| General conversation: first useful complete sentence | 1 second | 2.5 seconds |

Grounded/private-fact questions are a separate workload and must not be mixed
with the no-retrieval fast path to manufacture a better percentile. The current
small clip-cut comparison has no reviewed acoustic-end annotations and does
not establish either target.

Report cold/warm connection behavior, region, retrieval mode, model deployment,
sample size, cancellations, and failures alongside latency. Offline fake-provider
results cannot establish these targets.

## Verification status

As of September 23, 2026:

| Verification | Result |
| --- | --- |
| API Release build and automated tests | 373 passed; includes routing, exact prefetch reuse/retry, session context, semantic relevance and personal-fact safeguards |
| Live-provider probe unit tests | 83 passed; service, bounded cleanup, transcript-reference and redaction checks |
| Browser production build and unit tests | 61 passed, including confirmed session context and bounded coalesced rendering |
| Chromium lifecycle and real local API transport | 27 Windows browser tests passed; final published Linux image: 25 passed, 2 explicitly Vite-only auth tests skipped |
| Measurement tooling | 129 deterministic/boundary tests passed, including real FFmpeg padding verification; not 129 live meeting samples |
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
