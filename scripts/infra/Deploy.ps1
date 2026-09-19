[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ParametersFile,
    [string]$BicepPath = 'bicep',
    [string]$SubscriptionId = 'b0af194e-77a5-4471-bb43-67e78295b5c8',
    [string]$ResourceGroup,
    [string]$DeploymentName = 'voice-assistant',
    [switch]$Validate,
    [switch]$WhatIf,
    [switch]$Apply
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'Common.psm1') -Force
if (@($Validate, $WhatIf, $Apply).Where({ $_ }).Count -gt 1) { throw 'Choose only one of -Validate, -WhatIf or -Apply.' }
$templatePath = Join-Path $PSScriptRoot '..\..\infra\main.bicep'
$compiledPath = [IO.Path]::GetTempFileName()
try {
    & $BicepPath build $templatePath --outfile $compiledPath
    if ($LASTEXITCODE -ne 0) { throw 'Bicep compilation failed.' }
    $template = Get-Content -LiteralPath $compiledPath -Raw | ConvertFrom-Json
    $parameters = Get-Content -LiteralPath $ParametersFile -Raw | ConvertFrom-Json
    Assert-DeploymentParameters $parameters $template
    if (-not ($Validate -or $WhatIf -or $Apply)) {
        Write-Output 'Offline Bicep compilation and parameter validation passed. No Azure request or deployment performed.'
        return
    }
    if ([string]::IsNullOrWhiteSpace($ResourceGroup)) { throw 'Online operations require an explicit existing ResourceGroup.' }
    $null = Assert-Subscription $SubscriptionId $parameters.parameters.tenantId.value
    $null = Invoke-AzJson @('group', 'show', '--name', $ResourceGroup, '--subscription', $SubscriptionId)
    $arguments = @('--subscription', $SubscriptionId, '--resource-group', $ResourceGroup, '--name', $DeploymentName,
        '--template-file', $compiledPath, '--parameters', ('@' + (Resolve-Path -LiteralPath $ParametersFile).Path), '--mode', 'Incremental')
    if ($Validate) {
        $null = Invoke-AzJson (@('deployment', 'group', 'validate') + $arguments)
        Write-Output 'Azure deployment validation passed. No deployment applied.'
    } elseif ($WhatIf) {
        $preview = Invoke-AzJson (@('deployment', 'group', 'what-if', '--no-pretty-print') + $arguments)
        # Never print the full diff: secure parameters can materialize as application environment values.
        $preview.changes | ForEach-Object { [pscustomobject]@{ ChangeType = $_.changeType; ResourceId = $_.resourceId } }
    } else {
        $null = Invoke-AzJson (@('deployment', 'group', 'validate') + $arguments)
        $deployment = Invoke-AzJson (@('deployment', 'group', 'create') + $arguments)
        if ($deployment.properties.provisioningState -ne 'Succeeded') { throw 'Deployment did not finish successfully.' }
        Write-Output "Deployment succeeded. Browser origin: $($deployment.properties.outputs.browserUrl.value)"
        Write-Output 'Entra redirect/consent, index ingestion and live authorization/voice checks are still required.'
    }
} finally {
    Remove-Item -LiteralPath $compiledPath -Force
}
