Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# The approved Azure target (subscription, tenant, ownership tag) lives only in a git-ignored local file.
# Every online script reads it from there and fails closed when it is missing or still holds placeholders.
$script:missingMessage = 'The approved Azure target is not configured. Copy infra/release.example.json to ' +
    'infra/release.local.json (git-ignored) and fill in subscriptionId, tenantId and projectId. Online commands fail closed without it.'

function Get-ReleaseConfigPath {
    param([string]$Path)
    if (-not [string]::IsNullOrWhiteSpace($Path)) { return [IO.Path]::GetFullPath($Path) }
    if (-not [string]::IsNullOrWhiteSpace($env:VOICE_ASSISTANT_RELEASE_CONFIG)) {
        return [IO.Path]::GetFullPath($env:VOICE_ASSISTANT_RELEASE_CONFIG)
    }
    return [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\infra\release.local.json'))
}

function Read-ReleaseConfig {
    # Returns $null when the file is absent. Invalid content throws without echoing it.
    param([string]$Path)
    $file = Get-ReleaseConfigPath $Path
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { return $null }
    try { $config = Get-Content -LiteralPath $file -Raw | ConvertFrom-Json }
    catch { throw 'The release target configuration is not valid JSON. Its contents are not printed.' }
    if ($config -isnot [Management.Automation.PSCustomObject]) { throw 'The release target configuration must be a JSON object.' }
    return $config
}

function Test-SameGuid {
    param([string]$Left, [string]$Right)
    $first = [guid]::Empty
    $second = [guid]::Empty
    return ([guid]::TryParse($Left, [ref]$first) -and [guid]::TryParse($Right, [ref]$second) -and
        $first -ne [guid]::Empty -and $first -eq $second)
}

function Get-InvalidTargetFields {
    param($Config)
    $invalid = @()
    if (-not $Config.PSObject.Properties['version'] -or $Config.version -isnot [ValueType] -or $Config.version -is [bool] -or
        $Config.version -ne 1) { $invalid += 'version' }
    foreach ($name in @('subscriptionId', 'tenantId', 'projectId')) {
        $property = $Config.PSObject.Properties[$name]
        $guid = [guid]::Empty
        if (-not $property -or $property.Value -isnot [string] -or -not [guid]::TryParse($property.Value, [ref]$guid) -or
            $guid -eq [guid]::Empty) { $invalid += $name }
    }
    return $invalid
}

function Find-ApprovedTarget {
    # Status is Missing, Invalid or Valid. Configuration problems never throw here, so reports can classify them.
    param([string]$Path)
    try { $config = Read-ReleaseConfig $Path }
    catch { return [pscustomobject]@{ Status = 'Invalid'; Target = $null; InvalidFields = @('json') } }
    if ($null -eq $config) { return [pscustomobject]@{ Status = 'Missing'; Target = $null; InvalidFields = @() } }
    $invalid = @(Get-InvalidTargetFields $config)
    if ($invalid.Count) { return [pscustomobject]@{ Status = 'Invalid'; Target = $null; InvalidFields = $invalid } }
    $target = [pscustomobject]@{
        SubscriptionId = ([guid]$config.subscriptionId).ToString()
        TenantId = ([guid]$config.tenantId).ToString()
        ProjectId = ([guid]$config.projectId).ToString()
    }
    return [pscustomobject]@{ Status = 'Valid'; Target = $target; InvalidFields = @() }
}

function Get-ApprovedTarget {
    param([string]$Path)
    $found = Find-ApprovedTarget $Path
    if ($found.Status -eq 'Missing') { throw $script:missingMessage }
    if ($found.Status -eq 'Invalid') {
        throw "The release target configuration needs real values for: $($found.InvalidFields -join ', '). Values are not printed."
    }
    return $found.Target
}

function Resolve-ApprovedTarget {
    # Explicit -SubscriptionId/-TenantId may only restate the configured target; they never widen it.
    param([string]$SubscriptionId, [string]$TenantId, [string]$Path)
    $target = Get-ApprovedTarget $Path
    if ($SubscriptionId -and -not (Test-SameGuid $SubscriptionId $target.SubscriptionId)) {
        throw 'SubscriptionId does not match the approved target in the release configuration.'
    }
    if ($TenantId -and -not (Test-SameGuid $TenantId $target.TenantId)) {
        throw 'TenantId does not match the approved target in the release configuration.'
    }
    return $target
}

Export-ModuleMember -Function Get-ReleaseConfigPath, Read-ReleaseConfig, Test-SameGuid, Find-ApprovedTarget, Get-ApprovedTarget, Resolve-ApprovedTarget
