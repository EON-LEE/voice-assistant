[CmdletBinding()]
param(
    [string]$ConfigPath,
    [string]$Ref = 'origin/main',
    [string]$Prefix = 'rel',
    [string]$WorkRoot = 'C:\t',
    [string]$ImageDigest,
    [object]$SmokeTest,
    [switch]$Production,
    [switch]$Apply,
    [string]$SubscriptionId,
    [string]$TenantId,
    [string]$ResourceGroup,
    [string]$StagingResourceGroup,
    [string]$RegistryName,
    [string]$StagingApp,
    [string]$ProductionApp,
    [string]$AzPath = 'az',
    [string[]]$AzPrefix = @(),
    [string]$GitPath = 'git',
    [string]$TarPath = 'tar',
    [ValidateRange(1, 180)][int]$BuildTimeoutMinutes = 45,
    [ValidateRange(10, 3600)][int]$ReadyTimeoutSeconds = 600,
    [ValidateRange(1, 120)][int]$PollSeconds = 15
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot '..\infra\ReleaseTarget.psm1') -Force
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))

# Sent on every API response (src/VoiceAssistant.Api/Program.cs); each value must match all of its patterns.
$expectedHeaders = [ordered]@{
    'Strict-Transport-Security' = @('max-age=\d{8,}')
    'X-Content-Type-Options' = @('^nosniff$')
    'Referrer-Policy' = @('^no-referrer$')
    'Content-Security-Policy' = @("script-src 'self'", "frame-ancestors 'self'", "object-src 'none'")
    'Permissions-Policy' = @('camera=\(\)', 'microphone=\(self\)')
    'Cache-Control' = @('no-store')
}

function Write-Step {
    param([string]$Text)
    Write-Host $Text
}

function Get-Field {
    # Strict-mode-safe lookup of nested JSON properties; a missing step yields $null.
    param($Object, [string[]]$Path)
    $value = $Object
    foreach ($name in $Path) {
        if ($null -eq $value) { return $null }
        $property = $value.PSObject.Properties[$name]
        if (-not $property) { return $null }
        $value = $property.Value
    }
    return $value
}

function Get-Setting {
    param([string]$Explicit, [string]$Name, [string]$Default = '')
    if (-not [string]::IsNullOrWhiteSpace($Explicit)) { return $Explicit.Trim() }
    $value = Get-Field $config @($Name)
    if ($value -is [string] -and -not [string]::IsNullOrWhiteSpace($value)) { return $value.Trim() }
    return $Default
}

function Test-Guid {
    param([string]$Value)
    $guid = [guid]::Empty
    return ([guid]::TryParse($Value, [ref]$guid) -and $guid -ne [guid]::Empty)
}

function Test-Name {
    param([string]$Value, [string]$Pattern)
    return ($Value -and $Value -notlike 'REPLACE_*' -and $Value -cmatch $Pattern -and $Value -notmatch '--')
}

function Invoke-Git {
    param([string[]]$Arguments)
    $global:LASTEXITCODE = 0
    $output = & $GitPath -C $repoRoot @Arguments
    if ($LASTEXITCODE -ne 0) { throw "git $($Arguments[0]) failed (exit code $LASTEXITCODE)." }
    return @($output)
}

function Invoke-Az {
    param([string[]]$Arguments, [switch]$IgnoreExitCode)
    $previousEncoding = $env:PYTHONIOENCODING
    $previousPreference = $ErrorActionPreference
    # Without this, az crashes printing build-log characters such as a check mark on legacy code pages (for example cp949).
    $env:PYTHONIOENCODING = 'utf-8'
    try {
        $ErrorActionPreference = 'Continue'
        $global:LASTEXITCODE = 0
        $output = & $AzPath @AzPrefix @Arguments --only-show-errors --output json
        $code = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $previousPreference
        $env:PYTHONIOENCODING = $previousEncoding
    }
    $text = (@($output) | ForEach-Object { [string]$_ }) -join "`n"
    if ($code -ne 0 -and -not $IgnoreExitCode) { throw "az $($Arguments[0]) $($Arguments[1]) failed (exit code $code)." }
    $data = $null
    if (-not [string]::IsNullOrWhiteSpace($text)) {
        try { $data = $text | ConvertFrom-Json } catch { $data = $null }
    }
    return [pscustomobject]@{ ExitCode = $code; Data = $data; Text = $text }
}

