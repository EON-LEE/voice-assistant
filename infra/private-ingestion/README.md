# One-shot private synthetic knowledge ingestion

This optional deployment reaches the project's **public-network-disabled Blob
account without weakening its firewall or the web runtime's identity**.
The target subscription's observed management-group assignment
`MCAPSGovDeployPolicies`, definition `StorageAccount_PublicNetwork_Modify`, modifies
Storage public-network access. Do not re-enable public access, add public IP
exceptions, use account keys/SAS, or expand web runtime Blob roles to work around it.

## Scope and topology

`main.bicep` creates a dedicated VNet with a delegated Container Apps infrastructure
subnet and separate Blob private-endpoint subnet; a Blob-only private endpoint
with an explicit static private IP; linked `privatelink.blob.core.windows.net`
private DNS; an internal Consumption-profile Container Apps environment; a
separate user-assigned identity; and one **manual** ingestion job. It never
starts the job on deployment, schedules it, updates the existing web app or
storage resource, or deletes resources. No VM, NAT gateway, user credentials,
corporate-data crawler, or public inbound endpoint is introduced.

The separate identity receives only existing-resource roles: AcrPull on the
registry, OpenAI User on the model account, Search Service Contributor + Index
Data Contributor on Search, and Blob Data Contributor on **documents container**
only. The existing web runtime retains no Blob roles. Search/OpenAI stay on their
already-approved HTTPS endpoints; only Blob traffic uses Private Link.

One execution has parallelism/completion count 1, **zero automatic retries** and
an 1800-second deadline. The image uses the official PowerShell 7.4 Debian image
as an unprivileged UID, with no Azure CLI/Az SDK/user-token cache inside. It gets
service tokens from the platform's local managed-identity endpoint, selecting
the separate assigned identity. Tokens/platform identity headers are transient
memory only and never sent to another service or printed.

The job runs `Private-Ingestion.ps1`, reusing `KnowledgeSync.psm1` for schema
validation, embeddings, index documents, private source writes, lease exclusion
and version/tombstone behavior. It first verifies that the public Blob hostname
resolves **exclusively to the configured RFC1918 private IP**. An unexpected
public/mixed DNS answer fails before data writes. A lease-expired request fails
instead of weakening concurrency control.

## Explicitly approved input

This image contains only `approved-synthetic.md`, an original, visibly fictional
Lighthouse practice-meeting example. It is intentionally separate from offline
fixtures that must not be uploaded. It never mounts or scans local directories,
downloads documents, or accepts arbitrary text/paths from job environment values.
It ingests one stable `documentId: private-ingestion-synthetic` with the explicitly
provided user object GUID as its only ACL, a supplied RFC3339 version, and an
`example.invalid` synthetic citation (not a claim that a public source exists).
Do not substitute company data without a separate approval/design review.

## Build and offline validation

From repository root:

```powershell
$docker = 'C:\Program Files\Docker\Docker\resources\bin\docker.exe'
& $docker build -f .\infra\private-ingestion\Dockerfile -t voice-private-ingestion:approved .
& $docker run --rm --network none `
  -e INGEST_ALLOWED_PRINCIPAL_ID=11111111-1111-4111-8111-111111111111 `
  -e INGEST_DOCUMENT_UPDATED_AT=2026-09-20T00:00:00Z `
  -e INGEST_INDEX_NAME=meeting-knowledge `
  voice-private-ingestion:approved -ValidateOnly
```

The expected result is `PASS`, `scope: offline-synthetic-plan`, `uploaded: false`.
This verifies actual nonroot Linux execution with networking disabled, not cloud
access. No dependency installation is required inside the image. Push the
approved image to the project's existing registry via the approved identity path
and resolve its **actual repository digest**. A local Docker image ID is not the
registry manifest digest; do not substitute it.

## Prepare and deploy, without running ingestion

