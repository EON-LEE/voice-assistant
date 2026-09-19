[CmdletBinding()]
param([Parameter(Mandatory)][string]$RequestBase64)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$WarningPreference = 'SilentlyContinue'
$ProgressPreference = 'SilentlyContinue'

function Read-Option {
    param([string]$Name, [string]$Default = '')
    $position = [array]::IndexOf($script:arguments, $Name)
    if ($position -lt 0) { return $Default }
    if ($position + 1 -ge $script:arguments.Count) { throw 'Missing option value.' }
    return $script:arguments[$position + 1]
}
function Get-SingleArmHeader {
    param($Headers, [string]$Name)
    $entries = if ($Headers -is [Collections.IDictionary]) { @($Headers.GetEnumerator()) } else { @($Headers) }
    $values = @()
    foreach ($entry in $entries) {
        if ($null -ne $entry -and $entry.Key -ieq $Name) { $values += @($entry.Value) }
    }
    if ($values.Count -ne 1 -or $values[0] -isnot [string] -or [string]::IsNullOrWhiteSpace($values[0])) {
        throw 'ARM polling requires exactly one nonempty header value.'
    }
    return $values[0]
}
function Invoke-Arm {
    param([string]$Method, [string]$Path, $Body = $null, [int[]]$Expected = @(200, 201, 202))
    if ($Path -notlike "/subscriptions/$script:subscription/*" -and $Path -notlike "/subscriptions/$script:subscription`?*") { throw 'ARM request outside approved subscription.' }
    $options = @{ Path = $Path; Method = $Method; DefaultProfile = $script:context; ErrorAction = 'Stop' }
    if ($null -ne $Body) { $options.Payload = $Body | ConvertTo-Json -Depth 100 -Compress }
    $response = Invoke-AzRestMethod @options
    if ([int]$response.StatusCode -notin $Expected) { throw 'ARM response not successful.' }
    $data = $null
    if ($response.Content) { $data = $response.Content | ConvertFrom-Json }
    return [pscustomobject]@{ Status = [int]$response.StatusCode; Headers = $response.Headers; Data = $data }
}
function Get-ArmTags {
    $position = [array]::IndexOf($script:arguments, '--tags')
    if ($position -lt 0) { throw 'Explicit project ownership tags required.' }
    $tags = @{}
    for ($index = $position + 1; $index -lt $script:arguments.Count -and -not $script:arguments[$index].StartsWith('--'); $index++) {
        $pair = $script:arguments[$index].Split('=', 2)
        if ($pair.Count -ne 2) { throw 'Invalid project tag.' }
        $tags[$pair[0]] = $pair[1]
    }
    if ($tags.managedBy -ne 'voice-assistant-bootstrap' -or $tags.projectId -ne 'ff8f97b4-5db4-458e-99de-0d63cfc96a4a') { throw 'Unapproved project ownership.' }
    return $tags
}
function Get-RegistryDigest {
    param([string]$Registry, [string]$Image)
    if ($Registry -cnotmatch '^[a-z0-9]{5,50}$' -or $Image -cnotmatch '^[a-z0-9][a-z0-9._/-]*@sha256:[a-f0-9]{64}$') { throw 'Invalid registry image.' }
    $service = "$Registry.azurecr.io"
    $repository, $digest = $Image.Split('@', 2)
    $token = Get-AzAccessToken -ResourceUrl 'https://management.azure.com/' -TenantId $script:tenant -DefaultProfile $script:context -ErrorAction Stop
    $accessToken = if ($token.Token -is [Security.SecureString]) {
        (New-Object Management.Automation.PSCredential('unused', $token.Token)).GetNetworkCredential().Password
    } else { [string]$token.Token }
    # Transient registry OAuth exchange; no credential is written, cached or returned.
    $exchange = Invoke-RestMethod -Method POST -Uri "https://$service/oauth2/exchange" -Body @{
        grant_type = 'access_token'; service = $service; tenant = $script:tenant; access_token = $accessToken
    } -ContentType 'application/x-www-form-urlencoded' -TimeoutSec 20 -MaximumRedirection 0
    $accessToken = $null
    $authorization = Invoke-RestMethod -Method POST -Uri "https://$service/oauth2/token" -Body @{
        grant_type = 'refresh_token'; service = $service; scope = "repository:${repository}:pull"; refresh_token = $exchange.refresh_token
    } -ContentType 'application/x-www-form-urlencoded' -TimeoutSec 20 -MaximumRedirection 0
    $response = Invoke-WebRequest -Method HEAD -Uri "https://$service/v2/$repository/manifests/$digest" -Headers @{
        Authorization = "Bearer $($authorization.access_token)"
        Accept = 'application/vnd.oci.image.manifest.v1+json, application/vnd.oci.image.index.v1+json, application/vnd.docker.distribution.manifest.v2+json, application/vnd.docker.distribution.manifest.list.v2+json'
    } -UseBasicParsing -TimeoutSec 20 -MaximumRedirection 0
    return [string]$response.Headers['Docker-Content-Digest']
}
try {
    $request = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($RequestBase64)) | ConvertFrom-Json
    $script:arguments = [string[]]$request.arguments
    $script:subscription = Read-Option '--subscription'
    $script:tenant = '2573db8c-dfe5-4805-9e28-a0859692e705'
    if ($script:subscription -ne 'b0af194e-77a5-4471-bb43-67e78295b5c8') { throw 'Unapproved subscription.' }
    Import-Module Az.Accounts -ErrorAction Stop
    $contexts = @(Get-AzContext -ListAvailable | Where-Object {
        $_.Subscription.Id -eq $script:subscription -and $_.Tenant.Id -eq $script:tenant -and $_.Environment.Name -eq 'AzureCloud'
    })
    if (-not $contexts.Count) { throw 'No existing approved context.' }
    $script:context = $contexts[0]
    $command = ($script:arguments | Select-Object -First 2) -join ' '
    $base = "/subscriptions/$script:subscription"
    $group = Read-Option '--resource-group'
    $name = Read-Option '--name'
    foreach ($segment in @($group, $name)) {
        if ($segment -and $segment -cnotmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,89}$') { throw 'Invalid resource name.' }
    }
    $output = $null
    switch ($command) {
        'account show' {
            $data = (Invoke-Arm 'GET' "$base`?api-version=2022-12-01").Data
            $output = @{ id = $data.subscriptionId; tenantId = $data.tenantId; state = $data.state }
        }
        'provider show' {
            $provider = Read-Option '--namespace'
            if ($provider -notin @('Microsoft.ContainerRegistry', 'Microsoft.App', 'Microsoft.ManagedIdentity', 'Microsoft.CognitiveServices', 'Microsoft.Search', 'Microsoft.Storage', 'Microsoft.Insights')) { throw 'Unapproved provider.' }
            $output = (Invoke-Arm 'GET' "$base/providers/$provider`?api-version=2021-04-01").Data.registrationState
        }
        'group exists' {
            $result = Invoke-Arm 'GET' "$base/resourcegroups/$name`?api-version=2022-09-01" $null @(200, 404)
            $output = $result.Status -eq 200
        }
        'group show' { $output = (Invoke-Arm 'GET' "$base/resourcegroups/$name`?api-version=2022-09-01").Data }
        'group create' {
            $output = (Invoke-Arm 'PUT' "$base/resourcegroups/$name`?api-version=2022-09-01" @{
                location = (Read-Option '--location'); tags = (Get-ArmTags)
            }).Data
        }
        'acr list' {
            $output = @((Invoke-Arm 'GET' "$base/resourceGroups/$group/providers/Microsoft.ContainerRegistry/registries?api-version=2025-04-01").Data.value | ForEach-Object {
                @{ name = $_.name; location = $_.location; tags = $_.tags; sku = $_.sku;
                    adminUserEnabled = $_.properties.adminUserEnabled; provisioningState = $_.properties.provisioningState;
                    roleAssignmentMode = $_.properties.roleAssignmentMode }
            })
        }
        'acr check-name' {
            $output = (Invoke-Arm 'POST' "$base/providers/Microsoft.ContainerRegistry/checkNameAvailability?api-version=2023-07-01" @{
                name = $name; type = 'Microsoft.ContainerRegistry/registries'
            }).Data
        }
        'acr create' {
            $path = "$base/resourceGroups/$group/providers/Microsoft.ContainerRegistry/registries/$name`?api-version=2025-04-01"
            $data = (Invoke-Arm 'PUT' $path @{ location = (Read-Option '--location'); tags = (Get-ArmTags);
                sku = @{ name = (Read-Option '--sku') }; properties = @{ adminUserEnabled = $false; roleAssignmentMode = 'LegacyRegistryPermissions' } }).Data
            for ($attempt = 0; $attempt -lt 50 -and $data.properties.provisioningState -notin @('Succeeded', 'Failed', 'Canceled'); $attempt++) {
                Start-Sleep -Seconds 4
                $data = (Invoke-Arm 'GET' $path).Data
            }
            $output = @{ name = $data.name; location = $data.location; tags = $data.tags; sku = $data.sku;
                adminUserEnabled = $data.properties.adminUserEnabled; provisioningState = $data.properties.provisioningState }
        }
        'acr repository' {
            if ($script:arguments[2] -ne 'show') { throw 'Unsupported registry operation.' }
            $output = Get-RegistryDigest $name (Read-Option '--image')
        }
        'deployment group' {
            $operation = $script:arguments[2]
            if ($operation -notin @('validate', 'what-if', 'create')) { throw 'Unsupported deployment operation.' }
            if ((Read-Option '--mode') -ne 'Incremental') { throw 'Only Incremental deployments are supported.' }
            $template = Get-Content -LiteralPath (Read-Option '--template-file') -Raw | ConvertFrom-Json
            $parameterPath = Read-Option '--parameters'
            if (-not $parameterPath.StartsWith('@')) { throw 'Explicit parameter file required.' }
            $parameters = Get-Content -LiteralPath $parameterPath.Substring(1) -Raw | ConvertFrom-Json
            $body = @{ properties = @{ mode = 'Incremental'; template = $template; parameters = $parameters.parameters } }
            $path = "$base/resourcegroups/$group/providers/Microsoft.Resources/deployments/$name"
            $suffix = if ($operation -eq 'create') { '' } elseif ($operation -eq 'what-if') { '/whatIf' } else { '/validate' }
            $method = if ($operation -eq 'create') { 'PUT' } else { 'POST' }
            $result = Invoke-Arm $method "$path$suffix`?api-version=2022-09-01" $body
            if ($operation -eq 'create') {
                for ($attempt = 0; $attempt -lt 150; $attempt++) {
                    $result = Invoke-Arm 'GET' "$path`?api-version=2022-09-01"
                    if ($result.Data.properties.provisioningState -in @('Succeeded', 'Failed', 'Canceled')) { break }
                    Start-Sleep -Seconds 4
                }
            } elseif ($result.Status -eq 202) {
                $location = Get-SingleArmHeader $result.Headers 'Location'
                $pollUri = [uri]$location
                if (-not $pollUri.IsAbsoluteUri -or $pollUri.Scheme -ne 'https' -or $pollUri.Host -ne 'management.azure.com' -or
                    $pollUri.Port -ne 443 -or $pollUri.UserInfo -or $pollUri.Fragment) { throw 'Invalid ARM polling endpoint.' }
                # Keep a potentially signed query byte-for-byte, privately, rather than rebuilding it.
                $pathStart = $location.IndexOf('/', $location.IndexOf('://') + 3)
                if ($pathStart -lt 0) { throw 'ARM polling endpoint requires a subscription path.' }
                $pollPath = $location.Substring($pathStart)
                for ($attempt = 0; $attempt -lt 100; $attempt++) {
                    Start-Sleep -Seconds 3
                    $result = Invoke-Arm 'GET' $pollPath
                    if ($result.Status -ne 202) { break }
                }
                if ($result.Status -eq 202) { throw 'ARM polling deadline reached.' }
            }
            if ($result.Data.PSObject.Properties['error'] -or
                ($result.Data.PSObject.Properties['status'] -and $result.Data.status -in @('Failed', 'Canceled'))) { throw 'ARM deployment operation failed.' }
            $output = $result.Data
        }
        default { throw 'Unsupported Azure PowerShell operation.' }
    }
    ConvertTo-Json -InputObject $output -Depth 100 -Compress
} catch {
    # No provider exception, request payload, response body or credential reaches stdout/stderr.
    exit 93
}
