Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Invoke-BoundedJsonCommand {
    param(
        [Parameter(Mandatory)][string]$Path,
        [string[]]$Prefix = @(),
        [string[]]$Arguments = @(),
        [ValidateRange(1, 1800)][int]$TimeoutSeconds = 30
    )
    $command = Get-Command $Path -CommandType Application, ExternalScript -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $command) { return [pscustomobject]@{ Succeeded = $false; Code = 'TOOL_UNAVAILABLE'; Data = $null } }
    $payload = @{ path = $command.Source; arguments = @($Prefix) + @($Arguments) } | ConvertTo-Json -Depth 5 -Compress
    $encoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($payload))
    # An isolated, noninteractive host preserves argument boundaries for exe/cmd/ps1 launchers.
    $script = @'
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$env:AZURE_EXTENSION_USE_DYNAMIC_INSTALL = 'no'
$env:AZURE_CORE_COLLECT_TELEMETRY = 'no'
try {
    $request = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('__PAYLOAD__')) | ConvertFrom-Json
    $arguments = @($request.arguments)
    $global:LASTEXITCODE = 0
    $output = & $request.path @arguments 2>$null
    $code = $LASTEXITCODE
    if ($code -ne 0) { exit $code }
    [Console]::Out.Write(($output -join "`n"))
    exit 0
} catch { exit 91 }
'@
    $script = $script.Replace('__PAYLOAD__', $encoded)
    $hostPath = (Get-Process -Id $PID).Path
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = $hostPath
    $start.Arguments = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand ' +
        [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($script))
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $process = New-Object Diagnostics.Process
    $process.StartInfo = $start
    try {
        try { $null = $process.Start() }
        catch { return [pscustomobject]@{ Succeeded = $false; Code = 'PROCESS_START_FAILED'; Data = $null } }
        $outputTask = $process.StandardOutput.ReadToEndAsync()
        $errorTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            if ($env:OS -eq 'Windows_NT') {
                $kill = New-Object Diagnostics.ProcessStartInfo
                $kill.FileName = Join-Path $env:SystemRoot 'System32\taskkill.exe'
                $kill.Arguments = "/PID $($process.Id) /T /F"
                $kill.UseShellExecute = $false
                $kill.CreateNoWindow = $true
                $kill.RedirectStandardOutput = $true
                $kill.RedirectStandardError = $true
                $killer = [Diagnostics.Process]::Start($kill)
                try {
                    if (-not $killer.WaitForExit(5000) -or $killer.ExitCode -ne 0) {
                        return [pscustomobject]@{ Succeeded = $false; Code = 'TIMEOUT_CLEANUP_UNCONFIRMED'; Data = $null }
                    }
                } finally { $killer.Dispose() }
            } else { $process.Kill($true) }
            return [pscustomobject]@{ Succeeded = $false; Code = 'TIMEOUT'; Data = $null }
        }
        if ($process.ExitCode -ne 0) { return [pscustomobject]@{ Succeeded = $false; Code = 'COMMAND_FAILED'; Data = $null } }
        $text = $outputTask.GetAwaiter().GetResult()
        $null = $errorTask.GetAwaiter().GetResult()
        try {
            $data = $null
            if (-not [string]::IsNullOrWhiteSpace($text)) { $data = ConvertFrom-Json -InputObject $text }
            return [pscustomobject]@{ Succeeded = $true; Code = 'OK'; Data = $data }
        } catch {
            return [pscustomobject]@{ Succeeded = $false; Code = 'INVALID_JSON'; Data = $null }
        }
    } finally { $process.Dispose() }
}

function Invoke-AzPowerShellCommand {
    param([string[]]$Arguments, [ValidateRange(1, 1800)][int]$TimeoutSeconds = 30)
    $payload = @{ arguments = $Arguments } | ConvertTo-Json -Compress
    return Invoke-BoundedJsonCommand -Path (Get-Process -Id $PID).Path -Arguments @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass',
        '-File', (Join-Path $PSScriptRoot 'AzPowerShellCommand.ps1'), '-RequestBase64',
        [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($payload))) -TimeoutSeconds $TimeoutSeconds
}

Export-ModuleMember -Function Invoke-BoundedJsonCommand, Invoke-AzPowerShellCommand
