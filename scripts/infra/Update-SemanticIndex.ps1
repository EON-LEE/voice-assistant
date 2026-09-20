[CmdletBinding()]
param(
    [string]$SchemaFile,
    [string]$IndexName = 'meeting-knowledge',
    [string]$SearchName,
    [string]$ResourceGroup,
    [string]$SubscriptionId = 'b0af194e-77a5-4471-bb43-67e78295b5c8',
    [string]$TenantId = '2573db8c-dfe5-4805-9e28-a0859692e705',
    [ValidateSet('AzureCli', 'AzPowerShell')][string]$AuthProvider = 'AzureCli',
    [string]$AzPath = 'az',
    [string[]]$AzPrefix = @(),
    [switch]$Apply
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'SemanticIndex.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'Knowledge.psm1')
$null = Get-KnowledgeSchema $IndexName
if (-not $Apply) {
    if (-not $SchemaFile) { throw 'Offline mode requires -SchemaFile containing existing index metadata and its ETag. No Azure request was made.' }
    try { $schema = Get-Content -LiteralPath $SchemaFile -Raw | ConvertFrom-Json }
    catch { throw 'Cannot read valid index metadata JSON; input contents are suppressed.' }
    $plan = New-SemanticIndexUpdate $schema $IndexName
    Write-Output "Offline semantic metadata validation passed; change needed: $($plan.Changed). No Azure request, reindex, document operation or deletion occurred."
    return
}
if ($SearchName -cnotmatch '^[a-z0-9][a-z0-9-]{1,58}[a-z0-9]$' -or [string]::IsNullOrWhiteSpace($ResourceGroup)) {
    throw 'Apply requires explicit SearchName and ResourceGroup.'
}
Import-Module (Join-Path $PSScriptRoot 'Common.psm1') -Force
Set-AzureCli -AuthProvider $AuthProvider -AzPath $AzPath -AzPrefix $AzPrefix -TimeoutSeconds 90
$null = Assert-Subscription $SubscriptionId $TenantId
$service = Invoke-AzJson @('search', 'service', 'show', '--name', $SearchName, '--resource-group', $ResourceGroup, '--subscription', $SubscriptionId)
if ($service.name -ne $SearchName -or -not $service.properties.PSObject.Properties['semanticSearch'] -or
    $service.properties.semanticSearch -notin @('free', 'standard')) {
    throw 'Search semantic ranker must be explicitly enabled (free or standard) before migration. No index was changed.'
}
$token = Invoke-AzJson @('account', 'get-access-token', '--subscription', $SubscriptionId, '--tenant', $TenantId, '--resource', 'https://search.azure.com')
$uri = "https://$SearchName.search.windows.net/indexes/$IndexName`?api-version=2024-07-01"
$request = {
    param($Method, $Body, $Headers)
    $headersCopy = @{ Authorization = "Bearer $($token.accessToken)" }
    foreach ($key in $Headers.Keys) { $headersCopy[$key] = $Headers[$key] }
    $arguments = @{ Method = $Method; Uri = $uri; Headers = $headersCopy; ContentType = 'application/json; charset=utf-8';
        TimeoutSec = 30; MaximumRedirection = 0; UseBasicParsing = $true; ErrorAction = 'Stop'; Verbose = $false; Debug = $false }
    if ($null -ne $Body) { $arguments.Body = [Text.Encoding]::UTF8.GetBytes([string]$Body) }
    try { $response = Invoke-WebRequest @arguments }
    catch {
        $status = 0
        if ($_.Exception.PSObject.Properties['Response'] -and $_.Exception.Response) { $status = [int]$_.Exception.Response.StatusCode }
        throw "Semantic index metadata request failed (HTTP $status). HTTP 412 means concurrent index change; no automatic retry. Inspect service permissions/availability; response and credentials suppressed."
    }
    return ConvertFrom-SemanticIndexResponse -Method $Method -Response $response
}.GetNewClosure()
try {
    $result = Invoke-SemanticIndexUpdate -Request $request -IndexName $IndexName
    Write-Output "Semantic metadata verified; changed: $($result.Changed). No documents, vectors or ACLs were modified; no reindex or private job run occurred."
} finally { $token = $null }