Copy `parameters.example.json` to an ignored `parameters.local.json` and replace
every placeholder with existing resource names, the approved image digest,
consumer **user object ID**, and document version. Defaults use VNet
`10.246.0.0/23`, infrastructure `10.246.0.0/24`, endpoints `10.246.1.0/27`, and
Blob IP `10.246.1.4`. Review these for overlap/policy before deployment. If changing
them, keep the endpoint IP inside its endpoint subnet. All existing resources
must be in the selected project resource group/subscription.

Leave `createIndex: false` if the complete compatible index already exists. Set
it true only for first creation: a pre-existing index is never silently replaced.
The schema must include `documentId` and `sourceHash` in addition to the backend's
required fields. Leave `confirmExclusiveMaintenance: false` until the parent
operator has actually stopped/drained all application readers; changing this
parameter acknowledges that state but does not perform a stop.

Prerequisites: approved `Microsoft.Network` and `Microsoft.App` providers, VNet/
private endpoint/DNS and role-assignment permissions, an approved endpoint
connection, image pull access, and available regional Container Apps capacity.
Review the additional private endpoint + private DNS standing charges, potential
platform network charges and bounded job CPU/memory/embedding/storage operations.
Coordinator planning estimates of roughly $7.30/month per endpoint and $0.50/month
per DNS zone are **not verified current quotes or spending caps**; include actual
region/service prices in the application-scope cost review before apply.

Compile with verified Bicep, then use the existing Az adapter for **validate and
what-if before explicit create**. The main application's `Deploy.ps1` is not used
here because it intentionally fixes its own main-template/parameter contract:

```powershell
$sub = 'b0af194e-77a5-4471-bb43-67e78295b5c8'
$group = 'rg-voice-assistant-web'
$bicep = 'C:\tools\bicep.exe'
$compiled = 'C:\approved-artifacts\private-ingestion.json'
$parameters = 'C:\approved-artifacts\private-ingestion.parameters.json'
& $bicep build .\infra\private-ingestion\main.bicep --outfile $compiled
if ($LASTEXITCODE -ne 0) { throw 'Bicep compilation failed.' }
Import-Module .\scripts\infra\Common.psm1 -Force
Set-AzureCli -AuthProvider AzPowerShell -TimeoutSeconds 900
$args = @('--subscription', $sub, '--resource-group', $group, '--name', 'voice-private-ingestion',
  '--template-file', $compiled, '--parameters', ('@' + $parameters), '--mode', 'Incremental')
$null = Invoke-AzJson (@('deployment', 'group', 'validate') + $args)
$preview = Invoke-AzJson (@('deployment', 'group', 'what-if') + $args)
$preview.changes | Select-Object changeType, resourceId

# Only after approval of the network, identity grants, cost scope and preview:
$result = Invoke-AzJson (@('deployment', 'group', 'create') + $args)
if ($result.properties.provisioningState -ne 'Succeeded') { throw 'Private route did not finish successfully.' }
```

No command above starts ingestion. Verify the private endpoint connection is
Approved, DNS zone group/link are provisioned, and scoped RBAC has propagated.
Keep the app stopped during ingestion and do not weaken controls if deployment
or DNS is blocked by policy. Use an approved pre-existing private runner/network
instead if this subscription cannot provision the required network resources.

### Optional private-job diagnostics

`enableDiagnostics` defaults to **false**. Explicitly setting it true provisions
a dedicated `${namePrefix}-diagnostics` Log Analytics workspace with 30-day
retention and Entra-only access, changes only this ingestion environment's
destination to Azure Monitor, and attaches `ContainerAppConsoleLogs` and
`ContainerAppSystemLogs` diagnostic categories using its workspace resource ID.
No shared key is requested; no web environment, request/HTTP category, audio,
ticket or general-purpose application logging is enabled. Include workspace
ingestion/retention costs and `Microsoft.OperationalInsights` permissions in the
review. This opt-in is appropriate only for the fixed synthetic-only image,
whose entrypoint emits sanitized status/phase JSON and suppresses raw HTTP,
source content and credential diagnostics. Do not reuse this logging setting
for a general corporate-document ingestion image without another review.

