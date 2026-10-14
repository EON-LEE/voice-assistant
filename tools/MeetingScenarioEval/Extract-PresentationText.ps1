[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $InputPath,
    [Parameter(Mandatory = $true)]
    [string] $OutputPath,
    [ValidateSet('Confidential', 'Private', 'Public', 'Synthetic')]
    [string] $Classification = 'Confidential'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$inputFile = [IO.Path]::GetFullPath($InputPath)
$outputFile = [IO.Path]::GetFullPath($OutputPath)
if (-not (Test-Path -LiteralPath $inputFile -PathType Leaf)) {
    throw "Presentation not found: $inputFile"
}
if (Test-Path -LiteralPath $outputFile) {
    throw "Choose a new output path; extraction output is not overwritten: $outputFile"
}
$outputParent = Split-Path $outputFile -Parent
if (-not (Test-Path -LiteralPath $outputParent -PathType Container)) {
    throw "Output directory does not exist: $outputParent"
}
$repoRoot = [IO.Path]::GetFullPath((Split-Path (Split-Path $PSScriptRoot -Parent) -Parent))
$insideRepository = $outputFile.StartsWith($repoRoot + [IO.Path]::DirectorySeparatorChar,
    [StringComparison]::OrdinalIgnoreCase)
if ($Classification -in @('Confidential', 'Private') -and $insideRepository) {
    throw 'Confidential/private extraction output must stay outside the repository. Use a session artifact or other private path.'
}

$bytes = [IO.File]::ReadAllBytes($inputFile)
if ($bytes.Length -lt 8) { throw 'Presentation is too small to identify.' }
$signature = (($bytes[0..7] | ForEach-Object { $_.ToString('X2') }) -join '-')
$format = switch ($signature) {
    'D0-CF-11-E0-A1-B1-1A-E1' { 'ole-binary' }
    '50-4B-03-04-14-00-06-00' { 'openxml' }
    '50-4B-03-04-0A-00-00-00' { 'openxml' }
    default { 'unknown' }
}
if ($format -eq 'unknown') { throw "Unsupported presentation signature: $signature" }

$powerPoint = $null
$presentation = $null
try {
    $powerPoint = New-Object -ComObject PowerPoint.Application
    # Open(FileName, ReadOnly, Untitled, WithWindow). Never save or modify the source.
    $presentation = $powerPoint.Presentations.Open($inputFile, -1, 0, 0)
    $slides = @()
    foreach ($slide in $presentation.Slides) {
        $items = [Collections.Generic.List[object]]::new()
        foreach ($shape in $slide.Shapes) {
            $text = ''
            try {
                if ($shape.HasTextFrame -eq -1 -and $shape.TextFrame.HasText -eq -1) {
                    $text = [string]$shape.TextFrame.TextRange.Text
                }
            }
            catch {
                # Unsupported shape types are ignored; extraction remains text-only and read-only.
            }
            $text = ($text -replace "`r`n?", "`n").Trim()
            if ($text.Length -gt 0) {
                $items.Add([ordered]@{
                    shape = [int]$shape.Id
                    name = [string]$shape.Name
                    text = $text
                })
            }
        }
        $notes = ''
        try {
            foreach ($shape in $slide.NotesPage.Shapes) {
                if ($shape.HasTextFrame -eq -1 -and $shape.TextFrame.HasText -eq -1) {
                    $candidate = ([string]$shape.TextFrame.TextRange.Text -replace "`r`n?", "`n").Trim()
                    if ($candidate.Length -gt 0 -and $candidate -notmatch '^\d+$') {
                        $notes = @($notes, $candidate).Where({ $_ }).ForEach({ $_ }) -join "`n"
                    }
                }
            }
        }
        catch {
            # Notes are optional and must not make slide extraction fail.
        }
        $slides += [ordered]@{
            number = [int]$slide.SlideNumber
            text = @($items | ForEach-Object { $_.text }) -join "`n"
            notes = $notes
            shapes = $items
        }
    }
    $result = [ordered]@{
        schemaVersion = 1
        classification = $Classification.ToLowerInvariant()
        sourceName = [IO.Path]::GetFileName($inputFile)
        sourceFormat = $format
        sourceSignature = $signature
        extractedAt = [DateTimeOffset]::UtcNow.ToString('o')
        slideCount = $slides.Count
        slides = $slides
    }
    [IO.File]::WriteAllText(
        $outputFile,
        ($result | ConvertTo-Json -Depth 8),
        [Text.UTF8Encoding]::new($false))
}
finally {
    if ($presentation -ne $null) {
        $presentation.Close()
        [Runtime.InteropServices.Marshal]::FinalReleaseComObject($presentation) | Out-Null
    }
    if ($powerPoint -ne $null) {
        $powerPoint.Quit()
        [Runtime.InteropServices.Marshal]::FinalReleaseComObject($powerPoint) | Out-Null
    }
    [GC]::Collect()
    [GC]::WaitForPendingFinalizers()
}
