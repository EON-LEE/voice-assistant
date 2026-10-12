[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot '..\SemanticIndex.psm1') -Force
Import-Module (Join-Path $PSScriptRoot '..\Knowledge.psm1')
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
        if ($_.Exception.Message -notmatch $Pattern) { throw "FAIL: $Name (unexpected error)" }
        $caught = $true
    }
    Assert-True $caught $Name
}
function Copy-Json($Value) { $Value | ConvertTo-Json -Depth 100 | ConvertFrom-Json }
Assert-SearchSemanticCapability ([pscustomobject]@{ name = 'fixture-search'; semanticSearch = 'free' }) 'fixture-search'
Assert-True $true 'Azure CLI flattened semantic capability is recognized'
Assert-SearchSemanticCapability ([pscustomobject]@{ name = 'fixture-search'; properties = [pscustomobject]@{ semanticSearch = 'standard' } }) 'fixture-search'
Assert-True $true 'Azure PowerShell nested ARM semantic capability is recognized'
Assert-SearchSemanticCapability ([pscustomobject]@{ name = 'fixture-search'; semanticSearch = 'free'; properties = [pscustomobject]@{ semanticSearch = 'free' } }) 'fixture-search'
Assert-True $true 'Matching dual-shape capability metadata is accepted'
Assert-Throws { Assert-SearchSemanticCapability ([pscustomobject]@{ name = 'fixture-search'; semanticSearch = 'free';
    properties = [pscustomobject]@{ semanticSearch = 'disabled' } }) 'fixture-search' } 'Conflicting' 'Conflicting provider shapes never fall back to enabled capability'
Assert-Throws { Assert-SearchSemanticCapability ([pscustomobject]@{ name = 'fixture-search' }) 'fixture-search' } 'explicitly enabled' 'Missing capability metadata fails cleanly under strict mode'
Assert-Throws { Assert-SearchSemanticCapability ([pscustomobject]@{ name = 'fixture-search'; properties = $null }) 'fixture-search' } 'explicitly enabled' 'Null ARM properties cannot imply semantic availability'
foreach ($value in @('disabled', 'unknown', '', 'Free')) {
    Assert-Throws { Assert-SearchSemanticCapability ([pscustomobject]@{ name = 'fixture-search'; semanticSearch = $value }) 'fixture-search' } 'explicitly enabled' "Disabled or unrecognized semantic state is rejected: '$value'"
}
Assert-Throws { Assert-SearchSemanticCapability ([pscustomobject]@{ name = 'other-search'; semanticSearch = 'free' }) 'fixture-search' } 'identity' 'Provider-shape normalization cannot change the explicit target service'
$schema = Get-KnowledgeSchema
$schema | Add-Member -NotePropertyName '@odata.etag' -NotePropertyValue '"fixture-etag"'
$schema.PSObject.Properties.Remove('semantic')
$schema | Add-Member -NotePropertyName scoringProfiles -NotePropertyValue @([pscustomobject]@{ name = 'preserve-scoring'; text = @{ weights = @{ title = 4 } } })
$schema | Add-Member -NotePropertyName suggesters -NotePropertyValue @()
$schema | Add-Member -NotePropertyName corsOptions -NotePropertyValue ([pscustomobject]@{ allowedOrigins = @('https://fixture.invalid'); maxAgeInSeconds = 300 })
$before = $schema | ConvertTo-Json -Depth 100 -Compress
Assert-Throws { Assert-KnowledgeSchema $schema } 'semantic' 'Existing indexes lacking semantic metadata fail strict ingestion validation'
$plan = New-SemanticIndexUpdate $schema
Assert-True ($plan.Changed -and $plan.ETag -ceq '"fixture-etag"' -and $plan.Schema.semantic.defaultConfiguration -eq 'meeting-semantic') 'Migration adds named configuration using exact existing ETag'
Assert-True (($schema | ConvertTo-Json -Depth 100 -Compress) -ceq $before) 'Migration planning never mutates original metadata'
$stripped = Copy-Json $plan.Schema
$stripped.PSObject.Properties.Remove('semantic')
Assert-True (($stripped | ConvertTo-Json -Depth 100 -Compress) -ceq $before) 'All existing fields, ACL/vector/analyzer/scoring/CORS settings remain untouched'
$again = New-SemanticIndexUpdate $plan.Schema
Assert-True (-not $again.Changed) 'Already-correct semantic configuration is an idempotent no-op'
$custom = Copy-Json $schema
$custom | Add-Member -NotePropertyName semantic -NotePropertyValue ([pscustomobject]@{
    defaultConfiguration = 'other-config'; configurations = @([pscustomobject]@{ name = 'other-config'; prioritizedFields = @{ titleField = @{ fieldName = 'title' } } })
})
$p = New-SemanticIndexUpdate $custom
Assert-True ($p.Schema.semantic.defaultConfiguration -eq 'other-config' -and $p.Schema.semantic.configurations.Count -eq 2 -and
    $p.Schema.semantic.configurations[0].name -eq 'other-config') 'Existing semantic defaults and unrelated configurations are preserved'
