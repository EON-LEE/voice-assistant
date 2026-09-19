# Azure browser hosting and approved knowledge ingestion

This slice deploys an **installation-free browser app**, not the legacy desktop
client. One Azure Container App serves the built web frontend from the .NET 8
API's `wwwroot`, with same-origin HTTPS API/WebSocket traffic on container port
8080. No separate CDN, Teams bot, Graph connector, WorkIQ integration, automatic
document crawler, or production fake-authentication mode is provisioned.

## What is provisioned

`main.bicep` targets an **existing resource group**. It creates a Consumption
Container Apps environment/app, user-assigned runtime identity, Speech account
with a custom subdomain, Azure OpenAI account with chat and embedding deployments,
AI Search, an anonymous-access-disabled Blob documents container, and a
metric-only no-replica alert. The Azure OpenAI resource exposes the standard
Azure OpenAI endpoint usable with Foundry/Azure SDKs; this does not create a
Foundry project or deploy arbitrary model-catalog endpoints.

The image is a **required, already-pushed ACR digest**, never a fabricated demo
image. The existing ACR must be in this subscription, use ordinary registry RBAC
(not ABAC repository permissions), and permit managed-identity image pulls.
The nested registry module assigns the runtime identity `AcrPull` at that registry.
The deploying identity needs deployment/resource permissions in the target
resource group and role-assignment permissions at the relevant scopes, including
the existing registry. Provider registrations must already be approved.

| Identity | Role | Scope |
| --- | --- | --- |
| Runtime managed identity | Cognitive Services Speech User | Speech account |
| Runtime managed identity | Cognitive Services OpenAI User | OpenAI account |
| Runtime managed identity | Search Index Data Reader | Search service |
| Runtime managed identity | AcrPull | Existing registry |
| Optional ingestion operator | Search Service Contributor and Search Index Data Contributor | Search service |
| Optional ingestion operator | Cognitive Services OpenAI User | OpenAI account |
| Optional ingestion operator | Storage Blob Data Contributor | Documents container only |

The API has **no Blob access and no knowledge-write endpoint**. Set the optional
`ingestionPrincipalId` to the separately approved operator's Entra **object ID**,
not an application client ID; its type defaults to `User`. Empty means no
operator permissions are created. Role assignments are only made by an explicit
deployment `-Apply`, never by validation or the ingestion script. Allow time for
RBAC propagation. The operator also needs control-plane read access to the three
services for the ingestion preflight (for example existing resource-group Reader);
the ingestion data roles do not grant arbitrary subscription access.

Services have public network endpoints with Entra authentication; keys/local
authentication are disabled. **Private Blob here means no public/anonymous data
access, not Private Link/network isolation.** If company policy requires private
endpoints/VNet egress, do not deploy this baseline unchanged. Source blobs are
operator-only archives, not browser-readable citation links.

## Prerequisites and deployment

1. Sign in separately with Azure CLI. Target subscription:
   `ME-M365CPI74210306-eonlee-1`
   (`b0af194e-77a5-4471-bb43-67e78295b5c8`). Scripts pass `--subscription`
   explicitly, check enabled state/tenant, and never call `az account set` or
   change workstation defaults. No sign-in, resource group creation, provider
   registration, registry creation, role application or upload is automatic.
2. Create/approve two single-tenant Entra registrations: API and public SPA.
   The API must issue **v2 access tokens** and expose delegated `Meeting.Access`.
   Grant the SPA that API delegated permission and complete the required consent.
   Use authorization code + PKCE, **no client secret**, with the SPA redirect URI
   equal to the final browser HTTPS origin (`location.origin`). Do not enable
   implicit flow as a workaround. Entra registration/consent is outside this IaC.
