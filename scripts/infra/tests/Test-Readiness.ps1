[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$scriptsRoot = Split-Path $PSScriptRoot -Parent
Import-Module (Join-Path $scriptsRoot 'Readiness.psm1') -Force
Import-Module (Join-Path $scriptsRoot 'AzureProcess.psm1')
$script:passed = 0
function Assert-True {
    param([bool]$Condition, [string]$Name)
    if (-not $Condition) { throw "FAIL: $Name" }
    $script:passed++
    Write-Output "PASS: $Name"
}
function Has-Check {
    param($Report, [string]$Id, [string]$Status, [string]$Code)
    return @($Report.checks | Where-Object { $_.id -eq $Id -and $_.status -eq $Status -and $_.code -eq $Code }).Count -eq 1
}
$hostPath = (Get-Process -Id $PID).Path
$fixture = Join-Path $PSScriptRoot 'fixtures\cli fixture.ps1'
$prefix = @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', $fixture)
$result = Invoke-BoundedJsonCommand -Path $hostPath -Prefix ($prefix + @('-Mode', 'arguments')) -Arguments @('argument with spaces', 'semi;colon', 'amp&ersand') -TimeoutSeconds 10
Assert-True ($result.Succeeded -and $result.Data.arguments.Count -eq 3 -and
    $result.Data.arguments[0] -ceq 'argument with spaces' -and $result.Data.arguments[1] -ceq 'semi;colon' -and
    $result.Data.arguments[2] -ceq 'amp&ersand') 'Executable and prefix/argument arrays preserve boundaries without shell injection'
$result = Invoke-BoundedJsonCommand -Path 'missing-readiness-cli-fixture.exe' -TimeoutSeconds 1
Assert-True (-not $result.Succeeded -and $result.Code -eq 'TOOL_UNAVAILABLE') 'Missing CLI returns explicit unavailable result'
$result = Invoke-BoundedJsonCommand -Path $hostPath -Prefix ($prefix + @('-Mode', 'invalid')) -TimeoutSeconds 10
Assert-True (-not $result.Succeeded -and $result.Code -eq 'INVALID_JSON' -and
    ($result | ConvertTo-Json) -notmatch 'PRIVATE_DIAGNOSTIC_SENTINEL') 'Malformed CLI output is redacted, not echoed'
$result = Invoke-BoundedJsonCommand -Path $hostPath -Prefix ($prefix + @('-Mode', 'failure')) -TimeoutSeconds 10
Assert-True (-not $result.Succeeded -and $result.Code -eq 'COMMAND_FAILED' -and
    ($result | ConvertTo-Json) -notmatch 'PRIVATE_DIAGNOSTIC_SENTINEL') 'Nonzero CLI stderr is redacted and cannot masquerade as success'
$watch = [Diagnostics.Stopwatch]::StartNew()
$result = Invoke-BoundedJsonCommand -Path $hostPath -Prefix ($prefix + @('-Mode', 'timeout')) -TimeoutSeconds 2
$watch.Stop()
Assert-True (-not $result.Succeeded -and $result.Code -eq 'TIMEOUT' -and $watch.Elapsed.TotalSeconds -lt 10) 'Hung CLI is killed by PID tree within a bounded timeout'
$result = Invoke-BoundedJsonCommand -Path $hostPath -Prefix ($prefix + @('-Mode', 'no-auth')) -TimeoutSeconds 10
Assert-True ($result.Succeeded -and @($result.Data | Where-Object { $null -ne $_ }).Count -eq 0) 'Empty account list is preserved as no identity'

$processModule = Get-Module Readiness
$originalProcess = & $processModule { (Get-Command Invoke-BoundedJsonCommand).ScriptBlock }
$originalDeployment = & $processModule { (Get-Command Invoke-ReadinessDeployment).ScriptBlock }
$temp = [IO.Path]::GetTempFileName()
$invalid = [IO.Path]::GetTempFileName()
$reportPath = [IO.Path]::GetTempFileName()
$config = [ordered]@{
    version = 1; resourceGroup = 'fixture-meeting-group'; location = 'eastus'; registryName = 'fixturemeetingacr'
    registrySku = 'Basic'; estimatedMonthlyCostUsd = 100; estimateDateUtc = [datetime]::UtcNow.ToString('yyyy-MM-dd')
    pricingReference = 'Synthetic offline fixture only; never a real price estimate'
}
function Save-Config { $config | ConvertTo-Json -Compress | Set-Content -LiteralPath $temp -Encoding UTF8 }
Save-Config
$scenario = @{
    account = 'target'; provider = 'Registered'; group = $false; registry = $false; owned = $true; nameAvailable = $true
    registrySku = 'Basic'; location = 'eastus'; adminEnabled = $false; registryMode = 'LegacyRegistryPermissions'; failCommand = ''
    failCode = 'COMMAND_FAILED'; nameTimeout = 0; previewFailure = ''; deployCalls = (New-Object 'Collections.Generic.List[object]')
    budgets = (New-Object 'Collections.Generic.List[object]')
    powershellAuth = $false; calls = (New-Object 'Collections.Generic.List[string]'); createdGroup = 0; createdRegistry = 0
}
& $processModule {
    param($Scenario)
    $script:readinessScenario = $Scenario
    function script:Invoke-BoundedJsonCommand {
        param($Path, $Prefix, $Arguments, $TimeoutSeconds)
        $state = $script:readinessScenario
        if ($Path -ne 'fixture az.exe') {
            return [pscustomobject]@{ Succeeded = $true; Code = 'OK'; Data = @{ armAuthorized = $state.powershellAuth; code = 'NO_TARGET_CONTEXT' } }
        }
        $line = $Arguments -join ' '
        $state.calls.Add($line) > $null
        $state.budgets.Add(@{ command = $line; seconds = $TimeoutSeconds }) > $null
        if ($line.StartsWith('acr check-name')) { $state.nameTimeout = $TimeoutSeconds }
        if ($state.failCommand -and $line.StartsWith($state.failCommand)) {
            return [pscustomobject]@{ Succeeded = $false; Code = $state.failCode; Data = 'PRIVATE_DIAGNOSTIC_SENTINEL' }
        }
        $tags = [pscustomobject]@{ managedBy = 'voice-assistant-bootstrap'; projectId = 'ff8f97b4-5db4-458e-99de-0d63cfc96a4a' }
        if (-not $state.owned) { $tags.managedBy = 'unrelated-project' }
        $group = [pscustomobject]@{ name = 'fixture-meeting-group'; location = $state.location; tags = $tags }
        $registry = [pscustomobject]@{ name = 'fixturemeetingacr'; location = $state.location; tags = $tags;
            sku = @{ name = $state.registrySku }; adminUserEnabled = $state.adminEnabled; provisioningState = 'Succeeded'; roleAssignmentMode = $state.registryMode }
        $data = $null
        if ($line.StartsWith('account list')) {
            if ($state.account -ne 'none') {
                $data = @([pscustomobject]@{ id = 'b0af194e-77a5-4471-bb43-67e78295b5c8'; tenantId = '2573db8c-dfe5-4805-9e28-a0859692e705'; state = 'Enabled' })
                if ($state.account -eq 'wrong-subscription') { $data[0].id = '11111111-1111-4111-8111-111111111111' }
                if ($state.account -eq 'wrong-tenant') { $data[0].tenantId = '11111111-1111-4111-8111-111111111111' }
                if ($state.account -eq 'disabled') { $data[0].state = 'Disabled' }
            }
        } elseif ($line.StartsWith('provider show')) { $data = $state.provider }
        elseif ($line.StartsWith('group exists')) { $data = $state.group }
        elseif ($line.StartsWith('group show')) { $data = $group }
        elseif ($line.StartsWith('acr list')) { $data = @(); if ($state.registry) { $data = @($registry) } }
        elseif ($line.StartsWith('acr check-name')) { $data = @{ nameAvailable = $state.nameAvailable } }
        elseif ($line.StartsWith('acr repository show')) { $data = 'sha256:' + ('a' * 64) }
        elseif ($line.StartsWith('group create')) { $state.group = $true; $state.createdGroup++; $data = $group }
        elseif ($line.StartsWith('acr create')) { $state.registry = $true; $state.createdRegistry++; $data = $registry }
        else { throw 'Unexpected command in isolated readiness fixture.' }
        return [pscustomobject]@{ Succeeded = $true; Code = 'OK'; Data = $data }
    }
    function script:Invoke-ReadinessDeployment {
        param([hashtable]$Arguments)
        $state = $script:readinessScenario
        $state.deployCalls.Add($Arguments) > $null
        if (($Arguments.ContainsKey('Validate') -or $Arguments.ContainsKey('WhatIf')) -and $state.previewFailure) {
            $exception = New-Object InvalidOperationException('PRIVATE_DIAGNOSTIC_SENTINEL')
            $exception.Data['AzureOperationCode'] = $state.previewFailure
            throw $exception
        }
    }
} $scenario
try {
    $argsBase = @{ ConfigFile = $temp; AzPath = 'fixture az.exe'; AzPrefix = @('-m', 'azure.cli') }
    $report = Get-AzureReadiness @argsBase
    Assert-True ($report.mode -eq 'Plan' -and $report.overallStatus -eq 'BLOCKED' -and $scenario.calls.Count -eq 0) 'Default plan is offline and does not claim authenticated readiness'
    $report = Get-AzureReadiness @argsBase -Plan -Apply
    Assert-True (Has-Check $report 'configuration.flags' 'FAIL' 'CONFLICTING_FLAGS') 'Plan/Apply conflict fails before authentication'
    $report = Get-AzureReadiness @argsBase -Check -CostApproved
    Assert-True (Has-Check $report 'configuration.flags' 'FAIL' 'CONFLICTING_FLAGS') 'Cost approval is valid only with Apply'
    $report = Get-AzureReadiness @argsBase -SubscriptionId 'PRIVATE_DIAGNOSTIC_SENTINEL'
    Assert-True ((Has-Check $report 'configuration.target' 'FAIL' 'UNAPPROVED_TARGET') -and
        ($report | ConvertTo-Json -Depth 10) -notmatch 'PRIVATE_DIAGNOSTIC_SENTINEL') 'Unapproved subscription is rejected and not copied into diagnostics'
    $report = Get-AzureReadiness @argsBase -TenantId '11111111-1111-4111-8111-111111111111'
    Assert-True (Has-Check $report 'configuration.target' 'FAIL' 'UNAPPROVED_TARGET') 'Unapproved tenant is rejected before any probes'
    $scenario.account = 'none'
    $report = Get-AzureReadiness @argsBase -Check
    Assert-True ((Has-Check $report 'identity.azureCli' 'BLOCKED' 'NO_EXISTING_ACCOUNT') -and
        (Has-Check $report 'cloud.availability' 'BLOCKED' 'IDENTITY_REQUIRED_NO_LOGIN_ATTEMPTED')) 'No existing authentication is BLOCKED, not a cloud outage or fake success'
    Assert-True (@($scenario.calls | Where-Object { $_ -notmatch '^account list' }).Count -eq 0) 'No cloud/resource writes or login attempted without existing identity'
    $scenario.calls.Clear()
    $report = Get-AzureReadiness -Check -AzPath 'fixture az.exe'
    Assert-True ((Has-Check $report 'configuration.bootstrap' 'BLOCKED' 'CONFIGURATION_REQUIRED') -and
        (Has-Check $report 'identity.azureCli' 'BLOCKED' 'NO_EXISTING_ACCOUNT')) 'Missing inputs and missing identity are reported independently'
    $scenario.powershellAuth = $true
    $report = Get-AzureReadiness -Check -AzPath 'fixture az.exe'
    Assert-True ($report.selectedAuthProvider -eq 'AzPowerShell' -and
        (Has-Check $report 'identity.activeProvider' 'PASS' 'AZ_POWERSHELL_ARM_VERIFIED') -and
        -not (Has-Check $report 'identity.azureCli' 'BLOCKED' 'NO_EXISTING_ACCOUNT')) 'Verified existing Az identity is usable even when CLI has no account'
    $report = Get-AzureReadiness -Check -AzPath 'fixture az.exe' -AuthProvider AzureCli
    Assert-True (Has-Check $report 'identity.azureCli' 'BLOCKED' 'NO_EXISTING_ACCOUNT') 'Explicit CLI-only selection does not silently change providers'
    $scenario.powershellAuth = $false
    foreach ($account in @('wrong-subscription', 'wrong-tenant', 'disabled')) {
        $scenario.account = $account
        $report = Get-AzureReadiness @argsBase -Check
        Assert-True (Has-Check $report 'identity.azureCli' 'FAIL' 'TARGET_SUBSCRIPTION_TENANT_UNAVAILABLE') "Existing account mismatch fails closed: $account"
    }
    $scenario.account = 'target'
    $config.estimatedMonthlyCostUsd = 0
    Save-Config
    $report = Get-AzureReadiness @argsBase -Apply -CostApproved
    Assert-True ($report.execution -eq 'Blocked' -and (Has-Check $report 'configuration.cost' 'BLOCKED' 'RECENT_DOCUMENTED_COST_REVIEW_REQUIRED')) 'Apply cannot create resources with unknown costs'
    $config.estimatedMonthlyCostUsd = 100
    $config.estimateDateUtc = [datetime]::UtcNow.AddDays(-31).ToString('yyyy-MM-dd')
    Save-Config
    $report = Get-AzureReadiness @argsBase -Check
    Assert-True (Has-Check $report 'configuration.cost' 'BLOCKED' 'RECENT_DOCUMENTED_COST_REVIEW_REQUIRED') 'Stale cost review requires fresh approval'
    $config.estimateDateUtc = [datetime]::UtcNow.ToString('yyyy-MM-dd')
    Save-Config
    $report = Get-AzureReadiness @argsBase -Apply
    Assert-True (Has-Check $report 'configuration.costApproval' 'BLOCKED' 'EXPLICIT_COST_APPROVAL_REQUIRED') 'Valid estimate alone does not authorize Apply'
    $scenario.calls.Clear()
    $report = Get-AzureReadiness @argsBase -Check
    Assert-True ($report.overallStatus -eq 'PASS' -and $report.execution -eq 'NotRequested' -and -not $report.liveApplicationVerified -and
        @($scenario.calls | Where-Object { $_ -match ' create ' }).Count -eq 0) 'Read-only bootstrap check plans absent resources without creating or claiming live app'
    $scenario.provider = 'NotRegistered'
    $report = Get-AzureReadiness @argsBase -Apply -CostApproved
    Assert-True ($report.execution -eq 'Blocked' -and $scenario.createdGroup -eq 0) 'Provider registration is an availability blocker, never auto-registered'
    $scenario.provider = 'Registered'
    $scenario.nameAvailable = $false
    $report = Get-AzureReadiness @argsBase -Apply -CostApproved
    Assert-True (Has-Check $report 'cloud.registry' 'BLOCKED' 'REGISTRY_NAME_UNAVAILABLE') 'Occupied registry name cannot be adopted'
    $scenario.nameAvailable = $true
    $scenario.failCommand = 'acr check-name'
    $scenario.failCode = 'TIMEOUT'
    $report = Get-AzureReadiness @argsBase -Apply -CostApproved
    Assert-True ((Has-Check $report 'cloud.registry' 'BLOCKED' 'REGISTRY_NAME_CHECK_TIMEOUT') -and
        $report.execution -eq 'Blocked' -and $scenario.createdGroup -eq 0) 'Name-check timeout is explicitly unverified, not name occupied, and cannot create a group'
    Assert-True ($scenario.nameTimeout -eq 90) 'Registry lookup has a bounded 90-second Az initialization/ARM request budget'
    $scenario.failCommand = ''
    $scenario.failCode = 'COMMAND_FAILED'
    $report = Get-AzureReadiness @argsBase -Apply -CostApproved
    Assert-True ($report.overallStatus -eq 'PASS' -and $report.execution -eq 'Succeeded' -and
        $scenario.createdGroup -eq 1 -and $scenario.createdRegistry -eq 1) 'Approved authenticated bootstrap creates only dedicated RG and ACR'
    $report = Get-AzureReadiness @argsBase -Apply -CostApproved
    Assert-True ($report.overallStatus -eq 'PASS' -and $scenario.createdGroup -eq 1 -and $scenario.createdRegistry -eq 1) 'Repeated bootstrap reuses owned resources unchanged'
    Assert-True (@($scenario.calls | Where-Object { $_ -notmatch '^account list' -and
        $_ -notmatch '--subscription b0af194e-77a5-4471-bb43-67e78295b5c8' }).Count -eq 0) 'Every resource command is explicitly subscription-scoped'
    $scenario.owned = $false
    $report = Get-AzureReadiness @argsBase -Apply -CostApproved
    Assert-True ((Has-Check $report 'cloud.resourceGroup' 'FAIL' 'EXISTING_GROUP_NOT_OWNED_OR_LOCATION_MISMATCH') -and
        $scenario.createdGroup -eq 1) 'Unrelated existing group is never retagged or changed'
    $scenario.owned = $true
    $scenario.adminEnabled = $true
    $report = Get-AzureReadiness @argsBase -Apply -CostApproved
    Assert-True (Has-Check $report 'cloud.registry' 'FAIL' 'EXISTING_REGISTRY_NOT_OWNED_OR_INCOMPATIBLE') 'Registry admin credentials are rejected rather than silently disabled'
    $scenario.adminEnabled = $false
    $scenario.registryMode = 'AbacRepositoryPermissions'
    $report = Get-AzureReadiness @argsBase -Check
    Assert-True (Has-Check $report 'cloud.registry' 'FAIL' 'EXISTING_REGISTRY_NOT_OWNED_OR_INCOMPATIBLE') 'ABAC registries cannot falsely pass AcrPull deployment readiness'
    $scenario.registryMode = 'LegacyRegistryPermissions'
    $scenario.failCommand = 'group exists'
    $report = Get-AzureReadiness @argsBase -Check
    Assert-True ((Has-Check $report 'cloud.resourceGroup' 'BLOCKED' 'RESOURCE_GROUP_READ_COMMAND_FAILED') -and
        ($report | ConvertTo-Json -Depth 12) -notmatch 'PRIVATE_DIAGNOSTIC_SENTINEL') 'Cloud authorization/connectivity failure is not treated as absent resource'
    $scenario.failCode = 'TIMEOUT'
    foreach ($test in @(
        @{ command = 'provider show'; id = 'cloud.provider.Microsoft.ContainerRegistry'; code = 'PROVIDER_READ_TIMEOUT' },
        @{ command = 'group exists'; id = 'cloud.resourceGroup'; code = 'RESOURCE_GROUP_READ_TIMEOUT' },
        @{ command = 'group show'; id = 'cloud.resourceGroup'; code = 'RESOURCE_GROUP_READ_TIMEOUT' },
        @{ command = 'acr list'; id = 'cloud.registry'; code = 'REGISTRY_READ_TIMEOUT' }
    )) {
        $scenario.failCommand = $test.command
        $report = Get-AzureReadiness @argsBase -Apply -CostApproved
        Assert-True ((Has-Check $report $test.id 'BLOCKED' $test.code) -and $report.execution -eq 'Blocked' -and
            $scenario.createdGroup -eq 1 -and $scenario.createdRegistry -eq 1) "Readonly timeout preserves diagnostics and cannot mutate: $($test.command)"
    }
    $scenario.failCode = 'COMMAND_FAILED'
    $scenario.failCommand = ''
    $scenario.registry = $false
    $scenario.failCommand = 'acr create'
    $report = Get-AzureReadiness @argsBase -Apply -CostApproved
    Assert-True ($report.execution -eq 'Unknown' -and $report.overallStatus -eq 'FAIL') 'Unconfirmed cloud mutation cannot report success and requires recheck'
    $scenario.failCommand = ''
    $report = Get-AzureReadiness @argsBase -Stage Application -Check
    Assert-True (Has-Check $report 'configuration.application' 'BLOCKED' 'APPLICATION_PARAMETERS_REQUIRED') 'Bootstrap success does not substitute missing API/SPA/image/model application configuration'
    Assert-True (Has-Check $report 'configuration.costScope' 'BLOCKED' 'APPLICATION_COST_REVIEW_REQUIRED') 'Legacy registry-only cost review cannot authorize the Application stage'
    $config.costScope = 'Bootstrap'
    Save-Config
    $report = Get-AzureReadiness @argsBase -Stage Application -Apply -CostApproved
    Assert-True ((Has-Check $report 'configuration.costScope' 'BLOCKED' 'APPLICATION_COST_REVIEW_REQUIRED') -and
        $report.execution -eq 'Blocked') 'Explicit bootstrap cost approval cannot approve full application costs'
    $config.costScope = 'Application'
    Save-Config
    $report = Get-AzureReadiness @argsBase -Stage Application -Check
    Assert-True ((Has-Check $report 'configuration.costScope' 'PASS' 'APPLICATION_COST_SCOPE_DECLARED') -and
        (Has-Check $report 'configuration.application' 'BLOCKED' 'APPLICATION_PARAMETERS_REQUIRED')) 'Application-scope review passes only its cost gate, not missing deployment inputs'
    $report = Get-AzureReadiness @argsBase -Stage Bootstrap -Check
    Assert-True ($report.overallStatus -eq 'PASS') 'Full application cost review also covers the included bootstrap resources'
    $config.costScope = 'Any'
    Save-Config
    $report = Get-AzureReadiness @argsBase -Plan
    Assert-True (Has-Check $report 'configuration.bootstrap' 'FAIL' 'INVALID_OR_PLACEHOLDER_CONFIGURATION') 'Unknown cost scope cannot silently fall back to bootstrap'
    $config.costScope = 'Application'
    Save-Config
    $scenario.registry = $true
    @{ parameters = @{
        tenantId = @{ value = '2573db8c-dfe5-4805-9e28-a0859692e705' }
        registryName = @{ value = $config.registryName }; registryResourceGroup = @{ value = $config.resourceGroup }
        image = @{ value = $config.registryName + '.azurecr.io/fixture@sha256:' + ('a' * 64) }
    } } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $invalid
    $scenario.failCommand = 'acr repository show'
    $scenario.failCode = 'TIMEOUT'
    $report = Get-AzureReadiness @argsBase -Stage Application -ParametersFile $invalid -Apply -CostApproved
    Assert-True ((Has-Check $report 'cloud.image' 'BLOCKED' 'IMAGE_DIGEST_READ_TIMEOUT') -and
        $report.execution -eq 'Blocked' -and @($scenario.deployCalls | Where-Object { $_.ContainsKey('Apply') }).Count -eq 0) 'Image read timeout cannot reach application deployment'
    $scenario.failCommand = ''
    $scenario.previewFailure = 'TIMEOUT'
    $report = Get-AzureReadiness @argsBase -Stage Application -ParametersFile $invalid -Apply -CostApproved
    Assert-True ((Has-Check $report 'cloud.armPreview' 'BLOCKED' 'ARM_PREVIEW_TIMEOUT') -and
        $report.execution -eq 'Blocked' -and ($report | ConvertTo-Json -Depth 10) -notmatch 'PRIVATE_DIAGNOSTIC_SENTINEL') 'Structured ARM preview timeout survives redaction without becoming generic failure'
    $scenario.previewFailure = ''
    $report = Get-AzureReadiness @argsBase -Stage Application -ParametersFile $invalid -Apply -CostApproved
    Assert-True ($report.execution -eq 'Succeeded' -and
        @($scenario.deployCalls | Where-Object { $_.ContainsKey('Apply') -and $_.CommandTimeoutSeconds -eq 900 }).Count -eq 1) 'Successful checks retain separate bounded mutation budget with no automatic retries'
    Assert-True (@($scenario.budgets | Where-Object { $_.command -notmatch '^(group|acr) create ' -and $_.seconds -ne 90 }).Count -eq 0 -and
        @($scenario.deployCalls | Where-Object { ($_.ContainsKey('Validate') -or $_.ContainsKey('WhatIf')) -and $_.CommandTimeoutSeconds -ne 90 }).Count -eq 0) 'All readiness ARM/registry/image reads and previews use centralized 90-second budget'
    '{"version":1,"private":"PRIVATE_DIAGNOSTIC_SENTINEL"' | Set-Content -LiteralPath $invalid
    $report = Get-AzureReadiness -ConfigFile $invalid
    Assert-True ((Has-Check $report 'configuration.bootstrap' 'FAIL' 'INVALID_CONFIGURATION_JSON') -and
        ($report | ConvertTo-Json -Depth 12) -notmatch 'PRIVATE_DIAGNOSTIC_SENTINEL') 'Malformed input diagnostics never echo configuration text'
} finally {
    & $processModule { param($Original) Set-Item Function:script:Invoke-BoundedJsonCommand $Original } $originalProcess
    & $processModule { param($Original) Set-Item Function:script:Invoke-ReadinessDeployment $Original } $originalDeployment
    foreach ($file in @($temp, $invalid, $reportPath)) { Remove-Item -LiteralPath $file -Force }
}
$modulePath = $env:PSModulePath
$oldLog = $env:VOICE_INFRA_FIXTURE_LOG
$oldMode = $env:VOICE_INFRA_FIXTURE_MODE
$log = [IO.Path]::GetTempFileName()
$templateFile = [IO.Path]::GetTempFileName()
$parameterFile = [IO.Path]::GetTempFileName()
try {
    $env:VOICE_INFRA_FIXTURE_LOG = $log
    $env:PSModulePath = (Join-Path $PSScriptRoot 'fixtures') + [IO.Path]::PathSeparator + $modulePath
    $env:VOICE_INFRA_FIXTURE_MODE = ''
    '{}' | Set-Content -LiteralPath $templateFile
    '{"parameters":{}}' | Set-Content -LiteralPath $parameterFile
    $sub = @('--subscription', 'b0af194e-77a5-4471-bb43-67e78295b5c8')
    $r = Invoke-AzPowerShellCommand (@('account', 'show') + $sub) 15
    Assert-True ($r.Succeeded -and $r.Data.state -eq 'Enabled' -and (Get-Item $log).Length -gt 0) 'Az provider uses isolated module fixture and preserves target account'
    $r = Invoke-AzPowerShellCommand (@('group', 'exists', '--name', 'fixture-missing') + $sub) 15
    Assert-True ($r.Succeeded -and $r.Data -eq $false) 'Az provider distinguishes a real 404 from access errors'
    $r = Invoke-AzPowerShellCommand (@('provider', 'show', '--namespace', 'Microsoft.ContainerRegistry') + $sub) 15
    Assert-True ($r.Succeeded -and $r.Data -eq 'Registered') 'Az provider returns registration status without global context changes'
    $tags = @('--tags', 'managedBy=voice-assistant-bootstrap', 'projectId=ff8f97b4-5db4-458e-99de-0d63cfc96a4a')
    $r = Invoke-AzPowerShellCommand (@('group', 'create', '--name', 'fixture-meeting-group', '--location', 'eastus') + $tags + $sub) 15
    Assert-True ($r.Succeeded -and $r.Data.tags.managedBy -eq 'voice-assistant-bootstrap') 'Az provider creates exact tagged group using fixture ARM PUT'
    $r = Invoke-AzPowerShellCommand (@('acr', 'create', '--name', 'fixturemeetingacr', '--resource-group', 'fixture-meeting-group',
        '--location', 'eastus', '--sku', 'Basic') + $tags + $sub) 15
    $entries = @(Get-Content -LiteralPath $log | ForEach-Object { $_ | ConvertFrom-Json })
    $registryPut = @($entries | Where-Object { $_.method -eq 'PUT' -and $_.path -match '/registries/' })[0]
    $registryBody = $registryPut.payload | ConvertFrom-Json
    Assert-True ($r.Succeeded -and $registryBody.sku.name -eq 'Basic' -and $registryBody.properties.adminUserEnabled -eq $false -and
        $registryBody.properties.roleAssignmentMode -eq 'LegacyRegistryPermissions') 'Az registry PUT disables admin credentials and explicitly selects RBAC mode'
    $r = Invoke-AzPowerShellCommand (@('acr', 'list', '--resource-group', 'fixture-meeting-group') + $sub) 15
    Assert-True ($r.Succeeded -and @($r.Data)[0].roleAssignmentMode -eq 'LegacyRegistryPermissions') 'Az registry reads preserve authorization-mode evidence'
    $deployment = @('--name', 'fixture-deployment', '--resource-group', 'fixture-meeting-group', '--mode', 'Incremental',
        '--template-file', $templateFile, '--parameters', ('@' + $parameterFile)) + $sub
    $r = Invoke-AzPowerShellCommand (@('deployment', 'group', 'validate') + $deployment) 15
    Assert-True ($r.Succeeded -and $r.Data.properties.provisioningState -eq 'Succeeded') 'Az deployment validation maps explicit compiled template and parameters'
    $r = Invoke-AzPowerShellCommand (@('deployment', 'group', 'what-if') + $deployment) 15
    Assert-True ($r.Succeeded -and $r.Data.changes[0].changeType -eq 'Create') 'Az what-if follows a bounded subscription-scoped asynchronous result'
    $r = Invoke-AzPowerShellCommand (@('deployment', 'group', 'create') + $deployment) 15
    Assert-True ($r.Succeeded -and $r.Data.properties.provisioningState -eq 'Succeeded') 'Az deployment create polls until terminal ARM status'
    $r = Invoke-AzPowerShellCommand @('group', 'create', '--name', 'fixture-meeting-group', '--subscription', '11111111-1111-4111-8111-111111111111') 15
    Assert-True (-not $r.Succeeded) 'Az provider rejects a different subscription before executing any request'
    $env:VOICE_INFRA_FIXTURE_MODE = 'failure'
    $r = Invoke-AzPowerShellCommand (@('group', 'exists', '--name', 'fixture-missing') + $sub) 15
    Assert-True (-not $r.Succeeded -and ($r | ConvertTo-Json -Depth 10) -notmatch 'PRIVATE_DIAGNOSTIC_SENTINEL') 'Az provider exceptions remain explicit failures without raw provider content'
} finally {
    $env:PSModulePath = $modulePath
    $env:VOICE_INFRA_FIXTURE_LOG = $oldLog
    $env:VOICE_INFRA_FIXTURE_MODE = $oldMode
    foreach ($file in @($log, $templateFile, $parameterFile)) { Remove-Item -LiteralPath $file -Force }
}
Write-Output "All $script:passed readiness checks passed. Authentication/resource effects were isolated fixtures only."