function Get-Attempts {
    param([int]$Seconds)
    return [Math]::Max(1, [int][Math]::Ceiling($Seconds / [double]$PollSeconds))
}

function Get-ContainerApp {
    param([string]$App, [string]$Group)
    try { $shown = Invoke-Az @('containerapp', 'show', '--name', $App, '--resource-group', $Group, '--subscription', $target.subscriptionId) }
    catch { throw "Cannot read container app $App in $Group. Check the name, the resource group and your access." }
    if ($null -eq $shown.Data) { throw "Container app $App returned no metadata." }
    return $shown.Data
}

function Get-AppImage {
    param($Resource)
    return [string](Get-Field (@(Get-Field $Resource @('properties', 'template', 'containers')))[0] @('image'))
}

function New-SourceArchive {
    # A short path keeps az acr build clear of Windows long-path failures; git archive also leaves out untracked files.
    if (-not (Test-Path -LiteralPath $WorkRoot -PathType Container)) { $null = New-Item -ItemType Directory -Path $WorkRoot }
    $directory = Join-Path $WorkRoot ($tag + '-' + [DateTime]::UtcNow.ToString('HHmmss'))
    $archive = $directory + '.tar'
    if ((Test-Path -LiteralPath $directory) -or (Test-Path -LiteralPath $archive)) { throw "Work path already exists: $directory" }
    $null = Invoke-Git @('archive', '--format=tar', "--output=$archive", $commit)
    $null = New-Item -ItemType Directory -Path $directory
    $global:LASTEXITCODE = 0
    & $TarPath -xf $archive -C $directory
    if ($LASTEXITCODE -ne 0) { throw 'Extracting the source archive failed.' }
    if (-not (Test-Path -LiteralPath (Join-Path $directory 'src\VoiceAssistant.Api\Dockerfile') -PathType Leaf)) {
        throw 'The source archive has no src/VoiceAssistant.Api/Dockerfile.'
    }
    return [pscustomobject]@{ Directory = $directory; Archive = $archive }
}

function Find-BuildRun {
    param([datetimeoffset]$Since)
    $runs = Invoke-Az @('acr', 'task', 'list-runs', '--registry', $target.registryName, '--subscription', $target.subscriptionId, '--top', '10') -IgnoreExitCode
    $candidates = @(@($runs.Data) | Where-Object {
        # PowerShell 7 turns ISO timestamps into DateTime; Windows PowerShell keeps strings.
        $raw = Get-Field $_ @('createTime')
        $created = [datetimeoffset]::MinValue
        $known = if ($raw -is [datetime]) { $created = [datetimeoffset]$raw; $true } else {
            [datetimeoffset]::TryParse([string]$raw, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::AssumeUniversal, [ref]$created) }
        $_ -and $known -and $created -ge $Since.AddMinutes(-2) -and
            ((@(@(Get-Field $_ @('outputImages')) | Where-Object { $_ -and (Get-Field $_ @('tag')) -eq $tag }).Count -gt 0) -or
            ([string](Get-Field $_ @('status')) -in @('Queued', 'Started', 'Running')))
    })
    if ($candidates.Count -ne 1) {
        throw "Could not identify the build run for $tag. Inspect: az acr task list-runs --registry $($target.registryName) --subscription $($target.subscriptionId) --top 5"
    }
    return [string](Get-Field $candidates[0] @('runId'))
}

