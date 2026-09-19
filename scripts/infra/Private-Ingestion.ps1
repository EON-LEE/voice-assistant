[CmdletBinding()]
param([switch]$ValidateOnly)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
Import-Module (Join-Path $PSScriptRoot 'Knowledge.psm1')
Import-Module (Join-Path $PSScriptRoot 'KnowledgeSync.psm1')
Import-Module (Join-Path $PSScriptRoot 'ManagedIngestion.psm1')
$manifestPath = [IO.Path]::GetTempFileName()
$phase = 'plan'
try {
    $root = Join-Path $PSScriptRoot '..\..\infra\private-ingestion'
    $manifest = @{
        version = 1
        documents = @(@{
            documentId = 'private-ingestion-synthetic'
            operation = 'upsert'
            path = 'approved-synthetic.md'
            title = 'Fictional Lighthouse practice meeting - synthetic verification only'
            url = 'https://example.invalid/voice-assistant/synthetic-lighthouse'
            updatedAt = $env:INGEST_DOCUMENT_UPDATED_AT
            allowedPrincipalIds = @($env:INGEST_ALLOWED_PRINCIPAL_ID)
        })
    }
    $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
    $plan = New-KnowledgePlan -ManifestPath $manifestPath -ContentRoot $root -IndexName $env:INGEST_INDEX_NAME
    if ($ValidateOnly) {
        Write-Output '{"status":"PASS","scope":"offline-synthetic-plan","uploaded":false}'
        return
    }
    $phase = 'maintenance'
    if ($env:INGEST_CONFIRM_EXCLUSIVE_MAINTENANCE -cne 'true') { throw 'An explicit exclusive-maintenance acknowledgement is required.' }
    if ($env:INGEST_CREATE_INDEX -cnotin @('true', 'false')) { throw 'Create-index intent must be explicit.' }
    $storageName = $env:INGEST_STORAGE_NAME
    if ($storageName -cnotmatch '^[a-z0-9]{3,24}$') { throw 'Invalid private storage name.' }
    $searchRoot = "https://$($env:INGEST_SEARCH_NAME).search.windows.net"
    $openAIRoot = "https://$($env:INGEST_OPENAI_NAME).openai.azure.com"
    $storageHost = "$storageName.blob.core.windows.net"
    $storageRoot = "https://$storageHost/documents"
    $phase = 'private-dns'
    $addresses = @([Net.Dns]::GetHostAddresses($storageHost) | ForEach-Object { $_.IPAddressToString })
    Assert-PrivateBlobAddress -ExpectedIp $env:INGEST_BLOB_PRIVATE_IP -ResolvedAddresses $addresses
    $phase = 'identity-configuration'
    $request = New-ManagedIngestionRequest -IdentityEndpoint $env:IDENTITY_ENDPOINT -IdentityHeader $env:IDENTITY_HEADER `
        -ClientId $env:AZURE_CLIENT_ID -SearchRoot $searchRoot -StorageRoot $storageRoot -OpenAIRoot $openAIRoot
    $phase = 'sync'
    Invoke-KnowledgeSync -Plan $plan -Request $request -SearchRoot $searchRoot -StorageRoot $storageRoot -OpenAIRoot $openAIRoot `
        -EmbeddingDeployment $env:INGEST_EMBEDDING_DEPLOYMENT -CreateIndex:($env:INGEST_CREATE_INDEX -eq 'true')
    Write-Output '{"status":"PASS","scope":"approved-synthetic-ingestion","documents":1,"verifyAclBeforeRestart":true}'
} catch {
    [Console]::Error.WriteLine((@{ status = 'FAIL'; scope = 'approved-synthetic-ingestion'; phase = $phase;
        details = 'No content or credentials logged. Inspect job identity, private DNS, RBAC, schema and maintenance state before explicit retry.' } | ConvertTo-Json -Compress))
    exit 1
} finally {
    Remove-Item -LiteralPath $manifestPath -Force
}
