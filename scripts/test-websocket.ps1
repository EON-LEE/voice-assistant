[CmdletBinding()]
param(
    [uri] $Endpoint = 'ws://127.0.0.1:8080/api/meeting',
    [ValidateRange(5, 120)]
    [int] $TimeoutSeconds = 30
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $Endpoint.IsLoopback -or $Endpoint.Scheme -ne 'ws') {
    throw 'This synthetic-audio smoke test only supports a local, explicit fake-provider server.'
}

$socket = [System.Net.WebSockets.ClientWebSocket]::new()
$timeout = [System.Threading.CancellationTokenSource]::new()
$timeout.CancelAfter([TimeSpan]::FromSeconds($TimeoutSeconds))

function Send-Json {
    param([hashtable] $Message)
    $bytes = [System.Text.Encoding]::UTF8.GetBytes(($Message | ConvertTo-Json -Depth 8 -Compress))
    $segment = [System.ArraySegment[byte]]::new($bytes)
    $socket.SendAsync($segment, [System.Net.WebSockets.WebSocketMessageType]::Text, $true, $timeout.Token).GetAwaiter().GetResult()
}

function Receive-Event {
    $buffer = [byte[]]::new(16384)
    $message = [System.IO.MemoryStream]::new()
    try {
        do {
            $result = $socket.ReceiveAsync([System.ArraySegment[byte]]::new($buffer), $timeout.Token).GetAwaiter().GetResult()
            if ($result.MessageType -ne [System.Net.WebSockets.WebSocketMessageType]::Text) {
                throw "Expected a text event; received $($result.MessageType)."
            }
            $message.Write($buffer, 0, $result.Count)
            if ($message.Length -gt 1048576) {
                throw 'Server event exceeded the smoke-test size limit.'
            }
        } while (-not $result.EndOfMessage)
        $event = [System.Text.Encoding]::UTF8.GetString($message.ToArray()) | ConvertFrom-Json
        if ($event.type -eq 'error') {
            throw "Server error $($event.code): $($event.message)"
        }
        return $event
    }
    finally {
        $message.Dispose()
    }
}

try {
    $socket.ConnectAsync($Endpoint, $timeout.Token).GetAwaiter().GetResult()
    Send-Json @{
        type = 'session.start'
        protocolVersion = 1
        audio = @{ encoding = 'pcm_s16le'; sampleRate = 16000; channels = 1 }
    }
    do {
        $event = Receive-Event
    } while ($event.type -ne 'session.ready')

    # Synthetic silence exercises transport without capturing a microphone or meeting.
    $audio = [byte[]]::new(3200)
    for ($index = 0; $index -lt 10; $index++) {
        $socket.SendAsync(
            [System.ArraySegment[byte]]::new($audio),
            [System.Net.WebSockets.WebSocketMessageType]::Binary,
            $true,
            $timeout.Token
        ).GetAwaiter().GetResult()
    }

    $transcript = $null
    do {
        $event = Receive-Event
        if ($event.type -eq 'transcript.final') {
            $transcript = $event
        }
    } while ($null -eq $transcript)
    if ([string]::IsNullOrWhiteSpace($transcript.text)) {
        throw 'Final transcript was empty.'
    }

    Send-Json @{ type = 'response.request' }
    $responseId = $null
    $streamed = [System.Text.StringBuilder]::new()
    $complete = $null
    do {
        $event = Receive-Event
        switch ($event.type) {
            'response.started' {
                $responseId = $event.responseId
                [void] $streamed.Clear()
            }
            'response.delta' {
                if ($null -eq $responseId -or $event.responseId -ne $responseId) {
                    throw 'Response delta did not match the active response.'
                }
                [void] $streamed.Append($event.text)
            }
            'response.cancelled' {
                if ($event.responseId -eq $responseId) {
                    $responseId = $null
                    [void] $streamed.Clear()
                }
            }
            'response.completed' {
                if ($null -eq $responseId -or $event.responseId -ne $responseId) {
                    throw 'Completed response did not match the active response.'
                }
                $complete = $event
            }
        }
    } while ($null -eq $complete)

    if ($complete.turnId -ne $transcript.turnId) {
        throw 'Reply was generated for an unexpected transcript turn.'
    }
    if ($streamed.Length -eq 0 -or [string]::IsNullOrWhiteSpace($complete.text)) {
        throw 'Expected both incremental text and a nonempty completed reply.'
    }
    if ($streamed.ToString() -ne $complete.text) {
        throw 'Streamed text differs from the completed reply.'
    }
    Send-Json @{ type = 'session.stop' }
    Write-Output 'PASS: WebSocket handshake, synthetic PCM audio, final transcript, and consistent streaming reply.'
}
finally {
    $socket.Abort()
    $socket.Dispose()
    $timeout.Dispose()
}