function Start-ImageBuild {
    $since = [datetimeoffset]::UtcNow
    $source = New-SourceArchive
    try {
        Write-Step "Uploading $commitShort from $($source.Directory) to ACR Tasks (queued with --no-wait)..."
        $build = Invoke-Az @('acr', 'build', '--registry', $target.registryName, '--subscription', $target.subscriptionId,
            '--image', "$($target.imageRepository):$tag", '--file', 'src/VoiceAssistant.Api/Dockerfile', '--no-wait', $source.Directory) -IgnoreExitCode
    } finally {
        Remove-Item -LiteralPath $source.Directory -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $source.Archive -Force -ErrorAction SilentlyContinue
    }
    if ($build.ExitCode -ne 0) {
        Write-Step "az acr build exited with code $($build.ExitCode); the run may still be queued, so its status is read from ACR instead."
    }
    $runId = [string](Get-Field $build.Data @('runId'))
    if (-not $runId -and $build.Text -match 'ID:\s*([A-Za-z0-9]+)') { $runId = $Matches[1] }
    if (-not $runId) { $runId = Find-BuildRun $since }
    if ($runId -notmatch '^[A-Za-z0-9]{1,32}$') { throw 'ACR returned an invalid build run ID.' }
    return $runId
}

function Wait-BuildRun {
    param([string]$RunId)
    $logs = "az acr task logs --registry $($target.registryName) --subscription $($target.subscriptionId) --run-id $RunId"
    $lastStatus = ''
    for ($attempt = 1; $attempt -le (Get-Attempts ($BuildTimeoutMinutes * 60)); $attempt++) {
        # The CLI exit code is not trusted; the run status recorded by ACR is.
        $run = Invoke-Az @('acr', 'task', 'show-run', '--registry', $target.registryName, '--subscription', $target.subscriptionId, '--run-id', $RunId) -IgnoreExitCode
        $status = [string](Get-Field $run.Data @('status'))
        if ($status -and $status -ne $lastStatus) { Write-Step "Build run $($RunId): $status"; $lastStatus = $status }
        if ($status -eq 'Succeeded') {
            $images = @(@(Get-Field $run.Data @('outputImages')) | Where-Object {
                $_ -and (Get-Field $_ @('repository')) -eq $target.imageRepository -and (Get-Field $_ @('tag')) -eq $tag })
            $digest = if ($images.Count -eq 1) { [string](Get-Field $images[0] @('digest')) } else { '' }
            if ($digest -cnotmatch '^sha256:[a-f0-9]{64}$') { throw "Build run $RunId succeeded but reported no digest for $($target.imageRepository):$tag." }
            return $digest
        }
        if ($status -in @('Failed', 'Canceled', 'Error', 'Timeout')) { throw "Build run $RunId ended with status $status. Logs: $logs" }
        Start-Sleep -Seconds $PollSeconds
    }
    throw "Build run $RunId did not finish within $BuildTimeoutMinutes minutes. Status: az acr task show-run --registry $($target.registryName) --subscription $($target.subscriptionId) --run-id $RunId"
}

function Assert-TaggedDigest {
    $shown = Invoke-Az @('acr', 'repository', 'show', '--name', $target.registryName, '--subscription', $target.subscriptionId,
        '--image', "$($target.imageRepository):$tag")
    if ([string](Get-Field $shown.Data @('digest')) -cne $ImageDigest) {
        throw "The registry tag $($target.imageRepository):$tag does not point to -ImageDigest. Promote only a digest that this script built from $commitShort."
    }
}

function Wait-Revision {
    param([string]$App, [string]$Group, [string]$Revision, [string]$Label)
    for ($attempt = 1; $attempt -le (Get-Attempts $ReadyTimeoutSeconds); $attempt++) {
        $shown = Invoke-Az @('containerapp', 'revision', 'show', '--name', $App, '--resource-group', $Group,
            '--subscription', $target.subscriptionId, '--revision', $Revision) -IgnoreExitCode
        $provisioning = [string](Get-Field $shown.Data @('properties', 'provisioningState'))
        $running = [string](Get-Field $shown.Data @('properties', 'runningState'))
        if ($provisioning -eq 'Failed' -or $running -eq 'Failed') { throw "$Label revision $Revision failed ($provisioning/$running)." }
        if ($provisioning -eq 'Provisioned' -and (Get-Field $shown.Data @('properties', 'active')) -eq $true -and
            [string](Get-Field $shown.Data @('properties', 'healthState')) -ne 'Unhealthy') {
            if ((Get-AppImage $shown.Data) -cne $image) { throw "$Label revision $Revision does not run $image." }
            $current = Invoke-Az @('containerapp', 'show', '--name', $App, '--resource-group', $Group, '--subscription', $target.subscriptionId) -IgnoreExitCode
            if ([string](Get-Field $current.Data @('properties', 'latestReadyRevisionName')) -eq $Revision) { return }
        }
        Start-Sleep -Seconds $PollSeconds
    }
    throw "$Label revision $Revision was not provisioned, active and serving within $ReadyTimeoutSeconds seconds."
}

