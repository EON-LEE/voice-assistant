Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'Knowledge.psm1')

function Invoke-KnowledgeSync {
    param(
        [Parameter(Mandatory)]$Plan,
        [Parameter(Mandatory)][scriptblock]$Request,
        [Parameter(Mandatory)][string]$SearchRoot,
        [Parameter(Mandatory)][string]$StorageRoot,
        [Parameter(Mandatory)][string]$OpenAIRoot,
        [Parameter(Mandatory)][string]$EmbeddingDeployment,
        [switch]$CreateIndex
    )
    $indexRoot = "$SearchRoot/indexes/$($Plan.schema.name)"
    $searchVersion = 'api-version=2024-07-01'
    $documentRoot = "$StorageRoot/$($Plan.schema.name)"
    $stateUri = "$documentRoot/_ingestion-state.json"
    $context = @{ lease = ''; renewed = [datetime]::MinValue }
    function Invoke-Operation {
        param($Method, $Uri, $Audience, $Body = $null, $Headers = @{}, $ContentType = 'application/json; charset=utf-8', $Expected = @(200, 201, 202, 204))
        if ($context.lease -and ([datetime]::UtcNow - $context.renewed).TotalSeconds -gt 15) {
            $started = [datetime]::UtcNow
            $null = & $Request 'PUT' "$stateUri`?comp=lease" 'storage' $null @{
                'x-ms-lease-action' = 'renew'; 'x-ms-lease-id' = $context.lease
            } 'application/json' @(200) $context.renewed.AddSeconds(35)
            $context.renewed = $started
        }
        $deadline = if ($context.lease) { $context.renewed.AddSeconds(35) } else { [datetime]::MaxValue }
        & $Request $Method $Uri $Audience $Body $Headers $ContentType $Expected $deadline
    }
    function Save-State {
        $null = Invoke-Operation 'PUT' $stateUri 'storage' ($state | ConvertTo-Json -Depth 20 -Compress) @{
            'x-ms-blob-type' = 'BlockBlob'; 'x-ms-lease-id' = $context.lease
        }
    }
    function Write-IndexBatch {
        param([object[]]$Values)
        if ($Values.Count -eq 0) { return }
        $result = Invoke-Operation 'POST' "$indexRoot/docs/index?$searchVersion" 'search' (@{ value = $Values } | ConvertTo-Json -Depth 30 -Compress) @{} 'application/json; charset=utf-8' @(200, 207)
        if (@($result.Data.value).Count -ne $Values.Count -or @($result.Data.value | Where-Object { $_.status -ne $true }).Count -gt 0) {
            throw 'Search rejected one or more document operations. Partial writes are possible; keep readers stopped and retry the same approved manifest.'
        }
    }
    function Find-Chunks {
        param([string]$Id)
        $result = Invoke-Operation 'POST' "$indexRoot/docs/search?$searchVersion" 'search' (@{
            search = '*'; filter = "documentId eq '$Id'"; select = 'id,updatedAt,sourceHash'; top = 1000
        } | ConvertTo-Json -Compress)
        return @($result.Data.value)
    }

    $null = Invoke-Operation 'PUT' $stateUri 'storage' '{"version":1,"documents":[]}' @{
        'x-ms-blob-type' = 'BlockBlob'; 'If-None-Match' = '*'
    } 'application/json; charset=utf-8' @(201, 409, 412)
    $lease = [guid]::NewGuid().ToString()
    $started = [datetime]::UtcNow
    $null = & $Request 'PUT' "$stateUri`?comp=lease" 'storage' $null @{
        'x-ms-lease-action' = 'acquire'; 'x-ms-lease-duration' = '60'; 'x-ms-proposed-lease-id' = $lease
    } 'application/json' @(201) ([datetime]::MaxValue)
    $context.lease = $lease
    $context.renewed = $started
    try {
        $state = (Invoke-Operation 'GET' $stateUri 'storage').Data
        if ($state.version -ne 1) { throw 'Unsupported ingestion state version; do not remove the version/deletion ledger.' }
        if ($CreateIndex) {
            $null = Invoke-Operation 'PUT' "$indexRoot`?$searchVersion" 'search' ($Plan.schema | ConvertTo-Json -Depth 30 -Compress) @{
                'If-None-Match' = '*'
            }
        }
        $remoteSchema = (Invoke-Operation 'GET' "$indexRoot`?$searchVersion" 'search').Data
        Assert-KnowledgeSchema $remoteSchema $Plan.schema.name
        foreach ($document in $Plan.documents) {
            $existing = @($state.documents | Where-Object documentId -CEQ $document.documentId)
            if ($existing.Count -gt 1) { throw 'Duplicate document state found; ingestion ledger requires operator repair.' }
            if ($existing.Count -eq 1) { Assert-KnowledgeVersion $document $existing[0] }
            $previousChunks = @(Find-Chunks $document.documentId)
            foreach ($chunk in $previousChunks) { Assert-KnowledgeVersion $document $chunk }
            $record = [pscustomobject]@{ documentId = $document.documentId; updatedAt = $document.updatedAt;
                sourceHash = $document.sourceHash; operation = $document.operation; status = 'pending' }
            $state.documents = @($state.documents | Where-Object documentId -CNE $document.documentId) + @($record)
            Save-State

            # Delete first: revoked ACLs and shortened documents must never leave old chunks behind.
            $attempt = 0
            while ($previousChunks.Count -gt 0) {
                if ($attempt -ge 20) { throw 'Search deletion has not converged. Keep readers stopped and retry; old vectors must not coexist with replacement ACLs.' }
                $deletes = @($previousChunks | ForEach-Object { @{ '@search.action' = 'delete'; id = $_.id } })
                Write-IndexBatch $deletes
                $previousChunks = @(Find-Chunks $document.documentId)
                if ($previousChunks.Count -gt 0) { Start-Sleep -Seconds 1 }
                $attempt++
            }
            $blobUri = "$documentRoot/$($document.documentId).txt"
            $null = Invoke-Operation 'DELETE' $blobUri 'storage' $null @{} 'application/json' @(202, 404)
            if ($document.operation -eq 'upsert') {
                $batch = @()
                foreach ($chunk in $document.chunks) {
                    $deployment = [uri]::EscapeDataString($EmbeddingDeployment)
                    $embedding = (Invoke-Operation 'POST' "$OpenAIRoot/openai/deployments/$deployment/embeddings?api-version=2024-10-21" 'cognitive' (@{
                        input = $chunk.content; dimensions = 1536
                    } | ConvertTo-Json -Compress)).Data
                    if (@($embedding.data).Count -ne 1 -or @($embedding.data[0].embedding).Count -ne 1536) { throw 'Embedding service returned an incompatible vector shape.' }
                    $vector = @($embedding.data[0].embedding)
                    foreach ($value in $vector) {
                        if ($value -isnot [ValueType] -or $value -is [bool] -or
                            [double]::IsNaN([double]$value) -or [double]::IsInfinity([double]$value)) { throw 'Embedding vector contains invalid numeric values.' }
                    }
                    $upload = [ordered]@{ '@search.action' = 'upload' }
                    foreach ($property in $chunk.PSObject.Properties) { $upload[$property.Name] = $property.Value }
                    $upload.contentVector = $vector
                    $batch += $upload
                    if ($batch.Count -eq 100) { Write-IndexBatch $batch; $batch = @() }
                }
                Write-IndexBatch $batch
                $null = Invoke-Operation 'PUT' $blobUri 'storage' $document.text @{
                    'x-ms-blob-type' = 'BlockBlob'
                } 'text/plain; charset=utf-8'
            }
            $record.status = 'complete'
            Save-State
        }
    } finally {
        try {
            $null = & $Request 'PUT' "$stateUri`?comp=lease" 'storage' $null @{
                'x-ms-lease-action' = 'release'; 'x-ms-lease-id' = $lease
            } 'application/json' @(200) ([datetime]::MaxValue)
        } catch {
            Write-Warning 'Could not release the ingestion lease; it expires within 60 seconds. Verify connectivity before another run.'
        }
    }
}

Export-ModuleMember -Function Invoke-KnowledgeSync
