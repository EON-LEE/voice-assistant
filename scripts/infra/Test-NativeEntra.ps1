[CmdletBinding()]
param(
    [Parameter(Mandatory)][guid]$TenantId,
    [Parameter(Mandatory)][guid]$ApiClientId,
    [Parameter(Mandatory)][guid]$NativeClientId
)
$ErrorActionPreference = 'Stop'
Import-Module Az.Accounts -ErrorAction Stop
$context = Get-AzContext -ListAvailable | Where-Object { $_.Tenant.Id -eq $TenantId.ToString() } | Select-Object -First 1
if (-not $context) { throw 'An authorized Azure context for the supplied tenant is required.' }

$accessToken = Get-AzAccessToken -DefaultProfile $context -ResourceUrl 'https://graph.microsoft.com/' -ErrorAction Stop
$headers = @{ Authorization = 'Bearer ' + [Net.NetworkCredential]::new('', $accessToken.Token).Password }
function Get-Application {
    param([guid]$ClientId)
    $filter = [uri]::EscapeDataString("appId eq '$ClientId'")
    $response = Invoke-RestMethod -Uri ("https://graph.microsoft.com/v1.0/applications?`$filter=$filter") -Headers $headers -TimeoutSec 30
    if (@($response.value).Count -ne 1) { throw 'Expected exactly one application for the supplied client ID.' }
    return $response.value[0]
}
try {
    if ($ApiClientId -eq $NativeClientId) { throw 'The native public client and API must be separate applications.' }
    $native = Get-Application $NativeClientId
    $api = Get-Application $ApiClientId
    $scope = @($api.api.oauth2PermissionScopes | Where-Object { $_.value -ceq 'Meeting.Access' -and $_.isEnabled })
    if ($scope.Count -ne 1) { throw 'The API must expose one enabled Meeting.Access delegated scope.' }
    if ($native.signInAudience -cne 'AzureADMyOrg') { throw 'The native client must be single-tenant.' }
    if (@($native.publicClient.redirectUris) -cnotcontains 'http://localhost') {
        throw 'The native client must register http://localhost as a public-client redirect, not an SPA/web redirect.'
    }
    if (@($native.passwordCredentials).Count -gt 0 -or @($native.keyCredentials).Count -gt 0) {
        throw 'The native public client must not have embedded or application client credentials.'
    }
    $permission = @($native.requiredResourceAccess | Where-Object { $_.resourceAppId -eq $ApiClientId.ToString() })
    if ($permission.Count -ne 1 -or -not ($permission[0].resourceAccess | Where-Object {
        $_.id -eq $scope[0].id -and $_.type -ceq 'Scope'
    })) { throw 'The native client must request the API Meeting.Access delegated scope.' }
    $preauthorization = @($api.api.preAuthorizedApplications | Where-Object {
        $_.appId -eq $NativeClientId.ToString() -and @($_.delegatedPermissionIds) -contains $scope[0].id
    })
    if ($preauthorization.Count -ne 1) { throw 'The native app is not preauthorized for the API Meeting.Access scope.' }
    Write-Output 'PASS: Separate single-tenant native public client, localhost redirect, no client secrets, delegated Meeting.Access and API preauthorization.'
    Write-Output 'This read-only check does not prove interactive account login, membership, microphone input, or Azure inference.'
}
finally {
    $headers.Clear()
    $accessToken = $null
}
