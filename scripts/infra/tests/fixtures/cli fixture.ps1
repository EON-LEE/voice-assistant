param([string]$Mode = 'arguments', [Parameter(ValueFromRemainingArguments)][string[]]$Remaining)
switch ($Mode) {
    'arguments' { ConvertTo-Json -InputObject @{ arguments = @($Remaining) } -Compress }
    'invalid' { Write-Output 'PRIVATE_DIAGNOSTIC_SENTINEL not json' }
    'failure' { [Console]::Error.WriteLine('PRIVATE_DIAGNOSTIC_SENTINEL'); exit 9 }
    'timeout' { Start-Sleep -Seconds 20; Write-Output '{}' }
    'no-auth' { Write-Output '[]' }
    default { exit 8 }
}