function Get-HttpProbe {
    param([string]$Uri)
    try {
        $response = Invoke-WebRequest -Uri $Uri -Method Get -UseBasicParsing -TimeoutSec 15 -MaximumRedirection 0 -ErrorAction Stop
    } catch {
        $status = 0
        if ($_.Exception.PSObject.Properties['Response'] -and $_.Exception.Response) {
            try { $status = [int]$_.Exception.Response.StatusCode } catch { $status = 0 }
        }
        return [pscustomobject]@{ Status = $status; Headers = @{} }
    }
    $headers = @{}
    if ($null -ne $response.Headers) {
        foreach ($key in @($response.Headers.Keys)) {
            $value = $response.Headers[$key]
            $headers[[string]$key] = if ($value -is [string]) { $value } else { (@($value) | ForEach-Object { [string]$_ }) -join ', ' }
        }
    }
    return [pscustomobject]@{ Status = [int]$response.StatusCode; Headers = $headers }
}

function Wait-Healthy {
    param([string]$BaseUrl, [string]$Label)
    $last = 0
    for ($attempt = 1; $attempt -le (Get-Attempts $ReadyTimeoutSeconds); $attempt++) {
        $probe = Get-HttpProbe "$BaseUrl/health/ready"
        $last = $probe.Status
        if ($probe.Status -eq 200) {
            $wrong = @(foreach ($name in $expectedHeaders.Keys) {
                $value = if ($probe.Headers.ContainsKey($name)) { [string]$probe.Headers[$name] } else { '' }
                if (-not $value -or @($expectedHeaders[$name] | Where-Object { $value -notmatch $_ }).Count) { $name }
            })
            if ($wrong.Count) { throw "$Label /health/ready returned 200, but these security headers are missing or wrong: $($wrong -join ', ')." }
            Write-Step "$($Label): /health/ready returned 200 with the expected security headers."
            return
        }
        Start-Sleep -Seconds $PollSeconds
    }
    throw "$Label /health/ready did not return 200 within $ReadyTimeoutSeconds seconds (last status $last)."
}

function Get-RollbackCommands {
    param([string]$App, [string]$Group, [string]$PreviousRevision, [string]$PreviousImage)
    $scope = "--name $App --resource-group $Group --subscription $($target.subscriptionId)"
    $previousSuffix = if ($PreviousRevision -like "$App--*") { $PreviousRevision.Substring($App.Length + 2) } else { '' }
    $rollbackSuffix = 'rb-' + $previousSuffix
    if (-not $previousSuffix -or -not (Test-Name $rollbackSuffix '^[a-z][a-z0-9-]*[a-z0-9]$') -or ("$App--$rollbackSuffix").Length -gt 64) {
        $rollbackSuffix = 'rb' + [DateTime]::UtcNow.ToString('yyMMddHHmm')
    }
    return @(
        '  # Single-revision mode (zero downtime): a new revision that runs the previous image',
        "  az containerapp update $scope --image $PreviousImage --revision-suffix $rollbackSuffix",
        '  # Or reactivate the retained revision (traffic weights apply in multiple-revision mode)',
        "  az containerapp revision activate $scope --revision $PreviousRevision",
        "  az containerapp ingress traffic set $scope --revision-weight $PreviousRevision=100"
    )
}

