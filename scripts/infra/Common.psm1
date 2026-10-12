Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'AzureProcess.psm1')
$script:azurePath = 'az'
$script:azurePrefix = @()
$script:azureTimeout = 120
$script:authProvider = 'AzureCli'

function Set-AzureCli {
    param([string]$AzPath = 'az', [string[]]$AzPrefix = @(), [ValidateRange(1, 1800)][int]$TimeoutSeconds = 120,
        [ValidateSet('AzureCli', 'AzPowerShell')][string]$AuthProvider = 'AzureCli')
    $script:azurePath = $AzPath
    $script:azurePrefix = $AzPrefix
    $script:azureTimeout = $TimeoutSeconds
    $script:authProvider = $AuthProvider
}

function Invoke-AzJson {
    param([Parameter(Mandatory)][string[]]$Arguments)
    $result = if ($script:authProvider -eq 'AzPowerShell') {
        Invoke-AzPowerShellCommand -Arguments $Arguments -TimeoutSeconds $script:azureTimeout
    } else {
        Invoke-BoundedJsonCommand -Path $script:azurePath -Prefix $script:azurePrefix `
            -Arguments ($Arguments + @('--only-show-errors', '--output', 'json')) -TimeoutSeconds $script:azureTimeout
    }
    if (-not $result.Succeeded) {
        $exception = New-Object InvalidOperationException("Azure operation failed ($($result.Code)). Check identity, explicit subscription and permissions. Responses are suppressed; a timed-out mutation may still be running in Azure.")
        $exception.Data['AzureOperationCode'] = $result.Code
        throw $exception
    }
    return $result.Data
}

function Assert-Subscription {
    param([string]$SubscriptionId, [string]$TenantId = '')
    $parsed = [guid]::Empty
    if (-not [guid]::TryParse($SubscriptionId, [ref]$parsed)) { throw 'SubscriptionId must be a GUID.' }
    $account = Invoke-AzJson @('account', 'show', '--subscription', $SubscriptionId)
    if ($account.id -ne $SubscriptionId -or $account.state -ne 'Enabled') { throw 'Explicit subscription is unavailable or disabled.' }
    if ($TenantId -and $account.tenantId -ne $TenantId) { throw 'Subscription tenant does not match the configured Entra tenant.' }
    return $account
}

function Assert-DeploymentParameters {
    param([Parameter(Mandatory)]$Parameters, [Parameter(Mandatory)]$Template)
    foreach ($property in $Parameters.parameters.PSObject.Properties) {
        if (-not $Template.parameters.PSObject.Properties[$property.Name]) { throw "Unknown deployment parameter: $($property.Name)." }
        if (-not $property.Value.PSObject.Properties['value']) { throw 'Only explicit parameter values are supported; no secret references are required.' }
        if (($property.Value.value | ConvertTo-Json -Depth 10 -Compress) -match 'REPLACE_') { throw "Unresolved example parameter: $($property.Name)." }
    }
    foreach ($property in $Template.parameters.PSObject.Properties) {
        $supplied = $Parameters.parameters.PSObject.Properties[$property.Name]
        if (-not $supplied -and -not $property.Value.PSObject.Properties['defaultValue']) { throw "Missing deployment parameter: $($property.Name)." }
        if ($supplied -and $property.Value.PSObject.Properties['allowedValues'] -and
            $supplied.Value.value -cnotin $property.Value.allowedValues) { throw "Unsupported deployment parameter: $($property.Name)." }
    }
    $p = $Parameters.parameters
    foreach ($name in @('tenantId', 'spaClientId', 'apiAudience')) {
        $guid = [guid]::Empty
        if (-not [guid]::TryParse($p.$name.value, [ref]$guid) -or $guid -eq [guid]::Empty) { throw "$name must be a nonzero GUID (API v2 tokens use the API application client ID as audience)." }
    }
    if ($p.spaClientId.value -eq $p.apiAudience.value) { throw 'Use separate SPA and API application registrations.' }
    if ($p.apiScope.value -notmatch '^api://[^/\s]+/Meeting\.Access$') { throw 'apiScope must be the exposed api://.../Meeting.Access scope.' }
    if ($p.namePrefix.value -cnotmatch '^[a-z][a-z0-9]{2,11}$') { throw 'namePrefix must be 3-12 lowercase alphanumeric characters, starting with a letter.' }
    if ($p.registryName.value -cnotmatch '^[a-z0-9]{5,50}$') { throw 'registryName must be the existing lowercase ACR name.' }
    $imagePattern = '^' + [regex]::Escape($p.registryName.value) + '\.azurecr\.io/[a-z0-9][a-z0-9._/-]*@sha256:[a-f0-9]{64}$'
    if ($p.image.value -cnotmatch $imagePattern) { throw 'image must be pinned to a sha256 digest in the specified Azure public-cloud ACR.' }
    foreach ($name in @('chatCapacity', 'embeddingCapacity')) {
        if ($p.$name.value -isnot [int] -and $p.$name.value -isnot [long]) { throw "$name must be an integer." }
        if ($p.$name.value -lt 1) { throw "$name must be positive." }
    }
    if ($p.PSObject.Properties['ingestionPrincipalId'] -and $p.ingestionPrincipalId.value) {
        $guid = [guid]::Empty
        if (-not [guid]::TryParse($p.ingestionPrincipalId.value, [ref]$guid) -or $guid -eq [guid]::Empty) { throw 'ingestionPrincipalId must be a nonzero object GUID.' }
    }
}

Export-ModuleMember -Function Set-AzureCli, Invoke-AzJson, Assert-Subscription, Assert-DeploymentParameters
