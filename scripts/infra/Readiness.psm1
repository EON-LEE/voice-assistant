Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'AzureProcess.psm1')
Import-Module (Join-Path $PSScriptRoot 'Common.psm1')

function Add-ReadinessCheck {
    param($Report, [string]$Id, [string]$Category, [ValidateSet('PASS', 'FAIL', 'BLOCKED')][string]$Status, [string]$Code)
    $Report.checks.Add([pscustomobject]@{ id = $Id; category = $Category; status = $Status; code = $Code }) > $null
}

function Complete-ReadinessReport {
    param($Report)
    $Report.overallStatus = if (@($Report.checks | Where-Object status -EQ 'FAIL').Count) { 'FAIL' }
        elseif (@($Report.checks | Where-Object status -EQ 'BLOCKED').Count) { 'BLOCKED' } else { 'PASS' }
    return $Report
}

function Test-BootstrapConfiguration {
    param($Config)
    $names = @('version', 'resourceGroup', 'location', 'registryName', 'registrySku', 'estimatedMonthlyCostUsd', 'estimateDateUtc', 'pricingReference')
    if ($null -eq $Config -or @($Config.PSObject.Properties).Count -ne $names.Count) { return $false }
    foreach ($name in $names) { if (-not $Config.PSObject.Properties[$name]) { return $false } }
    if ($Config.version -ne 1 -or $Config.resourceGroup -cnotmatch '^[a-z][a-z0-9-]{2,63}$' -or
        $Config.location -cnotmatch '^[a-z][a-z0-9]{1,31}$' -or $Config.registryName -cnotmatch '^[a-z0-9]{5,50}$' -or
        $Config.registrySku -cnotin @('Basic', 'Standard', 'Premium')) { return $false }
    return $true
}

function Test-CostReview {
    param($Config)
    if ($Config.estimatedMonthlyCostUsd -isnot [ValueType] -or $Config.estimatedMonthlyCostUsd -is [bool] -or
        [double]$Config.estimatedMonthlyCostUsd -le 0 -or [double]::IsInfinity([double]$Config.estimatedMonthlyCostUsd) -or
        [double]::IsNaN([double]$Config.estimatedMonthlyCostUsd) -or
        [string]::IsNullOrWhiteSpace($Config.pricingReference) -or $Config.pricingReference -match 'REPLACE_') { return $false }
    $date = [datetime]::MinValue
    if (-not [datetime]::TryParseExact($Config.estimateDateUtc, 'yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture,
        [Globalization.DateTimeStyles]::None, [ref]$date)) { return $false }
    return ($date -le [datetime]::UtcNow.Date -and $date -ge [datetime]::UtcNow.Date.AddDays(-30))
}