function Publish-Revision {
    param([string]$App, [string]$Group, [string]$Label)
    $revision = "$App--$suffix"
    $before = Get-ContainerApp $App $Group
    $previousRevision = [string](Get-Field $before @('properties', 'latestRevisionName'))
    $previousImage = Get-AppImage $before
    $fqdn = [string](Get-Field $before @('properties', 'configuration', 'ingress', 'fqdn'))
    if ($fqdn -notmatch '^[A-Za-z0-9.-]+$') { throw "$Label app $App has no external ingress FQDN." }
    $rollback = @(Get-RollbackCommands $App $Group $previousRevision $previousImage)
    if ($Label -eq 'Production') {
        Write-Step "Production rollback target: revision $previousRevision, image $previousImage"
        Write-Step 'If verification fails, roll back with:'
        $rollback | ForEach-Object { Write-Step $_ }
    }
    if ($previousRevision -eq $revision -and $previousImage -ceq $image) {
        Write-Step "$($Label): $revision already runs this image; verifying only."
    } else {
        $existing = Invoke-Az @('containerapp', 'revision', 'list', '--name', $App, '--resource-group', $Group,
            '--subscription', $target.subscriptionId, '--all')
        if (@(@($existing.Data) | Where-Object { $_ -and [string](Get-Field $_ @('name')) -eq $revision }).Count) {
            throw "$Label app $App already has revision $revision. Revision suffixes are single-use; pass another -Prefix."
        }
        Write-Step "$($Label): deploying $image as $revision..."
        $null = Invoke-Az @('containerapp', 'update', '--name', $App, '--resource-group', $Group, '--subscription', $target.subscriptionId,
            '--image', $image, '--revision-suffix', $suffix)
    }
    try {
        Wait-Revision $App $Group $revision $Label
        Wait-Healthy "https://$fqdn" $Label
    } catch {
        if ($Label -eq 'Production') {
            Write-Step 'Production verification failed. Roll back with:'
            $rollback | ForEach-Object { Write-Step $_ }
        }
        throw
    }
    return [pscustomobject]@{ Revision = $revision; Url = "https://$fqdn"; PreviousRevision = $previousRevision
        PreviousImage = $previousImage; Rollback = $rollback }
}

# 1. Release target: the git-ignored local JSON plus optional parameter overrides. Errors name fields, never values.
$configFile = Get-ReleaseConfigPath $ConfigPath
$config = Read-ReleaseConfig $ConfigPath
$target = [ordered]@{
    subscriptionId = Get-Setting $SubscriptionId 'subscriptionId'
    tenantId = Get-Setting $TenantId 'tenantId'
    resourceGroup = Get-Setting $ResourceGroup 'resourceGroup'
    stagingResourceGroup = Get-Setting $StagingResourceGroup 'stagingResourceGroup'
    registryName = Get-Setting $RegistryName 'registryName'
    imageRepository = Get-Setting '' 'imageRepository' 'voice-assistant'
    stagingApp = Get-Setting $StagingApp 'stagingApp'
    productionApp = Get-Setting $ProductionApp 'productionApp'
}
if (-not $target.stagingResourceGroup) { $target.stagingResourceGroup = $target.resourceGroup }
$invalid = @()
foreach ($name in @('subscriptionId', 'tenantId')) {
    if (-not (Test-Guid $target[$name])) { $invalid += $name; continue }
    # A parameter may restate the configured subscription or tenant, never replace it.
    $configured = [string](Get-Field $config @($name))
    if ((Test-Guid $configured) -and -not (Test-SameGuid $configured $target[$name])) { $invalid += "$name (differs from the configuration)" }
}
foreach ($name in @('resourceGroup', 'stagingResourceGroup')) {
    if (-not (Test-Name $target[$name] '^[A-Za-z0-9._()-]{1,90}$') -or $target[$name].EndsWith('.')) { $invalid += $name }
}
if (-not (Test-Name $target.registryName '^[a-z0-9]{5,50}$')) { $invalid += 'registryName' }
if (-not (Test-Name $target.imageRepository '^[a-z0-9]+(?:[._-][a-z0-9]+)*(?:/[a-z0-9]+(?:[._-][a-z0-9]+)*)*$')) { $invalid += 'imageRepository' }
$appPattern = '^[a-z][a-z0-9-]{0,30}[a-z0-9]$'
if (-not (Test-Name $target.stagingApp $appPattern)) { $invalid += 'stagingApp' }
if ($Production -and -not (Test-Name $target.productionApp $appPattern)) { $invalid += 'productionApp' }
if ($invalid.Count) {
    $where = if ($null -eq $config) { 'No release configuration file was found; copy infra/release.example.json to infra/release.local.json (git-ignored).' }
        else { "Fix them in $configFile or pass parameters." }
    throw "Release target is incomplete or invalid: $($invalid -join ', '). $where Values are not printed."
}
if ($Production -and $target.productionApp -eq $target.stagingApp -and $target.resourceGroup -eq $target.stagingResourceGroup) {
    throw 'stagingApp and productionApp must be different apps; otherwise staging verification would run on production.'
}
if ($Prefix -cnotmatch '^[a-z][a-z0-9]{0,15}$') { throw '-Prefix must be 1-16 lowercase letters or digits, starting with a letter.' }
if ($ImageDigest -and $ImageDigest -cnotmatch '^sha256:[a-f0-9]{64}$') { throw '-ImageDigest must look like sha256:<64 lowercase hex characters>.' }
if ($Ref -notmatch '^[A-Za-z0-9][A-Za-z0-9._/-]*$') { throw '-Ref must be a branch, tag or commit name.' }
if (-not $ImageDigest -and -not [IO.Path]::IsPathRooted($WorkRoot)) { throw '-WorkRoot must be an absolute, short path such as C:\t.' }
if ($null -ne $SmokeTest -and $SmokeTest -isnot [scriptblock]) {
    $smokePath = [string]$SmokeTest
    if ($smokePath -notlike '*.ps1' -or -not (Test-Path -LiteralPath $smokePath -PathType Leaf)) { throw '-SmokeTest must be a scriptblock or the path of an existing .ps1 file.' }
    $SmokeTest = [IO.Path]::GetFullPath($smokePath)
}

