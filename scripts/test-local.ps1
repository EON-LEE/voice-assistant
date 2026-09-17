[CmdletBinding()]
param(
    [string] $Dotnet = 'dotnet',
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',
    [switch] $IncludeDesktop
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent

& $Dotnet --version
if ($LASTEXITCODE -ne 0) {
    throw 'A .NET SDK is required. Install the .NET 8 SDK or pass -Dotnet with its executable path.'
}

$applications = @(Get-ChildItem -LiteralPath (Join-Path $root 'src') -Filter '*.csproj' -Recurse)
$tests = @(Get-ChildItem -LiteralPath (Join-Path $root 'tests') -Filter '*.csproj' -Recurse)
if (-not $IncludeDesktop) {
    $applications = @($applications | Where-Object { $_.FullName -notmatch '[\\/]VoiceAssistant\.Desktop[\\/]' })
    $tests = @($tests | Where-Object { $_.FullName -notmatch '[\\/]VoiceAssistant\.Desktop\.Tests[\\/]' })
}
if ($applications.Count -eq 0 -or $tests.Count -eq 0) {
    throw 'Application and test projects must both be present. Integrate the component worktrees first.'
}

foreach ($project in $applications) {
    & $Dotnet build $project.FullName --configuration $Configuration --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "Build failed: $($project.FullName)"
    }
}

foreach ($project in $tests) {
    & $Dotnet test $project.FullName --configuration $Configuration --nologo --logger 'trx'
    if ($LASTEXITCODE -ne 0) {
        throw "Tests failed: $($project.FullName)"
    }
}
