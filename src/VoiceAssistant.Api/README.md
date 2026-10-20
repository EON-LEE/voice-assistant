# Browser streaming API (.NET 10)

ASP.NET Core serves the browser `wwwroot` and same-origin API/WebSocket. This is not a desktop application, Teams bot, or unattended recorder. See [protocol and security contract](../../contracts/websocket-v1.md) and [Search schema](../../contracts/search-index.json).

## Offline development

From repository root in PowerShell, with the .NET 10 SDK installed:

```powershell
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:Provider__Mode = 'Fake'
$env:ASPNETCORE_URLS = 'http://localhost:5080'
dotnet run --project .\src\VoiceAssistant.Api\VoiceAssistant.Api.csproj
```

For a portable SDK installed outside `PATH`, first set its local directory:

```powershell
$env:DOTNET_ROOT = 'C:\Tools\dotnet'
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
| `Session__MaxMinutes` | Session maximum duration, integer5..180, default30; invalid values prevent startup |

Missing mandatory Azure configuration prevents startup. Search is intentionally optional: no endpoint/index means `disabled`, never a pretend grounded response. Partial Search configuration prevents startup. No API keys/secrets are accepted. `DefaultAzureCredential` uses managed identity in Azure and developer credentials locally; do not put client secrets in source or environment configuration examples.

### Experiment switches

Each switch changes one behavior so it can be A/B tested on staging with the quality evaluation, one variable at a time. All are **off or unset by default**, which keeps the behavior described elsewhere in this README. An invalid value prevents startup instead of being ignored. At startup the API logs one content-free line with the values in effect (`Live reply switches: ...`).

The deployment template (`infra/main.bicep`) turns on the two switches that passed that evaluation: `Session__EarlyStartAtFinal` (settle wait about 350 → 70 ms) and `Speech__MaterialPhraseHints` (key-term recognition 0.48 → 0.67 on the synthetic suite, at the same latency and answer quality). `minimal` reasoning effort, also with api-version `2025-04-01-preview`, gave no first-token gain and is not used.

| Key (environment variable) | Values (default) | Effect |
| --- | --- | --- |
| `Azure__ChatReasoningEffort` | unset \| `minimal` \| `low` \| `medium` (unset) | Sends `reasoning_effort` with the live answer request. Unset sends no `reasoning_effort` (the deployment default). |
| `Azure__ChatAuxiliaryReasoningEffort` | unset \| `minimal` \| `low` \| `medium` (unset) | The same, for the turn classifier and the Korean translation requests. |
| `Azure__ChatApiVersion` | unset \| a dated version such as `2025-04-01-preview` (unset) | The `api-version` of the live chat requests (answer, classifier, translation). Unset keeps the pinned SDK's default (`2024-10-21`). Set it only if the deployment rejects `reasoning_effort` under the default. |
| `Session__EarlyStartAtFinal` | `true` \| `false` (`false`) | With `replySettleMs` > 0, a final that makes the gathered speech a complete question or request (the rule that picks the 300 ms settle) starts its reply at once instead of after the settle. |
| `Session__SpeculativeStart` | `true` \| `false` (`false`) | Requires `Session__EarlyStartAtFinal=true` (startup fails otherwise). With `replySettleMs` > 0, when the latest partial stays unchanged for 250 ms and makes the gathered speech a complete question or request, that reply is generated already and held back on the server: sent at once when the final has the same words, discarded unseen otherwise. |
| `Speech__MaterialPhraseHints` | `true` \| `false` (`false`) | Adds phrase hints from the caller's own materials to the live speech recognizer's phrase list, after the client's phrases. |
| `Reply__CommitmentPolicy` | `true` \| `false` (`false`) | Adds a short system instruction to the live answer: when asked to guarantee, promise or commit (date, price, discount, service level, delivery, refund, exception), decline politely; state the documented policy, limit or number exactly as written when the evidence has it, otherwise say it needs to be confirmed; never invent one. |

How the reasoning switches work:

- The pinned SDK has no reasoning-effort option. A configured effort is read into the same SDK property bag that carries `max_completion_tokens` and is sent as the wire property `reasoning_effort`. Offline tests check the actual request JSON, with and without the switch.
- If the deployment rejects a value (HTTP 400), nothing falls back silently. The reply fails with `response_unavailable` and the log line `Meeting response failed ... HTTP status 400`. A rejected classifier logs `Turn classifier failed: ..., HTTP status 400`, and a rejected Korean line logs `Reply translation failed: HTTP 400`.
- Practice and materials calls never send a reasoning effort.

How early start works (`Session__EarlyStartAtFinal`):

- The settle timer still runs. When it fires, the early reply stands unless the speech gathered after it changed the target. Backchannels such as `Yeah.` never change the target.
- If the target changed, the whole gathered speech is answered and replaces the early reply, exactly as a superseding request does today (`response.cancelled`, then `response.started`). A second complete question in the same window replaces it at its own final.
- An early start happens only when no reply is active, or to replace this window's own early reply. A reply still answering an earlier window keeps holding and queueing the next question as before.
- `voiceassistant.reply.settle_wait` shows the effect: about 0 ms for early replies.

How speculative start works (`Session__SpeculativeStart`, on top of early start):

- **Trigger.** The latest partial pauses: its words stay the same (case, punctuation and spacing aside) for 250 ms on the session clock. Speech partials carry no punctuation, so the pause stands in for the end of the sentence. The gathered speech plus the partial must then pass the early-start rule above (a question or request that does not end on a dangling word), and no reply may be active.
- **Why 250 ms.** The Speech service sends a partial as each word is processed and none during silence, so until the final (about 1 s after speech ends) a pause in partials is the only sign that a question is finished; a repeated partial is no such sign. 250 ms is the pause the partial prefetch already waits before it retrieves, so the reply and its retrieval start from the same text, and a shorter gap between words never starts one.
- **Generation.** The reply is generated exactly as early start would begin it at that text's final: the same bounded and focused history (worked out as if the partial were the final), route, settings and 30 s reply deadline. Its search follows the same rule as for a reply started at the final: it takes over the partial's prefetched search when that search has already started for exactly this text, and otherwise searches on its own at once, so it never waits behind the prefetch worker. The adopted reply does not search again. A confirmed profile introduction is not speculated, because it is composed at once anyway.
- **Held back.** Nothing reaches the client before the final. Deltas, the completion, `grounding_unavailable`, errors and Korean lines are held in order on the server, so the protocol is unchanged and nothing is ever retracted. Failure log lines and reply metrics are held too.
- **Adopted.** The final adopts the held reply when it has the same words (only case, punctuation and spacing may differ) and early start would begin a reply for it now with the same history, route and settings. Then `response.started` is sent, followed at once by everything the reply produced so far, in order; the rest streams as usual. Numbering, superseding and the settle check work exactly as for a reply started at the final.
- **Discarded.** Otherwise the held reply is cancelled silently and the final takes today's path. It is also discarded when newer speech changes its target, when another reply starts, on `response.cancel`, `response.request`, `session.update` or `session.stop`, when the recognizer restarts, and when the session ends (for example when a reconnect supersedes it). New words change the target; words that only add a backchannel such as `okay` do not, as for an early reply. A discarded reply sends nothing, logs no reply or classifier failure, and records no reply metric.
- **Bounds.** At most one speculative generation runs per session; a pause waits until a discarded one has finished unwinding.
- **Metrics.** `voiceassistant.reply.speculation` counts started, adopted and discarded replies (with the discard reason), and `voiceassistant.reply.speculative_head_start` records how long before its final an adopted reply started. An adopted reply that already had its first line shows about 0 ms in `settle_wait`, `stt_final_to_first_delta` and `final_to_primary_done`. Call metrics (`retrieval_duration`, `retrieval.failures`, `chat.throttled`, `chat.retries`) count every real call, speculative ones included.

How material phrase hints work (`Speech__MaterialPhraseHints`):

- **Source.** Hints come only from the caller's own materials, read through the same ACL filter as retrieval: the document titles plus up to 1,000 chunks (one Search page). They never come from transcripts.
- **Extraction.** Hints are document titles (without the file extension), acronyms, product codes such as `ORN-4`, CamelCase names, and capitalized names of two or more words that occur at least twice. An all-capital word that also appears in lower case is treated as a shouted heading, not an acronym. Common heading words are dropped.
- **Ranking and caps.** Titles come first, then terms by frequency. At most 100 hints of at most 50 characters are kept. They are merged after the client's `phrases`, case-insensitively without duplicates.
- **Scope.** Hints go only to the recognizer's phrase list (including in-process recognizer restarts). Prompts and Korean translation keep the client's phrases. Practice (`transcribeOnly`) sessions get no hints.
- **Cache.** Hints are cached per user for up to 30 minutes, and dropped when that user uploads or deletes a document. Search shows a write only after a short delay, so a build applies the materials service's own record of that user's latest uploads and deletes: a deleted document's chunks are left out even while Search still returns them, and a just-uploaded document contributes its title before Search returns its text. Hints built within 10 s of an upload or delete, or while Search does not show it yet, are kept for 15 s only, so they are rebuilt once Search has caught up. Issuing a session ticket starts building them in the background. A session start waits at most 1 s for hints that are not ready, then uses the client's phrases only. A failed build yields no hints and is retried by the next session.
- **Logs.** Only counts and timings are logged (`Material phrase hints built: N phrases ...`, `Speech phrase list: N from the client and M from materials.`), never the phrases.

Chat requests explicitly use `max_completion_tokens`, not legacy `max_tokens`, and omit custom temperature. The pinned Azure.AI.OpenAI2.1 requires its documented `SetNewMaxCompletionTokensPropertyEnabled` extension to prevent rewriting the token property. Options use the public SDK model reader to initialize the property bag required by that extension; an offline transport test verifies the actual request JSON. A bounded live GPT-5 diagnostic reproduced HTTP400 `unsupported_parameter` for `max_tokens` before this correction and streamed output afterward.

Assign the runtime identity **Cognitive Services Speech User**, **Cognitive Services OpenAI User**, and (when Search/materials are enabled) **Search Index Data Contributor**, scoped to the respective resources. Configure a Speech custom subdomain for Entra authentication. The API refreshes Speech authorization every five minutes. Operator-managed ingestion remains separate and must enforce original document permissions. Personal materials use the authenticated API described below. The backend has no Blob permissions.

Entra API registration must issue v2 tokens and expose delegated `Meeting.Access`; SPA registration uses code+PKCE and redirect URI equal to the browser origin. JWT issuer, signing key, expiry, API audience, delegated scope and GUID `oid` are validated before issuing tickets. TLS terminates at trusted Azure ingress. Origin validation uses the configured allowlist, not forwarded-host headers. **Keep one replica/process** until ticket storage is distributed or reliable ticket-to-WebSocket affinity is implemented.

Every response carries `Cache-Control: no-store`, `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`, `Permissions-Policy: camera=(), geolocation=(), microphone=(self), display-capture=(self)` and `Content-Security-Policy: frame-ancestors 'self'; object-src 'none'; base-uri 'self'` (no script restrictions yet; `'self'` rather than `'none'` because Microsoft sign-in renews tokens in a hidden same-origin iframe). Outside Development, a request that arrived over HTTPS (directly, or as `X-Forwarded-Proto: https` from the Container Apps ingress, which ends TLS) also gets `Strict-Transport-Security: max-age=31536000; includeSubDomains`.

The optional shared demo login (`DemoLogin__Username`, `DemoLogin__Password`, `DemoLogin__ObjectId`, `DemoLogin__SigningKey`) issues bearer tokens for one fixed identity that are valid for `DemoLogin__TokenDays` days: default 7, allowed 1..30; any other value prevents startup. Such a token cannot be revoked except by rotating the signing key.

`GET /health/live` indicates only that the process is running; it never depends on Azure. `GET /health/ready` returns 200 `{"status":"ready"}` once a startup warm-up has completed, and 503 with `{"status":"warming"}` until then. Fake mode is ready at once. In Azure mode, the warm-up runs in the background:

- It acquires the Cognitive Services token (Speech and Azure OpenAI share that audience) through the runtime credential, so the first session finds it cached.
- It then opens a TLS connection to the Azure OpenAI endpoint through the client and shared transport the reply path uses. It also checks that the Speech endpoint (or the regional speech-to-text host) is reachable. The Speech SDK opens its own connection per session, so for Speech only DNS and TLS reachability are checked.
- Readiness requires only those stages. Search is best-effort: a reply without it still works (it reports `grounding_unavailable`), so the Search token and connection are warmed beside them and readiness never waits for them. A failed Search attempt is logged (stage, exception type, HTTP status) and counted in `voiceassistant.warmup.failures`, then retried in the background with a 1-2-4-...-60 s backoff until it succeeds once.
- Any HTTP status counts as reached. The requests are an endpoint-root GET and a Search document count: no model, speech or semantic-ranking call, and nothing billable.
- Each stage is bounded to 10 s. A failed required attempt is retried with a 1-2-4-8-15 s backoff.
- After 60 s of failures, the service logs one content-free error line (stage, exception type, HTTP status) and the endpoint reports `{"status":"not_ready"}`. Retries continue, and the replica becomes ready as soon as an attempt succeeds.
- Once ready, the replica stays ready: a later transient token or network failure never removes it from rotation. Such failures surface on the reply path instead.

Neither endpoint exposes credentials. Operational monitoring must still check resource and RBAC availability separately, because a 401/403 answer also counts as reached.

## Semantic relevance and no-match responses

Nearest neighbors/RRF ranking always produce candidates when the index has documents; a candidate alone is **not** relevant grounding. Search now requests Azure-native semantic reranking over the ACL-prefiltered hybrid query: vector `k=50`, response candidate pool50, `queryType=semantic`, named semantic configuration, and `semanticErrorHandling=fail`. The index must enable semantic configuration `meeting-semantic` with `title` and `content` prioritized, and the Search service must have semantic ranking enabled. See [semantic ranking](https://learn.microsoft.com/azure/search/semantic-search-overview) and [query setup](https://learn.microsoft.com/azure/search/semantic-how-to-query-request).

A candidate is relevant only with a finite `@search.rerankerScore` in0..4; the API never substitutes BM25/RRF/vector scores. Only candidates at or above `Azure__SearchMinimumRerankerScore` enter model evidence, capped at5 sources/chunks; the completion lists each document once (de-duplicated by URL, else title, in relevance order) even when several of its chunks are evidence. A candidate without a valid reranker score is skipped (never used as evidence) as long as other candidates were scored; when none was scored, or on partial semantic response metadata, HTTP206, incomplete paginated candidate sets, disabled/misconfigured semantic ranking and service errors, retrieval fails as `grounding_unavailable`, never with an unranked fallback. Documents below the threshold are neither supplied to the model nor emitted as sources.

Live retrieval uses Search and embedding clients without SDK retries (`AzureServiceClients.RetrievalSearch`/`RetrievalOpenAI`) and retries once itself: a transient failure (HTTP429, any5xx, a ranker time-out or capacity limit, a time-out or network error) is retried after100ms with the remaining ~700ms of an 800ms budget that the first attempt leaves inside the10-second cap; a successful embedding is reused. Permanent failures (HTTP400/401/402/403/404, credentials, invalid results, no scored candidate) are not retried. Each reply that loses its documents logs one content-free warning: failure kind, transient flag, exception type, HTTP status, Search error code (identifiers only) and whether a retry happened; never the query, transcript or service message.

Default2.0 means **somewhat relevant** per Azure's scale, not a universal truth/quality guarantee. Calibrate with representative authorized queries and document content; ranking model changes can move score distributions. Compare a positive original query such as `What is the fictional Project Lighthouse plan?` with unrelated NASA/video transcripts using actual Azure reranker scores. Unit tests inject numeric scores to verify policy and wire behavior; they do not claim Azure has assigned those values to real queries. No query rewrite, secondary classifier or prefetch is added.

When semantic retrieval succeeds but no candidate passes the gate (including an empty authorized result set), `grounding:no_matches`, `sources:[]` is returned with a **real streamed model reply based only on the meeting transcript**. Instructions prohibit company-knowledge claims, invented details and document citations; general conversational phrasing or clarification is allowed. This intentionally replaces the earlier protocol description's fixed no-match clarification. UI should label it as transcript-only/not document-grounded. Missing Search configuration remains `disabled`. An actual Search failure first sends the non-fatal `grounding_unavailable` error and then a real streamed model reply without documents (`grounding:unavailable`, `sources:[]`): an extra instruction keeps it brief, general and non-committal, without company-specific facts, numbers, names, dates or commitments, and lets it say that details still need to be confirmed. Only when that model call fails too does the deterministic unavailable completion follow, without a second error. These are distinct states and no-match model failures must not be reported as successful fallback output.

## Personal meeting materials

See [the materials contract](../../contracts/knowledge-v1.md). `GET /api/knowledge`, `POST /api/knowledge` (one multipart file with optional title, or JSON `{title,text}`), and `DELETE /api/knowledge/{id}` reuse the existing Search index and validated caller `oid`. Azure mode always requires the existing `Meeting` JWT/scope policy. POST/DELETE require the exact allowed Origin. Bearer-only GET permits absent Origin but rejects a supplied disallowed Origin.

Explicit Development Fake uses an in-memory store with the same fixed local identity as the fake socket; it does **not** provide a production authentication bypass. Fake requests require loopback peer/Host; GET permits absent Origin, otherwise loopback Origin is required. Cross-site requests are rejected. The browser can exercise upload/list/delete offline; Fake answer generation remains explicitly synthetic, not Azure document retrieval.

Supported inputs are UTF-8 text/code (BOM tolerated, invalid sequences rejected), docx paragraphs, pptx slides in presentation order and slide-linked notes, and text-layer PDF. PdfPig0.1.11 is a pinned managed Apache-2.0 dependency. Original input bytes and parsed text are memory-only; no `IFormFile` buffering, temporary files, shell converters, macros, OCR, external XML entities, or content logging. HTML tags are stripped. PDFs use only the text layer; images/scans with no text return422.

Bounds: file5MiB; title200 characters; extracted text400000 characters; JSON/multipart request5MiB+64KiB; Office archive1024 entries,4MiB per expanded entry,20MiB combined declared expansion, maximum200:1 compression ratio above64KiB. XML readers prohibit DTDs and have null resolvers and character caps. PDF200-page maximum,4MiB decoded stream and20MiB cumulative filtered-output budget, bounded predictor dimensions; Flate/ASCII filters are supported, uncommon compressed/encrypted/malformed documents may be rejected rather than parsed without bounds. Resave rejected documents as a simple text-layer PDF or UTF-8 text.

Extraction has a10-second deadline, with at most two parser workers globally. A timed-out worker retains its slot until the managed parser unwinds; it is not unsafely terminated or replaced with unlimited background workers. Per-user admission independently permits at most two uploads (third returns429); mutation serialization prevents concurrent uploads from overspending that user's300-document/5000-chunk quota. Requests have a2-minute deadline. Admission and quota synchronization are process-local: keep the existing single API replica/process constraint; multi-replica ingestion needs a distributed owner lock/reservation mechanism before scaling.

Chunks are at most1400 characters including filename/nearest heading context, usually around1000–1400 characters with150-character overlap, preferring line/paragraph boundaries and not splitting UTF-16 surrogate pairs. IDs are `kb-<random32hex>-<0000>` and ACLs contain only the caller's canonical GUID. URL metadata is `https://my-materials.invalid/<id>`; this is a title-only source, not a navigation target. Embeddings use the same configured deployment as retrieval, exactly1536 dimensions and at most16 inputs/request. Azure upload prepares **all** embeddings before writing, checks every Search indexing result, and returns201 only after every chunk is accepted.

