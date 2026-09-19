[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ManifestPath,
    [Parameter(Mandatory)][string]$ContentRoot,
    [string]$IndexName = 'meeting-knowledge',
    [string]$SubscriptionId = 'b0af194e-77a5-4471-bb43-67e78295b5c8',
    [string]$TenantId,
    [string]$ResourceGroup,
    [string]$SearchName,
    [string]$OpenAIName,
    [string]$StorageAccountName,
    [string]$EmbeddingDeployment = 'meeting-embedding',
    [string]$AzPath = 'az',
    [string[]]$AzPrefix = @(),
    [ValidateSet('AzureCli', 'AzPowerShell')][string]$AuthProvider = 'AzureCli',
    [ValidateRange(1, 1800)][int]$CommandTimeoutSeconds = 90,
    [switch]$CreateIndex,
    [switch]$ConfirmExclusiveMaintenance,
    [switch]$Apply
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'Knowledge.psm1') -Force
$plan = New-KnowledgePlan $ManifestPath $ContentRoot $IndexName
$chunkCount = @($plan.documents | ForEach-Object { $_.chunks }).Count
if (-not $Apply) {
    Write-Output "Offline validation passed: $($plan.documents.Count) operations, $chunkCount chunks, explicit user ACLs, 1536-dimensional index. No authentication, embedding, upload, or deletion occurred."
    return
}
if (-not $ConfirmExclusiveMaintenance) { throw '-Apply requires -ConfirmExclusiveMaintenance: stop/drain the app, clear active sessions, and reserve this index for this ingestion tool before modifying knowledge.' }
foreach ($value in @($TenantId, $ResourceGroup, $SearchName, $OpenAIName, $StorageAccountName, $EmbeddingDeployment)) {
    if ([string]::IsNullOrWhiteSpace($value)) { throw 'Apply requires explicit tenant, resource group, Search/OpenAI/storage names and embedding deployment.' }
}
if ($SearchName -cnotmatch '^[a-z0-9][a-z0-9-]{1,58}[a-z0-9]$' -or
    $OpenAIName -cnotmatch '^[a-zA-Z0-9][a-zA-Z0-9-]{1,62}[a-zA-Z0-9]$' -or
    $StorageAccountName -cnotmatch '^[a-z0-9]{3,24}$') { throw 'Invalid Azure public-cloud resource names.' }
Import-Module (Join-Path $PSScriptRoot 'Common.psm1') -Force
Set-AzureCli -AzPath $AzPath -AzPrefix $AzPrefix -TimeoutSeconds $CommandTimeoutSeconds -AuthProvider $AuthProvider
Import-Module (Join-Path $PSScriptRoot 'KnowledgeSync.psm1') -Force
$null = Assert-Subscription $SubscriptionId $TenantId
$search = Invoke-AzJson @('search', 'service', 'show', '--name', $SearchName, '--resource-group', $ResourceGroup, '--subscription', $SubscriptionId)
$ai = Invoke-AzJson @('cognitiveservices', 'account', 'show', '--name', $OpenAIName, '--resource-group', $ResourceGroup, '--subscription', $SubscriptionId)
$storage = Invoke-AzJson @('storage', 'account', 'show', '--name', $StorageAccountName, '--resource-group', $ResourceGroup, '--subscription', $SubscriptionId)
if ($search.name -ne $SearchName -or $ai.kind -ne 'OpenAI' -or $ai.properties.customSubDomainName -ne $OpenAIName -or
    $storage.name -ne $StorageAccountName) { throw 'Resource identity/endpoint mismatch; this script targets only the public-cloud resources created by this template.' }
$cache = @{}
$resources = @{ search = 'https://search.azure.com'; cognitive = 'https://cognitiveservices.azure.com'; storage = 'https://storage.azure.com/' }
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$request = {
    param($Method, $Uri, $Audience, $Body, $Headers, $ContentType, $Expected, [datetime]$Deadline)
    if (-not $cache.ContainsKey($Audience) -or $cache[$Audience].expiry -lt [datetimeoffset]::UtcNow.AddMinutes(2)) {
        $token = Invoke-AzJson @('account', 'get-access-token', '--subscription', $SubscriptionId, '--tenant', $TenantId, '--resource', $resources[$Audience])
        $cache[$Audience] = @{
            value = $token.accessToken
            expiry = [datetimeoffset]::Parse($token.expiresOn, [Globalization.CultureInfo]::InvariantCulture)
        }
    }
    $headersCopy = @{ Authorization = "Bearer $($cache[$Audience].value)" }
    foreach ($key in $Headers.Keys) { $headersCopy[$key] = $Headers[$key] }
    if ($Audience -eq 'storage') {
        $headersCopy['x-ms-version'] = '2023-11-03'
        $headersCopy['x-ms-date'] = [datetime]::UtcNow.ToString('R')
    }
    $arguments = @{ Method = $Method; Uri = $Uri; Headers = $headersCopy; ContentType = $ContentType;
        TimeoutSec = 20; MaximumRedirection = 0; UseBasicParsing = $true; ErrorAction = 'Stop'; Verbose = $false; Debug = $false }
    if ($null -ne $Body) { $arguments.Body = [Text.Encoding]::UTF8.GetBytes([string]$Body) }
    # Authentication can block; never start a data request after its lease safety window.
    if ([datetime]::UtcNow -gt $Deadline) { throw 'Ingestion lease safety window expired. No data request was sent; retry during maintenance.' }
    try {
        $response = Invoke-WebRequest @arguments
    } catch {
        $status = 0
        if ($_.Exception.PSObject.Properties['Response'] -and $_.Exception.Response) { $status = [int]$_.Exception.Response.StatusCode }
        if ($status -in $Expected) { return [pscustomobject]@{ Status = $status; Data = $null } }
        throw "Azure $Audience request failed (HTTP $status). No response body or credentials are printed. Check permissions, quotas, schema and connectivity; maintain the outage until recovery."
    }
    if ([int]$response.StatusCode -notin $Expected) { throw "Unexpected Azure $Audience response status." }
    $data = $null
    if ($response.Content -and $response.Headers['Content-Type'] -match 'application/json') {
        try { $data = $response.Content | ConvertFrom-Json }
        catch { throw "Azure $Audience returned invalid JSON; response content is intentionally suppressed." }
    }
    return [pscustomobject]@{ Status = [int]$response.StatusCode; Data = $data }
}.GetNewClosure()
try {
    Invoke-KnowledgeSync -Plan $plan -Request $request -SearchRoot "https://$SearchName.search.windows.net" `
        -StorageRoot "https://$StorageAccountName.blob.core.windows.net/documents" -OpenAIRoot "https://$OpenAIName.openai.azure.com" `
        -EmbeddingDeployment $EmbeddingDeployment -CreateIndex:$CreateIndex
    Write-Output "Applied $($plan.documents.Count) approved operations. Verify ACL isolation and Search visibility before restarting the app."
} finally {
    $cache.Clear()
}