$bad = Copy-Json $plan.Schema
$bad.semantic.configurations[0].prioritizedFields.titleField.fieldName = 'content'
Assert-Throws { New-SemanticIndexUpdate $bad } 'priorities' 'Conflicting named semantic configuration requires explicit review, not overwrite'
$bad = Copy-Json $schema
$bad.PSObject.Properties.Remove('@odata.etag')
Assert-Throws { New-SemanticIndexUpdate $bad } 'ETag' 'Missing ETag prevents unconditional index replacement'
$bad = Copy-Json $schema
$bad.'@odata.etag' = '*'
Assert-Throws { New-SemanticIndexUpdate $bad } 'ETag' 'Wildcard ETag cannot bypass concurrency checks'
$bad = Copy-Json $schema
$bad.fields[8].dimensions = 3072
Assert-Throws { New-SemanticIndexUpdate $bad } 'dimensions' 'Vector contract mismatch prevents metadata migration'
$state = @{ schema = Copy-Json $schema; calls = (New-Object 'Collections.Generic.List[object]'); conflict = $false; drift = $false }
$request = {
    param($Method, $Body, $Headers)
    $state.calls.Add(@{ method = $Method; headers = $Headers }) > $null
    if ($Method -eq 'PUT') {
        if ($state.conflict) { throw 'HTTP 412 fixture concurrency conflict' }
        $state.schema = $Body | ConvertFrom-Json
        $state.schema.'@odata.etag' = '"updated-etag"'
        if ($state.drift) { $state.schema.corsOptions.maxAgeInSeconds = 600 }
    }
    return ($state.schema | ConvertTo-Json -Depth 100 | ConvertFrom-Json)
}.GetNewClosure()
$result = Invoke-SemanticIndexUpdate -Request $request
Assert-True ($result.Verified -and $result.Changed -and $state.calls.Count -eq 3 -and
    $state.calls[1].method -eq 'PUT' -and $state.calls[1].headers['If-Match'] -ceq '"fixture-etag"') 'Apply performs GET, conditional PUT, then verified GET only'
$state.calls.Clear()
$result = Invoke-SemanticIndexUpdate -Request $request
Assert-True (-not $result.Changed -and $state.calls.Count -eq 1) 'Idempotent apply makes no write'
$state.schema = Copy-Json $schema
$state.calls.Clear()
$state.conflict = $true
Assert-Throws { Invoke-SemanticIndexUpdate -Request $request } '412' 'Concurrent update fails explicitly without retry/reindex/delete'
Assert-True ($state.calls.Count -eq 2) 'ETag conflict is never automatically retried'
$state.conflict = $false
$state.drift = $true
$state.schema = Copy-Json $schema
Assert-Throws { Invoke-SemanticIndexUpdate -Request $request } 'metadata changed' 'Post-update verification detects unrelated setting drift'
$emptyResponse = [pscustomobject]@{ StatusCode = 204 }
Assert-True ($null -eq (ConvertFrom-SemanticIndexResponse 'PUT' $emptyResponse)) 'PUT 204 requires no Content property and does not attempt JSON parsing'
Assert-Throws { ConvertFrom-SemanticIndexResponse 'GET' $emptyResponse } 'Unexpected' 'GET 204 is not accepted as index metadata'
Assert-Throws { ConvertFrom-SemanticIndexResponse 'GET' ([pscustomobject]@{ StatusCode = 201; Content = '{}' }) } 'Unexpected' 'GET requires HTTP 200, never creation status'
Assert-Throws { ConvertFrom-SemanticIndexResponse 'GET' ([pscustomobject]@{ StatusCode = 200; Content = '' }) } 'Invalid' 'Empty GET metadata fails explicitly'
foreach ($status in @(200, 201)) {
    $response = [pscustomobject]@{ StatusCode = $status; Content = [Text.Encoding]::UTF8.GetBytes(($schema | ConvertTo-Json -Depth 100)) }
    Assert-True ((ConvertFrom-SemanticIndexResponse 'PUT' $response).name -eq 'meeting-knowledge') "PUT $status metadata JSON remains supported"
}
$state.schema = Copy-Json $schema
$state.calls.Clear()
$transportRequest = {
    param($Method, $Body, $Headers)
    $state.calls.Add(@{ method = $Method; headers = $Headers }) > $null
    if ($Method -eq 'PUT') {
        $state.schema = $Body | ConvertFrom-Json
        $state.schema.'@odata.etag' = '"etag-after-204"'
        return ConvertFrom-SemanticIndexResponse $Method ([pscustomobject]@{ StatusCode = 204 })
    }
    return ConvertFrom-SemanticIndexResponse $Method ([pscustomobject]@{
        StatusCode = 200; Content = ($state.schema | ConvertTo-Json -Depth 100)
    })
}.GetNewClosure()
$result = Invoke-SemanticIndexUpdate -Request $transportRequest
Assert-True ($result.Verified -and $result.Changed -and ($state.calls.method -join ',') -eq 'GET,PUT,GET') 'Realistic PUT 204 is followed by mandatory JSON GET preservation verification'
$file = [IO.Path]::GetTempFileName()
try {
    $schema | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $file -Encoding UTF8
    $result = & (Join-Path $PSScriptRoot '..\Update-SemanticIndex.ps1') -SchemaFile $file
    Assert-True ($result -match 'No Azure request') 'Default migration command validates metadata fully offline'
    Assert-Throws { & (Join-Path $PSScriptRoot '..\Update-SemanticIndex.ps1') } 'Offline mode requires' 'Missing offline input does not silently contact Azure'
} finally { Remove-Item -LiteralPath $file -Force }
Write-Output "All $script:passed semantic metadata checks passed. No Azure request or document operation occurred."
