Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-TextHash {
    param([string]$Text)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($Text)))).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
}

function Get-RequiredProperty {
    param($Object, [string]$Name)
    if (-not $Object.PSObject.Properties[$Name] -or $null -eq $Object.$Name) { throw "Missing required property: $Name." }
    return $Object.$Name
}

function Get-KnowledgeSchema {
    param([string]$IndexName = 'meeting-knowledge')
    if ($IndexName -cnotmatch '^[a-z0-9][a-z0-9-]{0,126}[a-z0-9]$' -or $IndexName.Contains('--')) { throw 'Invalid Search index name.' }
    $schema = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\..\infra\knowledge\index.json') -Raw | ConvertFrom-Json
    $schema.name = $IndexName
    return $schema
}

function Assert-KnowledgeSchema {
    param($Schema, [string]$IndexName = 'meeting-knowledge')
    $expected = Get-KnowledgeSchema $IndexName
    if ($Schema.name -cne $expected.name) { throw 'Search index name mismatch.' }
    foreach ($field in $expected.fields) {
        $actual = @($Schema.fields | Where-Object name -CEQ $field.name)
        if ($actual.Count -ne 1) { throw "Missing/duplicate index field: $($field.name)." }
        foreach ($property in $field.PSObject.Properties) {
            if (-not $actual[0].PSObject.Properties[$property.Name] -or $actual[0].($property.Name) -cne $property.Value) {
                throw "Incompatible index field: $($field.name).$($property.Name)."
            }
        }
    }
    $profile = @($Schema.vectorSearch.profiles | Where-Object name -CEQ 'meeting-vector-profile')
    $algorithm = @($Schema.vectorSearch.algorithms | Where-Object name -CEQ 'meeting-hnsw')
    if ($profile.Count -ne 1 -or $profile[0].algorithm -cne 'meeting-hnsw' -or
        $algorithm.Count -ne 1 -or $algorithm[0].kind -cne 'hnsw' -or $algorithm[0].hnswParameters.metric -cne 'cosine') {
        throw 'Search vector profile/algorithm mismatch.'
    }
}

function ConvertTo-VersionTime {
    param([string]$Value)
    if ($Value -notmatch '^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{1,7})?(Z|[+-]\d{2}:\d{2})$') { throw 'updatedAt must be an explicit RFC3339 timestamp with timezone.' }
    return [datetimeoffset]::Parse($Value, [Globalization.CultureInfo]::InvariantCulture).ToUniversalTime()
}

function Split-KnowledgeText {
    param([string]$Text, [int]$ChunkSize = 1500, [int]$Overlap = 150)
    if ($ChunkSize -lt 100 -or $ChunkSize -gt 2000 -or $Overlap -lt 0 -or $Overlap -ge $ChunkSize) { throw 'Invalid bounded chunk size/overlap.' }
    $start = 0
    while ($start -lt $Text.Length) {
        $end = [Math]::Min($start + $ChunkSize, $Text.Length)
        if ($end -lt $Text.Length -and [char]::IsHighSurrogate($Text[$end - 1])) { $end-- }
        $Text.Substring($start, $end - $start)
        if ($end -eq $Text.Length) { break }
        $start = $end - $Overlap
        if ($start -gt 0 -and [char]::IsLowSurrogate($Text[$start])) { $start-- }
    }
}

