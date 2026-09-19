param([string]$OutputDirectory = $PSScriptRoot)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Speech
$text = 'Project Lumen needs a brief update by Friday.'
$voice = New-Object System.Speech.Synthesis.SpeechSynthesizer
$memory = New-Object System.IO.MemoryStream
try {
    $voice.SelectVoice('Microsoft Zira Desktop')
    $format = New-Object System.Speech.AudioFormat.SpeechAudioFormatInfo(
        16000, [System.Speech.AudioFormat.AudioBitsPerSample]::Sixteen,
        [System.Speech.AudioFormat.AudioChannel]::Mono)
    $voice.SetOutputToAudioStream($memory, $format)
    $voice.Speak($text)
    $voice.SetOutputToNull()
    $pcm = $memory.ToArray()
    if (!$pcm -or $pcm.Length -gt 16000 * 2 * 15) { throw 'Unexpected synthesis output.' }
    $path = Join-Path $OutputDirectory 'original-project.wav'
    $file = [IO.File]::Create($path)
    $writer = New-Object IO.BinaryWriter($file)
    try {
        $size = $pcm.Length + 32000
        $writer.Write([Text.Encoding]::ASCII.GetBytes('RIFF'))
        $writer.Write([int](36 + $size))
        $writer.Write([Text.Encoding]::ASCII.GetBytes('WAVEfmt '))
        $writer.Write([int]16); $writer.Write([int16]1); $writer.Write([int16]1)
        $writer.Write([int]16000); $writer.Write([int]32000)
        $writer.Write([int16]2); $writer.Write([int16]16)
        $writer.Write([Text.Encoding]::ASCII.GetBytes('data')); $writer.Write([int]$size)
        $writer.Write($pcm); $writer.Write((New-Object byte[] 32000))
    } finally { $writer.Dispose(); $file.Dispose() }
    $metadata = [ordered]@{
        schemaVersion = 1; synthetic = $true; approvedForLiveUse = $true; language = 'en-US'
        sha256 = (Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant()
        speechEndSample = $null
        synthesisEndSample = $pcm.Length / 2
        trailingSilenceSamples = 16000
        text = $text; generator = 'Windows System.Speech / Microsoft Zira Desktop'
    }
    [IO.File]::WriteAllText((Join-Path $OutputDirectory 'original-project.json'),
        ($metadata | ConvertTo-Json), (New-Object Text.UTF8Encoding($false)))
} finally { $voice.Dispose(); $memory.Dispose() }