function Invoke-ReadinessAz {
    param($Options, [string[]]$Arguments, [int]$TimeoutSeconds = 30)
    if ($Options.ContainsKey('SelectedAuthProvider') -and $Options.SelectedAuthProvider -eq 'AzPowerShell') {
        return Invoke-AzPowerShellCommand -Arguments $Arguments -TimeoutSeconds $TimeoutSeconds
    }
    return Invoke-BoundedJsonCommand -Path $Options.AzPath -Prefix $Options.AzPrefix `
        -Arguments ($Arguments + @('--only-show-errors', '--output', 'json')) -TimeoutSeconds $TimeoutSeconds
}

function Get-ExistingIdentityChecks {
    param($Report, $Options)
    # Account metadata only: never request credentials or inspect credential/cache files.
    $accounts = Invoke-ReadinessAz $Options @('account', 'list', '--query', '[].{id:id,tenantId:tenantId,state:state}')
    $identityReady = $false
    if (-not $accounts.Succeeded) {
        Add-ReadinessCheck $Report 'identity.azureCli' 'identity' 'BLOCKED' $accounts.Code
    } else {
        $matched = @($accounts.Data | Where-Object { $_ -and $_.id -eq $Options.SubscriptionId -and $_.tenantId -eq $Options.TenantId -and $_.state -eq 'Enabled' })
        if ($matched.Count -eq 1) {
            Add-ReadinessCheck $Report 'identity.azureCli' 'identity' 'PASS' 'TARGET_ACCOUNT_AVAILABLE'
            $identityReady = $true
        } elseif (@($accounts.Data | Where-Object { $null -ne $_ }).Count -eq 0) {
            Add-ReadinessCheck $Report 'identity.azureCli' 'identity' 'BLOCKED' 'NO_EXISTING_ACCOUNT'
        } else {
            Add-ReadinessCheck $Report 'identity.azureCli' 'identity' 'FAIL' 'TARGET_SUBSCRIPTION_TENANT_UNAVAILABLE'
        }
    }
    $azd = Get-Command azd -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($azd) {
        $probe = Invoke-BoundedJsonCommand -Path $azd.Source -Arguments @('auth', 'login', '--check-status', '--no-prompt', '--output', 'json') -TimeoutSeconds 20
        $Report.identityAlternatives.Add([pscustomobject]@{ tool = 'azd'; status = $(if ($probe.Succeeded) { 'PASS' } else { 'BLOCKED' });
            code = $(if ($probe.Succeeded) { 'CHECK_STATUS_SUCCEEDED_NOT_CLI_AUTH' } else { $probe.Code }) }) > $null
    } else {
        $Report.identityAlternatives.Add([pscustomobject]@{ tool = 'azd'; status = 'BLOCKED'; code = 'TOOL_UNAVAILABLE' }) > $null
    }
    $probe = Invoke-BoundedJsonCommand -Path (Get-Process -Id $PID).Path -Arguments @('-NoProfile', '-NonInteractive',
        '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $PSScriptRoot 'Probe-AzPowerShell.ps1'),
        '-SubscriptionId', $Options.SubscriptionId, '-TenantId', $Options.TenantId) -TimeoutSeconds 45
    $alternativeReady = $probe.Succeeded -and $probe.Data -and $probe.Data.armAuthorized -eq $true
    $Report.identityAlternatives.Add([pscustomobject]@{ tool = 'Az.Accounts'; status = $(if ($alternativeReady) { 'PASS' } else { 'BLOCKED' });
        code = $(if ($alternativeReady) { 'EXISTING_IDENTITY_ARM_VERIFIED' } elseif (-not $probe.Succeeded) { $probe.Code } else { $probe.Data.code }) }) > $null
    if (($Options.AuthProvider -eq 'AzPowerShell' -or ($Options.AuthProvider -eq 'Auto' -and -not $identityReady)) -and $alternativeReady) {
        $cliCheck = @($Report.checks | Where-Object id -EQ 'identity.azureCli')[0]
        $Report.identityAlternatives.Add([pscustomobject]@{ tool = 'AzureCli'; status = $cliCheck.status; code = $cliCheck.code }) > $null
        $null = $Report.checks.Remove($cliCheck)
        Add-ReadinessCheck $Report 'identity.activeProvider' 'identity' 'PASS' 'AZ_POWERSHELL_ARM_VERIFIED'
        $Options.SelectedAuthProvider = 'AzPowerShell'
        $Report.selectedAuthProvider = 'AzPowerShell'
        return $true
    }
    if ($Options.AuthProvider -eq 'AzPowerShell') {
        Add-ReadinessCheck $Report 'identity.activeProvider' 'identity' 'BLOCKED' 'REQUESTED_AZ_POWERSHELL_AUTH_UNAVAILABLE'
        return $false
    }
    if ($identityReady) {
        $Options.SelectedAuthProvider = 'AzureCli'
        $Report.selectedAuthProvider = 'AzureCli'
    }
    return $identityReady
}

function Test-OwnedResource {
    param($Resource)
    return ($null -ne $Resource -and $Resource.PSObject.Properties['tags'] -and $Resource.tags -and
        $Resource.tags.PSObject.Properties['managedBy'] -and $Resource.tags.managedBy -ceq 'voice-assistant-bootstrap' -and
        $Resource.tags.PSObject.Properties['projectId'] -and $Resource.tags.projectId -ceq 'ff8f97b4-5db4-458e-99de-0d63cfc96a4a')
}

function Get-AzureReadiness {
    param(
        [string]$ConfigFile,
        [string]$ParametersFile,
        [string]$BicepPath = 'bicep',
        [string]$AzPath = 'az',
        [string[]]$AzPrefix = @(),
        [ValidateSet('Auto', 'AzureCli', 'AzPowerShell')][string]$AuthProvider = 'Auto',
        [string]$SubscriptionId = 'b0af194e-77a5-4471-bb43-67e78295b5c8',
        [string]$TenantId = '2573db8c-dfe5-4805-9e28-a0859692e705',
        [ValidateSet('Bootstrap', 'Application')][string]$Stage = 'Bootstrap',
        [switch]$Plan,
        [switch]$Check,
        [switch]$Apply,
        [switch]$CostApproved
    )
    $report = [pscustomobject]@{
        schemaVersion = 1; generatedAtUtc = [datetime]::UtcNow.ToString('o')
        mode = $(if ($Apply) { 'Apply' } elseif ($Check) { 'Check' } else { 'Plan' }); stage = $Stage
        target = @{ subscriptionId = $SubscriptionId; tenantId = $TenantId }
        overallStatus = 'BLOCKED'; execution = 'NotRequested'; liveApplicationVerified = $false; selectedAuthProvider = $null
        checks = (New-Object 'Collections.Generic.List[object]')
        identityAlternatives = (New-Object 'Collections.Generic.List[object]')
        plannedActions = @()
    }
    if (@($Plan, $Check, $Apply).Where({ $_ }).Count -gt 1 -or ($CostApproved -and -not $Apply)) {
        Add-ReadinessCheck $report 'configuration.flags' 'configuration' 'FAIL' 'CONFLICTING_FLAGS'
        return Complete-ReadinessReport $report
    }
    if ($SubscriptionId -ne 'b0af194e-77a5-4471-bb43-67e78295b5c8' -or $TenantId -ne '2573db8c-dfe5-4805-9e28-a0859692e705') {
        # Do not echo unvalidated target strings into the report.
        $report.target = $null
        Add-ReadinessCheck $report 'configuration.target' 'configuration' 'FAIL' 'UNAPPROVED_TARGET'
        return Complete-ReadinessReport $report
    }
    Add-ReadinessCheck $report 'configuration.target' 'configuration' 'PASS' 'APPROVED_TARGET'
    $config = $null
    if (-not $ConfigFile -or -not (Test-Path -LiteralPath $ConfigFile -PathType Leaf)) {
        Add-ReadinessCheck $report 'configuration.bootstrap' 'configuration' 'BLOCKED' 'CONFIGURATION_REQUIRED'
    } else {
        try { $config = Get-Content -LiteralPath $ConfigFile -Raw | ConvertFrom-Json }
        catch { Add-ReadinessCheck $report 'configuration.bootstrap' 'configuration' 'FAIL' 'INVALID_CONFIGURATION_JSON' }
        if ($config) {
            if (Test-BootstrapConfiguration $config) {
                Add-ReadinessCheck $report 'configuration.bootstrap' 'configuration' 'PASS' 'VALID'
                Add-ReadinessCheck $report 'configuration.cost' 'configuration' $(if (Test-CostReview $config) { 'PASS' } else { 'BLOCKED' }) 'RECENT_DOCUMENTED_COST_REVIEW_REQUIRED'
                $report.plannedActions = if ($Stage -eq 'Bootstrap') {
                    @('Create dedicated tagged resource group only if absent', 'Create tagged ACR only if absent; do not alter existing resources')
                } else { @('Verify existing image digest and ARM validation/what-if', 'Incremental application deployment only with Apply and cost approval') }
            } else {
                Add-ReadinessCheck $report 'configuration.bootstrap' 'configuration' 'FAIL' 'INVALID_OR_PLACEHOLDER_CONFIGURATION'
            }
        }
    }
    if ($Apply -and -not $CostApproved) { Add-ReadinessCheck $report 'configuration.costApproval' 'configuration' 'BLOCKED' 'EXPLICIT_COST_APPROVAL_REQUIRED' }
    if ($Stage -eq 'Application') {
        if (-not $ParametersFile -or -not (Test-Path -LiteralPath $ParametersFile -PathType Leaf)) {
            Add-ReadinessCheck $report 'configuration.application' 'configuration' 'BLOCKED' 'APPLICATION_PARAMETERS_REQUIRED'
        } else {
            try {
                $null = & (Join-Path $PSScriptRoot 'Deploy.ps1') -ParametersFile $ParametersFile -BicepPath $BicepPath
                $parameters = Get-Content -LiteralPath $ParametersFile -Raw | ConvertFrom-Json
                if (-not $config -or $parameters.parameters.tenantId.value -ne $TenantId -or
                    $parameters.parameters.registryName.value -ne $config.registryName -or
                    $parameters.parameters.registryResourceGroup.value -ne $config.resourceGroup) { throw 'Binding mismatch.' }
                Add-ReadinessCheck $report 'configuration.application' 'configuration' 'PASS' 'COMPILED_AND_BOUND_TO_BOOTSTRAP'
            } catch {
                Add-ReadinessCheck $report 'configuration.application' 'configuration' 'FAIL' 'APPLICATION_COMPILE_OR_PARAMETER_BINDING_FAILED'
            }
        }
    }
    if (-not ($Check -or $Apply)) {
        Add-ReadinessCheck $report 'identity.azureCli' 'identity' 'BLOCKED' 'NOT_PROBED_OFFLINE_PLAN'
        Add-ReadinessCheck $report 'cloud.availability' 'availability' 'BLOCKED' 'NOT_PROBED_OFFLINE_PLAN'
        return Complete-ReadinessReport $report
    }
    $options = @{ AzPath = $AzPath; AzPrefix = $AzPrefix; SubscriptionId = $SubscriptionId; TenantId = $TenantId; AuthProvider = $AuthProvider }
    if (-not (Get-ExistingIdentityChecks $report $options)) {
        Add-ReadinessCheck $report 'cloud.availability' 'availability' 'BLOCKED' 'IDENTITY_REQUIRED_NO_LOGIN_ATTEMPTED'
        if ($Apply) { $report.execution = 'Blocked' }
        return Complete-ReadinessReport $report
    }
    if (@($report.checks | Where-Object { $_.status -ne 'PASS' }).Count) {
        Add-ReadinessCheck $report 'cloud.availability' 'availability' 'BLOCKED' 'VALID_CONFIGURATION_REQUIRED'
        if ($Apply) { $report.execution = 'Blocked' }
        return Complete-ReadinessReport $report
    }
    $providerNames = if ($Stage -eq 'Bootstrap') { @('Microsoft.ContainerRegistry') } else {
        @('Microsoft.ContainerRegistry', 'Microsoft.App', 'Microsoft.ManagedIdentity', 'Microsoft.CognitiveServices', 'Microsoft.Search', 'Microsoft.Storage', 'Microsoft.Insights')
    }
    foreach ($provider in $providerNames) {
        $result = Invoke-ReadinessAz $options @('provider', 'show', '--namespace', $provider, '--subscription', $SubscriptionId, '--query', 'registrationState')
        if (-not $result.Succeeded -or $result.Data -ne 'Registered') {
            Add-ReadinessCheck $report "cloud.provider.$provider" 'availability' 'BLOCKED' 'PROVIDER_REGISTRATION_OR_ACCESS_REQUIRED'
        } else { Add-ReadinessCheck $report "cloud.provider.$provider" 'availability' 'PASS' 'REGISTERED' }
    }
    $exists = Invoke-ReadinessAz $options @('group', 'exists', '--name', $config.resourceGroup, '--subscription', $SubscriptionId)
    $groupMissing = $false
    $registryMissing = $true
    if (-not $exists.Succeeded -or $exists.Data -isnot [bool]) {
        Add-ReadinessCheck $report 'cloud.resourceGroup' 'availability' 'BLOCKED' 'RESOURCE_GROUP_READ_FAILED'
    } elseif (-not $exists.Data) {
        $groupMissing = $true
        Add-ReadinessCheck $report 'cloud.resourceGroup' 'availability' $(if ($Stage -eq 'Bootstrap') { 'PASS' } else { 'BLOCKED' }) 'BOOTSTRAP_CREATION_REQUIRED'
    } else {
        $group = Invoke-ReadinessAz $options @('group', 'show', '--name', $config.resourceGroup, '--subscription', $SubscriptionId)
        if (-not $group.Succeeded -or -not (Test-OwnedResource $group.Data) -or $group.Data.location -ne $config.location) {
            Add-ReadinessCheck $report 'cloud.resourceGroup' 'availability' 'FAIL' 'EXISTING_GROUP_NOT_OWNED_OR_LOCATION_MISMATCH'
        } else {
            Add-ReadinessCheck $report 'cloud.resourceGroup' 'availability' 'PASS' 'OWNED_GROUP_REUSED_UNCHANGED'
            $registries = Invoke-ReadinessAz $options @('acr', 'list', '--resource-group', $config.resourceGroup, '--subscription', $SubscriptionId)
            if (-not $registries.Succeeded) {
                Add-ReadinessCheck $report 'cloud.registry' 'availability' 'BLOCKED' 'REGISTRY_READ_FAILED'
            } else {
                $registry = @($registries.Data | Where-Object { $_ -and $_.name -eq $config.registryName })
                if ($registry.Count -eq 1) {
                    $registryMissing = $false
                    if (-not (Test-OwnedResource $registry[0]) -or $registry[0].location -ne $config.location -or
                        $registry[0].sku.name -ne $config.registrySku -or $registry[0].adminUserEnabled -ne $false -or
                        ($registry[0].PSObject.Properties['roleAssignmentMode'] -and $registry[0].roleAssignmentMode -ne 'LegacyRegistryPermissions')) {
                        Add-ReadinessCheck $report 'cloud.registry' 'availability' 'FAIL' 'EXISTING_REGISTRY_NOT_OWNED_OR_INCOMPATIBLE'
                    } else { Add-ReadinessCheck $report 'cloud.registry' 'availability' 'PASS' 'OWNED_REGISTRY_REUSED_UNCHANGED' }
                }
            }
        }
    }
    if ($registryMissing -and -not @($report.checks | Where-Object { $_.status -ne 'PASS' }).Count) {
        # Az context loading/token refresh plus ARM name lookup can exceed the usual 30-second read budget.
        $name = Invoke-ReadinessAz $options @('acr', 'check-name', '--name', $config.registryName, '--subscription', $SubscriptionId) 90
        if (-not $name.Succeeded) {
            Add-ReadinessCheck $report 'cloud.registry' 'availability' 'BLOCKED' "REGISTRY_NAME_CHECK_$($name.Code)"
        } elseif ($null -eq $name.Data -or
            (-not ($name.Data -is [Collections.IDictionary]) -and -not $name.Data.PSObject.Properties['nameAvailable']) -or
            ($name.Data -is [Collections.IDictionary] -and -not $name.Data.Contains('nameAvailable'))) {
            Add-ReadinessCheck $report 'cloud.registry' 'availability' 'BLOCKED' 'REGISTRY_NAME_CHECK_INVALID_RESPONSE'
        } elseif ($name.Data.nameAvailable -isnot [bool]) {
            Add-ReadinessCheck $report 'cloud.registry' 'availability' 'BLOCKED' 'REGISTRY_NAME_CHECK_INVALID_RESPONSE'
        } elseif (-not $name.Data.nameAvailable) {
            Add-ReadinessCheck $report 'cloud.registry' 'availability' 'BLOCKED' 'REGISTRY_NAME_UNAVAILABLE'
        } else {
            Add-ReadinessCheck $report 'cloud.registry' 'availability' $(if ($Stage -eq 'Bootstrap') { 'PASS' } else { 'BLOCKED' }) 'BOOTSTRAP_CREATION_REQUIRED'
        }
    }
    if (@($report.checks | Where-Object { $_.status -ne 'PASS' }).Count) {
        if ($Apply) { $report.execution = 'Blocked' }
        return Complete-ReadinessReport $report
    }
    if ($Stage -eq 'Application') {
        $image = $parameters.parameters.image.value
        $imageName = $image.Substring($image.IndexOf('/') + 1)
        $metadata = Invoke-ReadinessAz $options @('acr', 'repository', 'show', '--name', $config.registryName, '--image', $imageName, '--subscription', $SubscriptionId, '--query', 'digest')
        if (-not $metadata.Succeeded -or $metadata.Data -ne $image.Substring($image.IndexOf('@') + 1)) {
            Add-ReadinessCheck $report 'cloud.image' 'availability' 'BLOCKED' 'IMAGE_DIGEST_UNAVAILABLE_OR_UNAUTHORIZED'
            if ($Apply) { $report.execution = 'Blocked' }
            return Complete-ReadinessReport $report
        }
        Add-ReadinessCheck $report 'cloud.image' 'availability' 'PASS' 'EXISTING_DIGEST_VERIFIED'
        $deploy = @{ ParametersFile = $ParametersFile; BicepPath = $BicepPath; AzPath = $AzPath; AzPrefix = $AzPrefix
            SubscriptionId = $SubscriptionId; ResourceGroup = $config.resourceGroup; AuthProvider = $options.SelectedAuthProvider }
        try {
            $null = & (Join-Path $PSScriptRoot 'Deploy.ps1') @deploy -Validate
            $null = & (Join-Path $PSScriptRoot 'Deploy.ps1') @deploy -WhatIf
            Add-ReadinessCheck $report 'cloud.armPreview' 'availability' 'PASS' 'VALIDATE_AND_WHAT_IF_COMPLETED'
        } catch {
            Add-ReadinessCheck $report 'cloud.armPreview' 'availability' 'BLOCKED' 'ARM_VALIDATION_OR_WHAT_IF_FAILED_REVIEW_REQUIRED'
            if ($Apply) { $report.execution = 'Blocked' }
            return Complete-ReadinessReport $report
        }
    }
    if (-not $Apply) { return Complete-ReadinessReport $report }
    $tags = @('managedBy=voice-assistant-bootstrap', 'projectId=ff8f97b4-5db4-458e-99de-0d63cfc96a4a')
    if ($Stage -eq 'Bootstrap') {
        if ($groupMissing) {
            $created = Invoke-ReadinessAz $options (@('group', 'create', '--name', $config.resourceGroup, '--location', $config.location, '--tags') + $tags + @('--subscription', $SubscriptionId)) 120
            if (-not $created.Succeeded -or -not (Test-OwnedResource $created.Data)) {
                $report.execution = 'Unknown'
                Add-ReadinessCheck $report 'execution.bootstrap' 'mutation' 'FAIL' 'GROUP_CREATE_FAILED_OR_UNCONFIRMED_RECHECK_BEFORE_RETRY'
                return Complete-ReadinessReport $report
            }
        }
        if ($registryMissing) {
            $created = Invoke-ReadinessAz $options (@('acr', 'create', '--name', $config.registryName, '--resource-group', $config.resourceGroup,
                '--location', $config.location, '--sku', $config.registrySku, '--admin-enabled', 'false', '--tags') + $tags + @('--subscription', $SubscriptionId)) 300
            if (-not $created.Succeeded -or -not (Test-OwnedResource $created.Data) -or $created.Data.provisioningState -ne 'Succeeded') {
                $report.execution = 'Unknown'
                Add-ReadinessCheck $report 'execution.bootstrap' 'mutation' 'FAIL' 'REGISTRY_CREATE_FAILED_OR_UNCONFIRMED_RECHECK_BEFORE_RETRY'
                return Complete-ReadinessReport $report
            }
        }
    } else {
        try { $null = & (Join-Path $PSScriptRoot 'Deploy.ps1') @deploy -Apply }
        catch {
            $report.execution = 'Unknown'
            Add-ReadinessCheck $report 'execution.application' 'mutation' 'FAIL' 'APPLICATION_DEPLOYMENT_FAILED_OR_UNCONFIRMED_RECHECK_BEFORE_RETRY'
            return Complete-ReadinessReport $report
        }
    }
    $report.execution = 'Succeeded'
    Add-ReadinessCheck $report 'execution.result' 'mutation' 'PASS' 'REQUESTED_STAGE_COMPLETED_NOT_LIVE_APPLICATION_VERIFICATION'
    return Complete-ReadinessReport $report
}

Export-ModuleMember -Function Get-AzureReadiness
