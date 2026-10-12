[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ReportPath,
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
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'Readiness.psm1') -Force
$reportFile = [IO.Path]::GetFullPath($ReportPath)
foreach ($inputPath in @($ConfigFile, $ParametersFile)) {
    if ($inputPath -and [IO.Path]::GetFullPath($inputPath) -eq $reportFile) { throw 'ReportPath must not overwrite an input configuration file.' }
}
# Prove report persistence before any mutation; an interrupted run cannot leave an old PASS behind.
@{ schemaVersion = 1; overallStatus = 'BLOCKED'; execution = 'NotStarted'; liveApplicationVerified = $false } |
    ConvertTo-Json | Set-Content -LiteralPath $reportFile -Encoding UTF8
$arguments = @{}
foreach ($key in $PSBoundParameters.Keys) { if ($key -ne 'ReportPath') { $arguments[$key] = $PSBoundParameters[$key] } }
try { $report = Get-AzureReadiness @arguments }
catch {
    # Unexpected exceptions must produce a failed report, never a successful fallback or raw diagnostic.
    $report = [pscustomobject]@{ schemaVersion = 1; generatedAtUtc = [datetime]::UtcNow.ToString('o');
        overallStatus = 'FAIL'; execution = 'Unknown'; liveApplicationVerified = $false;
        checks = @(@{ id = 'orchestration'; category = 'tooling'; status = 'FAIL'; code = 'UNEXPECTED_ORCHESTRATION_FAILURE' }) }
}
$report | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $reportFile -Encoding UTF8
Write-Output "Azure readiness: $($report.overallStatus); execution: $($report.execution). Report written; no live-application success is implied."
if ($report.overallStatus -eq 'FAIL') { exit 1 }
if ($report.overallStatus -eq 'BLOCKED') { exit 2 }
