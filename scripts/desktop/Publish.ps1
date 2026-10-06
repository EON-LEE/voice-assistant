[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$DotnetCommand = 'dotnet',
    [switch]$FrameworkDependent
)
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$project = Join-Path $repoRoot 'src\VoiceAssistant.Desktop\VoiceAssistant.Desktop.csproj'
$output = [IO.Path]::GetFullPath($OutputDirectory)
if ($output -eq $repoRoot -or $output.StartsWith($repoRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Publish outside the repository so generated binaries are not committed.'
}
if ($env:OS -ne 'Windows_NT') { throw 'The in-person overlay is a Windows application.' }
if (Test-Path $output) {
    if (Get-ChildItem -LiteralPath $output -Force | Select-Object -First 1) {
        throw 'Choose a new or empty output directory; existing files will not be overwritten.'
    }
}
$selfContained = if ($FrameworkDependent) { 'false' } else { 'true' }
& $DotnetCommand publish $project -c Release -r win-x64 --self-contained $selfContained `
    -p:PublishSingleFile=false --nologo -v minimal -o $output
if ($LASTEXITCODE -ne 0) { throw 'Native app publish failed. No launcher was created.' }
$executable = Join-Path $output 'VoiceAssistant.Desktop.exe'
if (-not (Test-Path -LiteralPath $executable)) { throw 'Publish did not produce the native app executable.' }
$launcher = @'
@echo off
cd /d "%~dp0"
start "" "%~dp0VoiceAssistant.Desktop.exe"
'@
[IO.File]::WriteAllText((Join-Path $output 'Open-Meeting-Coach.cmd'), $launcher, [Text.Encoding]::ASCII)
Write-Output "Published: $executable"
Write-Output "Double-click Open-Meeting-Coach.cmd in $output. Launching does not grant microphone consent."
