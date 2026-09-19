if (-not $env:VOICE_INFRA_FIXTURE_LOG) { throw 'This is an offline test fixture, not an Azure authentication module.' }
function Get-AzContext {
    param([switch]$ListAvailable)
    [pscustomobject]@{
        Subscription = @{ Id = 'b0af194e-77a5-4471-bb43-67e78295b5c8' }
        Tenant = @{ Id = '2573db8c-dfe5-4805-9e28-a0859692e705' }
        Environment = @{ Name = 'AzureCloud' }
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
            $headers.Location = 'https://management.azure.com/subscriptions/b0af194e-77a5-4471-bb43-67e78295b5c8/providers/Microsoft.Resources/operationResults/fixture?api-version=2022-09-01'
            $body = @{ status = 'Running' }
        } elseif ($Path -match '/validate\?') { $body = @{ properties = @{ provisioningState = 'Succeeded' } } }
        elseif ($Method -eq 'PUT') { $status = 201; $body = @{ properties = @{ provisioningState = 'Running' } } }
        else { $body = @{ properties = @{ provisioningState = 'Succeeded'; outputs = @{ browserUrl = @{ value = 'https://fixture.invalid' } } } } }
    } elseif ($Path -match '/operationResults/') {
        $body = @{ status = 'Succeeded'; changes = @(@{ resourceId = '/fixture'; changeType = 'Create' }) }
    } elseif ($Path -match '/checkNameAvailability') { $body = @{ nameAvailable = $true } }
    elseif ($Path -match '/registries') {
        $registry = @{ name = 'fixturemeetingacr'; location = 'eastus'; tags = $tags; sku = @{ name = 'Basic' }
            properties = @{ provisioningState = 'Succeeded'; adminUserEnabled = $false; roleAssignmentMode = 'LegacyRegistryPermissions' } }
        if ($Path -match '/registries\?') { $body = @{ value = @($registry) } } else { $body = $registry }
    } elseif ($Path -match '/resourcegroups/fixture-missing') { $status = 404; $body = @{ error = @{ code = 'ResourceGroupNotFound' } } }
    elseif ($Path -match '/resourcegroups/') { $body = @{ name = 'fixture-meeting-group'; location = 'eastus'; tags = $tags } }
    elseif ($Path -match '/providers/') { $body = @{ registrationState = 'Registered' } }
    else { $body = @{ subscriptionId = 'b0af194e-77a5-4471-bb43-67e78295b5c8'; tenantId = '2573db8c-dfe5-4805-9e28-a0859692e705'; state = 'Enabled' } }
    [pscustomobject]@{ StatusCode = $status; Headers = $headers; Content = ($body | ConvertTo-Json -Depth 10 -Compress) }
}
Export-ModuleMember -Function Get-AzContext, Invoke-AzRestMethod