# 2. Source: a clean working tree whose HEAD is exactly the release ref.
if (@(Invoke-Git @('status', '--porcelain') | Where-Object { $_ -and "$_".Trim() }).Count) {
    throw 'The working tree is not clean (see git status). Commit or stash changes before releasing.'
}
$head = [string](@(Invoke-Git @('rev-parse', '--verify', 'HEAD^{commit}'))[0])
$commit = [string](@(Invoke-Git @('rev-parse', '--verify', "$Ref^{commit}"))[0])
if ($commit -notmatch '^[0-9a-f]{40}([0-9a-f]{24})?$' -or $head -ne $commit) {
    throw "HEAD is not $Ref. Fetch, then check out the release commit (git switch --detach $Ref), or pass the -Ref you checked out."
}
$commitShort = $commit.Substring(0, 7)
$tag = "$Prefix-$commitShort"
$suffix = $tag
$registryHost = "$($target.registryName).azurecr.io"
$scope = "--subscription $($target.subscriptionId)"

$mode = if ($Apply) { 'APPLY' } else { 'DRY RUN (nothing is built or deployed; add -Apply to execute)' }
Write-Step "Release plan - $mode"
Write-Step "  Source:     $commit ($Ref), working tree clean"
if ($ImageDigest) {
    Write-Step "  Image:      $registryHost/$($target.imageRepository)@$ImageDigest (tag $tag must point to it; no build)"
} else {
    Write-Step "  Image:      $registryHost/$($target.imageRepository):$tag, then pinned by digest"
    Write-Step "  Build:      git archive to $WorkRoot\$tag-<time>, az acr build --no-wait, poll az acr task show-run"
}
Write-Step "  Staging:    $($target.stagingApp) in $($target.stagingResourceGroup), revision $($target.stagingApp)--$suffix; wait for /health/ready 200 and security headers"
$smokeText = if ($null -eq $SmokeTest) { 'none' } elseif ($SmokeTest -is [scriptblock]) { 'scriptblock' } else { [string]$SmokeTest }
Write-Step "  Smoke test: $smokeText"
if ($Production) {
    Write-Step "  Production: $($target.productionApp) in $($target.resourceGroup), revision $($target.productionApp)--$suffix, same digest, after staging passes"
} else {
    Write-Step '  Production: not requested (add -Production)'
}
if (-not $Apply) {
    Write-Step 'Commands that -Apply runs (every az call passes --subscription; the default subscription is never changed):'
    Write-Step "  az account show $scope"
    if ($ImageDigest) {
        Write-Step "  az acr repository show --name $($target.registryName) $scope --image $($target.imageRepository):$tag"
    } else {
        Write-Step "  git archive --format=tar --output=$WorkRoot\$tag-<time>.tar $commitShort; tar -xf <archive> -C $WorkRoot\$tag-<time>"
        Write-Step "  `$env:PYTHONIOENCODING='utf-8'; az acr build --registry $($target.registryName) $scope --image $($target.imageRepository):$tag --file src/VoiceAssistant.Api/Dockerfile --no-wait <source>"
        Write-Step "  az acr task show-run --registry $($target.registryName) $scope --run-id <run>   (until Succeeded; outputImages[0].digest)"
    }
    Write-Step "  az containerapp update --name $($target.stagingApp) --resource-group $($target.stagingResourceGroup) $scope --image $registryHost/$($target.imageRepository)@<digest> --revision-suffix $suffix"
    if ($Production) {
        Write-Step "  az containerapp update --name $($target.productionApp) --resource-group $($target.resourceGroup) $scope --image $registryHost/$($target.imageRepository)@<digest> --revision-suffix $suffix"
    }
    Write-Step 'Dry run complete. No Azure command was run.'
    return
}

