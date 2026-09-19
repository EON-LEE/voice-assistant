Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-PrivateBlobAddress {
    param([string]$ExpectedIp, [string[]]$ResolvedAddresses)
    $ip = $null
    if (-not [Net.IPAddress]::TryParse($ExpectedIp, [ref]$ip) -or $ip.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork) {
        throw 'An explicit private IPv4 Blob endpoint is required.'
    }
    $bytes = $ip.GetAddressBytes()
    if (-not ($bytes[0] -eq 10 -or ($bytes[0] -eq 172 -and $bytes[1] -ge 16 -and $bytes[1] -le 31) -or
        ($bytes[0] -eq 192 -and $bytes[1] -eq 168))) { throw 'Blob endpoint must use an RFC1918 address.' }
    if ($ResolvedAddresses.Count -eq 0 -or @($ResolvedAddresses | Where-Object { $_ -ne $ExpectedIp }).Count) {
        throw 'Blob DNS did not resolve exclusively to the approved private endpoint. No ingestion was attempted.'
    }
}

function Invoke-ManagedIngestionHttp {
    param($Method, $Uri, $Headers, $Body, $ContentType, [int[]]$Expected, [int]$TimeoutSeconds)
    $arguments = @{ Method = $Method; Uri = $Uri; Headers = $Headers; ContentType = $ContentType;
        TimeoutSec = $TimeoutSeconds; MaximumRedirection = 0; UseBasicParsing = $true; ErrorAction = 'Stop'; Verbose = $false; Debug = $false }
    if ($null -ne $Body) { $arguments.Body = [Text.Encoding]::UTF8.GetBytes([string]$Body) }
    try { $response = Invoke-WebRequest @arguments }
    catch {
        $status = 0
        if ($_.Exception.PSObject.Properties['Response'] -and $_.Exception.Response) { $status = [int]$_.Exception.Response.StatusCode }
        if ($status -in $Expected) { return [pscustomobject]@{ Status = $status; Data = $null } }
        throw "Private ingestion HTTP request failed (status $status); response, URL, content and credentials suppressed."
    }
    if ([int]$response.StatusCode -notin $Expected) { throw 'Unexpected private ingestion response status.' }
    $data = $null
    if ($response.Content -and $response.Headers['Content-Type'] -match 'application/json') {
        try { $data = $response.Content | ConvertFrom-Json }
        catch { throw 'Private ingestion response is not valid JSON; content suppressed.' }
    }
    return [pscustomobject]@{ Status = [int]$response.StatusCode; Data = $data }
}

function New-ManagedIngestionRequest {
    param(
        [Parameter(Mandatory)][string]$IdentityEndpoint,
        [Parameter(Mandatory)][string]$IdentityHeader,
        [Parameter(Mandatory)][string]$ClientId,
        [Parameter(Mandatory)][string]$SearchRoot,
        [Parameter(Mandatory)][string]$StorageRoot,
        [Parameter(Mandatory)][string]$OpenAIRoot,
        [scriptblock]$HttpTransport = ${function:Invoke-ManagedIngestionHttp}
    )
    $identityUri = $null
    if (-not [uri]::TryCreate($IdentityEndpoint, [UriKind]::Absolute, [ref]$identityUri) -or
        -not $identityUri.IsLoopback -or $identityUri.Scheme -notin @('http', 'https') -or $identityUri.UserInfo -or
        $identityUri.Fragment -or $identityUri.Query) { throw 'Expected the platform-provided local Container Apps identity endpoint.' }
    $guid = [guid]::Empty
    if (-not [guid]::TryParse($ClientId, [ref]$guid) -or $guid -eq [guid]::Empty -or [string]::IsNullOrWhiteSpace($IdentityHeader)) {
        throw 'The separate job managed identity configuration is required.'
    }
    if ($SearchRoot -cnotmatch '^https://[a-z0-9][a-z0-9-]+\.search\.windows\.net$' -or
        $StorageRoot -cnotmatch '^https://[a-z0-9]{3,24}\.blob\.core\.windows\.net/documents$' -or
        $OpenAIRoot -cnotmatch '^https://[a-z0-9][a-z0-9-]+\.openai\.azure\.com$') { throw 'Unexpected ingestion service endpoints.' }
    $roots = @{ search = $SearchRoot; storage = $StorageRoot; cognitive = $OpenAIRoot }
    $resources = @{ search = 'https://search.azure.com'; storage = 'https://storage.azure.com/'; cognitive = 'https://cognitiveservices.azure.com' }
    $cache = @{}
    return {
        param($Method, $Uri, $Audience, $Body, $Headers, $ContentType, $Expected, [datetime]$Deadline)
        if (-not $roots.ContainsKey($Audience) -or -not ([string]$Uri).StartsWith($roots[$Audience] + '/', [StringComparison]::Ordinal)) {
            throw 'The ingestion request is outside its approved service boundary.'
        }
        $parsed = [uri]$Uri
        $approved = [uri]$roots[$Audience]
        if ($parsed.UserInfo -or $parsed.Fragment -or $parsed.Host -ne $approved.Host -or
            -not $parsed.AbsolutePath.StartsWith($approved.AbsolutePath.TrimEnd('/') + '/', [StringComparison]::Ordinal)) {
            throw 'Invalid ingestion request endpoint.'
        }
        if (-not $cache.ContainsKey($Audience) -or $cache[$Audience].expires -lt [datetimeoffset]::UtcNow.AddMinutes(2)) {
            $tokenUri = $IdentityEndpoint + '?api-version=2019-08-01&resource=' + [uri]::EscapeDataString($resources[$Audience]) +
                '&client_id=' + [uri]::EscapeDataString($ClientId)
            $response = & $HttpTransport 'GET' $tokenUri @{ 'X-IDENTITY-HEADER' = $IdentityHeader } $null 'application/json' @(200) 10
            $token = $response.Data
            $seconds = [long]0
            if ($null -eq $token -or -not $token.PSObject.Properties['access_token'] -or
                -not $token.PSObject.Properties['expires_on'] -or [string]::IsNullOrWhiteSpace($token.access_token) -or
                -not [long]::TryParse([string]$token.expires_on, [ref]$seconds)) { throw 'Managed identity returned an invalid token envelope; values suppressed.' }
            $expiry = [datetimeoffset]::FromUnixTimeSeconds($seconds)
            if ($expiry -le [datetimeoffset]::UtcNow.AddMinutes(2)) { throw 'Managed identity returned an expired token.' }
            $cache[$Audience] = @{ token = $token.access_token; expires = $expiry }
        }
        if ([datetime]::UtcNow -gt $Deadline) { throw 'Ingestion lease safety window expired before a data request; retry during maintenance.' }
        $requestHeaders = @{ Authorization = 'Bearer ' + $cache[$Audience].token }
        foreach ($key in $Headers.Keys) {
            if ($key -ieq 'Authorization' -or $key -ieq 'X-IDENTITY-HEADER') { throw 'Ingestion callers may not override identity headers.' }
            $requestHeaders[$key] = $Headers[$key]
        }
        if ($Audience -eq 'storage') {
            $requestHeaders['x-ms-version'] = '2023-11-03'
            $requestHeaders['x-ms-date'] = [datetime]::UtcNow.ToString('R')
        }
        & $HttpTransport $Method $Uri $requestHeaders $Body $ContentType $Expected 20
    }.GetNewClosure()
}

Export-ModuleMember -Function Assert-PrivateBlobAddress, New-ManagedIngestionRequest
