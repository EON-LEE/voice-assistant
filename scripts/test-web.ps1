[CmdletBinding()]
param(
    [string] $Npm = 'npm.cmd',
    [switch] $InstallBrowsers
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
$web = Join-Path $root 'src\VoiceAssistant.Web'
$e2e = Join-Path $root 'tests\VoiceAssistant.Web.E2E'

foreach ($directory in @($web, $e2e)) {
    if (-not (Test-Path (Join-Path $directory 'package-lock.json'))) {
        throw "A committed dependency lockfile is required: $directory"
    }
    Push-Location $directory
    try {
        & $Npm ci --no-audit --no-fund
        if ($LASTEXITCODE -ne 0) { throw "Dependency restore failed: $directory" }
        if ($directory -eq $web) {
            & $Npm run build
            if ($LASTEXITCODE -ne 0) { throw 'Web production build failed.' }
            & $Npm test
            if ($LASTEXITCODE -ne 0) { throw 'Web unit tests failed.' }
        }
        else {
            if ($InstallBrowsers) {
                & $Npm exec -- playwright install chromium
                if ($LASTEXITCODE -ne 0) { throw 'Chromium installation failed.' }
            }
            & $Npm test
            if ($LASTEXITCODE -ne 0) { throw 'Browser integration tests failed.' }
        }
    }
    finally {
        Pop-Location
    }
}