# 3. Azure: the CLI must already be signed in to the approved subscription and tenant.
$account = Invoke-Az @('account', 'show', '--subscription', $target.subscriptionId)
if (-not (Test-SameGuid ([string](Get-Field $account.Data @('id'))) $target.subscriptionId) -or
    -not (Test-SameGuid ([string](Get-Field $account.Data @('tenantId'))) $target.tenantId) -or
    [string](Get-Field $account.Data @('state')) -ne 'Enabled') {
    throw 'The Azure CLI account is not signed in to the approved, enabled subscription and tenant. Sign in to that tenant; this script never changes the default subscription.'
}
# Catch app-name mistakes before spending a build.
$null = Get-ContainerApp $target.stagingApp $target.stagingResourceGroup
if ($Production) { $null = Get-ContainerApp $target.productionApp $target.resourceGroup }

if ($ImageDigest) {
    Assert-TaggedDigest
    $digest = $ImageDigest
} else {
    $runId = Start-ImageBuild
    Write-Step "Build run $runId queued for $($target.imageRepository):$tag."
    $digest = Wait-BuildRun $runId
}
$image = "$registryHost/$($target.imageRepository)@$digest"
Write-Step "Image: $image"

$staging = Publish-Revision $target.stagingApp $target.stagingResourceGroup 'Staging'
if ($null -ne $SmokeTest) {
    $context = [pscustomobject]@{ Environment = 'staging'; BaseUrl = $staging.Url; App = $target.stagingApp; Revision = $staging.Revision
        Image = $image; Digest = $digest; Commit = $commit }
    Write-Step "Running the smoke test against $($staging.Url)..."
    $global:LASTEXITCODE = 0
    try { & $SmokeTest $context }
    catch { throw "The smoke test failed: $($_.Exception.Message) Production was not changed." }
    if ($LASTEXITCODE -ne 0) { throw "The smoke test failed (exit code $LASTEXITCODE). Production was not changed." }
    Write-Step 'Smoke test passed.'
}
if (-not $Production) {
    Write-Step "Staging release complete: $($staging.Revision) at $($staging.Url) runs $image."
    Write-Step "To promote this exact image later, rerun with the same -Ref and -Prefix plus: -Production -ImageDigest $digest"
    return
}
$released = Publish-Revision $target.productionApp $target.resourceGroup 'Production'
Write-Step "Production release complete: $($released.Revision) at $($released.Url) runs $image."
Write-Step "Previous production revision: $($released.PreviousRevision) ($($released.PreviousImage)). Roll back with:"
$released.Rollback | ForEach-Object { Write-Step $_ }
