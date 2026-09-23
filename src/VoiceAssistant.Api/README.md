# Browser streaming API (.NET 8)

ASP.NET Core serves the browser `wwwroot` and same-origin API/WebSocket. This is not a desktop application, Teams bot, or unattended recorder. See [protocol and security contract](../../contracts/websocket-v1.md) and [Search schema](../../contracts/search-index.json).

## Offline development

From repository root in PowerShell, with .NET 8 installed:

```powershell
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:Provider__Mode = 'Fake'
$env:ASPNETCORE_URLS = 'http://localhost:5080'
dotnet run --project .\src\VoiceAssistant.Api\VoiceAssistant.Api.csproj
```

For the coordinator-provided portable SDK, first set:

```powershell
$env:DOTNET_ROOT = 'C:\Users\eonlee.REDMOND\.copilot\session-state\94af7cb9-a5ba-44f9-baac-b89831b37a39\files\dotnet'
$env:PATH = "$env:DOTNET_ROOT;$env:PATH"
```

Run the browser separately with its Vite proxy targeting `http://localhost:5080` and preserving a loopback Host. The browser's synthetic test must send a loopback Origin (for example `http://localhost:5173`). A real browser supplies this automatically. The fake provider does not contact Azure and is deliberately unusable from non-loopback clients or Production. There is no production authentication bypass.

```powershell
dotnet test .\tests\VoiceAssistant.Api.Tests\VoiceAssistant.Api.Tests.csproj
dotnet publish .\src\VoiceAssistant.Api\VoiceAssistant.Api.csproj -c Release -o .\src\VoiceAssistant.Api\bin\publish
```

The test project starts actual Kestrel listeners on ephemeral loopback ports and uses real WebSocket clients, deterministic providers, and locally signed JWTs; it never calls Azure.

## Azure configuration

Environment variables (`__` maps to `:`):

| Key | Meaning |
| --- | --- |
| `Provider__Mode` | `Azure` (default); `Fake` explicitly requires Development |
| `Authentication__TenantId` | Single Entra tenant GUID |
| `Authentication__Audience` | API token `aud` value, distinct from SPA client ID |
| `Authentication__ClientId` | Public SPA application/client GUID |
| `Authentication__Scope` | Full scope URI ending `/Meeting.Access` |
| `Security__AllowedOrigins__0` | Exact public browser HTTPS origin, no trailing slash/path; additional indices allowed |
| `Azure__SpeechRegion` | Speech resource region |
| `Azure__SpeechResourceId` | Full ARM Speech resource ID for `aad#resourceId#token` authorization |
| `Azure__SpeechEndpoint` | Optional HTTPS custom Speech endpoint; omitted uses regional endpoint |
| `Azure__OpenAIEndpoint` | HTTPS Azure OpenAI endpoint |
| `Azure__ChatDeployment` | Deployment supporting streaming chat completions |
| `Azure__ChatMaxOutputTokens` | Optional completion token budget64..4096, default2048 (reasoning models share it with reasoning tokens); temperature is omitted for compatibility |
| `Azure__SearchEndpoint` | Optional HTTPS Search endpoint |
| `Azure__SearchIndex` | Required with Search endpoint |
| `Azure__SearchSemanticConfiguration` | Semantic configuration name, default `meeting-semantic` (title + content) |
| `Azure__SearchMinimumRerankerScore` | Finite semantic reranker cutoff0..4, invariant decimal; default2.0 |
| `Azure__EmbeddingDeployment` | Required with Search; `text-embedding-3-small`, 1536 dimensions |
| `ASPNETCORE_HTTP_PORTS` | `8080` in container |

Missing mandatory Azure configuration prevents startup. Search is intentionally optional: no endpoint/index means `disabled`, never a pretend grounded response. Partial Search configuration prevents startup. No API keys/secrets are accepted. `DefaultAzureCredential` uses managed identity in Azure and developer credentials locally; do not put client secrets in source or environment configuration examples.

Chat requests explicitly use `max_completion_tokens`, not legacy `max_tokens`, and omit custom temperature. The pinned Azure.AI.OpenAI2.1 requires its documented `SetNewMaxCompletionTokensPropertyEnabled` extension to prevent rewriting the token property. Options use the public SDK model reader to initialize the property bag required by that extension; an offline transport test verifies the actual request JSON. A bounded live GPT-5 diagnostic reproduced HTTP400 `unsupported_parameter` for `max_tokens` before this correction and streamed output afterward.

