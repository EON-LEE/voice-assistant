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
| `Azure__EmbeddingDeployment` | Required with Search; `text-embedding-3-small`, 1536 dimensions |
| `ASPNETCORE_HTTP_PORTS` | `8080` in container |

Missing mandatory Azure configuration prevents startup. Search is intentionally optional: no endpoint/index means `disabled`, never a pretend grounded response. Partial Search configuration prevents startup. No API keys/secrets are accepted. `DefaultAzureCredential` uses managed identity in Azure and developer credentials locally; do not put client secrets in source or environment configuration examples.

Chat requests explicitly use `max_completion_tokens`, not legacy `max_tokens`, and omit custom temperature. The pinned Azure.AI.OpenAI2.1 requires its documented `SetNewMaxCompletionTokensPropertyEnabled` extension to prevent rewriting the token property. Options use the public SDK model reader to initialize the property bag required by that extension; an offline transport test verifies the actual request JSON. A bounded live GPT-5 diagnostic reproduced HTTP400 `unsupported_parameter` for `max_tokens` before this correction and streamed output afterward.

Assign the runtime identity **Cognitive Services Speech User**, **Cognitive Services OpenAI User**, and (when enabled) **Search Index Data Reader**, scoped to the respective resources. Configure a Speech custom subdomain for Entra authentication. The API refreshes Speech authorization every five minutes. Ingestion uses a separate operator identity with write permissions and must enforce the original document permissions. The backend has no Blob permissions or content ingestion endpoint.

Entra API registration must issue v2 tokens and expose delegated `Meeting.Access`; SPA registration uses code+PKCE and redirect URI equal to the browser origin. JWT issuer, signing key, expiry, API audience, delegated scope and GUID `oid` are validated before issuing tickets. TLS terminates at trusted Azure ingress. Origin validation uses the configured allowlist, not forwarded-host headers. **Keep one replica/process** until ticket storage is distributed or reliable ticket-to-WebSocket affinity is implemented.

`GET /health/live` indicates the process is running. `GET /health/ready` indicates validated configuration and a running HTTP pipeline, not external Azure dependency availability; neither endpoint performs billable calls or exposes credentials. Operational monitoring must separately check resource/RBAC availability.

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

Audio exists only in bounded transient buffers and the live Speech SDK push stream; transcript/answer context is memory-only and cleared when the session ends. No application transcript/audio/query/body/credential logging or persistence is configured. Azure services still process submitted content under their service data policies: configure retention, network access and diagnostics to organizational requirements. Disable full request URL/query capture in Azure ingress, access logs, telemetry and reverse proxies because the upgrade URL carries a short-lived ticket.

Errors are explicit safe messages; raw SDK exception details are suppressed. Startup, idle, response, write and session deadlines bound resource usage; 100 total sessions and one per identity, bounded queues, message/audio rates and bounded history prevent unbounded accumulation. A slow peer or overloaded recognizer is disconnected rather than silently dropping transcript/audio. New turns and manual cancellation invalidate stale generation before sending subsequent output.

SDK references: [Speech Entra auth](https://learn.microsoft.com/azure/ai-services/speech-service/how-to-configure-azure-ad-auth), [Azure OpenAI .NET streaming](https://learn.microsoft.com/dotnet/api/overview/azure/ai.openai-readme), [Search vector quickstart](https://learn.microsoft.com/azure/search/search-get-started-vector).

## Content-free timing metrics

The `VoiceAssistant.Api` .NET `Meter` exposes histograms in milliseconds:

| Instrument | Meaning |
| --- | --- |
| `voiceassistant.stt_final_to_first_delta` | Final STT **callback** to first successfully sent response delta |
| `voiceassistant.stt_final_to_completed` | Final STT callback to successfully sent model response completion |
| `voiceassistant.retrieval_duration` | Retrieval call elapsed time, including disabled/no-match/error/cancellation paths |

These use monotonic timestamps. They are **not actual speech-end latency**: the API has no annotated client acoustic speech-end ground truth. Manual response metrics include the time between finalization and the manual request. Superseded/cancelled generation does not record subsequent delta/completion timings. Grounding-unavailable deterministic refusal is not recorded as a successful model completion.

Tags are bounded `provider=Azure|Fake|TestDouble`; response `trigger=automatic|manual`; retrieval `outcome=grounded|disabled|no_matches|unavailable|unknown|failed|cancelled`. No content, IDs, URLs or arbitrary error messages are metric tags. No exporter, persistence, or new WebSocket event is enabled; an approved operational `MeterListener`/OpenTelemetry configuration may subscribe. Never mix Fake/TestDouble observations with Azure measurements.

The [live-provider acceptance probe](../../tools/VoiceAssistant.LiveProbe/README.md) directly exercises this same provider with an explicit opt-in original synthetic WAV and standard noninteractive `DefaultAzureCredential`. It separately reports content-free real service evidence or **BLOCKED**, never fake Azure success.

### Speech endpoint verification

Keep `Azure__SpeechEndpoint` as the HTTPS resource endpoint from the portal, for example `https://<custom-subdomain>.cognitiveservices.azure.com/`; do not append a guessed recognition WebSocket path. [Speech SDK release notes](https://learn.microsoft.com/azure/ai-services/speech-service/releasenotes) document portal-endpoint `FromEndpoint` support beginning1.43, before this project's pinned1.46. [Private endpoint guidance](https://learn.microsoft.com/azure/ai-services/speech-service/speech-services-private-link#construct-endpoint-url) states the SDK chooses the service URL path. A native SDK regression test checks that the HTTPS root, en-US language and segmentation configuration are preserved; actual Azure endpoint/RBAC reachability still requires the live probe.
