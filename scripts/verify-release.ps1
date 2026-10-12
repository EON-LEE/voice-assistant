[CmdletBinding()]
param(
    [string] $Dotnet = 'dotnet',
    [string] $Npm = 'npm.cmd',
    [string] $Python = 'python',
    [Parameter(Mandatory = $true)]
    [string] $BicepPath,
    [Parameter(Mandatory = $true)]
    [string] $ReportPath,
    [switch] $InstallBrowsers,
    [switch] $RequireLive
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
$reportFile = [IO.Path]::GetFullPath($ReportPath)
if (Test-Path -LiteralPath $reportFile) {
    throw 'Choose a new report path; verification evidence is not overwritten.'
}
$parent = Split-Path $reportFile -Parent
if (-not (Test-Path -LiteralPath $parent -PathType Container)) {
    throw 'The report parent directory must already exist.'
}
$revision = & git -C $root rev-parse HEAD
if ($LASTEXITCODE -ne 0) { throw 'Cannot identify the source revision.' }
$dirty = & git -C $root status --porcelain
if ($LASTEXITCODE -ne 0) { throw 'Cannot identify uncommitted changes.' }
$report = [ordered]@{
    schemaVersion = 1
    startedAt = [DateTimeOffset]::UtcNow.ToString('o')
    completedAt = $null
    revision = $revision.Trim()
    sourceDirty = [bool]$dirty
    status = 'RUNNING'
    localVerification = 'NOT_RUN'
    liveAzure = 'NOT_RUN'
    stages = @()
}

function Save-Report {
    $json = $report | ConvertTo-Json -Depth 8
    [IO.File]::WriteAllText($reportFile, $json, [Text.UTF8Encoding]::new($false))
}

function Invoke-Verification {
    param([string] $Name, [scriptblock] $Run)
    $stage = [ordered]@{
        name = $Name
        status = 'RUNNING'
        startedAt = [DateTimeOffset]::UtcNow.ToString('o')
        durationMs = 0
    }
    $report.stages += $stage
    Save-Report
    $clock = [Diagnostics.Stopwatch]::StartNew()
    try {
        & $Run
        $stage.status = 'PASS'
    }
    catch {
        $stage.status = 'FAIL'
        Write-Error -ErrorAction Continue "Verification stage failed: $Name. Inspect its command output."
        throw
    }
    finally {
        $stage.durationMs = [long]$clock.Elapsed.TotalMilliseconds
        Save-Report
    }
}

$previousBackend = $env:VOICE_ASSISTANT_BACKEND_E2E
$previousExternal = $env:VOICE_ASSISTANT_API_EXTERNAL
$previousWebUrl = $env:VOICE_ASSISTANT_WEB_URL
$previousViteAuth = $env:VOICE_ASSISTANT_VITE_AUTH_TESTS
Push-Location $root
try {
    Save-Report
    Invoke-Verification 'api-build-and-tests' {
        & (Join-Path $PSScriptRoot 'test-local.ps1') -Dotnet $Dotnet
    }
    $env:VOICE_ASSISTANT_BACKEND_E2E = '1'
    $env:VOICE_ASSISTANT_VITE_AUTH_TESTS = '1'
    Remove-Item Env:VOICE_ASSISTANT_API_EXTERNAL -ErrorAction SilentlyContinue
    Remove-Item Env:VOICE_ASSISTANT_WEB_URL -ErrorAction SilentlyContinue
    Invoke-Verification 'web-build-unit-and-real-local-api-browser-tests' {
        & (Join-Path $PSScriptRoot 'test-web.ps1') -Npm $Npm -InstallBrowsers:$InstallBrowsers
    }
    Invoke-Verification 'infrastructure-offline-tests' {
        & (Join-Path $PSScriptRoot 'infra\tests\Test-Offline.ps1') `
            -BicepPath $BicepPath -BackendSchemaPath (Join-Path $root 'contracts\search-index.json')
    }
    Invoke-Verification 'meeting-benchmark-tests' {
        & $Python -m unittest discover -s (Join-Path $root 'tools\MeetingBenchmark') -p 'test_*.py' -v
        if ($LASTEXITCODE -ne 0) { throw 'Meeting boundary and latency-analysis tests failed.' }
    }
    Invoke-Verification 'live-probe-job-template' {
        $compiled = [IO.Path]::GetTempFileName()
        try {
            & $BicepPath build (Join-Path $root 'tools\VoiceAssistant.LiveProbe\cloud-job.bicep') --outfile $compiled
            if ($LASTEXITCODE -ne 0) { throw 'Live acceptance job template did not compile.' }
            $template = Get-Content -LiteralPath $compiled -Raw | ConvertFrom-Json
            $jobs = @($template.resources | Where-Object { $_.type -eq 'Microsoft.App/jobs' })
            if ($template.resources.Count -ne 1 -or $jobs.Count -ne 1) {
                throw 'Live acceptance must not create roles, identities, or additional resources.'
            }
            $configuration = $jobs[0].properties.configuration
            $container = $jobs[0].properties.template.containers[0]
            if ($configuration.triggerType -ne 'Manual' -or $configuration.replicaRetryLimit -ne 0 -or
                $configuration.replicaTimeout -ne 180 -or $configuration.manualTriggerConfig.parallelism -ne 1 -or
                $container.args -notcontains '--live' -or $container.args -contains '--help') {
                throw 'Live acceptance execution limits or explicit service-call intent changed.'
            }
            Write-Output 'PASS: bounded manual acceptance job compiles without creating additional privileges.'
        }
        finally {
            Remove-Item -LiteralPath $compiled
        }
    }
    $report.localVerification = 'PASS'
    $report.status = 'LOCAL_PASS_LIVE_NOT_VERIFIED'
    if ($RequireLive) {
        $report.status = 'BLOCKED'
        throw 'Local checks passed, but this run did not verify Azure deployment, real Speech/OpenAI, or real tab sharing. Release gate remains blocked.'
    }
    Write-Output "Local verification passed. Live Azure and real tab sharing remain NOT VERIFIED. Evidence: $reportFile"
}
catch {
    if ($report.status -ne 'BLOCKED') {
        $report.localVerification = 'FAIL'
        $report.status = 'FAIL'
    }
    throw
}
finally {
    $report.completedAt = [DateTimeOffset]::UtcNow.ToString('o')
    Save-Report
    $env:VOICE_ASSISTANT_BACKEND_E2E = $previousBackend
    $env:VOICE_ASSISTANT_API_EXTERNAL = $previousExternal
    $env:VOICE_ASSISTANT_WEB_URL = $previousWebUrl
    $env:VOICE_ASSISTANT_VITE_AUTH_TESTS = $previousViteAuth
    Pop-Location
}
