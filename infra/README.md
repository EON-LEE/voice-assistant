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

### Readiness and optional dedicated-project bootstrap

`scripts\infra\Prepare-Azure.ps1` closes the resource-group/ACR prerequisite
without adopting unrelated resources. It is locked to subscription
`b0af194e-77a5-4471-bb43-67e78295b5c8`, tenant
`2573db8c-dfe5-4805-9e28-a0859692e705`. Default **Plan** is offline; **Check**
is bounded/read-only; **Apply** requires valid concrete configuration, a recent
documented positive cost estimate, explicit `-CostApproved`, and an existing
authorized identity. Never use synthetic fixture prices as a real cost review.

Copy `bootstrap.example.json` to ignored `bootstrap.local.json`, then supply a
dedicated resource group (for example `rg-voice-assistant-web`), approved region,
globally unique registry name and SKU. `estimatedMonthlyCostUsd` covers the
planned scope; `estimateDateUtc` must be UTC `yyyy-MM-dd` within 30 days and
`pricingReference` must identify the actual cost review. Set `costScope` to
`Bootstrap` for RG/ACR-only costs or `Application` for the full planned application
including the registry. Legacy configurations omitting this field are treated
as bootstrap-only. **Application checks/apply reject a bootstrap-only review**;
update the amount, date and reference together with `costScope: "Application"`.
This records the operator's reviewed scope; it does not independently verify the
price calculation or turn a monthly estimate into a maximum. These are review
evidence, not an automated price quote, cap or resource budget.

```powershell
# Offline: produces BLOCKED identity/availability, never fake deployment readiness.
.\scripts\infra\Prepare-Azure.ps1 -ConfigFile .\infra\bootstrap.local.json `
  -ReportPath C:\reports\bootstrap-plan.json -Plan

# Existing Az.Accounts identity, explicitly scoped; no interactive login.
.\scripts\infra\Prepare-Azure.ps1 -ConfigFile .\infra\bootstrap.local.json `
  -ReportPath C:\reports\bootstrap-check.json -AuthProvider AzPowerShell -Check

# Creates only absent project RG + ACR; does not build images, create app registrations,
# change another project's resources, register providers, or grant roles.
.\scripts\infra\Prepare-Azure.ps1 -ConfigFile .\infra\bootstrap.local.json `
  -ReportPath C:\reports\bootstrap-apply.json -AuthProvider AzPowerShell -Apply -CostApproved
```

The report directory must exist. The report is initialized before any mutation
so a failed/interrupted run does not leave a previous success report in place.
Groups/registries are tagged `managedBy=voice-assistant-bootstrap` and
`projectId=ff8f97b4-5db4-458e-99de-0d63cfc96a4a`. Repeated runs reuse matching
resources unchanged; unowned/untagged resources, location/SKU mismatches,
admin-enabled registries and incompatible ABAC mode fail instead of being
retagged or altered. Serialize bootstrap ownership with other operators; a
concurrent creator can invalidate any read-before-create preview. No automatic
cleanup/rollback/deletion occurs after partial failure.

`-AuthProvider Auto` (default) checks the configured Azure CLI account metadata,
installed azd check-status, and normal Az.Accounts context/token/ARM access.
`AzPowerShell` uses the matching existing **AzureCloud** context explicitly via
`-DefaultProfile` without changing the global context. Successful token
acquisition alone is not enough: the exact subscription must be readable and
enabled in the expected tenant. Tokens never enter reports, files or environment
variables. No login, MFA bypass, credential enrollment or token-cache inspection
is attempted. azd identity is discovery-only; provisioning uses Azure CLI or
Az.Accounts. `AzureCli` explicitly selects CLI only; an absent CLI account does
not block Auto when an existing Az.Accounts identity is verified.

Portable CLI installations are supported by **Prepare**, **Deploy**, and
**Import-Knowledge** without PATH/global configuration changes:

```powershell
$portable = @{
  AzPath = 'C:\approved-tools\azure-cli\Scripts\python.exe'
  AzPrefix = @('-m', 'azure.cli')
}
.\scripts\infra\Prepare-Azure.ps1 @portable -ReportPath C:\reports\readiness.json -Check
```

All CLI launches are noninteractive, bounded, argument-array-safe and suppress
raw stdout/stderr on error. Timeouts kill only the launched PID tree; a timed-out
Azure mutation may still complete server-side, so its outcome is **Unknown**
and must be read back before retry. Automatic CLI extension installation is
disabled per process. No credentials are bridged from Az.Accounts into CLI.
ACR name lookup allows 90 seconds for context/token initialization and ARM
response. `REGISTRY_NAME_CHECK_TIMEOUT` means availability is **unknown**, not
that the name is occupied; do not pick a different registry to conceal a timeout.
Knowledge ingestion still uses its documented Azure CLI operator path; the
Az.Accounts adapter in this follow-up covers bootstrap/application deployment.

Once an image has been built/pushed and complete main parameters are prepared:

```powershell
.\scripts\infra\Prepare-Azure.ps1 -Stage Application -ConfigFile .\infra\bootstrap.local.json `
  -ParametersFile .\infra\parameters.local.json -BicepPath C:\tools\bicep.exe `
  -ReportPath C:\reports\application-check.json -AuthProvider AzPowerShell -Check