Assign the runtime identity **Cognitive Services Speech User**, **Cognitive Services OpenAI User**, and (when enabled) **Search Index Data Reader**, scoped to the respective resources. Configure a Speech custom subdomain for Entra authentication. The API refreshes Speech authorization every five minutes. Ingestion uses a separate operator identity with write permissions and must enforce the original document permissions. The backend has no Blob permissions or content ingestion endpoint.

Entra API registration must issue v2 tokens and expose delegated `Meeting.Access`; SPA registration uses code+PKCE and redirect URI equal to the browser origin. JWT issuer, signing key, expiry, API audience, delegated scope and GUID `oid` are validated before issuing tickets. TLS terminates at trusted Azure ingress. Origin validation uses the configured allowlist, not forwarded-host headers. **Keep one replica/process** until ticket storage is distributed or reliable ticket-to-WebSocket affinity is implemented.

`GET /health/live` indicates the process is running. `GET /health/ready` indicates validated configuration and a running HTTP pipeline, not external Azure dependency availability; neither endpoint performs billable calls or exposes credentials. Operational monitoring must separately check resource/RBAC availability.

## Semantic relevance and no-match responses

Nearest neighbors/RRF ranking always produce candidates when the index has documents; a candidate alone is **not** relevant grounding. Search now requests Azure-native semantic reranking over the ACL-prefiltered hybrid query: vector `k=50`, response candidate pool50, `queryType=semantic`, named semantic configuration, and `semanticErrorHandling=fail`. The index must enable semantic configuration `meeting-semantic` with `title` and `content` prioritized, and the Search service must have semantic ranking enabled. See [semantic ranking](https://learn.microsoft.com/azure/search/semantic-search-overview) and [query setup](https://learn.microsoft.com/azure/search/semantic-how-to-query-request).

Every returned candidate must have a finite `@search.rerankerScore` in0..4; the API never substitutes BM25/RRF/vector scores. Only candidates at or above `Azure__SearchMinimumRerankerScore` enter model evidence, capped at5 sources/chunks. Missing/invalid scores, partial semantic response metadata, HTTP206, incomplete paginated candidate sets, disabled/misconfigured semantic ranking and service errors all fail explicitly as `grounding_unavailable`, not a retrieval fallback. Documents below the threshold are neither supplied to the model nor emitted as sources.

Default2.0 means **somewhat relevant** per Azure's scale, not a universal truth/quality guarantee. Calibrate with representative authorized queries and document content; ranking model changes can move score distributions. Compare a positive original query such as `What is the fictional Project Lighthouse plan?` with unrelated NASA/video transcripts using actual Azure reranker scores. Unit tests inject numeric scores to verify policy and wire behavior; they do not claim Azure has assigned those values to real queries. No query rewrite, secondary classifier or prefetch is added.

When semantic retrieval succeeds but no candidate passes the gate (including an empty authorized result set), `grounding:no_matches`, `sources:[]` is returned with a **real streamed model reply based only on the meeting transcript**. Instructions prohibit company-knowledge claims, invented details and document citations; general conversational phrasing or clarification is allowed. This intentionally replaces the earlier protocol description's fixed no-match clarification. UI should label it as transcript-only/not document-grounded. Missing Search configuration remains `disabled`; actual Search failure remains a visible error and deterministic unavailable completion without a model call. These are distinct states and no-match model failures must not be reported as successful fallback output.

## Container

After the sibling browser component is present, build from repository root:

```powershell
docker build -f .\src\VoiceAssistant.Api\Dockerfile -t voice-assistant .
```

If the build environment cannot connect to NuGet.org, an approved NuGet v3
source can be selected without disabling TLS verification. The Microsoft public
mirror used for local container verification is:

```powershell
docker build --build-arg NUGET_SOURCE=https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public/nuget/v3/index.json -f .\src\VoiceAssistant.Api\Dockerfile -t voice-assistant .
```

Keep credentials out of build arguments. This override is for a public or
otherwise credential-free approved feed; it does not change runtime behavior.

The multi-stage build runs frontend `npm ci` + `npm run build`, publishes the API, and copies `dist` to `wwwroot` in the final non-root .NET 8 image (port 8080). An API-only checkout can build/test/publish with dotnet; the full container intentionally requires browser sources. Repository `.dockerignore` should exclude all `node_modules`, `bin`, `obj`, `.git` and local secrets.

## Data handling and limits

Captured meeting speech is other participants' input, not a verified profile
of the app user. The response policy must not invent the user's employer,
project, personal history, availability, or commitments, including when a
moderator asks for introductions. With missing personal details it should ask
for clarification or offer a neutral response, not assign another speaker's
experience to the user. Prompt-contract tests alone are not proof of model
compliance; actual conversation acceptance runs also inspect this behavior.

Audio exists only in bounded transient buffers and the live Speech SDK push stream; transcript/answer context is memory-only and cleared when the session ends. No application transcript/audio/query/body/credential logging or persistence is configured. Azure services still process submitted content under their service data policies: configure retention, network access and diagnostics to organizational requirements. Disable full request URL/query capture in Azure ingress, access logs, telemetry and reverse proxies because the upgrade URL carries a short-lived ticket.

Errors are explicit safe messages; raw SDK exception details are suppressed. Startup, idle, response, write and session deadlines bound resource usage; 100 total sessions and one per identity, bounded queues, message/audio rates and bounded history prevent unbounded accumulation. A slow peer or overloaded recognizer is disconnected rather than silently dropping transcript/audio. New turns and manual cancellation invalidate stale generation before sending subsequent output.

SDK references: [Speech Entra auth](https://learn.microsoft.com/azure/ai-services/speech-service/how-to-configure-azure-ad-auth), [Azure OpenAI .NET streaming](https://learn.microsoft.com/dotnet/api/overview/azure/ai.openai-readme), [Search vector quickstart](https://learn.microsoft.com/azure/search/search-get-started-vector).

## Optional fast-answer session context

The [v1 start-options contract](../../contracts/websocket-v1.md) supports `responseMode`, a confirmed bounded name/role/project profile, topic, phrase list and `endSilenceMs`. No options means legacy grounded retrieval and700ms silence; the browser explicitly chooses balanced/500ms. Initial JSON is32KiB maximum (supports escaped Unicode at the field limits), subsequent commands remain4KiB, and binary audio remains32KiB. Invalid context fails startup rather than silently falling back.

Balanced routing is a conservative anchored allowlist, not an LLM classifier: clear general technical/conversational questions and covered profile introductions can skip Search; ambiguous/company/customer/date/commitment requests retrieve. Bounded history can require retrieval but never promote an ambiguous fragment to a knowledge-free route. Explicit conversation mode skips Search while requiring private-fact abstention; grounded always retrieves. Replies are prompted to start with a useful10-22-word direct sentence and use1-2 English sentences. This is a generation instruction, not a promise of perfect word counts or factuality; no second model/rewriter is used. Unconfirmed profile fields never enter the model as user facts. Unknown past experience/motivation/employer is not invented.

Only knowledge-routed substantive stable partials can prefetch retrieval, after250ms debounce, capped at3 starts/turn and one speculative worker per session. Exact final reuse folds case/whitespace only; negations/digits/punctuation stay significant. No cross-user cache, fuzzy matching, changed ACL, query logging or profile persistence. Matched Search failures stay visible. Completion exposes additive `responseRoute` and `retrievalPrefetched`; an in-flight exact match counts as prefetched, without claiming saved latency. Awaiting retrieval/generation runs outside the socket actor so audio/cancel/stop remain responsive. Retired response tasks are bounded and share a5-second cleanup wait; session cancellation also stops the prefetch worker.

Phrase hints use the official [Speech phrase-list API](https://learn.microsoft.com/azure/ai-services/speech-service/improve-accuracy-phrase-list) (`PhraseListGrammar.FromRecognizer`/`AddPhrase`) before recognition; explicit350..1500ms segmentation silence is applied to SpeechConfig. The original provider overloads remain available for LiveProbe with legacy settings. This does not alter the native audio format, Entra authentication, semantic relevance gate or credential flow.

## Content-free timing metrics

The answer prompt treats chronological STT segments as possible continuations of a preceding complete question. A short audience qualifier must not by itself trigger a clarification when a general benefits/mechanism question is already clear. This does not authorize invented private outcomes, customer commitments or personal history, nor revival of a superseded question. Four SDK prompt-contract cases cover: incremental-synchronization benefits + `For us.`, database-index mechanism + `For others.`, an unknown customer delivery commitment + `For us.`, and unknown personal history + `In your previous project.` These deterministic tests verify context ordering and policy transmission, not actual model answer quality. Routing and exact prefetch matching are unchanged; the three observed real comparison clips had zero prefetch hits, so no real prefetch latency benefit is claimed from them.

The `VoiceAssistant.Api` .NET `Meter` exposes histograms in milliseconds:

| Instrument | Meaning |
| --- | --- |
| `voiceassistant.stt_final_to_first_delta` | Final STT **callback** to first successfully sent response delta |
| `voiceassistant.stt_final_to_completed` | Final STT callback to successfully sent model response completion |
| `voiceassistant.retrieval_duration` | Retrieval call elapsed time, including disabled/no-match/error/cancellation paths |

These use monotonic timestamps. They are **not actual speech-end latency**: the API has no annotated client acoustic speech-end ground truth. Manual response metrics include the time between finalization and the manual request. Superseded/cancelled generation does not record subsequent delta/completion timings. Response histograms record `disabled`/`grounded`/`no_matches` model paths; `no_matches` now invokes the model using transcript-only context rather than returning a fixed clarification. Grounding-unavailable deterministic refusal remains excluded. Retrieval metrics still record all outcomes.

Tags are bounded `provider=Azure|Fake|TestDouble`; response `trigger=automatic|manual`; retrieval `outcome=grounded|disabled|no_matches|unavailable|unknown|failed|cancelled`. No content, IDs, URLs or arbitrary error messages are metric tags. No exporter, persistence, or new WebSocket event is enabled; an approved operational `MeterListener`/OpenTelemetry configuration may subscribe. Never mix Fake/TestDouble observations with Azure measurements.

The [live-provider acceptance probe](../../tools/VoiceAssistant.LiveProbe/README.md) directly exercises this same provider with an explicit opt-in original synthetic WAV and standard noninteractive `DefaultAzureCredential`. It separately reports content-free real service evidence or **BLOCKED**, never fake Azure success.

The speech-stream contract separately exposes `CompleteInputAsync` for finite recordings: signal push-stream EOF and drain terminal recognition callbacks before disposal. The WebSocket session does not call it during live capture; silence and new utterances keep using the same continuous stream. Expected `EndOfStream`/`NoError` is normal only after explicit input completion; service errors remain explicit.

### Speech endpoint verification

Keep `Azure__SpeechEndpoint` as the HTTPS resource endpoint from the portal, for example `https://<custom-subdomain>.cognitiveservices.azure.com/`; do not append a guessed recognition WebSocket path. [Speech SDK release notes](https://learn.microsoft.com/azure/ai-services/speech-service/releasenotes) document portal-endpoint `FromEndpoint` support beginning1.43, before this project's pinned1.48.2. [Private endpoint guidance](https://learn.microsoft.com/azure/ai-services/speech-service/speech-services-private-link#construct-endpoint-url) states the SDK chooses the service URL path. A native SDK regression test checks that the HTTPS root, en-US language and segmentation configuration are preserved; actual Azure endpoint/RBAC reachability still requires the live probe.

Speech1.48.2 fixes the [partitioned CRL compatibility defect](https://learn.microsoft.com/azure/ai-services/speech-service/migrate-to-sdk-1-48-2) affecting native Linux/Android clients before July2026 certificate changes. Because1.48.1+ disables CRL checking by default and1.47+ can tolerate CRL download failures, the API explicitly sets `OPENSSL_DISABLE_CRL_CHECK=false` and `OPENSSL_CONTINUE_ON_CRL_DOWNLOAD_FAILURE=false`. This preserves certificate revocation checking and fails closed if CRLs cannot be retrieved; production egress must permit required certificate/CRL endpoints. Do not disable these checks to hide a network or certificate error. Windows authentication failures are a separate issue and are not explained by the Linux CRL defect.
