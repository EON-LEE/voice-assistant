Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'Knowledge.psm1')

function New-SemanticIndexUpdate {
    param([Parameter(Mandatory)]$Schema, [string]$IndexName = 'meeting-knowledge')
    Assert-KnowledgeSchema $Schema $IndexName -SkipSemanticValidation
    if (-not $Schema.PSObject.Properties['@odata.etag'] -or
        [string]::IsNullOrWhiteSpace($Schema.'@odata.etag') -or $Schema.'@odata.etag' -eq '*') {
        throw 'An exact existing index ETag is required; unconditional replacement is prohibited.'
    }
    # Clone the entire definition; retain every field, analyzer, vector and other index setting.
    $updated = $Schema | ConvertTo-Json -Depth 100 | ConvertFrom-Json
    $updated.PSObject.Properties.Remove('@odata.context')
    $desired = (Get-KnowledgeSchema $IndexName).semantic.configurations[0]
    if (-not $updated.PSObject.Properties['semantic'] -or $null -eq $updated.semantic) {
        $updated | Add-Member -NotePropertyName semantic -NotePropertyValue ([pscustomobject]@{ configurations = @() }) -Force
    }
    if (-not $updated.semantic.PSObject.Properties['configurations']) {
        $updated.semantic | Add-Member -NotePropertyName configurations -NotePropertyValue @()
    }
    $existing = @($updated.semantic.configurations | Where-Object name -CEQ 'meeting-semantic')
    if ($existing.Count -gt 1) { throw 'Duplicate semantic configurations require manual review.' }
    if ($existing.Count -eq 1) {
        Assert-KnowledgeSchema $updated $IndexName
        return [pscustomobject]@{ Changed = $false; ETag = $Schema.'@odata.etag'; Schema = $updated }
    }
    $updated.semantic.configurations = @($updated.semantic.configurations) + @($desired)
    # Do not replace another application's existing default configuration.
    if (-not $updated.semantic.PSObject.Properties['defaultConfiguration'] -or $null -eq $updated.semantic.defaultConfiguration) {
        $updated.semantic | Add-Member -NotePropertyName defaultConfiguration -NotePropertyValue 'meeting-semantic' -Force
    }
    Assert-KnowledgeSchema $updated $IndexName
    return [pscustomobject]@{ Changed = $true; ETag = $Schema.'@odata.etag'; Schema = $updated }
}

function Invoke-SemanticIndexUpdate {
    param([scriptblock]$Request, [string]$IndexName = 'meeting-knowledge')
    $current = & $Request 'GET' $null @{}
    $plan = New-SemanticIndexUpdate $current $IndexName
    if (-not $plan.Changed) { return [pscustomobject]@{ Changed = $false; Verified = $true } }
    $null = & $Request 'PUT' ($plan.Schema | ConvertTo-Json -Depth 100 -Compress) @{ 'If-Match' = $plan.ETag }
    $verified = & $Request 'GET' $null @{}
    Assert-KnowledgeSchema $verified $IndexName
    $expected = $plan.Schema | ConvertTo-Json -Depth 100 | ConvertFrom-Json
    foreach ($annotation in @('@odata.etag', '@odata.context')) {
        $expected.PSObject.Properties.Remove($annotation)
        $verified.PSObject.Properties.Remove($annotation)
    }
    # Compare every expected setting structurally; property ordering is not significant.
    Assert-SemanticSettingsPreserved $expected $verified
    return [pscustomobject]@{ Changed = $true; Verified = $true }
}

function Assert-SemanticSettingsPreserved {
    param($Expected, $Actual)
    if ($null -eq $Expected) { if ($null -ne $Actual) { throw 'Index metadata changed unexpectedly; do not retry blindly.' }; return }
    if ($Expected -is [pscustomobject]) {
        if ($null -eq $Actual) { throw 'Index metadata was lost.' }
        foreach ($property in $Expected.PSObject.Properties) {
            if (-not $Actual.PSObject.Properties[$property.Name]) { throw 'Index metadata was lost.' }
            Assert-SemanticSettingsPreserved $property.Value $Actual.($property.Name)
        }
    } elseif ($Expected -is [array]) {
        if (@($Actual).Count -ne $Expected.Count) { throw 'Index settings array changed unexpectedly.' }
        for ($i = 0; $i -lt $Expected.Count; $i++) { Assert-SemanticSettingsPreserved $Expected[$i] $Actual[$i] }
    } elseif ($Expected -cne $Actual) { throw 'Index metadata changed unexpectedly; inspect before retry.' }
}

Export-ModuleMember -Function New-SemanticIndexUpdate, Invoke-SemanticIndexUpdate
