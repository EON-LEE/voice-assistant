[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$releaseScript = Join-Path (Split-Path $PSScriptRoot -Parent) 'Release.ps1'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
$fixtureConfig = Join-Path $repoRoot 'scripts\infra\tests\fixtures\release.fixture.json'
$approved = Get-Content -LiteralPath $fixtureConfig -Raw | ConvertFrom-Json
$script:passed = 0
function Assert-True {
    param([bool]$Condition, [string]$Name)
    if (-not $Condition) { throw "FAIL: $Name" }
    $script:passed++
    Write-Output "PASS: $Name"
}

$commit = '0123456789abcdef0123456789abcdef01234567'
$tag = 'rel-0123456'
$digest = 'sha256:' + ('d' * 64)
$registry = 'fixturemeetingacr.azurecr.io'
$newImage = "$registry/voice-assistant@$digest"
$oldImage = "$registry/voice-assistant@sha256:" + ('e' * 64)
$subscription = "--subscription $($approved.subscriptionId)"
# Scratch files stay in a git-ignored TestResults folder next to this script.
$scratch = Join-Path $PSScriptRoot ('TestResults\' + [guid]::NewGuid().ToString('N'))
$workRoot = Join-Path $scratch 'work'
$null = New-Item -ItemType Directory -Path $scratch -Force

function New-Scenario {
    $apps = @{}
    foreach ($name in @('fixture-web-staging', 'fixture-web')) {
        $revisions = New-Object 'Collections.Generic.List[string]'
        $revisions.Add("$name--rel-1111111")
        $apps[$name] = @{ revision = "$name--rel-1111111"; ready = "$name--rel-1111111"; image = $oldImage; pending = 0
            fqdn = "$name.example.invalid"; revisions = $revisions }
    }
    return @{
        dirty = $false; head = $commit; ref = $commit; apps = $apps
        account = @{ id = $approved.subscriptionId; tenantId = $approved.tenantId; state = 'Enabled' }
        buildMode = 'json'; runStatuses = @('Queued', 'Running', 'Succeeded'); runIndex = 0; runTag = $tag; runDigest = $digest
        listRuns = @(); tagDigest = $digest; revisionChecks = 1; revisionState = 'Provisioned'
        firstStatuses = (New-Object 'Collections.Generic.List[int]'); statusByHost = @{}
        headers = @{
            'Strict-Transport-Security' = 'max-age=31536000; includeSubDomains'; 'X-Content-Type-Options' = 'nosniff'
            'Referrer-Policy' = 'no-referrer'; 'Content-Security-Policy' = "script-src 'self'; frame-ancestors 'self'; object-src 'none'; base-uri 'self'"
            'Permissions-Policy' = 'camera=(), geolocation=(), microphone=(self), display-capture=(self)'; 'Cache-Control' = 'no-store'
        }
        calls = (New-Object 'Collections.Generic.List[string]'); encodings = (New-Object 'Collections.Generic.List[string]')
        gitCalls = (New-Object 'Collections.Generic.List[string]'); tarCalls = (New-Object 'Collections.Generic.List[string]')
        http = (New-Object 'Collections.Generic.List[string]'); sleeps = 0; smoke = $null; buildSourceReady = $false
    }
}

function global:Get-MockOption {
    param([object[]]$Arguments, [string]$Name)
    $index = [array]::IndexOf($Arguments, $Name)
    if ($index -ge 0 -and $index + 1 -lt $Arguments.Count) { return [string]$Arguments[$index + 1] }
    return ''
}
function global:git {
    $state = $global:ReleaseScenario
    $global:LASTEXITCODE = 0
    $state.gitCalls.Add((@($args) -join ' ')) > $null
    $rest = @($args | Select-Object -Skip 2)
    switch ($rest[0]) {
        'status' { if ($state.dirty) { return ' M src/changed.cs' } else { return } }
        'rev-parse' { if ($rest[-1] -eq 'HEAD^{commit}') { return $state.head } else { return $state.ref } }
        'archive' {
            $output = [string](@($rest | Where-Object { $_ -like '--output=*' })[0]).Substring(9)
            Set-Content -LiteralPath $output -Value 'synthetic archive'
            return
        }
    }
    $global:LASTEXITCODE = 1
}
function global:tar {
    $state = $global:ReleaseScenario
    $global:LASTEXITCODE = 0
    $state.tarCalls.Add((@($args) -join ' ')) > $null
    $null = New-Item -ItemType File -Path (Join-Path $args[3] 'src\VoiceAssistant.Api\Dockerfile') -Force
}
function global:az {
    $state = $global:ReleaseScenario
    $global:LASTEXITCODE = 0
    $arguments = @($args)
    $line = $arguments -join ' '
    $state.calls.Add($line) > $null
    $state.encodings.Add([string]$env:PYTHONIOENCODING) > $null
    $name = Get-MockOption $arguments '--name'
    $app = if ($name -and $state.apps.ContainsKey($name)) { $state.apps[$name] } else { $null }
    if ($line.StartsWith('account show')) { return ($state.account | ConvertTo-Json -Compress) }
    if ($line.StartsWith('acr build')) {
        $source = [string]$arguments[[array]::IndexOf($arguments, '--no-wait') + 1]
        $state.buildSourceReady = Test-Path -LiteralPath (Join-Path $source 'src\VoiceAssistant.Api\Dockerfile')
        switch ($state.buildMode) {
            'json' { return '{"runId":"ca1","status":"Queued"}' }
            'text' { $global:LASTEXITCODE = 1; return 'Queued a build with ID: ca9' }
            'crash' { $global:LASTEXITCODE = 1; return }
        }
    }
    if ($line.StartsWith('acr task list-runs')) { return (ConvertTo-Json -InputObject @($state.listRuns) -Depth 6 -Compress) }
    if ($line.StartsWith('acr task show-run')) {
        $status = $state.runStatuses[[Math]::Min($state.runIndex, $state.runStatuses.Count - 1)]
        $state.runIndex++
        $run = @{ runId = (Get-MockOption $arguments '--run-id'); status = $status; outputImages = @() }
        if ($status -eq 'Succeeded') {
            $run.outputImages = @(@{ registry = $registry; repository = 'voice-assistant'; tag = $state.runTag; digest = $state.runDigest })
        }
        return ($run | ConvertTo-Json -Depth 6 -Compress)
    }
    if ($line.StartsWith('acr repository show')) { return (@{ digest = $state.tagDigest } | ConvertTo-Json -Compress) }
    if ($null -eq $app) { $global:LASTEXITCODE = 3; return }
    if ($line.StartsWith('containerapp show')) {
        return (@{ name = $name; properties = @{ latestRevisionName = $app.revision; latestReadyRevisionName = $app.ready
            template = @{ containers = @(@{ image = $app.image }) }; configuration = @{ ingress = @{ fqdn = $app.fqdn } } } } |
            ConvertTo-Json -Depth 8 -Compress)
    }
    if ($line.StartsWith('containerapp revision list')) {
        return (ConvertTo-Json -InputObject @($app.revisions | ForEach-Object { @{ name = $_ } }) -Compress)
    }
    if ($line.StartsWith('containerapp update')) {
        $revision = "$name--$(Get-MockOption $arguments '--revision-suffix')"
        $app.revisions.Add($revision)
        $app.revision = $revision
        $app.image = Get-MockOption $arguments '--image'
        $app.pending = $state.revisionChecks
        return (@{ name = $name } | ConvertTo-Json -Compress)
    }
    if ($line.StartsWith('containerapp revision show')) {
        $revision = Get-MockOption $arguments '--revision'
        $provisioning = $state.revisionState
        if ($app.pending -gt 0) { $app.pending--; $provisioning = 'Provisioning' }
        elseif ($provisioning -eq 'Provisioned') { $app.ready = $revision }
        $running = if ($provisioning -eq 'Failed') { 'Failed' } else { 'Running' }
        return (@{ name = $revision; properties = @{ provisioningState = $provisioning; runningState = $running; active = $true
            healthState = 'Healthy'; template = @{ containers = @(@{ image = $app.image }) } } } | ConvertTo-Json -Depth 8 -Compress)
    }
    $global:LASTEXITCODE = 2
}
function global:Invoke-WebRequest {
    param([string]$Uri, [string]$Method, [switch]$UseBasicParsing, [int]$TimeoutSec, [int]$MaximumRedirection, $ErrorAction)
    $state = $global:ReleaseScenario
    $state.http.Add($Uri) > $null
    $status = 200
    $hostName = ([uri]$Uri).Host
    if ($state.firstStatuses.Count) { $status = $state.firstStatuses[0]; $state.firstStatuses.RemoveAt(0) }
    elseif ($state.statusByHost.ContainsKey($hostName)) { $status = $state.statusByHost[$hostName] }
    if ($status -ne 200) { throw "Synthetic HTTP $status" }
    return [pscustomobject]@{ StatusCode = 200; Headers = $state.headers }
}
function global:Start-Sleep {
    param([int]$Seconds, [int]$Milliseconds)
    $global:ReleaseScenario.sleeps++
}

function Invoke-Release {
    param([hashtable]$Parameters, [switch]$Defaults)
    $arguments = @{ ConfigPath = $fixtureConfig; WorkRoot = $workRoot; PollSeconds = 1; ReadyTimeoutSeconds = 10; BuildTimeoutMinutes = 1 }
    if ($Defaults) { $arguments = @{} }
    foreach ($key in $Parameters.Keys) { $arguments[$key] = $Parameters[$key] }
    $lines = New-Object 'Collections.Generic.List[string]'
    $failure = ''
    try { & $releaseScript @arguments 6>&1 | ForEach-Object { $lines.Add([string]$_) } }
    catch { $failure = $_.Exception.Message }
    return [pscustomobject]@{ Text = ($lines -join "`n"); Error = $failure }
}
function Get-Calls {
    param([string]$Pattern)
    return @($global:ReleaseScenario.calls | Where-Object { $_ -like $Pattern })
}

$previousEncoding = $env:PYTHONIOENCODING
$previousConfig = $env:VOICE_ASSISTANT_RELEASE_CONFIG
try {
    $tokens = $null
    $errors = $null
    $null = [Management.Automation.Language.Parser]::ParseFile($releaseScript, [ref]$tokens, [ref]$errors)
    $bytes = [IO.File]::ReadAllBytes($releaseScript)
    Assert-True ($errors.Count -eq 0 -and @($bytes | Where-Object { $_ -gt 127 }).Count -eq 0) 'Release.ps1 parses and is ASCII-only for Windows PowerShell 5.1'

    $global:ReleaseScenario = New-Scenario
    $r = Invoke-Release @{}
    $s = $global:ReleaseScenario
    Assert-True (-not $r.Error -and $r.Text -match 'DRY RUN' -and $r.Text -match 'Dry run complete\. No Azure command was run\.' -and
        $s.calls.Count -eq 0 -and $s.tarCalls.Count -eq 0) 'Default run is a dry run: no az, archive or tar call'
    Assert-True (@($s.gitCalls | Where-Object { $_ -notmatch ' (status --porcelain|rev-parse --verify)\b' }).Count -eq 0) 'Dry run uses only read-only git commands'
    Assert-True ($r.Text.Contains("revision fixture-web-staging--$tag") -and $r.Text.Contains("--image voice-assistant:$tag") -and
        $r.Text.Contains("--revision-suffix $tag") -and $r.Text.Contains($subscription)) 'Plan shows the image tag, revision suffix and explicit subscription'

    $global:ReleaseScenario = New-Scenario
    $global:ReleaseScenario.dirty = $true
    $r = Invoke-Release @{ Apply = $true }
    Assert-True ($r.Error -match 'not clean' -and $global:ReleaseScenario.calls.Count -eq 0) 'Uncommitted changes stop the release before Azure'
    $global:ReleaseScenario = New-Scenario
    $global:ReleaseScenario.head = 'f' * 40
    $r = Invoke-Release @{ Apply = $true }
    Assert-True ($r.Error -match 'HEAD is not origin/main' -and $global:ReleaseScenario.calls.Count -eq 0) 'HEAD must equal origin/main (or the explicit -Ref)'

    $global:ReleaseScenario = New-Scenario
    $r = Invoke-Release @{ ConfigPath = (Join-Path $scratch 'absent-release.local.json') }
    Assert-True ($r.Error -match 'copy infra/release.example.json' -and $global:ReleaseScenario.gitCalls.Count -eq 0) 'A missing local configuration fails closed before any command'
    $r = Invoke-Release @{ ConfigPath = (Join-Path $repoRoot 'infra\release.example.json') }
    Assert-True ($r.Error -match 'subscriptionId, tenantId, resourceGroup, stagingResourceGroup, registryName, stagingApp' -and
        $r.Error -notmatch 'REPLACE_') 'Placeholder configuration fails closed, naming fields but not values'
    $r = Invoke-Release @{ SubscriptionId = '99999999-9999-4999-8999-999999999999' }
    Assert-True ($r.Error -match 'subscriptionId \(differs from the configuration\)' -and $r.Error -notmatch '99999999') 'A parameter cannot replace the configured subscription'
    $explicit = @{ ConfigPath = (Join-Path $scratch 'absent-release.local.json'); SubscriptionId = $approved.subscriptionId; TenantId = $approved.tenantId
        ResourceGroup = 'fixture-meeting-group'; RegistryName = 'fixturemeetingacr'; StagingApp = 'fixture-web-staging' }
    $r = Invoke-Release $explicit
    Assert-True (-not $r.Error -and $r.Text -match 'Dry run complete') 'Parameters alone can describe the target'
    foreach ($case in @(@{ Prefix = 'Rel' }, @{ ImageDigest = 'sha256:XYZ' }, @{ Ref = '--upload-pack=x' }, @{ SmokeTest = (Join-Path $scratch 'missing.ps1') },
        @{ Production = $true; ProductionApp = 'fixture-web-staging' })) {
        $r = Invoke-Release $case
        Assert-True ($r.Error -and $global:ReleaseScenario.calls.Count -eq 0) "Invalid input is rejected before Azure: $($case.Keys -join ',')"
    }

    $env:PYTHONIOENCODING = 'cp949-sentinel'
    $global:ReleaseScenario = New-Scenario
    $global:ReleaseScenario.firstStatuses.Add(503)
    $r = Invoke-Release @{ Apply = $true }
    $s = $global:ReleaseScenario
    $buildIndex = [array]::IndexOf(@($s.calls), @(Get-Calls 'acr build*')[0])
    Assert-True (-not $r.Error -and $r.Text -match 'Staging release complete') ("Staging release succeeds" + $(if ($r.Error) { ": $($r.Error)" }))
    Assert-True ($s.calls[0] -like "account show $subscription*") 'The approved subscription and tenant are verified first'
    Assert-True (@(Get-Calls "acr build --registry fixturemeetingacr $subscription --image voice-assistant:$tag --file src/VoiceAssistant.Api/Dockerfile --no-wait *").Count -eq 1 -and
        $s.buildSourceReady -and $s.encodings[$buildIndex] -eq 'utf-8') 'az acr build gets the extracted archive, --no-wait and PYTHONIOENCODING=utf-8'
    Assert-True ($env:PYTHONIOENCODING -eq 'cp949-sentinel') 'The caller encoding is restored'
    Assert-True (@(Get-Calls 'acr task show-run *--run-id ca1*').Count -ge 3 -and $s.sleeps -gt 0) 'The build is polled with show-run until Succeeded'
    Assert-True (@(Get-Calls "containerapp update --name fixture-web-staging --resource-group fixture-meeting-group $subscription --image $newImage --revision-suffix $tag*").Count -eq 1) 'Staging is updated to the built digest with the revision suffix'
    Assert-True (@(Get-Calls 'containerapp update --name fixture-web --*').Count -eq 0) 'Production is untouched without -Production'
    Assert-True (@($s.calls | Where-Object { $_ -notlike "*$subscription*" -or $_ -like '*account set*' }).Count -eq 0) 'Every az call is subscription-scoped and never changes the default'
    Assert-True ($s.http -contains 'https://fixture-web-staging.example.invalid/health/ready' -and $r.Text -match 'expected security headers') 'Staging waits for /health/ready 200 with the security headers'
    Assert-True (@(Get-ChildItem -LiteralPath $workRoot -Force).Count -eq 0 -and $s.tarCalls.Count -eq 1) 'The short-path source copy is removed after upload'
    Assert-True ($r.Text.Contains("-Production -ImageDigest $digest")) 'The staged digest is printed for promotion'

    $global:ReleaseScenario = New-Scenario
    $global:ReleaseScenario.buildMode = 'crash'
    $global:ReleaseScenario.listRuns = @(
        @{ runId = 'ca7'; status = 'Running'; createTime = [DateTime]::UtcNow.ToString('o'); outputImages = @() },
        @{ runId = 'ca6'; status = 'Succeeded'; createTime = [DateTime]::UtcNow.AddHours(-3).ToString('o'); outputImages = @(@{ tag = 'rel-old' }) })
    $r = Invoke-Release @{ Apply = $true }
    Assert-True (-not $r.Error -and $r.Text -match 'exited with code 1' -and @(Get-Calls 'acr task show-run *--run-id ca7*').Count -ge 1) 'A crashed az acr build is not trusted; the run is found and polled'
    $global:ReleaseScenario = New-Scenario
    $global:ReleaseScenario.buildMode = 'text'
    $r = Invoke-Release @{ Apply = $true }
    Assert-True (-not $r.Error -and @(Get-Calls 'acr task show-run *--run-id ca9*').Count -ge 1) 'A queued run ID printed as text is used'
    $global:ReleaseScenario = New-Scenario
    $global:ReleaseScenario.runStatuses = @('Running', 'Failed')
    $r = Invoke-Release @{ Apply = $true }
    Assert-True ($r.Error -match 'ended with status Failed' -and $r.Error -match 'az acr task logs' -and
        @(Get-Calls 'containerapp update*').Count -eq 0) 'A failed build stops the release and points to its logs'

    $global:ReleaseScenario = New-Scenario
    $global:ReleaseScenario.statusByHost['fixture-web-staging.example.invalid'] = 503
    $r = Invoke-Release @{ Apply = $true }
    Assert-True ($r.Error -match 'did not return 200') 'A staging revision that never becomes ready fails the release'
    $global:ReleaseScenario = New-Scenario
    $global:ReleaseScenario.headers.Remove('Strict-Transport-Security')
    $global:ReleaseScenario.headers['Content-Security-Policy'] = "default-src 'self'"
    $r = Invoke-Release @{ Apply = $true }
    Assert-True ($r.Error -match 'Strict-Transport-Security, Content-Security-Policy') 'Missing or weakened security headers fail the release'
    $global:ReleaseScenario = New-Scenario
    $global:ReleaseScenario.revisionState = 'Failed'
    $r = Invoke-Release @{ Apply = $true }
    Assert-True ($r.Error -match 'revision fixture-web-staging--rel-0123456 failed') 'A failed revision fails the release'
    $global:ReleaseScenario = New-Scenario
    $global:ReleaseScenario.apps['fixture-web-staging'].revisions.Add("fixture-web-staging--$tag")
    $r = Invoke-Release @{ Apply = $true }
    Assert-True ($r.Error -match 'single-use' -and @(Get-Calls 'containerapp update*').Count -eq 0) 'A used revision suffix is never reused for another image'
    $global:ReleaseScenario = New-Scenario
    $global:ReleaseScenario.account.tenantId = '99999999-9999-4999-8999-999999999999'
    $r = Invoke-Release @{ Apply = $true }
    Assert-True ($r.Error -match 'not signed in to the approved' -and @(Get-Calls 'acr build*').Count -eq 0) 'A CLI account in another tenant stops the release before the build'
    $global:ReleaseScenario = New-Scenario
    $global:ReleaseScenario.apps.Remove('fixture-web')
    $r = Invoke-Release @{ Apply = $true; Production = $true }
    Assert-True ($r.Error -match 'Cannot read container app fixture-web ' -and @(Get-Calls 'acr build*').Count -eq 0) 'A wrong production app name is caught before the build'

    $global:ReleaseScenario = New-Scenario
    $global:ReleaseScenario.tagDigest = 'sha256:' + ('f' * 64)
    $r = Invoke-Release @{ Apply = $true; ImageDigest = $digest }
    Assert-True ($r.Error -match 'does not point to -ImageDigest' -and @(Get-Calls 'acr build*').Count -eq 0 -and
        @(Get-Calls 'containerapp update*').Count -eq 0) 'A promoted digest must be the one built for this commit'
    $global:ReleaseScenario = New-Scenario
    $staging = $global:ReleaseScenario.apps['fixture-web-staging']
    $staging.revisions.Add("fixture-web-staging--$tag")
    $staging.revision = "fixture-web-staging--$tag"
    $staging.ready = $staging.revision
    $staging.image = $newImage
    $r = Invoke-Release @{ Apply = $true; ImageDigest = $digest }
    Assert-True (-not $r.Error -and $r.Text -match 'already runs this image; verifying only' -and @(Get-Calls 'acr build*').Count -eq 0 -and
        @(Get-Calls 'containerapp update*').Count -eq 0) 'Re-running a promoted digest only verifies staging'

    $global:ReleaseScenario = New-Scenario
    $r = Invoke-Release @{ Apply = $true; SmokeTest = { param($Context) $global:ReleaseScenario.smoke = $Context } }
    $smoke = $global:ReleaseScenario.smoke
    Assert-True (-not $r.Error -and $smoke.BaseUrl -eq 'https://fixture-web-staging.example.invalid' -and $smoke.Environment -eq 'staging' -and
        $smoke.Revision -eq "fixture-web-staging--$tag" -and $smoke.Image -eq $newImage -and $smoke.Commit -eq $commit) 'The smoke hook receives the staging URL, revision and image'
    $global:ReleaseScenario = New-Scenario
    $r = Invoke-Release @{ Apply = $true; Production = $true; SmokeTest = { throw 'synthetic smoke failure' } }
    Assert-True ($r.Error -match 'smoke test failed: synthetic smoke failure' -and @(Get-Calls 'containerapp update --name fixture-web --*').Count -eq 0) 'A failing smoke hook stops before production'
    $smokeScript = Join-Path $scratch 'smoke.ps1'
    Set-Content -LiteralPath $smokeScript -Value @'
param($Context)
Set-Content -LiteralPath (Join-Path $PSScriptRoot 'smoke-ran.txt') -Value $Context.BaseUrl
exit 3
'@
    $global:ReleaseScenario = New-Scenario
    $r = Invoke-Release @{ Apply = $true; Production = $true; SmokeTest = $smokeScript }
    Assert-True ($r.Error -match 'exit code 3' -and (Get-Content -LiteralPath (Join-Path $scratch 'smoke-ran.txt')) -eq 'https://fixture-web-staging.example.invalid' -and
        @(Get-Calls 'containerapp update --name fixture-web --*').Count -eq 0) 'A smoke script path runs and its nonzero exit code stops production'

    $global:ReleaseScenario = New-Scenario
    $r = Invoke-Release @{ Apply = $true; Production = $true }
    $s = $global:ReleaseScenario
    $updates = @(Get-Calls 'containerapp update*')
    $scope = "--name fixture-web --resource-group fixture-meeting-group $subscription"
    Assert-True (-not $r.Error -and $updates.Count -eq 2 -and $updates[0] -like '* fixture-web-staging *' -and
        $updates[1] -like "containerapp update $scope --image $newImage --revision-suffix $tag*") ("Production gets the same digest after staging" + $(if ($r.Error) { ": $($r.Error)" }))
    Assert-True ($r.Text.Contains("Production rollback target: revision fixture-web--rel-1111111, image $oldImage") -and
        $r.Text.Contains("az containerapp update $scope --image $oldImage --revision-suffix rb-rel-1111111") -and
        $r.Text.Contains("az containerapp revision activate $scope --revision fixture-web--rel-1111111") -and
        $r.Text.Contains("az containerapp ingress traffic set $scope --revision-weight fixture-web--rel-1111111=100")) 'The previous revision and exact rollback commands are printed'
    Assert-True ($s.http -contains 'https://fixture-web.example.invalid/health/ready' -and $r.Text -match 'Production release complete') 'Production health and headers are verified'
    $global:ReleaseScenario = New-Scenario
    $global:ReleaseScenario.statusByHost['fixture-web.example.invalid'] = 503
    $r = Invoke-Release @{ Apply = $true; Production = $true }
    Assert-True ($r.Error -match 'Production /health/ready did not return 200' -and
        $r.Text -match 'Production verification failed\. Roll back with:' -and
        $r.Text.Contains("az containerapp revision activate $scope --revision fixture-web--rel-1111111")) 'A failed production check repeats the rollback commands'

    $env:VOICE_ASSISTANT_RELEASE_CONFIG = $fixtureConfig
    $global:ReleaseScenario = New-Scenario
    $r = Invoke-Release @{ WorkRoot = $workRoot } -Defaults
    Assert-True (-not $r.Error -and $r.Text -match 'DRY RUN') 'VOICE_ASSISTANT_RELEASE_CONFIG selects the configuration file'
    Write-Output "All $script:passed release checks passed. No Azure, registry or network call was made."
} finally {
    $env:PYTHONIOENCODING = $previousEncoding
    $env:VOICE_ASSISTANT_RELEASE_CONFIG = $previousConfig
    foreach ($name in @('git', 'tar', 'az', 'Invoke-WebRequest', 'Start-Sleep', 'Get-MockOption')) { Remove-Item -LiteralPath "Function:\$name" -ErrorAction SilentlyContinue }
    Remove-Variable ReleaseScenario -Scope Global -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue
    $results = Join-Path $PSScriptRoot 'TestResults'
    if ((Test-Path -LiteralPath $results) -and -not (Get-ChildItem -LiteralPath $results -Force)) { Remove-Item -LiteralPath $results -Force }
}