3. Build the integrated repository's API Dockerfile from repository root and push
   to the approved existing ACR using your approved image-publishing procedure:
   `docker build -f .\src\VoiceAssistant.Api\Dockerfile -t <registry>.azurecr.io/voice-assistant:<version> .`
   That Dockerfile builds `src/VoiceAssistant.Web`, publishes .NET 8 and copies
   the frontend to `wwwroot`. Resolve the pushed image digest and supply
   `<registry>.azurecr.io/voice-assistant@sha256:<64-hex-digest>`.
   Build/push tools are operator prerequisites, **not end-user installations**.
4. Acquire an official [Bicep CLI release](https://github.com/Azure/bicep/releases),
   verify its release-asset SHA256, and keep it in an isolated tools directory.
   Windows PowerShell 5.1 is sufficient for these scripts. If local execution
   policy requires it, use a process-only invocation such as
   `powershell.exe -NoProfile -ExecutionPolicy Bypass -File ...`; do not change
   machine/user policy. No PowerShell modules or npm dependencies are required.
5. Copy `parameters.example.json` to `parameters.local.json` (ignored by Git).
   Replace every `REPLACE_` value. Choose actual available chat model/version,
   deployment SKU/capacity and service regions after checking subscription
   quota, policy, residency, retirement and regional availability. The backend
   requires streaming chat supporting temperature and output-token limits.
   Embeddings are currently **text-embedding-3-small with 1536 dimensions**;
   model version/SKU/capacity remain explicit inputs. Changing dimension/model
   compatibility requires a coordinated backend + index migration.

`tenantId`, `apiAudience`, `spaClientId`, and `apiScope` are secure ARM parameters.
They are public identity metadata, **not secrets**: API audience is the API
application/client GUID in a v2 token's `aud`; SPA client GUID is different;
scope is the complete exposed `api://.../Meeting.Access` URI.
`GET /api/client-config` returns only `{clientId, authority, scope}`, with a full
tenant authority. No Azure keys, connection strings or bearer tokens are exposed.
`AZURE_CLIENT_ID` selects the assigned runtime identity for `DefaultAzureCredential`.
Speech region, custom endpoint and full `SpeechResourceId` are wired separately.

From repository root, edit the paths/group before executing:

```powershell
$bicep = 'C:\tools\bicep.exe'
$parameters = '.\infra\parameters.local.json'
$subscription = 'b0af194e-77a5-4471-bb43-67e78295b5c8'
$group = '<existing-resource-group>'

# Offline compile + fail-fast parameter checks only (the default).
.\scripts\infra\Deploy.ps1 -ParametersFile $parameters -BicepPath $bicep

# Explicit online ARM checks, no deployment.
.\scripts\infra\Deploy.ps1 -ParametersFile $parameters -BicepPath $bicep -SubscriptionId $subscription -ResourceGroup $group -Validate
.\scripts\infra\Deploy.ps1 -ParametersFile $parameters -BicepPath $bicep -SubscriptionId $subscription -ResourceGroup $group -WhatIf

# Only after approval of resource cost, role grants and the reviewed change.
.\scripts\infra\Deploy.ps1 -ParametersFile $parameters -BicepPath $bicep -SubscriptionId $subscription -ResourceGroup $group -Apply
```

What-if output is deliberately limited to resource IDs/change types; full diffs
can contain environment values. Apply repeats validation before deployment.
ARM validation cannot prove quota, image pull, consent or data-plane connectivity.
The deterministic default-domain browser origin is computed from app name and
environment domain and configured as the backend's exact allowed origin; update
Entra's SPA redirect after deployment. Custom domains are not configured.

### One replica is intentional, not high availability

The API issues single-use, 30-second in-memory WebSocket tickets after bearer
authentication. `minReplicas = maxReplicas = 1` and a single active revision are
deliberate. **Rolling revisions can still overlap processes and lose ticket
affinity**; replacement/restart also loses all tickets and active sessions.
Drain users for updates and have clients obtain a new ticket/reconnect. Do not
scale out or claim seamless rollouts until distributed one-use ticket storage or
verified ticket-to-WebSocket affinity exists. Search also starts at one replica/
partition; this baseline is not an HA/SLA design.

## Approved local text/markdown ingestion

The default is entirely offline: no token acquisition, HTTP, embeddings, index
creation, uploads or deletion. Only explicitly listed UTF-8 `.txt`/`.md` files
inside `ContentRoot` are accepted (1 MiB per file, 100 operations per manifest);
traversal, symlinks, empty text and public/wildcard/empty ACLs fail.

Use `scripts\infra\tests\fixtures\manifest.json` as the shape reference, **not as
production data**. Each upsert supplies `documentId`, `operation: "upsert"`,
relative `path`, `title`, original HTTPS `url`, RFC3339 `updatedAt`, and
`allowedPrincipalIds`. The operator must verify export approval, source URL and
current document permissions in the named tenant. IDs must be **user object IDs**,
not client IDs or group IDs: the current backend filters the caller's `oid`
and does not resolve group membership. The tool cannot verify real document
ownership/permissions from a local file and does not claim to sync them.

```powershell
.\scripts\infra\Import-Knowledge.ps1 -ManifestPath C:\approved\manifest.json -ContentRoot C:\approved

# Only during an approved maintenance window with the app stopped/drained.
.\scripts\infra\Import-Knowledge.ps1 -ManifestPath C:\approved\manifest.json -ContentRoot C:\approved `
  -SubscriptionId b0af194e-77a5-4471-bb43-67e78295b5c8 -TenantId '<tenant-guid>' `
  -ResourceGroup '<existing-resource-group>' -SearchName '<deployed-search-name>' `
  -OpenAIName '<deployed-openai-name>' -StorageAccountName '<deployed-storage-name>' `
  -EmbeddingDeployment meeting-embedding -CreateIndex -ConfirmExclusiveMaintenance -Apply
```

`-CreateIndex` is for first creation only; it uses `If-None-Match: *` and never
silently replaces an existing index. Omit it on subsequent runs. Existing indexes
must match `infra/knowledge/index.json`: it preserves every backend field and
adds filterable `documentId` and retrievable `sourceHash` for lifecycle management.
An incompatible/legacy index requires a separately approved new index and app
configuration migration, not an in-place destructive schema overwrite.

Chunks are deterministic, at most 1500 UTF-16 code units with 150 overlap (Unicode
surrogate pairs are preserved). Each carries title, original URL, update time,
explicit normalized ACL and a 1536-element embedding. The original normalized
text is stored privately under `documents/<index>/<documentId>.txt`.
All service access uses the signed-in operator's Entra tokens in memory; no
keys/SAS, credential files or request/response bodies are printed.

### Permission changes, deletion and recovery

Treat this index as owned exclusively by this ingestion tool. Stop/drain all app
revisions and clear active sessions before any update, especially permission
revocation: session context may already contain retrieved evidence. The
`-ConfirmExclusiveMaintenance` switch is an **operator acknowledgment**, not an
automatic stop or proof of an outage. No script changes app revisions.

For changed content, title, URL, **ACL**, or deletion, advance `updatedAt`
strictly. Equal timestamps are accepted only for the exact same operation/hash.
Old chunks are deleted and removal observed before replacement embeddings are
published, so shortened files cannot leave orphaned chunks with obsolete ACLs.
For removal, submit a manifest document containing only:

```json
{
  "documentId": "approved-document",
  "operation": "delete",
  "updatedAt": "2026-09-19T10:00:00Z"
}
```

Deletion removes all that document's vectors and source blob. Omitting a document
from a manifest does **not** delete it. An index-specific Blob version/tombstone
ledger prevents old manifests resurrecting deleted sources; do not delete that
ledger to work around version conflicts. A renewable 60-second Blob lease
serializes writers. Bounded HTTP requests and lease-deadline checks fail closed
if authentication/requests outlive the lease safety window.

There is no cross-service transaction: a failed run can leave missing/partial
new chunks and a `pending` ledger record. Keep readers stopped; retry the exact
approved manifest without `-CreateIndex`, resolve any reported error, and confirm
Search visibility/ACL isolation with authorized and unauthorized test identities
before restarting the app. Search writes and permission propagation are
eventually consistent; do not promise immediate revocation from a successful
upload response alone. Blob versioning/soft delete are disabled in this baseline
to avoid hidden retained source versions; service-side deletion policies still
apply. No source contents, transcripts, audio or generated answers are kept in
the version ledger.

## Content-safe operations, costs and readiness

Container environment log collection is `none`; there is no Application
Insights request/body capture or diagnostic log export. ASP.NET request logs are
suppressed. Do not later enable full URL/query logging at ingress, proxies or
telemetry: WebSocket upgrade URLs carry one-use tickets. The metric alert checks
`Replicas < 1`; an optional pre-existing `alertActionGroupId` routes notifications,
otherwise alerts are portal-only. Missing metric series are not a reliable
outage signal. `/health/live` and `/health/ready` are process/configuration checks,
not billable Speech/OpenAI/Search dependency probes. Verify those dependencies
separately with approved synthetic data and inspect platform metrics. Provider
content-processing/abuse-monitoring policies still apply; this template does not
override service retention policy or disable model content filtering.

Cost drivers are the always-on Container App (0.5 vCPU/1 GiB), Search
replicas/partitions, Speech audio duration, chat input/output tokens, ingestion/
retrieval embeddings, Blob capacity/operations, the existing registry and Azure
Monitor alert evaluations/notifications. Region, model SKU and quota allocation
change prices; no cost estimate or availability guarantee is embedded. Configure
approved subscription/resource-group **budget alerts** and owner notifications
before apply. **A budget alert is not a spending cap and does not stop services.**
Apply, reindexing and live tests can incur charges; deployments are not free
because a script's default is dry-run.

Local evidence: Bicep **v0.47.16**, official `bicep-win-x64.exe` SHA256
`3f343ab1ce41feac156464adee3dc499cb6c197366fc731aed276192011d867c`,
compiled without diagnostics. **51 offline checks passed** on Windows PowerShell
5.1, including the integrated backend Search contract:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\infra\tests\Test-Offline.ps1 `
  -BicepPath C:\tools\bicep.exe -BackendSchemaPath .\contracts\search-index.json
```

The test suite requires no Azure account or real uploads. It checks compiled
security/hosting settings, parameter rejection, mocked validation/what-if/apply
subscription gates, offline defaults, ACLs, chunking, Unicode, index/vector
compatibility, mocked embeddings/uploads, stale versions, deletions, lease
exclusion and partial-failure recovery. Omitting `BackendSchemaPath` skips the
one cross-component schema comparison.

**Not yet verified in Azure:** authenticated subscription/policy/provider checks,
live ARM validate/what-if, regional model capacity/quota, container build/push/
pull, RBAC propagation, Entra registration/consent/redirect, live Speech and model
calls, real index ingestion and ACL isolation, and browser microphone/tab-audio
capture over the deployed HTTPS origin. No resources, roles or documents were
applied during this slice. Compilation and mocked tests are preparation evidence,
not an Azure deployment or an end-to-end production-readiness claim.

References: [Container Apps Bicep schema](https://learn.microsoft.com/azure/templates/microsoft.app/2024-03-01/containerapps),
[model deployment schema](https://learn.microsoft.com/azure/templates/microsoft.cognitiveservices/2024-10-01/accounts/deployments),
[Speech Entra authentication](https://learn.microsoft.com/azure/ai-services/speech-service/how-to-configure-azure-ad-auth),
[AI RBAC roles](https://learn.microsoft.com/azure/role-based-access-control/built-in-roles/ai-machine-learning),
[Container Apps metrics](https://learn.microsoft.com/azure/azure-monitor/reference/supported-metrics/microsoft-app-containerapps-metrics).
