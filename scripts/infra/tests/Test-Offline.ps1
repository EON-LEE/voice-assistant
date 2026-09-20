[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BicepPath,
    [string]$BackendSchemaPath
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$scriptsRoot = Split-Path $PSScriptRoot -Parent
$repoRoot = [IO.Path]::GetFullPath((Join-Path $scriptsRoot '..\..'))
Import-Module (Join-Path $scriptsRoot 'Common.psm1') -Force
Import-Module (Join-Path $scriptsRoot 'KnowledgeSync.psm1') -Force
Import-Module (Join-Path $scriptsRoot 'Knowledge.psm1') -Force
$script:passed = 0
function Assert-True {
    param([bool]$Condition, [string]$Name)
    if (-not $Condition) { throw "FAIL: $Name" }
    $script:passed++
    Write-Output "PASS: $Name"
}
function Assert-Throws {
    param([scriptblock]$Action, [string]$Pattern, [string]$Name)
    $caught = $false
    try { & $Action | Out-Null } catch {
        if ($_.Exception.Message -notmatch $Pattern) { throw "FAIL: $Name (unexpected error: $($_.Exception.Message))" }
        $caught = $true
    }
    Assert-True $caught $Name
}
function Copy-Json {
    param($Value)
    return ($Value | ConvertTo-Json -Depth 50 -Compress | ConvertFrom-Json)
}
function New-FakeRequest {
    param($Store)
    return {
        param($Method, $Uri, $Audience, $Body, $Headers, $ContentType, $Expected, [datetime]$Deadline)
        if ([datetime]::UtcNow -gt $Deadline) { throw 'Expired request deadline.' }
        $Store.calls.Add("$Method $Audience") > $null
        if ($Uri.EndsWith('?comp=lease')) {
            switch ($Headers['x-ms-lease-action']) {
                'acquire' {
                    if ($Store.lease) { throw 'Lease already held.' }
                    $Store.lease = $Headers['x-ms-proposed-lease-id']
                }
                'renew' { if (-not $Store.lease) { throw 'Lease lost.' } }
                'release' { $Store.lease = '' }
            }
            return [pscustomobject]@{ Status = 200; Data = $null }
        }
        if ($Uri.EndsWith('/_ingestion-state.json')) {
            if ($Method -eq 'GET') { return [pscustomobject]@{ Status = 200; Data = ($Store.state | ConvertTo-Json -Depth 30 | ConvertFrom-Json) } }
            if ($Headers.ContainsKey('If-None-Match') -and $Store.state) { return [pscustomobject]@{ Status = 412; Data = $null } }
            if (-not $Headers.ContainsKey('If-None-Match') -and $Headers['x-ms-lease-id'] -ne $Store.lease) { throw 'Missing lease guard.' }
            $Store.state = $Body | ConvertFrom-Json
            return [pscustomobject]@{ Status = 201; Data = $null }
        }
        if ($Audience -eq 'storage') {
            if ($Method -eq 'DELETE') { $Store.blob = ''; return [pscustomobject]@{ Status = 202; Data = $null } }
            $Store.blob = $Body
            return [pscustomobject]@{ Status = 201; Data = $null }
        }
        if ($Audience -eq 'cognitive') {
            $Store.embeddings++
            $length = if ($Store.badVector) { 1535 } else { 1536 }
            return [pscustomobject]@{ Status = 200; Data = [pscustomobject]@{ data = @([pscustomobject]@{ embedding = @(1..$length | ForEach-Object { 0.01 }) }) } }
        }
        if ($Uri.Contains('/docs/search?')) {
            $query = $Body | ConvertFrom-Json
            $id = $query.filter.Substring(15).Trim("'")
            $values = @($Store.chunks.Values | Where-Object documentId -CEQ $id)
            return [pscustomobject]@{ Status = 200; Data = [pscustomobject]@{ value = $values } }
        }
        if ($Uri.Contains('/docs/index?')) {
            $items = @()
            foreach ($item in ($Body | ConvertFrom-Json).value) {
                if ($item.'@search.action' -eq 'delete') {
                    $Store.chunks.Remove($item.id)
                    $Store.events.Add('delete') > $null
                } else {
                    $Store.chunks[$item.id] = $item
                    $Store.events.Add('upload') > $null
                }
                $items += [pscustomobject]@{ key = $item.id; status = (-not $Store.failUpload) }
            }
            return [pscustomobject]@{ Status = 200; Data = [pscustomobject]@{ value = $items } }
        }
        if ($Method -eq 'PUT') {
            if ($Store.schema) { throw 'Index already exists.' }
            $Store.schema = $Body | ConvertFrom-Json
        }
        return [pscustomobject]@{ Status = 200; Data = $Store.schema }
    }.GetNewClosure()
}

$temporary = @([IO.Path]::GetTempFileName(), [IO.Path]::GetTempFileName(), [IO.Path]::GetTempFileName())
try {
    $tokens = $null
    $parseErrors = $null
    foreach ($file in Get-ChildItem -LiteralPath $scriptsRoot -Recurse -File | Where-Object Extension -In @('.ps1', '.psm1')) {
        $null = [Management.Automation.Language.Parser]::ParseFile($file.FullName, [ref]$tokens, [ref]$parseErrors)
        if ($parseErrors.Count) { throw "PowerShell parse error in $($file.Name): $parseErrors" }
    }
    Assert-True $true 'All PowerShell files parse without errors'
    & $BicepPath build (Join-Path $repoRoot 'infra\main.bicep') --outfile $temporary[0]
    Assert-True ($LASTEXITCODE -eq 0) 'Official Bicep compiles all modules'
    $template = Get-Content -LiteralPath $temporary[0] -Raw | ConvertFrom-Json
    $app = @($template.resources | Where-Object type -EQ 'Microsoft.App/containerApps')[0]
    Assert-True ($app.properties.template.scale.minReplicas -eq 1 -and $app.properties.template.scale.maxReplicas -eq 1 -and
        $app.properties.configuration.activeRevisionsMode -eq 'Single') 'Ticket memory enforces one replica and single active revision'
    Assert-True ($app.properties.configuration.ingress.external -eq $true -and $app.properties.configuration.ingress.targetPort -eq 8080 -and
        $app.properties.configuration.ingress.allowInsecure -eq $false) 'External HTTPS ingress targets port 8080'
    $env = $app.properties.template.containers[0].env
    Assert-True ((@($env | Where-Object name -EQ 'Provider__Mode')[0].value -eq 'Azure') -and
        (@($env | Where-Object name -EQ 'ASPNETCORE_ENVIRONMENT')[0].value -eq 'Production')) 'No production fake authentication configuration'
    foreach ($name in @('tenantId', 'apiAudience', 'spaClientId', 'apiScope')) {
        Assert-True ($template.parameters.$name.type -eq 'secureString') "Protected deployment parameter: $name"
    }
    $storage = @($template.resources | Where-Object type -EQ 'Microsoft.Storage/storageAccounts')[0]
    Assert-True ($storage.properties.allowBlobPublicAccess -eq $false -and $storage.properties.allowSharedKeyAccess -eq $false -and
        $storage.properties.publicNetworkAccess -eq 'Disabled') 'Private documents disable public networking, anonymous and shared-key access'
    $search = @($template.resources | Where-Object type -EQ 'Microsoft.Search/searchServices')[0]
    Assert-True ($search.properties.disableLocalAuth -eq $true -and
        -not $search.properties.PSObject.Properties['authOptions']) 'Entra-only Search disables keys without conflicting aadOrApiKey authOptions'
    Assert-True ($search.properties.semanticSearch -eq "[parameters('searchSemanticSearch')]" -and
        $template.parameters.searchSemanticSearch.defaultValue -eq 'free' -and
        ($template.parameters.searchSemanticSearch.allowedValues -join ',') -eq 'free,standard') 'Semantic ranker capability is explicit free or standard, default free'
    Assert-True ((@($env | Where-Object name -EQ 'Azure__SearchSemanticConfiguration')[0].value -eq 'meeting-semantic') -and
        (@($env | Where-Object name -EQ 'Azure__SearchMinimumRerankerScore')[0].value -eq '2.0')) 'Runtime semantic configuration and relevance threshold match API contract'
    $environment = @($template.resources | Where-Object type -EQ 'Microsoft.App/managedEnvironments')[0]
    Assert-True (-not $environment.properties.appLogsConfiguration.PSObject.Properties['destination'] -and
        -not $environment.properties.appLogsConfiguration.PSObject.Properties['logAnalyticsConfiguration']) 'Log destination/workspace are unset; never send the unsupported literal none'
    $blobRoles = @($template.resources | Where-Object { $_.type -eq 'Microsoft.Authorization/roleAssignments' -and
        $_.properties.roleDefinitionId -match 'ba92f5b4-2d11-453d-a403-e96b0029c9fe' })
    Assert-True ($blobRoles.Count -eq 1 -and $blobRoles[0].properties.principalId -eq "[parameters('ingestionPrincipalId')]" -and
        $blobRoles[0].scope -match 'containers') 'Only the optional operator gets container-scoped Blob access'

    $parameters = Get-Content -LiteralPath (Join-Path $repoRoot 'infra\parameters.example.json') -Raw | ConvertFrom-Json
    Assert-Throws { Assert-DeploymentParameters $parameters $template } 'Unresolved example' 'Example placeholders fail closed'
    $p = $parameters.parameters
    foreach ($name in @('location', 'speechLocation', 'openAILocation', 'searchLocation')) { $p.$name.value = 'eastus' }
    $p.registryName.value = 'fixtureacr'
    $p.registryResourceGroup.value = 'fixture-registry'
    $p.image.value = 'fixtureacr.azurecr.io/voice-assistant@sha256:' + ('a' * 64)
    $p.tenantId.value = '11111111-1111-4111-8111-111111111111'
    $p.apiAudience.value = '22222222-2222-4222-8222-222222222222'
    $p.spaClientId.value = '33333333-3333-4333-8333-333333333333'
    $p.apiScope.value = 'api://22222222-2222-4222-8222-222222222222/Meeting.Access'
    $p.chatModelName.value = 'gpt-4o-mini'
    $p.chatModelVersion.value = '2024-07-18'
    $p.chatDeploymentSku.value = 'GlobalStandard'
    $p.embeddingModelVersion.value = '1'
    $p.embeddingDeploymentSku.value = 'Standard'
    Assert-DeploymentParameters $parameters $template
    Assert-True $true 'Complete fixture parameters validate offline (not a quota/availability promise)'
    $invalid = Copy-Json $parameters
    $invalid.parameters.spaClientId.value = $p.apiAudience.value
    Assert-Throws { Assert-DeploymentParameters $invalid $template } 'separate SPA' 'API audience cannot silently reuse SPA client ID'
    $invalid = Copy-Json $parameters
    $invalid.parameters.image.value = 'fixtureacr.azurecr.io/voice-assistant:latest'
    Assert-Throws { Assert-DeploymentParameters $invalid $template } 'sha256 digest' 'Mutable image tags rejected'
    $parameters | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $temporary[1] -Encoding UTF8
    $result = & (Join-Path $scriptsRoot 'Deploy.ps1') -BicepPath $BicepPath -ParametersFile $temporary[1]
    Assert-True ($result -match 'No Azure request') 'Default deployment invocation is offline only'
    Assert-Throws { & (Join-Path $scriptsRoot 'Deploy.ps1') -BicepPath $BicepPath -ParametersFile $temporary[1] -Apply -WhatIf } 'only one' 'Conflicting deployment switches rejected before Azure access'
    $global:MockAzCalls = New-Object 'Collections.Generic.List[string]'
    $processModule = Get-Module -All AzureProcess | Select-Object -First 1
    $originalProcess = & $processModule { (Get-Command Invoke-BoundedJsonCommand).ScriptBlock }
    & $processModule {
        function script:Invoke-BoundedJsonCommand {
            param($Path, $Prefix, $Arguments, $TimeoutSeconds)
            $result = & az @Arguments
            return [pscustomobject]@{ Succeeded = $true; Code = 'OK'; Data = ($result | ConvertFrom-Json) }
        }
    }
    function global:az {
        $global:LASTEXITCODE = 0
        $arguments = @($args)
        $line = $arguments -join ' '
        $global:MockAzCalls.Add($line) > $null
        if ($line.StartsWith('account show')) {
            return '{"id":"b0af194e-77a5-4471-bb43-67e78295b5c8","tenantId":"11111111-1111-4111-8111-111111111111","state":"Enabled"}'
        }
        if ($line.StartsWith('deployment group what-if')) {
            return '{"changes":[{"changeType":"Create","resourceId":"/mock/resource","after":{"mustNotPrint":"PRIVATE_DIFF_SENTINEL"}}]}'
        }
        if ($line.StartsWith('deployment group create')) {
            return '{"properties":{"provisioningState":"Succeeded","outputs":{"browserUrl":{"value":"https://fixture.invalid"}}}}'
        }
        return '{}'
    }
    try {
        $result = & (Join-Path $scriptsRoot 'Deploy.ps1') -BicepPath $BicepPath -ParametersFile $temporary[1] -ResourceGroup 'fixture-rg' -Validate
        Assert-True ($result -match 'validation passed' -and @($global:MockAzCalls | Where-Object { $_ -match 'deployment group create' }).Count -eq 0) 'Mocked Validate does not deploy'
        $preview = & (Join-Path $scriptsRoot 'Deploy.ps1') -BicepPath $BicepPath -ParametersFile $temporary[1] -ResourceGroup 'fixture-rg' -WhatIf
        Assert-True ($preview.ChangeType -eq 'Create' -and ($preview | ConvertTo-Json -Depth 10) -notmatch 'PRIVATE_DIFF_SENTINEL') 'Mocked WhatIf suppresses parameter/environment diff values'
        $result = & (Join-Path $scriptsRoot 'Deploy.ps1') -BicepPath $BicepPath -ParametersFile $temporary[1] -ResourceGroup 'fixture-rg' -Apply
        Assert-True (@($global:MockAzCalls | Where-Object { $_ -match 'deployment group create' }).Count -eq 1) 'Only explicit Apply issues a mocked create'
        Assert-True (@($global:MockAzCalls | Where-Object { $_ -notmatch '--subscription b0af194e-77a5-4471-bb43-67e78295b5c8' -or $_ -match 'account set' }).Count -eq 0) 'All online commands target explicit subscription without changing global defaults'
        Assert-Throws { Assert-Subscription 'b0af194e-77a5-4471-bb43-67e78295b5c8' '99999999-9999-4999-8999-999999999999' } 'tenant does not match' 'Wrong subscription tenant rejected'
    } finally {
        & $processModule { param($Original) Set-Item Function:script:Invoke-BoundedJsonCommand $Original } $originalProcess
        Remove-Item Function:\az
        Remove-Variable MockAzCalls -Scope Global
    }

    $fixtures = Join-Path $PSScriptRoot 'fixtures'
    $manifestPath = Join-Path $fixtures 'manifest.json'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $plan = New-KnowledgePlan $manifestPath $fixtures
    $knowledgeModule = Get-Module Knowledge
    $normalized = & $knowledgeModule { ConvertTo-VersionTime ([datetime]::SpecifyKind([datetime]'2026-09-01T00:00:00', [DateTimeKind]::Utc)) }
    Assert-True ($normalized -is [datetimeoffset] -and $normalized.ToString('yyyy-MM-ddTHH:mm:ssZ') -eq '2026-09-01T00:00:00Z') 'UTC dates materialized by PowerShell 7 JSON preserve version semantics'
    Assert-Throws { & $knowledgeModule { ConvertTo-VersionTime ([datetime]::SpecifyKind([datetime]'2026-09-01T00:00:00', [DateTimeKind]::Unspecified)) } } 'RFC3339' 'Materialized unzoned JSON dates are still rejected'
    Assert-True ($plan.documents.Count -eq 1 -and $plan.documents[0].chunks.Count -eq 1) 'Approved synthetic markdown yields deterministic chunk plan'
    $again = New-KnowledgePlan $manifestPath $fixtures
    Assert-True ($again.documents[0].sourceHash -ceq $plan.documents[0].sourceHash -and
        $again.documents[0].chunks[0].id -ceq $plan.documents[0].chunks[0].id) 'Same source/ACL/version gives stable IDs and hash'
    Assert-True ($plan.documents[0].chunks[0].allowedPrincipalIds.Count -eq 1 -and
        $plan.documents[0].chunks[0].url -eq $manifest.documents[0].url) 'Every chunk carries original explicit ACL and citation metadata'
    $bad = Copy-Json $manifest
    $bad.documents[0].allowedPrincipalIds = @()
    $bad | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $temporary[2] -Encoding UTF8
    Assert-Throws { New-KnowledgePlan $temporary[2] $fixtures } 'explicit Entra' 'Empty ACL cannot accidentally publish public chunks'
    $bad.documents[0].allowedPrincipalIds = @('*')
    $bad | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $temporary[2] -Encoding UTF8
    Assert-Throws { New-KnowledgePlan $temporary[2] $fixtures } 'GUIDs' 'Wildcard ACL is rejected'
    $bad = Copy-Json $manifest
    $bad.documents[0].path = '..\..\Common.psm1'
    $bad | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $temporary[2] -Encoding UTF8
    Assert-Throws { New-KnowledgePlan $temporary[2] $fixtures } 'escapes' 'Path traversal rejected'
    $bad = Copy-Json $manifest
    $bad.documents[0].updatedAt = '2026-09-01'
    $bad | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $temporary[2] -Encoding UTF8
    Assert-Throws { New-KnowledgePlan $temporary[2] $fixtures } 'RFC3339' 'Unzoned timestamps rejected'
    $bad = Copy-Json $manifest
    $bad.documents[0].url = 'http://example.invalid/private'
    $bad | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $temporary[2] -Encoding UTF8
    Assert-Throws { New-KnowledgePlan $temporary[2] $fixtures } 'HTTPS' 'Insecure source citation rejected'
    $bad = Copy-Json $manifest
    $bad.documents += $bad.documents[0]
    $bad | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $temporary[2] -Encoding UTF8
    Assert-Throws { New-KnowledgePlan $temporary[2] $fixtures } 'unique' 'Duplicate operations for one document rejected'
    $chunks = @(Split-KnowledgeText ('a' * 3001))
    Assert-True ($chunks.Count -eq 3 -and @($chunks | Where-Object Length -GT 1500).Count -eq 0 -and
        $chunks[0].Substring(1350) -ceq $chunks[1].Substring(0, 150)) 'Chunk size and overlap match measured bounds'
    $unicode = ('x' * 1499) + [char]::ConvertFromUtf32(0x1F600) + ('y' * 1600)
    $chunks = @(Split-KnowledgeText $unicode)
    $encoding = New-Object Text.UTF8Encoding($false, $true)
    foreach ($chunk in $chunks) { $null = $encoding.GetBytes($chunk) }
    Assert-True $true 'Chunk boundaries preserve Unicode surrogate pairs'
    $badSchema = Get-KnowledgeSchema
    (@($badSchema.fields | Where-Object name -EQ 'contentVector')[0]).dimensions = 3072
    Assert-Throws { Assert-KnowledgeSchema $badSchema } 'dimensions' 'Backend/vector dimension mismatch rejected'
    $badSchema = Get-KnowledgeSchema
    (@($badSchema.fields | Where-Object name -EQ 'allowedPrincipalIds')[0]).filterable = $false
    Assert-Throws { Assert-KnowledgeSchema $badSchema } 'filterable' 'Nonfilterable ACL schema rejected'
    if ($BackendSchemaPath) {
        $backendSchema = Get-Content -LiteralPath $BackendSchemaPath -Raw | ConvertFrom-Json
        foreach ($field in $backendSchema.fields) {
            $ours = @($plan.schema.fields | Where-Object name -CEQ $field.name)[0]
            foreach ($property in $field.PSObject.Properties) {
                if ($ours.($property.Name) -cne $property.Value) { throw "Backend schema drift: $($field.name).$($property.Name)" }
            }
        }
        Assert-True $true 'All backend Search fields/properties are preserved'
        if ($backendSchema.PSObject.Properties['semantic']) {
            Assert-True (($backendSchema.semantic | ConvertTo-Json -Depth 20 -Compress) -ceq
                ($plan.schema.semantic | ConvertTo-Json -Depth 20 -Compress)) 'Backend semantic title/content priorities and default match infrastructure'
        } else { throw 'Backend schema has no semantic configuration; integrate the coordinated API contract before release.' }
    }
    $result = & (Join-Path $scriptsRoot 'Import-Knowledge.ps1') -ManifestPath $manifestPath -ContentRoot $fixtures
    Assert-True ($result -match 'No authentication, embedding, upload, or deletion') 'Ingestion defaults to fully offline dry-run'
    Assert-Throws { & (Join-Path $scriptsRoot 'Import-Knowledge.ps1') -ManifestPath $manifestPath -ContentRoot $fixtures -Apply } 'ConfirmExclusiveMaintenance' 'Apply without maintenance acknowledgment cannot contact Azure'

    $store = @{ state = $null; schema = $null; lease = ''; chunks = @{}; blob = ''; embeddings = 0; badVector = $false; failUpload = $false;
        calls = (New-Object 'Collections.Generic.List[string]'); events = (New-Object 'Collections.Generic.List[string]') }
    $request = New-FakeRequest $store
    $syncArguments = @{ Request = $request; SearchRoot = 'https://search.invalid'; StorageRoot = 'https://storage.invalid/documents';
        OpenAIRoot = 'https://ai.invalid'; EmbeddingDeployment = 'meeting-embedding' }
    Invoke-KnowledgeSync -Plan $plan @syncArguments -CreateIndex
    Assert-True ($store.chunks.Count -eq 1 -and $store.embeddings -eq 1 -and $store.blob.Length -gt 0 -and
        $store.state.documents[0].status -eq 'complete' -and -not $store.lease) 'Mocked apply embeds/uploads approved text, saves state, releases lease'
    Assert-True ($store.chunks[$plan.documents[0].chunks[0].id].contentVector.Count -eq 1536) 'Uploaded vector has exact backend dimensions'
    Invoke-KnowledgeSync -Plan $plan @syncArguments
    Assert-True ($store.chunks.Count -eq 1 -and $store.events[1] -eq 'delete' -and $store.events[2] -eq 'upload') 'Retry is idempotent and removes prior chunks before upload'
    $changed = Copy-Json $manifest
    $changed.documents[0].allowedPrincipalIds = @('44444444-4444-4444-8444-444444444444')
    $changed | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $temporary[2] -Encoding UTF8
    $changedPlan = New-KnowledgePlan $temporary[2] $fixtures
    Assert-Throws { Invoke-KnowledgeSync -Plan $changedPlan @syncArguments } 'Stale or conflicting' 'Same-version ACL/content conflict cannot overwrite published data'
    $changed.documents[0].updatedAt = '2026-09-02T00:00:00Z'
    $changed | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $temporary[2] -Encoding UTF8
    $changedPlan = New-KnowledgePlan $temporary[2] $fixtures
    $oldChunk = Copy-Json $store.chunks[$plan.documents[0].chunks[0].id]
    $oldChunk.id = 'synthetic-meeting_000099'
    $store.chunks[$oldChunk.id] = $oldChunk
    Invoke-KnowledgeSync -Plan $changedPlan @syncArguments
    Assert-True ($store.chunks.Count -eq 1 -and
        $store.chunks[$plan.documents[0].chunks[0].id].allowedPrincipalIds[0] -eq '44444444-4444-4444-8444-444444444444') 'ACL changes and shortened documents remove all obsolete chunks'
    $deleted = [pscustomobject]@{ version = 1; documents = @([pscustomobject]@{
        documentId = 'synthetic-meeting'; operation = 'delete'; updatedAt = '2026-09-03T00:00:00Z'
    }) }
    $deleted | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $temporary[2] -Encoding UTF8
    $deletePlan = New-KnowledgePlan $temporary[2] $fixtures
    Invoke-KnowledgeSync -Plan $deletePlan @syncArguments
    Assert-True ($store.chunks.Count -eq 0 -and -not $store.blob -and
        $store.state.documents[0].operation -eq 'delete') 'Deletion removes vectors/source and preserves a tombstone'
    Assert-Throws { Invoke-KnowledgeSync -Plan $changedPlan @syncArguments } 'Stale or conflicting' 'Tombstone prevents stale document resurrection'
    $store.lease = 'another-writer'
    Assert-Throws { Invoke-KnowledgeSync -Plan $plan @syncArguments } 'Lease already held' 'Concurrent ingestion writer rejected'
    $store.lease = ''
    $changed.documents[0].updatedAt = '2026-09-04T00:00:00Z'
    $changed | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $temporary[2] -Encoding UTF8
    $changedPlan = New-KnowledgePlan $temporary[2] $fixtures
    $store.badVector = $true
    Assert-Throws { Invoke-KnowledgeSync -Plan $changedPlan @syncArguments } 'vector shape' 'Invalid embedding response fails explicitly'
    Assert-True ($store.state.documents[0].status -eq 'pending' -and -not $store.lease) 'Failed ingestion leaves recoverable pending version and releases lock'
    $store.badVector = $false
    $store.failUpload = $true
    Assert-Throws { Invoke-KnowledgeSync -Plan $changedPlan @syncArguments } 'Search rejected' 'Per-item Search failures are not reported as success'
    $store.failUpload = $false
    Invoke-KnowledgeSync -Plan $changedPlan @syncArguments
    Assert-True ($store.state.documents[0].status -eq 'complete' -and $store.chunks.Count -eq 1) 'Same manifest recovers a partial upload without stale ACLs'
    Write-Output "All $script:passed offline checks passed. No Azure connection or real data upload was made."
} finally {
    foreach ($file in $temporary) { Remove-Item -LiteralPath $file -Force }
}
& (Join-Path $PSScriptRoot 'Test-Readiness.ps1')
& (Join-Path $PSScriptRoot 'Test-PrivateIngestion.ps1') -BicepPath $BicepPath
& (Join-Path $PSScriptRoot 'Test-SemanticIndex.ps1')