If indexing partially fails or its response is lost, rollback deletes **every generated key**, including keys in uncertain batches, under an independent30-second deadline. Each failed rollback batch is retried once and other batches are still attempted. A rollback that cannot be confirmed is explicitly logged without content/IDs and returns503, never success. Search has no multi-document transaction: temporary visibility during indexing and a service outage preventing compensation cannot be eliminated without a schema/staging design. If a503 leaves visible fragments, the caller can list and delete that document after service recovery. No guarantee of successful rollback during an unavailable Search service is implied.

Listing/deletion always uses the ACL filter, pages all results, selects only `id,title,url,updatedAt`, and accepts only exact personal-material chunk IDs/URLs; operator documents are neither listed nor deleted. Search returns at most 1000 rows per page, so one listing requests up to 100,000 rows and follows every continuation page; this is what makes quota counts and deletes cover chunks beyond the first 1000 rows. A listing that reaches 100,000 rows fails as503 instead of undercounting the quota or leaving chunks behind. Locally accepted uploads remain counted until Search listing catches up, and locally owned uploaded keys permit safe immediate deletion before Search refresh. Another user's document ID cannot access that ownership ledger. Deletes are idempotent; quota/count convergence follows Search refresh. Neither raw files nor vectors are returned by these endpoints.

The runtime identity now needs **Search Index Data Contributor** on the existing Search service (plus its existing OpenAI role); this component does not create roles/resources or access Blob Storage. `AzureServiceClients` shares the standard credential, OpenAI and Search clients with `AzureMeetingProvider`. Missing Search/embedding configuration yields503 for materials, not a fake store. Retrieval keeps the existing ACL-prefiltered semantic relevance gate unchanged.