# Only after the above and the deployment/role/cost review:
.\scripts\infra\Prepare-Azure.ps1 -Stage Application -ConfigFile .\infra\bootstrap.local.json `
  -ParametersFile .\infra\parameters.local.json -BicepPath C:\tools\bicep.exe `
  -ReportPath C:\reports\application-apply.json -AuthProvider AzPowerShell -Apply -CostApproved
```

Application preparation binds tenant/registry/resource group to the bootstrap
configuration, checks registered providers and the exact existing image digest,
then runs ARM validation and what-if. The Az.Accounts adapter uses resource-scoped
ARM requests, bounded asynchronous polling, and transient in-memory ACR OAuth
exchange for manifest read (no registry credentials saved). It does not register
Entra applications, build/push an image, approve quota, or automatically consent.
The lower-level `Deploy.ps1` also supports `-AuthProvider AzPowerShell`.

Reports have `schemaVersion: 1`, `mode`, `stage`, `target`,
`selectedAuthProvider`, `overallStatus` (`PASS`, `FAIL`, `BLOCKED`), `execution`
(`NotRequested`, `Blocked`, `Succeeded`, `Unknown`), `checks` containing only
`id/category/status/code`, `identityAlternatives` and `plannedActions`.
`liveApplicationVerified` is always **false**: stage-readiness or successful ARM
deployment is not a browser/audio/data-plane test. Exit codes are **0** for a
passed requested stage, **1** for failed configuration/operation, and **2** for
blocked prerequisites. Input, identity, and cloud availability are separate
categories. Reports contain no raw error bodies, parameter values, user account
names, request URLs with tokens, or credentials.

### Main application prerequisites

1. Sign in separately with Azure CLI. Target subscription:
   `ME-M365CPI74210306-eonlee-1`
   (`b0af194e-77a5-4471-bb43-67e78295b5c8`). Scripts pass `--subscription`
   explicitly, check enabled state/tenant, and never call `az account set` or
   change workstation defaults. Alternatively use the verified existing
   Az.Accounts path above; no new login is needed if that identity is usable.
   Resource group/registry creation is optional explicit bootstrap Apply only;
   provider registration, role application or upload is never implicit.
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
compiled without diagnostics. The original **51 offline checks**, plus **53
readiness/bootstrap/adapter checks**, passed on Windows PowerShell 5.1, including
the integrated backend Search contract:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\infra\tests\Test-Offline.ps1 `
  -BicepPath C:\tools\bicep.exe -BackendSchemaPath .\contracts\search-index.json
```

`Test-Offline.ps1` invokes `Test-Readiness.ps1` automatically; the latter can also
run independently. Its authentication/resource requests use isolated fixtures,
including a synthetic Az.Accounts module; it never provisions cloud resources.
The test suite requires no Azure account or real uploads. It checks compiled
security/hosting settings, parameter rejection, mocked validation/what-if/apply
subscription gates, offline defaults, ACLs, chunking, Unicode, index/vector
compatibility, mocked embeddings/uploads, stale versions, deletions, lease
exclusion and partial-failure recovery. Omitting `BackendSchemaPath` skips the
one cross-component schema comparison.

**Follow-up read-only Azure evidence:** an existing Az.Accounts identity acquired
an ARM token and read the approved enabled subscription/tenant successfully.
The new adapter's `account show` and dedicated group-existence requests were
also verified read-only. This proves only ARM access, not deployment privileges,
data-plane access, quota or application readiness.

**Not yet verified by this slice in Azure:** deployment policy/provider checks,
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
