if (-not $env:VOICE_INFRA_FIXTURE_LOG) { throw 'This is an offline test fixture, not an Azure authentication module.' }
function Get-AzContext {
    param([switch]$ListAvailable)
    [pscustomobject]@{
        Subscription = @{ Id = 'b0af194e-77a5-4471-bb43-67e78295b5c8' }
        Tenant = @{ Id = '2573db8c-dfe5-4805-9e28-a0859692e705' }
        Environment = @{ Name = 'AzureCloud' }
    }
}
function Get-AzAccessToken {
    param($ResourceUrl, $TenantId, $DefaultProfile)
    if ($env:VOICE_INFRA_FIXTURE_MODE -eq 'token-failure') { throw 'PRIVATE_TOKEN_SENTINEL' }
    [pscustomobject]@{
        Token = (ConvertTo-SecureString 'PRIVATE_TOKEN_SENTINEL' -AsPlainText -Force)
        ExpiresOn = [datetimeoffset]::UtcNow.AddHours(1)
    }
}
function Invoke-AzRestMethod {
    param($Path, $Method, $DefaultProfile, $Payload)
    @{ path = $Path; method = $Method; payload = $Payload } | ConvertTo-Json -Compress | Add-Content -LiteralPath $env:VOICE_INFRA_FIXTURE_LOG
    $status = 200
    $headers = @{}
    $tags = @{ managedBy = 'voice-assistant-bootstrap'; projectId = 'ff8f97b4-5db4-458e-99de-0d63cfc96a4a' }
    if ($env:VOICE_INFRA_FIXTURE_MODE -eq 'failure') { throw 'PRIVATE_DIAGNOSTIC_SENTINEL' }
    if ($Path -match '/deployments/fixture-deployment') {
        if ($Path -match '/whatIf\?') {
            $status = 202
            $location = 'https://management.azure.com/subscriptions/b0af194e-77a5-4471-bb43-67e78295b5c8/providers/Microsoft.Resources/operationResults/fixture?api-version=2022-09-01&sig=PRIVATE_POLL_SENTINEL%2B%2f%3D'
            if ($env:VOICE_INFRA_FIXTURE_MODE -eq 'header-dictionary') { $headers.Location = $location }
            else {
                $headers = New-Object 'Collections.Generic.List[Collections.Generic.KeyValuePair[string,string[]]]'
                $values = [string[]]@($location)
                if ($env:VOICE_INFRA_FIXTURE_MODE -eq 'header-duplicate-values') { $values = @($location, $location) }
                if ($env:VOICE_INFRA_FIXTURE_MODE -eq 'header-bad-host') { $values = @($location.Replace('management.azure.com', 'example.invalid')) }
                $key = if ($env:VOICE_INFRA_FIXTURE_MODE -eq 'header-missing') { 'Retry-After' } else { 'lOcAtIoN' }
                $headers.Add([Collections.Generic.KeyValuePair[string,string[]]]::new($key, $values))
                if ($env:VOICE_INFRA_FIXTURE_MODE -eq 'header-duplicate-entries') {
                    $headers.Add([Collections.Generic.KeyValuePair[string,string[]]]::new('Location', [string[]]@($location)))
                }
            }
            $body = @{ status = 'Running' }
        } elseif ($Path -match '/validate\?') { $body = @{ properties = @{ provisioningState = 'Succeeded' } } }
        elseif ($Method -eq 'PUT') { $status = 201; $body = @{ properties = @{ provisioningState = 'Running' } } }
        else { $body = @{ properties = @{ provisioningState = 'Succeeded'; outputs = @{ browserUrl = @{ value = 'https://fixture.invalid' } } } } }
    } elseif ($Path -match '/operationResults/') {
        $body = @{ status = 'Succeeded'; properties = @{ correlationId = 'fixture-correlation';
            changes = @(@{ resourceId = '/fixture'; changeType = 'Create'; after = @{ private = 'PRIVATE_DIFF_SENTINEL' } }) } }
        if ($env:VOICE_INFRA_FIXTURE_MODE -eq 'whatif-empty-changes') { $body.properties.changes = @() }
        if ($env:VOICE_INFRA_FIXTURE_MODE -eq 'whatif-missing-changes') { $body.properties.Remove('changes') }
    } elseif ($Path -match '/checkNameAvailability') { $body = @{ nameAvailable = $true } }
    elseif ($Path -match '/registries') {
        $registry = @{ name = 'fixturemeetingacr'; location = 'eastus'; tags = $tags; sku = @{ name = 'Basic' }
            properties = @{ provisioningState = 'Succeeded'; adminUserEnabled = $false; roleAssignmentMode = 'LegacyRegistryPermissions' } }
        if ($Path -match '/registries\?') { $body = @{ value = @($registry) } } else { $body = $registry }
    } elseif ($Path -match '/Microsoft.Search/searchServices/') {
        $body = @{ name = 'fixture-search'; properties = @{ disableLocalAuth = $true } }
    } elseif ($Path -match '/Microsoft.CognitiveServices/accounts/') {
        $body = @{ name = 'fixture-ai'; kind = 'OpenAI'; properties = @{ customSubDomainName = 'fixture-ai'; disableLocalAuth = $true } }
    } elseif ($Path -match '/Microsoft.Storage/storageAccounts/') {
        $body = @{ name = 'fixturestorage'; properties = @{ allowSharedKeyAccess = $false } }
    } elseif ($Path -match '/resourcegroups/fixture-missing') { $status = 404; $body = @{ error = @{ code = 'ResourceGroupNotFound' } } }
    elseif ($Path -match '/resourcegroups/') { $body = @{ name = 'fixture-meeting-group'; location = 'eastus'; tags = $tags } }
    elseif ($Path -match '/providers/') { $body = @{ registrationState = 'Registered' } }
    else { $body = @{ subscriptionId = 'b0af194e-77a5-4471-bb43-67e78295b5c8'; tenantId = '2573db8c-dfe5-4805-9e28-a0859692e705'; state = 'Enabled' } }
    [pscustomobject]@{ StatusCode = $status; Headers = $headers; Content = ($body | ConvertTo-Json -Depth 10 -Compress) }
}
Export-ModuleMember -Function Get-AzContext, Get-AzAccessToken, Invoke-AzRestMethod