### Session duration

`Session:MaxMinutes` / `Session__MaxMinutes` replaces the fixed30-minute limit (valid5..180, default30). At expiry the server sends the existing error event with `code:"session_time_limit"`, `retryable:false`, message `Session reached its time limit. Start a new session to continue.`, then closes. Other session cancellations retain `session_ended`; the90-second receive-idle timeout is unchanged. Receiving is cancelled only after the expiry error is sent, because cancelling a WebSocket receive first would abort the socket and lose that notification.

## Container build

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

The multi-stage build runs frontend `npm ci` + `npm run build`, publishes the API, and copies `dist` to `wwwroot` in the final non-root .NET 10 image (`aspnet:10.0-noble`, Ubuntu 24.04, port 8080). .NET 10 publishes no Debian images, so the runtime stage installs Ubuntu's `libasound2t64` (the renamed ALSA library the Speech SDK needs) instead of Debian's `libasound2`. An API-only checkout can build/test/publish with dotnet; the full container intentionally requires browser sources. Repository `.dockerignore` should exclude all `node_modules`, `bin`, `obj`, `.git` and local secrets.

## Data handling and limits

Captured meeting speech is other participants' input, not a verified profile
of the app user. The response policy must not invent the user's employer,
project, personal history, availability, or commitments, including when a
moderator asks for introductions. With missing personal details it should ask
for clarification or offer a neutral response, not assign another speaker's
experience to the user. Prompt-contract tests alone are not proof of model
compliance; actual conversation acceptance runs also inspect this behavior.

