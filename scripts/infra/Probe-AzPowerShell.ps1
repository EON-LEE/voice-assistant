[CmdletBinding()]
param(
    [string]$SubscriptionId,
    [string]$TenantId
)
$ErrorActionPreference = 'Stop'
$WarningPreference = 'SilentlyContinue'
$ProgressPreference = 'SilentlyContinue'
$result = [ordered]@{ contextAvailable = $false; tokenAcquired = $false; expiresOn = $null; armAuthorized = $false;
    subscriptionId = $null; tenantId = $null; code = 'NO_TARGET_CONTEXT' }
try {
    Import-Module (Join-Path $PSScriptRoot 'ReleaseTarget.psm1') -Force
    $approved = Find-ApprovedTarget
    if ($approved.Status -eq 'Valid') {
        if (-not $SubscriptionId) { $SubscriptionId = $approved.Target.SubscriptionId }
        if (-not $TenantId) { $TenantId = $approved.Target.TenantId }
    }
    if ($approved.Status -ne 'Valid') {
        $result.code = 'APPROVED_TARGET_REQUIRED'
    } elseif (-not (Test-SameGuid $SubscriptionId $approved.Target.SubscriptionId) -or -not (Test-SameGuid $TenantId $approved.Target.TenantId)) {
        $result.code = 'UNAPPROVED_TARGET'
    } elseif (Get-Module -ListAvailable Az.Accounts) {
        Import-Module Az.Accounts -ErrorAction Stop
        $contexts = @(Get-AzContext -ListAvailable | Where-Object {
            $_.Subscription.Id -eq $SubscriptionId -and $_.Tenant.Id -eq $TenantId -and $_.Environment.Name -eq 'AzureCloud'
        })
        if ($contexts.Count -gt 0) {
            $context = $contexts[0]
            $result.contextAvailable = $true
            $result.subscriptionId = $SubscriptionId
            $result.tenantId = $TenantId
            $result.code = 'EXISTING_TOKEN_UNAVAILABLE'
            $token = Get-AzAccessToken -ResourceUrl 'https://management.azure.com/' -TenantId $TenantId -DefaultProfile $context -ErrorAction Stop
            $result.tokenAcquired = $true
            $result.expiresOn = $token.ExpiresOn.ToUniversalTime().ToString('o')
            $token = $null
            $result.code = 'ARM_READ_UNAUTHORIZED_OR_UNAVAILABLE'
            $response = Invoke-AzRestMethod -Path "/subscriptions/$SubscriptionId`?api-version=2022-12-01" `
                -Method GET -DefaultProfile $context -ErrorAction Stop
            if ($response.StatusCode -eq 200) {
                $subscription = $response.Content | ConvertFrom-Json
                if ($subscription.subscriptionId -eq $SubscriptionId -and $subscription.tenantId -eq $TenantId -and $subscription.state -eq 'Enabled') {
                    $result.armAuthorized = $true
                    $result.code = 'EXISTING_IDENTITY_ARM_VERIFIED'
                }
            }
        }
    } else { $result.code = 'TOOL_UNAVAILABLE' }
} catch {
    # Keep only the phase-specific failure code. Authentication exceptions can contain account details.
    $result.armAuthorized = $false
}
$result | ConvertTo-Json -Compress