function New-KnowledgePlan {
    param([Parameter(Mandatory)][string]$ManifestPath, [Parameter(Mandatory)][string]$ContentRoot,
        [string]$IndexName = 'meeting-knowledge')
    $schema = Get-KnowledgeSchema $IndexName
    Assert-KnowledgeSchema $schema $IndexName
    $manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
    if ((Get-RequiredProperty $manifest 'version') -ne 1) { throw 'Unsupported manifest version.' }
    $entries = @(Get-RequiredProperty $manifest 'documents')
    if ($entries.Count -eq 0 -or $entries.Count -gt 100) { throw 'A manifest must contain 1-100 explicitly approved document operations.' }
    $root = (Get-Item -LiteralPath $ContentRoot).FullName.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    $seen = @{}
    $documents = @()
    foreach ($entry in $entries) {
        $id = [string](Get-RequiredProperty $entry 'documentId')
        if ($id -cnotmatch '^[a-z0-9][a-z0-9_-]{0,63}$' -or $seen.ContainsKey($id)) { throw 'documentId must be unique lowercase alphanumeric/dash/underscore, 1-64 characters.' }
        $seen[$id] = $true
        $operation = [string](Get-RequiredProperty $entry 'operation')
        if ($operation -cnotin @('upsert', 'delete')) { throw 'operation must be upsert or delete.' }
        $updatedAt = (ConvertTo-VersionTime (Get-RequiredProperty $entry 'updatedAt')).ToString('o')
        if ($operation -eq 'delete') {
            $documents += [pscustomobject]@{ documentId = $id; operation = $operation; updatedAt = $updatedAt;
                sourceHash = (Get-TextHash "delete|$id|$updatedAt"); chunks = @(); text = '' }
            continue
        }
        $relative = [string](Get-RequiredProperty $entry 'path')
        if ([IO.Path]::IsPathRooted($relative)) { throw 'Document paths must be relative to ContentRoot.' }
        $path = [IO.Path]::GetFullPath((Join-Path $root $relative))
        if (-not $path.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { throw 'Document path escapes ContentRoot.' }
        $file = Get-Item -LiteralPath $path
        if ($file.PSIsContainer -or $file.Extension -notin @('.txt', '.md') -or $file.Length -gt 1MB) { throw 'Only UTF-8 .txt/.md files up to 1 MiB are accepted.' }
        $ancestor = $file
        while ($null -ne $ancestor) {
            if ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Symlinks/reparse points are not accepted for approved source documents.' }
            $ancestor = if ($ancestor -is [IO.FileInfo]) { $ancestor.Directory } else { $ancestor.Parent }
        }
        $encoding = New-Object Text.UTF8Encoding($false, $true)
        $text = $encoding.GetString([IO.File]::ReadAllBytes($path)).TrimStart([char]0xFEFF).Replace("`r`n", "`n").Replace("`r", "`n")
        if ([string]::IsNullOrWhiteSpace($text) -or $text.Contains([string][char]0)) { throw 'Source document must contain nonempty UTF-8 text without NUL characters.' }
        $title = [string](Get-RequiredProperty $entry 'title')
        if ([string]::IsNullOrWhiteSpace($title) -or $title.Length -gt 300) { throw 'Document title must be 1-300 characters.' }
        $url = [string](Get-RequiredProperty $entry 'url')
        $uri = $null
        if (-not [uri]::TryCreate($url, [UriKind]::Absolute, [ref]$uri) -or $uri.Scheme -ne 'https' -or
            $uri.UserInfo -or $url.Length -gt 2048) { throw 'Source url must be an approved HTTPS URL without credentials, at most 2048 characters.' }
        $principals = @(Get-RequiredProperty $entry 'allowedPrincipalIds')
        if ($principals.Count -eq 0 -or $principals.Count -gt 100) { throw 'Every upsert requires 1-100 explicit Entra user object IDs; empty/public ACLs are rejected. Use delete to revoke all access.' }
        $normalized = @()
        foreach ($principal in $principals) {
            $guid = [guid]::Empty
            if (-not [guid]::TryParse([string]$principal, [ref]$guid) -or $guid -eq [guid]::Empty) { throw 'ACL values must be nonzero Entra user object GUIDs, not names, groups or wildcard permissions.' }
            $normalized += $guid.ToString()
        }
        $normalized = @($normalized | Sort-Object -Unique)
        $hash = Get-TextHash (([ordered]@{ text = $text; title = $title; url = $url; updatedAt = $updatedAt; allowedPrincipalIds = $normalized }) | ConvertTo-Json -Depth 10 -Compress)
        $chunks = @()
        $index = 0
        foreach ($chunk in @(Split-KnowledgeText $text)) {
            $chunks += [pscustomobject]@{ id = ('{0}_{1:d6}' -f $id, $index); documentId = $id; sourceHash = $hash;
                title = $title; content = $chunk; url = $url; updatedAt = $updatedAt; allowedPrincipalIds = $normalized }
            $index++
        }
        $documents += [pscustomobject]@{ documentId = $id; operation = $operation; updatedAt = $updatedAt; sourceHash = $hash; chunks = $chunks; text = $text }
    }
    return [pscustomobject]@{ schema = $schema; documents = $documents }
}

function Assert-KnowledgeVersion {
    param($Incoming, $Existing)
    if ($null -eq $Existing) { return }
    $incomingTime = ConvertTo-VersionTime $Incoming.updatedAt
    $existingTime = ConvertTo-VersionTime $Existing.updatedAt
    if ($incomingTime -lt $existingTime -or
        ($incomingTime -eq $existingTime -and $Incoming.sourceHash -cne $Existing.sourceHash)) {
        throw 'Stale or conflicting document version rejected. Changes to content, ACLs, metadata, or deletion require a strictly newer updatedAt.'
    }
}

Export-ModuleMember -Function Get-KnowledgeSchema, Assert-KnowledgeSchema, New-KnowledgePlan, Assert-KnowledgeVersion, Split-KnowledgeText