Audio exists only in bounded transient buffers and the live Speech SDK push stream; transcript/answer context is memory-only and cleared when the session ends. No application transcript/audio/query/body/credential logging or persistence is configured. Azure services still process submitted content under their service data policies: configure retention, network access and diagnostics to organizational requirements. Disable full request URL/query capture in Azure ingress, access logs, telemetry and reverse proxies because the upgrade URL carries a short-lived ticket.

Metric export (optional, see [Content-free timing metrics](#content-free-timing-metrics)) sends only this app's content-free meter to the configured Application Insights resource. The exporter's own usage telemetry, Statsbeat, is switched off. Statsbeat would otherwise send exporter usage data to Microsoft-owned Application Insights resources and probe the instance metadata endpoint. When export is enabled and `APPLICATIONINSIGHTS_STATSBEAT_DISABLED` is unset, the app sets it to `true` before the exporter starts; an explicit value from the operator is kept.

Errors are explicit safe messages; raw SDK exception details are suppressed. Startup, idle, response, write and session deadlines bound resource usage; 100 total sessions and one per identity, bounded queues, message/audio rates and bounded history prevent unbounded accumulation. A slow peer or overloaded recognizer is disconnected rather than silently dropping transcript/audio. Continuous partial/final speech does not abort an active reply: the latest final utterance is queued (intermediate queued utterances still enter recognized history) and starts after the active reply completes or fails. Manual regeneration/cancellation and session stop still invalidate obsolete generation immediately. Clients retain response/turn ownership and label replies to earlier questions accordingly.

SDK references: [Speech Entra auth](https://learn.microsoft.com/azure/ai-services/speech-service/how-to-configure-azure-ad-auth), [Azure OpenAI .NET streaming](https://learn.microsoft.com/dotnet/api/overview/azure/ai.openai-readme), [Search vector quickstart](https://learn.microsoft.com/azure/search/search-get-started-vector).

## Optional fast-answer session context

The [v1 start-options contract](../../contracts/websocket-v1.md) supports `responseMode`, a confirmed bounded name/role/project profile, topic, phrase list, `endSilenceMs`, `replySettleMs` and `replyKorean`. After start only `responseMode` and `replyKorean` can change, through `session.update`; the server announces this with `sessionUpdate:true` in `session.ready`, and every other option stays fixed for the socket. No options means legacy grounded retrieval and700ms silence; the browser explicitly chooses balanced/500ms. Initial JSON is32KiB maximum (supports escaped Unicode at the field limits), subsequent commands remain4KiB, and binary audio remains32KiB. Invalid context fails startup rather than silently falling back.

Balanced routing is a conservative anchored allowlist, not an LLM classifier: clear general technical/conversational questions and covered profile introductions can skip Search; ambiguous/company/customer/date/commitment requests retrieve. Bounded history can require retrieval but never promote an ambiguous fragment to a knowledge-free route. Explicit conversation mode skips Search while requiring private-fact abstention; grounded always retrieves. Replies are prompted to start with a useful10-22-word direct sentence and use1-2 English sentences. This is a generation instruction, not a promise of perfect word counts or factuality; no second model/rewriter is used. Unconfirmed profile fields never enter the model as user facts. Unknown past experience/motivation/employer is not invented.

Only knowledge-routed substantive stable partials can prefetch retrieval, after250ms debounce, capped at3 starts/turn and one speculative worker per session. A **final** transcript that is worth a reply is retrieved at once, with no debounce and no start cap, because with `replySettleMs` its reply waits out the settle window anyway; a final promotes an identical candidate that is still waiting out the debounce. With `Session__SpeculativeStart`, a speculative reply on a paused partial takes over that partial's search like a reply started at the final would (only a search that has already started), and newer partials do not replace it while that reply waits for its final; the final that adopts the reply does not search again. A discarded speculative reply gives its search back: newer speech then cancels it (at once, if newer speech already replaced it), and an identical final may still reuse it. Newer speech cancels speculative work nobody waits for, but never a retrieval a reply has already taken over; that one finishes on its own and is not reused afterwards. Exact reuse folds case, whitespace and punctuation at the edges of words only (a speech partial and its final differ in exactly these); negations, digits (`10` vs `100`), hyphens, signs and apostrophes inside words (`can` / `cannot` / `can't`) stay significant. No cross-user cache, fuzzy matching, changed ACL, query logging or profile persistence. Matched Search failures stay visible as `grounding_unavailable`. Completion exposes additive `responseRoute` and `retrievalPrefetched`; an in-flight exact match counts as prefetched, without claiming saved latency. Awaiting retrieval/generation runs outside the socket actor so audio/cancel/stop remain responsive. Retired response tasks are bounded and share a5-second cleanup wait; session cancellation also stops the prefetch worker.

**Reply trigger and settle window.** With `replySettleMs` the server answers the gathered meaning of recent speech instead of every final. `ReplyTrigger` discards backchannels (`Yeah.`, `OK, good.`), answers questions and direct requests (including requests without a question mark, such as `Tell me about yourself.` or `Walk us through the rollout.`) and answers a statement once it has 12 words. The wait is `replySettleMs`, shortened to 300 ms when the gathered speech ends like a finished question or request (a trailing preposition, auxiliary, article, conjunction or filler keeps the full wait), and to 300 ms once speech has been gathered for 12 seconds. New speech re-arms the full wait, so a question split across several finals still becomes one reply. Gathered speech never outgrows the bounded history: a pending segment that leaves the 12-turn window is dropped from the reply target (a run that is then only backchannels is discarded), so a long stretch of short remarks cannot end the session. Known tradeoffs: a second question that arrives after a short gap queues behind the first reply, and a statement that reaches a reply only through its 12 words keeps the full wait. The turn-emphasis classifier starts before retrieval, so it has normally finished when the answer does; completion waits for it at most 800 ms and treats a slow or failed classifier as "no emphasis".

**Early primary line.** `response.delta` carries `primaryDone: true` exactly once per streamed reply, on the delta that ends the first line or, when that end is only known later (a single-line reply ends with the stream), on one extra empty delta; empty deltas exist only with the marker. The concatenated deltas equal `response.completed.text` (no server post-processing of the primary line), so the overlay can show the primary line before completion; completion timing itself is unchanged.

**Korean push.** A client that sends `replyKorean:true` receives the Korean meaning of every reply line as `response.korean` events (see the contract). The same `meeting-chat` deployment translates beside the answer using `KoreanTranslation`, the rule set the HTTP enrichment path also uses, plus the meeting topic and the names from the configured phrases and the retrieved evidence titles, which stay as written. The primary line is translated as soon as it is complete, so its Korean can be ready about when the English finishes; the alternative starts when the stream ends. A failed or rejected translation is reported as `failed:true` and the web client then translates that line over HTTP. A client that did not opt in, or that switched the push off with `session.update`, gets no events and no translation calls. A change applies from the next reply: a reply that already started keeps the settings it started with, and `session.updated` reports the effective values in event order. This describes the design; the measured effect on Azure is reported separately and is not claimed here.

Phrase hints use the official [Speech phrase-list API](https://learn.microsoft.com/azure/ai-services/speech-service/improve-accuracy-phrase-list) (`PhraseListGrammar.FromRecognizer`/`AddPhrase`) before recognition; explicit350..1500ms segmentation silence is applied to SpeechConfig for silence segmentation and recognition-only sessions. Under `semanticSegmentation` the value is validated but not sent, so it has no effect. The original provider overloads remain available for LiveProbe with legacy settings. This does not alter the native audio format, Entra authentication, semantic relevance gate or credential flow.

## Korean assist, read-aloud and practice

The additive POST endpoints are `/api/assist/enrich`, `/api/assist/speak`, `/api/practice/turn`,
`/api/practice/suggest`, `/api/practice/feedback` and `/api/practice/summary`; see
[practice-v1](../../contracts/practice-v1.md). Azure requires the existing Meeting JWT policy and exact Origin;
explicit Development Fake uses the same loopback peer/Host/Origin guard and fixed local owner as the fake socket.
Requests are strictly validated JSON (16KiB maximum), with unknown/duplicate fields, invalid types and forbidden
controls rejected. Eight non-speech requests may be active per owner, with60 in a rolling minute across enrichment
and practice; speech has a separate three-active/30-per-minute bucket. Rejections carry429 and Retry-After.
The deadlines are20seconds for model operations and15seconds for speech. A noncooperative timed-out provider keeps
its concurrency slot until its task unwinds instead of spawning unlimited background work.

Azure chat reuses the existing client/options and JSON response format, with a minimum4096 completion-token budget
(reasoning tokens also consume it). Outputs are structurally validated; invalid model JSON/schema gets one retry,
then502, never a Fake fallback. A token-cap finish is logged as `completion_token_limit`, distinct from empty content.
The larger practice-only budget reduces reasoning-token exhaustion risk but does not prove it caused any prior502;
live response limits and all per-field output caps remain unchanged. JSON-object mode is retained rather than
assuming additional strict-schema API compatibility with the pinned SDK/deployed model.

Model output only (never incoming request validation) tolerates harmless NFC/whitespace normalization, curly
apostrophes in generated English, a complete outer JSON markdown fence, or a sole `result`/`data`/`output` object
wrapper. Unrecognized model properties are discarded at known object boundaries, so model-supplied turn numbers,
source titles, grounding status and similar extras cannot override server-computed metadata. Ambiguous wrappers,
duplicate properties, trailing prose, non-JSON, missing/wrong-type required fields, overlong outputs, unsafe style,
English-only Korean echoes and actual pronunciation word changes still fail. Pronunciation comparison ignores
case/punctuation/whitespace but requires identical normalized word count, order and spelling; the server then
rebuilds every `en` chunk from the original input words, restoring the caller's exact casing and punctuation.
If model chunk sizes are0 or above4, the server rechunks to at most3 original words, only when the number of supplied
Hangul chunks matches the new chunk count. It never manufactures missing Hangul or repairs word additions,
omissions or reordering. Input punctuation-only standalone tokens are rejected for alignment rather than guessed.
Korean coaching may contain English technical terms
without an arbitrary Latin-vs-Hangul character ratio and may quote the learner's English terms; Hangul text is still
required, and the enrichment meaning translation must be Hangul-dominant (`schema:korean:hangul_dominant`): it
needs Hangul words and is rejected when more than two Latin-only words are not outnumbered by Hangul words, so a kept
leading name such as `Copilot이 ...` is valid while an English echo is not. Feedback/headline caps are800characters
and summary-phrase Korean meanings400, matching browser limits; enrichment translation remains400 and point text120.
Hangul-only sound chunks retain their stricter script rule. Do not mistake these structural tolerances for semantic
correction of model output.

Feedback permits up to3 Korean sentences within the800-character limit, recognizing ASCII and Korean/full-width
sentence endings and ignoring trailing closing quotes for sentence counting. Partner questions remain at most
30 words and2 sentences including a short acknowledgement, ending in `?`; contractions, colons, semicolons and
in-sentence hyphens are accepted. Markdown/list prefixes, emoji/non-English characters, URLs and meta-answer
commentary remain invalid. Diagnostics now include exact suffixes such as `schema:text:english_style:max_words`,
`schema:text:english_style:max_sentences`, `schema:feedbackKo:max_characters`,
`schema:feedbackKo:max_sentences`, and `schema:pronunciation:rejoin:word_sequence`, without rejected text.

Practice diagnostics log **Warning** records containing only fixed endpoint name, fixed failure category,
numeric HTTP status (zero if unavailable), and attempt number. Model attempts are1 and2; attempt0 identifies an
outer endpoint timeout/provider failure. Retrieval is attempted at most twice, each attempt bounded to about5 s:
a first failure logs `retrieval_retry` with attempt1 and is retried after200 ms, a second failure logs
`retrieval_unavailable` with attempt2 and the endpoint continues scenario-only. Categories include `empty`,
`json_parse`, `completion_token_limit`, `completion_not_stopped`, `schema:<fixed field/rule>` (for example
`schema:text:repeat` and `schema:points:ko:<rule>`), `http_error`, `timeout`, `retrieval_retry` and
`retrieval_unavailable`. No exception object, body, prompt, user text, identity, URL or rejected property name is logged.
An exhausted schema retry can therefore yield two detailed safe model warnings and one generic endpoint warning.

Enrichment accepts the optional boolean `translationOnly` (default false). When true, the model is never asked for
a Hangul reading (the pronunciation rules are omitted from the prompt), no alignment runs, and `pronunciation` is JSON
null for both kinds. The live desktop overlay uses this because it shows only Korean meaning; it saves output tokens and
removes the alignment/Hangul-chunk retry path that could make a reply translation slow or fail502.
Question and translation-only enrichment treat `pronunciation` as server-owned: any model-provided value is discarded and the response
always contains JSON null, including when the field was omitted. Korean translation still passes validation.
Reply enrichment has no null-success fallback: pronunciation remains required and aligned after at most one retry.
On that retry only the allowlisted failed validator category is added to the system prompt (for example
`schema:pronunciation:rejoin:word_sequence`), never the rejected output or an arbitrary property/message.
Unknown optional model fields remain discarded; missing/invalid required reply fields still fail502 after retry.

Suggestions/corrected sample answers are prompted to be only first-person words the speaker would say, not
commentary about "provided information" or "a safe answer". The style validator rejects such meta-answer prefixes;
it does not strip them into fabricated success. Without materials the model may express a general preference or
approach, never assert made-up company facts, dates or commitments. When supplied evidence answers the question the
answer is given directly in the first sentence, and the model hedges at most once and only about a specific missing
detail. The prompt carries no example sentences the model could copy as its own answer, and each practice operation
receives only its own role block: the partner `turn` operation is told it is the meeting partner (never the learner
or a coach) and must ask one new question about a different point; `suggest`, `feedback` and `summary` receive the
first-person learner wording rules. Partner lines that repeat an earlier partner line or its closing question fail as
`schema:text:repeat` and are retried once with a hint. Feedback points whose Korean text has no Hangul are dropped
rather than failing the response. Turn and feedback policies share these limits.

Hangul guides are **approximate pronunciation aids**, not pronunciation assessment.
Aligned English chunks must reconstruct the input exactly after whitespace collapse, and Hangul sound chunks reject
Latin echoes. The validator enforces form/length/alignment, not semantic translation correctness, CEFR level or the
truth of generated coaching: real Azure/user review is still necessary. Feedback uses transcripts, not voice/accent.
User context and retrieved evidence are fenced serialized data, never system instructions. Materials use the existing
caller-ACL/semantic retrieval; retrieval failure is explicitly `unavailable` with scenario-only practice output, as
specified by the practice contract. Live WebSocket grounding-failure behavior is unchanged.

Read-aloud maps `coach` to **en-US-JennyNeural** and `partner` to **en-US-GuyNeural**, with XML-writer-escaped SSML and
normal (`0%`), slow (`-20%`) and fast (`+25%`, the web default) rates. Speech SDK returns24kHz48kbit/s mono MP3 without local speaker playback; Fake returns a valid100ms
silent24kHz mono PCM WAV. Authorization reuses SpeechAuthorization and the standard managed-identity credential,
retaining CRL checks. The existing Cognitive Services Speech User role is the expected runtime permission.
**The deployed Korea Central voice list, live AAD synthesis and audio playback have not been checked by this change**;
the coordinator must verify those selected voices against the resource before claiming live acceptance.
No input text, audio, feedback or provider error body is logged or persisted. Response caching remains disabled.

Fake enrichment deliberately uses the contract's `[fake-ko] ` marker and an input echo (truncated to400characters),
not a real Korean translation; this marker is the explicit Fake-only exception to Hangul-dominant translation validation.
Pronunciation still passes exact alignment/Hangul validation. Very word-dense replies that cannot fit40chunks fail502,
rather than returning invalid alignment.

`session.start.options.transcribeOnly:true` creates an immutable recognition-only channel: Speech phrase/silence
options still apply, partial/final transcripts continue, but no history-driven reply, Search, profile composition or
speculative retrieval occurs. Manual `response.request` returns nonfatal-in-session `transcribe_only` with
`retryable:false`; the socket remains usable until stop/expiry. Missing/false retains the existing live behavior.

## Content-free timing metrics

**Deterministic confirmed introductions:** a recognized individual/group introduction request in balanced/conversation mode uses fixed English labels plus verbatim confirmed name/role/project fields, not a model call. It emits `responseRoute:profile`, `grounding:disabled`, `sources:[]`, `retrievalPrefetched:false`. Unknown history/first encounter is omitted entirely. Partial known profiles work without adding unknown placeholders; missing/unconfirmed profiles keep conservative model behavior. Mixed private customer/date/commitment/employer requests do not qualify, and grounded mode continues retrieving. The browser should request concise English profile text. The deterministic reply is excluded from all model first-delta/completion histograms; no new latency metric claims model work occurred.

For mixed introduction/history questions, confirmed name/role/project fields support a brief introduction even when the first encounter or past experience is unknown. The model must state only supplied facts, omit unsupported history or ask a targeted follow-up after the known facts; it must not withhold the entire introduction or invent history/employer/role. With no relevant confirmed fields the personal-fact abstention guard remains. SDK prompt tests cover both confirmed and unconfirmed versions; live model validation is still needed before claiming response quality.

The answer prompt treats chronological STT segments as possible continuations of a preceding complete question. A short audience qualifier must not by itself trigger a clarification when a general benefits/mechanism question is already clear. This does not authorize invented private outcomes, customer commitments or personal history, nor revival of a superseded question. Four SDK prompt-contract cases cover: incremental-synchronization benefits + `For us.`, database-index mechanism + `For others.`, an unknown customer delivery commitment + `For us.`, and unknown personal history + `In your previous project.` These deterministic tests verify context ordering and policy transmission, not actual model answer quality. Routing and exact prefetch matching are unchanged; the three observed real comparison clips had zero prefetch hits, so no real prefetch latency benefit is claimed from them.

The `VoiceAssistant.Api` .NET `Meter` exposes these instruments. Durations are milliseconds and, per reply, are measured from that reply's own final STT callback unless stated otherwise:

| Instrument | Type | Extra tags | Meaning |
| --- | --- | --- | --- |
| `voiceassistant.stt_final_to_first_delta` | histogram | reply tags | Final STT **callback** to first successfully sent model response delta |
| `voiceassistant.stt_final_to_completed` | histogram | reply tags | Final STT callback to successfully sent model response completion |
| `voiceassistant.reply.settle_wait` | histogram | reply tags without `grounding` | Final to the reply starting: the settle window plus any wait behind an active reply (manual: until the request) |
| `voiceassistant.reply.retrieval_wait` | histogram | reply tags, `prefetched=true\|false` | Knowledge route only: how long the started reply still waited for its documents |
| `voiceassistant.reply.first_token` | histogram | reply tags | Answer request sent to the first model text (not from the final) |
| `voiceassistant.reply.final_to_primary_done` | histogram | reply tags | Final to the sent delta that carries `primaryDone` |
| `voiceassistant.reply.classifier_wait` | histogram | reply tags, `outcome=respond\|listen\|timeout\|failed` | How long the finished answer waited for the turn classifier before `response.completed` |
| `voiceassistant.reply.korean_ready` | histogram | reply tags, `line=primary\|alternative`, `outcome=translated\|failed` | Final to each sent `response.korean` |
| `voiceassistant.retrieval_duration` | histogram | `provider`, `outcome` | Retrieval call elapsed time, including disabled/no-match/error/cancellation paths |
| `voiceassistant.reply.prefetch` | counter | `result=hit\|miss` | Knowledge replies that reused a prefetched retrieval or started their own |
| `voiceassistant.reply.grounding` | counter | reply tags | Completed replies (deterministic ones included) by final grounding status |
| `voiceassistant.retrieval.failures` | counter | `class=transient\|permanent`, `kind`, `status_class=4xx\|5xx\|none\|...`, `retried=true\|false` | Retrievals that finally failed |
| `voiceassistant.chat.throttled` | counter | `operation=answer\|classifier\|translation` | HTTP 429 answers to live chat calls |
| `voiceassistant.chat.retries` | counter | `operation` | Live chat calls that were retried |
| `voiceassistant.session.errors` | counter | `code` | `error` events sent to clients, by error code |
| `voiceassistant.session.ended` | counter | `reason` | Ended sessions: `stopped`, `client_closed`, `superseded`, `overloaded`, `time_limit`, `aborted`, `ended`, `startup_timeout`, `transport_error`, `provider_unavailable` or the ending error code |
| `voiceassistant.speech.restarts` | counter | `code=connection_failure\|service_timeout\|service_unavailable`, `outcome=restarted\|failed` | In-process speech recognizer restart attempts after a retryable cancellation |
| `voiceassistant.warmup.failures` | counter | `stage`, `required=true\|false` | Failed startup warm-up attempts; `required=false` is the best-effort Search warm-up |
| `voiceassistant.reply.speculation` | counter | `provider`, `route`, `response_mode`, `outcome=started\|adopted\|discarded`, `reason=mismatch\|target_changed\|cancelled` (discarded only) | Speculative replies (`Session__SpeculativeStart`): started on a paused partial, adopted by their final, or discarded |
| `voiceassistant.reply.speculative_head_start` | histogram | `provider`, `route`, `response_mode` | Adopted speculative replies: their final's time minus the speculative start |
| `voiceassistant.sessions.active` | observable gauge | none | Open meeting sessions |

Reply tags are `provider=Azure|Fake|TestDouble`, `trigger=automatic|manual`, `route=knowledge|transcript|profile`, `response_mode=balanced|grounded|conversation` and `grounding=grounded|no_matches|unavailable|disabled|pending`.

These use monotonic timestamps. They are **not actual speech-end latency**: the API has no annotated client acoustic speech-end ground truth. Manual response metrics include the time between finalization and the manual request. A reply queued behind an active one is timed from its own final, so its wait in the queue is included and a later final that did not start a reply does not shorten it. Superseded/cancelled generation does not record subsequent delta/completion timings. A discarded speculative reply records no reply metric at all; an adopted one records them as a reply started at its final. Response histograms record `disabled`/`grounded`/`no_matches`/`unavailable` model paths; `no_matches` invokes the model using transcript-only context rather than returning a fixed clarification, and so does a failed search. The deterministic unavailable completion (used only when that model call also fails) and deterministic profile introductions remain excluded from the model timings; `reply.grounding` still counts them. Retrieval metrics still record all outcomes.

Every tag value comes from a fixed vocabulary; an unexpected value is reported as `other`. No content, user IDs, URLs or arbitrary error messages are metric tags. Never mix Fake/TestDouble observations with Azure measurements.

**Export.** Metrics are exported to Application Insights only when `APPLICATIONINSIGHTS_CONNECTION_STRING` is non-empty. The exporter (`Azure.Monitor.OpenTelemetry.Exporter`) uses a `MeterProvider` limited to this meter: no ASP.NET Core/HTTP request tracing, request telemetry or log export is enabled, because WebSocket URLs carry one-use tickets and console logs already reach Log Analytics. Ingestion is authorized with Entra because Application Insights local authentication is disabled: the user-assigned managed identity from `AZURE_CLIENT_ID` in Azure (when the managed identity endpoint is present), `DefaultAzureCredential` elsewhere. The service name (Application Insights cloud role) is `OTEL_SERVICE_NAME`, or `voice-web` when unset. Offline retry storage is disabled, so a failed export is dropped and nothing is written to disk. With the connection string empty or unset nothing is exported. The exporter parses the connection string when it starts; a value it cannot parse turns export off with one content-free warning (the exception type only), and the app keeps running. Statsbeat, the exporter's own usage telemetry to Microsoft-owned resources, is switched off (see [Data handling and limits](#data-handling-and-limits)).

Live recommendations and Korean enrichment share the chat deployment's quota.
Ten requests/minute is insufficient for continuous speech plus1-2 reply translations;
check the deployment's actual `rateLimits`, not only the HTTP endpoint limiter.
The deployed GlobalStandard `meeting-chat` allocation is100 requests/minute and100,000 tokens/minute,
within the existing subscription quota, with the same usage-based pricing (no provisioned-throughput SKU).
Translation-only enrichment uses a short dedicated translation prompt and a1024-token output budget,
not the4096-token pronunciation/feedback budget. Upstream429 is surfaced as `busy` for enrichment
and `model_busy` for meeting generation, with content-free failure diagnostics.

**Live chat retry policy.** The live reply calls (the answer stream, the turn classifier and Korean lines) use their own chat client with `LiveChatRetryPolicy` instead of the SDK default. The default waits out `Retry-After` intervals of many seconds up to three times while the overlay stays silent. The live policy works as follows:

- A throttled (429) or transiently failed (408, 5xx or network) call is retried **at most once**.
- The retry happens only when the service asks for a wait of at most 1 s (`retry-after-ms`, `x-ms-retry-after-ms` or `Retry-After`), or 250 ms without a hint.
- The call must also have at least 2 s of its deadline left after that wait. The answer and the classifier are bounded by the reply's own 30 s deadline, counted from the reply's start (`MeetingSession` passes it as an absolute timestamp where it creates the reply's cancellation), so time already spent on retrieval counts. The classifier also has its own 10 s budget. A Korean line has its own 10 s deadline.
- Otherwise the failure surfaces at once through the existing paths: `model_busy` (429) or `response_unavailable` for the reply, `response.korean` `failed:true` for a Korean line, and no emphasis for the classifier.

Every 429 is counted in `voiceassistant.chat.throttled`, and every retry in `voiceassistant.chat.retries`, both by `operation`. Practice, materials ingestion and other non-live calls keep the SDK default retries.

The [live-provider acceptance probe](../../tools/VoiceAssistant.LiveProbe/README.md) directly exercises this same provider with an explicit opt-in original synthetic WAV and standard noninteractive `DefaultAzureCredential`. It separately reports content-free real service evidence or **BLOCKED**, never fake Azure success.

The speech-stream contract separately exposes `CompleteInputAsync` for finite recordings: signal push-stream EOF and drain terminal recognition callbacks before disposal. The WebSocket session does not call it during live capture; silence and new utterances keep using the same continuous stream. Expected `EndOfStream`/`NoError` is normal only after explicit input completion; service errors remain explicit.

**Speech recovery.** A recognizer cancelled with `ConnectionFailure`, `ServiceTimeout` or `ServiceUnavailable` is replaced in-process instead of ending the session. `MeetingSession` handles it as follows:

- It disposes the failed recognizer and waits 0.5, 1, then 2 s on the session clock before each attempt.
- It starts a new recognizer with the same options. The event loop keeps draining audio meanwhile and drops it. The conversation history is kept.
- At most `MeetingSession.MaxSpeechRestarts` (3) attempts are made per session, and failed attempts count. After that, or for any other cancellation, the session ends with `speech_unavailable` as before.
- Errors of a recognizer that was already replaced are ignored, using a per-recognizer number. A failure that a new recognizer reports before the loop takes it over is held, then handled like any other.
- Each attempt is counted in `voiceassistant.speech.restarts`.

Token renewal (every 5 minutes) no longer ends the session on the first failure. While the current token is still valid, it retries every 30 s, but never past the expiry. Only a token that expired without a successful renewal ends the session with `speech_authentication_failed`.

### Speech endpoint verification

Keep `Azure__SpeechEndpoint` as the HTTPS resource endpoint from the portal, for example `https://<custom-subdomain>.cognitiveservices.azure.com/`; do not append a guessed recognition WebSocket path. [Speech SDK release notes](https://learn.microsoft.com/azure/ai-services/speech-service/releasenotes) document portal-endpoint `FromEndpoint` support beginning1.43, before this project's pinned1.48.2. [Private endpoint guidance](https://learn.microsoft.com/azure/ai-services/speech-service/speech-services-private-link#construct-endpoint-url) states the SDK chooses the service URL path. A native SDK regression test checks that the HTTPS root, en-US language and segmentation configuration are preserved; actual Azure endpoint/RBAC reachability still requires the live probe.

Speech1.48.2 fixes the [partitioned CRL compatibility defect](https://learn.microsoft.com/azure/ai-services/speech-service/migrate-to-sdk-1-48-2) affecting native Linux/Android clients before July2026 certificate changes. Because1.48.1+ disables CRL checking by default and1.47+ can tolerate CRL download failures, the API explicitly sets `OPENSSL_DISABLE_CRL_CHECK=false` and `OPENSSL_CONTINUE_ON_CRL_DOWNLOAD_FAILURE=false`. This preserves certificate revocation checking and fails closed if CRLs cannot be retrieved; production egress must permit required certificate/CRL endpoints. Do not disable these checks to hide a network or certificate error. Windows authentication failures are a separate issue and are not explained by the Linux CRL defect.
