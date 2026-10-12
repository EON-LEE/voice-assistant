[CmdletBinding()]
param([Parameter(Mandatory)][string]$BicepPath)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$scriptsRoot = Split-Path $PSScriptRoot -Parent
$repoRoot = [IO.Path]::GetFullPath((Join-Path $scriptsRoot '..\..'))
Import-Module (Join-Path $scriptsRoot 'ManagedIngestion.psm1') -Force
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
        if ($_.Exception.Message -notmatch $Pattern) { throw "FAIL: $Name (unexpected safe error)" }
        $caught = $true
    }
    Assert-True $caught $Name
}
$compiled = [IO.Path]::GetTempFileName()
try {
    & $BicepPath build (Join-Path $repoRoot 'infra\private-ingestion\main.bicep') --outfile $compiled
    Assert-True ($LASTEXITCODE -eq 0) 'Private ingestion network/job template compiles'
    $template = Get-Content -LiteralPath $compiled -Raw | ConvertFrom-Json
    $job = @($template.resources | Where-Object type -EQ 'Microsoft.App/jobs')[0]
    Assert-True ($job.properties.configuration.triggerType -eq 'Manual' -and
        $job.properties.configuration.replicaRetryLimit -eq 0 -and $job.properties.configuration.replicaTimeout -eq 1800 -and
        $job.properties.configuration.manualTriggerConfig.parallelism -eq 1 -and
        $job.properties.configuration.manualTriggerConfig.replicaCompletionCount -eq 1) 'Job is manual, single-replica, no retries, bounded to 30 minutes'
    Assert-True (@($template.resources | Where-Object { $_.type -eq 'Microsoft.Storage/storageAccounts' -or
        $_.type -eq 'Microsoft.App/containerApps' -or $_.type -eq 'Microsoft.Resources/deploymentScripts' }).Count -eq 0) 'Private route neither updates storage firewall/web app nor starts jobs on deploy'
    $environment = @($template.resources | Where-Object type -EQ 'Microsoft.App/managedEnvironments')[0]
    Assert-True ($environment.properties.vnetConfiguration.internal -eq $true -and
        $environment.properties.vnetConfiguration.infrastructureSubnetId -match 'environment' -and
        $template.parameters.enableDiagnostics.defaultValue -eq $false -and
        $environment.properties.appLogsConfiguration -match "if\(parameters\('enableDiagnostics'\)") 'Dedicated internal environment defaults to no diagnostics and requires explicit opt-in'
    $network = @($template.resources | Where-Object type -EQ 'Microsoft.Network/virtualNetworks')[0]
    Assert-True ($network.properties.subnets.Count -eq 2 -and
        $network.properties.subnets[0].properties.delegations[0].properties.serviceName -eq 'Microsoft.App/environments') 'Separate delegated infrastructure and private endpoint subnets'
    $endpoint = @($template.resources | Where-Object type -EQ 'Microsoft.Network/privateEndpoints')[0]
    Assert-True ($endpoint.properties.privateLinkServiceConnections[0].properties.groupIds[0] -eq 'blob' -and
        $endpoint.properties.ipConfigurations[0].properties.privateIPAddress -eq "[parameters('privateEndpointIp')]") 'Private endpoint is only Blob with explicit IP pin'
    Assert-True (@($template.resources | Where-Object type -EQ 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups').Count -eq 1 -and
        @($template.resources | Where-Object type -EQ 'Microsoft.Network/privateDnsZones/virtualNetworkLinks').Count -eq 1) 'Private Blob DNS is linked to both endpoint and dedicated VNet'
    $roles = @($template.resources | Where-Object type -EQ 'Microsoft.Authorization/roleAssignments')
    Assert-True ($roles.Count -eq 5 -and @($roles.properties.principalId | Sort-Object -Unique).Count -eq 1 -and
        ($roles.properties.principalId -join '') -notmatch 'voice-runtime') 'All five roles belong only to separate job identity'
    $blobRole = @($roles | Where-Object { $_.properties.roleDefinitionId -match 'ba92f5b4-2d11-453d-a403-e96b0029c9fe' })[0]
    Assert-True ($blobRole.scope -match 'containers' -and $blobRole.scope -match 'documents') 'Job Blob access is documents-container scoped'
    Assert-True ($template.parameters.confirmExclusiveMaintenance.defaultValue -eq $false -and
        $template.parameters.createIndex.defaultValue -eq $false) 'Maintenance/index-creation intent is never silently assumed'
    $flags = @($job.properties.template.containers[0].env | Where-Object name -In @('INGEST_CREATE_INDEX', 'INGEST_CONFIRM_EXCLUSIVE_MAINTENANCE'))
    Assert-True ($flags.Count -eq 2 -and
        @($flags | Where-Object { $_.value -notmatch ", 'true', 'false'\)\]$" -or $_.value -match '\[string\(' }).Count -eq 0) 'ARM boolean intent is explicitly lowercase, never string(bool) TitleCase'
    $workspace = @($template.resources | Where-Object type -EQ 'Microsoft.OperationalInsights/workspaces')[0]
    Assert-True ($workspace.condition -eq "[parameters('enableDiagnostics')]" -and $workspace.properties.retentionInDays -eq 30 -and
        $workspace.properties.features.disableLocalAuth -eq $true) 'Private diagnostics workspace is opt-in, 30-day retention and Entra-only'
    $diagnostic = @($template.resources | Where-Object type -EQ 'Microsoft.Insights/diagnosticSettings')[0]
    Assert-True ($diagnostic.condition -eq "[parameters('enableDiagnostics')]" -and $diagnostic.scope -match 'managedEnvironments' -and
        $diagnostic.properties.workspaceId -match 'diagnostics' -and
        ($diagnostic.properties.logs.category -join ',') -ceq 'ContainerAppConsoleLogs,ContainerAppSystemLogs' -and
        -not $diagnostic.properties.PSObject.Properties['metrics']) 'Only private environment console/system categories export; no web or request diagnostics'
    Assert-True (($template | ConvertTo-Json -Depth 100 -Compress) -notmatch 'listKeys|sharedKey|allLogs|HTTPLogs') 'Diagnostics never retrieve workspace keys or enable broad HTTP/log groups'
} finally { Remove-Item -LiteralPath $compiled -Force }

Assert-PrivateBlobAddress '10.246.1.4' @('10.246.1.4')
Assert-True $true 'Only expected private DNS address is accepted'
Assert-Throws { Assert-PrivateBlobAddress '20.0.0.1' @('20.0.0.1') } 'RFC1918' 'Public Blob address cannot pass as a private endpoint'
Assert-Throws { Assert-PrivateBlobAddress '10.246.1.4' @('10.246.1.5') } 'exclusively' 'Unexpected private DNS target is rejected'
Assert-Throws { Assert-PrivateBlobAddress '10.246.1.4' @('10.246.1.4', '20.0.0.1') } 'exclusively' 'Mixed public/private DNS answer is rejected'
Assert-Throws { Assert-PrivateBlobAddress '10.246.1.4' @() } 'exclusively' 'Empty private DNS answer is rejected'
$state = @{ calls = (New-Object 'Collections.Generic.List[object]'); expired = $false }
$transport = {
    param($Method, $Uri, $Headers, $Body, $ContentType, $Expected, $TimeoutSeconds)
    $state.calls.Add(@{ uri = $Uri; headers = $Headers; timeout = $TimeoutSeconds }) > $null
    if ($Uri.StartsWith('http://localhost:42356/')) {
        $expiry = if ($state.expired) { [datetimeoffset]::UtcNow.AddMinutes(-1) } else { [datetimeoffset]::UtcNow.AddHours(1) }
        return [pscustomobject]@{ Status = 200; Data = [pscustomobject]@{ access_token = 'PRIVATE_FAKE_TOKEN'; expires_on = $expiry.ToUnixTimeSeconds().ToString() } }
    }
    return [pscustomobject]@{ Status = 200; Data = @{ value = @() } }
}.GetNewClosure()
$options = @{
    IdentityEndpoint = 'http://localhost:42356/msi/token'; IdentityHeader = 'PRIVATE_FAKE_HEADER'
    ClientId = '11111111-1111-4111-8111-111111111111'; SearchRoot = 'https://fixture-search.search.windows.net'
    StorageRoot = 'https://fixturestorage.blob.core.windows.net/documents'; OpenAIRoot = 'https://fixture-ai.openai.azure.com'
    HttpTransport = $transport
}
$request = New-ManagedIngestionRequest @options
$searchUrl = $options.SearchRoot + '/indexes/fixture/docs/search?api-version=2024-07-01'
$null = & $request 'POST' $searchUrl 'search' '{}' @{} 'application/json' @(200) ([datetime]::MaxValue)
$null = & $request 'POST' $searchUrl 'search' '{}' @{} 'application/json' @(200) ([datetime]::MaxValue)
Assert-True ($state.calls.Count -eq 3 -and $state.calls[0].uri -match 'api-version=2019-08-01' -and
    $state.calls[0].uri -match 'client_id=11111111-' -and $state.calls[0].headers['X-IDENTITY-HEADER'] -eq 'PRIVATE_FAKE_HEADER') 'Platform identity request uses explicit separate client identity and caches token'
Assert-True ($state.calls[1].headers.Authorization -eq 'Bearer PRIVATE_FAKE_TOKEN' -and
    -not $state.calls[1].headers.ContainsKey('X-IDENTITY-HEADER') -and $state.calls[1].timeout -eq 20) 'Data request receives bearer only, never platform identity header'
$null = & $request 'GET' ($options.StorageRoot + '/fixture/state.json') 'storage' $null @{} 'application/json' @(200) ([datetime]::MaxValue)
Assert-True ($state.calls.Count -eq 5 -and $state.calls[4].headers['x-ms-version'] -eq '2023-11-03' -and
    $state.calls[4].headers.ContainsKey('x-ms-date')) 'Storage gets its own audience and required service headers'
$count = $state.calls.Count
Assert-Throws { & $request 'GET' $searchUrl 'search' $null @{} 'application/json' @(200) ([datetime]::UtcNow.AddSeconds(-1)) } 'lease safety' 'Expired lease window cannot send a data request'
Assert-True ($state.calls.Count -eq $count) 'Expired-deadline rejection emits no HTTP request'
Assert-Throws { & $request 'GET' 'https://example.invalid/x' 'storage' $null @{} 'application/json' @(200) ([datetime]::MaxValue) } 'boundary' 'Unexpected service endpoint cannot receive bearer credentials'
Assert-Throws { & $request 'GET' ($options.StorageRoot + '/../outside') 'storage' $null @{} 'application/json' @(200) ([datetime]::MaxValue) } 'endpoint' 'Normalized path traversal cannot escape Blob container'
Assert-Throws { & $request 'GET' $searchUrl 'search' $null @{ Authorization = 'override' } 'application/json' @(200) ([datetime]::MaxValue) } 'override' 'Callers cannot replace managed identity headers'
$options.IdentityEndpoint = 'https://example.invalid/token'
Assert-Throws { New-ManagedIngestionRequest @options } 'platform-provided' 'External identity endpoint cannot receive platform identity header'
$options.IdentityEndpoint = 'http://localhost:42356/msi/token'
$state.expired = $true
$request = New-ManagedIngestionRequest @options
Assert-Throws { & $request 'GET' $searchUrl 'search' $null @{} 'application/json' @(200) ([datetime]::MaxValue) } 'expired token' 'Expired managed identity tokens fail closed'

$oldAcl = $env:INGEST_ALLOWED_PRINCIPAL_ID
$oldVersion = $env:INGEST_DOCUMENT_UPDATED_AT
$oldIndex = $env:INGEST_INDEX_NAME
try {
    $env:INGEST_ALLOWED_PRINCIPAL_ID = '11111111-1111-4111-8111-111111111111'
    $env:INGEST_DOCUMENT_UPDATED_AT = '2026-09-20T00:00:00Z'
    $env:INGEST_INDEX_NAME = 'meeting-knowledge'
    $result = & (Join-Path $scriptsRoot 'Private-Ingestion.ps1') -ValidateOnly | ConvertFrom-Json
    Assert-True ($result.status -eq 'PASS' -and $result.uploaded -eq $false) 'Container entrypoint validates only original synthetic document without identity/network access'
} finally {
    $env:INGEST_ALLOWED_PRINCIPAL_ID = $oldAcl
    $env:INGEST_DOCUMENT_UPDATED_AT = $oldVersion
    $env:INGEST_INDEX_NAME = $oldIndex
}
Write-Output "All $script:passed private-ingestion checks passed. No cloud writes or real data uploads occurred."