Terminal replicas can be cleaned up before live console logs are collected.
The official CLI job-replica/log-auth API uses `2023-11-02-preview`; requesting
replicas with `2024-03-01` can return Unsupported API version. After deletion,
console streaming returns no replicas/404, so use opt-in retained diagnostics
for an explicitly approved retry rather than repeatedly starting jobs blind.
Environment system events are available through its documented event stream;
decode a byte-array response as UTF-8 before processing JSON lines and inspect
only the named job/execution. Never print stream credentials or signed URLs.

The first observed execution pulled/started its image, then exited immediately:
ARM `string(bool)` had emitted `"True"` while the fail-closed entrypoint requires
`"true"`. The template now emits explicit lowercase ternary literals for both
intent flags. Actual failed-execution environment metadata and local
network-disabled reproduction confirmed this cause; it was not evidence of a
DNS, NAT, image-pull or managed-identity networking failure.

## Run once and verify

The parent operator owns cloud mutation and uses its existing, explicitly
selected `$ctx` (matching the target subscription/tenant). Start **one** execution
after approved maintenance is in effect:

```powershell
$jobPath = "/subscriptions/$sub/resourceGroups/$group/providers/Microsoft.App/jobs/voice-ingest-job"
$start = Invoke-AzRestMethod -Method POST -Path "$jobPath/start?api-version=2024-03-01" -DefaultProfile $ctx
# Inspect only execution name/status, not job environment/token contents.
```

Record the returned execution name and poll the documented job-execution
resource/list until that execution reaches **Succeeded** or **Failed**, with a
bounded overall wait slightly longer than its 30-minute execution deadline.
Do not start another execution merely because a client-side wait expired.
Use ARM job-execution status, not the readiness of the unrelated web app, as the
completion signal. Console output contains only redacted status/safe phase
markers; Log Analytics collection occurs only with the private diagnostic opt-in.

After `Succeeded`, query only `documentId eq 'private-ingestion-synthetic'` in
Search using the explicit approved principal ACL filter, confirm expected
synthetic title/content/version, and verify a different principal returns zero
matches. The embedding field remains non-retrievable and 1536-dimensional.
Successful job completion entails source/ledger Blob writes through the private
endpoint; the external workstation is still correctly blocked from Blob.
No chat model call is required for this ingestion proof.

Only then restart the app, check HTTPS health and unchanged Entra requirements,
and obtain fresh tickets/sessions. For failure, keep readers stopped, inspect
redacted phase/status and endpoint/RBAC/schema metadata, and retry **explicitly**
with the same document version. If the first execution created the index before
failing, set `createIndex: false` before retrying. If content/ACL/version changes,
advance the approved RFC3339 version. No automatic network exception, identity
expansion, schema replacement or data deletion is part of recovery.

## Evidence boundaries

The template compiled without diagnostics; 30 offline private-ingestion checks
passed for private network shape, separate/scoped identity, bounded manual job,
private-IP pinning, managed-identity request boundaries and synthetic-only input.
The Docker image was built and its real nonroot Linux entrypoint passed with
`--network none -ValidateOnly`. The shared version parser was corrected for
PowerShell 7.4's materialized JSON dates while still rejecting unzoned versions.
`Test-Offline.ps1` includes these tests automatically.

No private endpoint, private environment or job execution was applied by this
implementation slice. Real policy approval, provisioning, DNS, identity token
delivery, data-plane RBAC, Blob writes, embeddings and ACL-filtered retrieval
remain live verification steps for the parent operator.

References: [jobs schema](https://learn.microsoft.com/azure/templates/microsoft.app/2024-03-01/jobs),
[managed identity REST endpoint](https://learn.microsoft.com/azure/container-apps/managed-identity),
[Storage private endpoints](https://learn.microsoft.com/azure/storage/common/storage-private-endpoints).
