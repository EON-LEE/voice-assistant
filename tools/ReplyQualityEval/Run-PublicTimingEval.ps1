[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $AmiZip,
    [Parameter(Mandatory = $true)]
    [string] $IcsiZip,
    [Parameter(Mandatory = $true)]
    [string] $OutputPath,
    [string] $Dotnet = 'dotnet',
    [string] $ManifestPath = (Join-Path $PSScriptRoot 'public-timing-samples.json')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$ami = [IO.Path]::GetFullPath($AmiZip)
$icsi = [IO.Path]::GetFullPath($IcsiZip)
$output = [IO.Path]::GetFullPath($OutputPath)
foreach ($path in @($ami, $icsi, [IO.Path]::GetFullPath($ManifestPath))) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required input not found: $path" }
}
if (Test-Path -LiteralPath $output) { throw "Choose a new output path; results are not overwritten: $output" }
if (-not (Test-Path -LiteralPath (Split-Path $output -Parent) -PathType Container)) {
    throw 'Output parent directory does not exist.'
}

$manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
if ($manifest.classification -ne 'public' -or -not $manifest.evaluationKinds.timingSimulation -or
    $manifest.evaluationKinds.audioAsrE2E -or $manifest.evaluationKinds.llmJudge) {
    throw 'Timing manifest must be public transcript simulation only.'
}
$specs = @($manifest.samples | ForEach-Object {
    $archive = if ($_.corpus -eq 'AMI') { $ami } elseif ($_.corpus -eq 'ICSI') { $icsi } else { throw "Unknown corpus: $($_.corpus)" }
    $target = if ($_.PSObject.Properties.Name -contains 'targetSpeaker') { "|$($_.targetSpeaker)" } else { '' }
    "$archive|$($_.meeting)|$($_.startSeconds)$target"
})

$previous = $env:EVAL_TIMING_ONLY
try {
    $env:EVAL_TIMING_ONLY = '1'
    & $Dotnet run --project (Join-Path $PSScriptRoot 'ReplyQualityEval.csproj') --configuration Release -- $output @specs
    if ($LASTEXITCODE -ne 0) { throw 'Public timing simulation failed.' }
}
finally {
    $env:EVAL_TIMING_ONLY = $previous
}
