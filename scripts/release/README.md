# Release script

`Release.ps1` codifies the manual release: build the reviewed commit in ACR, pin the image digest, deploy it to the
staging Container App, verify health and security headers, optionally run a smoke test, and only then (with
`-Production`) promote the same digest to production and print the rollback commands.

It is a **dry run by default**: it checks the source and prints the plan and the exact commands, but runs no Azure
command. Add `-Apply` to execute. It runs on Windows PowerShell 5.1 and PowerShell 7.

## Prerequisites

- Azure CLI signed in (`az login`) to the approved tenant. The script passes `--subscription` on every call and never
  runs `az account set`.
- `git` and `tar` (included in Windows 10 and later) on `PATH`, or pass `-GitPath`/`-TarPath`; `-AzPath`/`-AzPrefix`
  select a portable Azure CLI.
- Your git-ignored `infra/release.local.json`, copied from [`infra/release.example.json`](../../infra/release.example.json):

| Field | Meaning |
| --- | --- |
| `subscriptionId`, `tenantId` | The approved subscription and its tenant; the CLI account must match both |
| `projectId` | Ownership tag GUID used by `scripts/infra` bootstrap (not used by this script) |
| `resourceGroup` | Resource group of the production app |
| `stagingResourceGroup` | Optional; defaults to `resourceGroup` |
| `registryName` | Existing ACR name without `.azurecr.io` |
| `imageRepository` | Image repository, `voice-assistant` |
| `stagingApp`, `productionApp` | Container App names; they must differ |

Parameters (`-SubscriptionId`, `-TenantId`, `-ResourceGroup`, `-StagingResourceGroup`, `-RegistryName`, `-StagingApp`,
`-ProductionApp`) override single fields, or describe the whole target when there is no file. A parameter may restate the
configured subscription or tenant but never replace it. Errors name the missing or invalid fields, never their values.
`-ConfigPath` or the `VOICE_ASSISTANT_RELEASE_CONFIG` environment variable selects another file.

## Usage

```powershell
git fetch origin
git switch --detach origin/main

# Dry run: checks the tree, prints the plan and the commands.
.\scripts\release\Release.ps1 -Prefix w2

# Build, deploy to staging and verify it.
.\scripts\release\Release.ps1 -Prefix w2 -Apply

# The same, plus a smoke test and production.
.\scripts\release\Release.ps1 -Prefix w2 -Apply -Production -SmokeTest C:\ops\smoke.ps1

# Promote a digest that an earlier run built and staged (no rebuild).
.\scripts\release\Release.ps1 -Prefix w2 -Apply -Production -ImageDigest sha256:<digest printed by the staging run>
```

`-Prefix` (default `rel`, 1-16 lowercase letters or digits) names both the image tag and the revision suffix:
`<prefix>-<first 7 characters of the commit>`. Revision suffixes are single-use per app, so pick a new prefix to
redeploy the same commit after a failed attempt.

## What `-Apply` does

1. **Source.** The working tree must be clean (`git status --porcelain` empty) and `HEAD` must equal `-Ref` (default
   `origin/main`). The script does not fetch; fetch first.
2. **Account.** `az account show --subscription <id>` must return the configured subscription and tenant, enabled. Both
   app names are read before the build, so a typo fails early.
3. **Build.** `git archive` exports the commit into a new folder under `-WorkRoot` (default `C:\t`): a short path, and
   only tracked files, because `az acr build` on Windows fails on long `node_modules` paths even with `.dockerignore`.
   `az acr build --image <repo>:<tag> --file src/VoiceAssistant.Api/Dockerfile --no-wait` queues the run with
   `PYTHONIOENCODING=utf-8`. The CLI exit code is not trusted (on a cp949 console it can crash printing build-log
   characters while the run continues): the script finds the run ID, polls `az acr task show-run` until `Succeeded`, and
   reads the digest from `outputImages`. The local copy is deleted after the upload.
4. **Staging.** `az containerapp update --image <registry>.azurecr.io/<repo>@<digest> --revision-suffix <tag>`, then
   it waits until the revision is provisioned, active and the app's latest ready revision, and until
   `https://<fqdn>/health/ready` returns 200 with `Strict-Transport-Security`, `X-Content-Type-Options: nosniff`,
   `Referrer-Policy: no-referrer`, `Content-Security-Policy` (`frame-ancestors 'self'`, `object-src 'none'`),
   `Permissions-Policy` (`camera=()`, `microphone=(self)`) and `Cache-Control: no-store`. If staging already runs that
   revision and image, it only verifies.
5. **Smoke test** (optional, `-SmokeTest`). A scriptblock or the path of a `.ps1` file, called with one argument: an
   object with `Environment` (`staging`), `BaseUrl`, `App`, `Revision`, `Image`, `Digest` and `Commit`. A thrown error
   or a nonzero `$LASTEXITCODE` stops the release before production. Keep private helpers outside the repository, for
   example `-SmokeTest { param($c) & C:\ops\smoke.ps1 -BaseUrl $c.BaseUrl }`. A synthetic option is
   [QualityEval](../../docs/runbooks/quality-baseline.md).
6. **Production** (only with `-Production`). It prints the current revision, its image and the rollback commands
   **before** the update, deploys the same digest with the same suffix, repeats the checks, and prints the rollback
   commands again on success or failure:

   ```text
   # Single-revision mode (zero downtime): a new revision that runs the previous image
   az containerapp update --name <app> --resource-group <group> --subscription <id> --image <previous image> --revision-suffix rb-<previous suffix>
   # Or reactivate the retained revision (traffic weights apply in multiple-revision mode)
   az containerapp revision activate --name <app> --resource-group <group> --subscription <id> --revision <previous revision>
   az containerapp ingress traffic set --name <app> --resource-group <group> --subscription <id> --revision-weight <previous revision>=100
   ```

Timeouts: `-BuildTimeoutMinutes` (45), `-ReadyTimeoutSeconds` (600) and `-PollSeconds` (15).

In Azure mode `/health/ready` returns 200 only after the startup warm-up (credential, Azure OpenAI and Speech; Search is
best-effort), so the health wait also covers a cold revision. It does not prove that grounded answers work: that is
what the smoke test is for (see [infra/README.md](../../infra/README.md)).

## Troubleshooting

| Symptom | Action |
| --- | --- |
| `Release target is incomplete or invalid: ...` | Fill the named fields in `infra/release.local.json` |
| `The working tree is not clean` / `HEAD is not origin/main` | Commit or stash; `git fetch origin`; `git switch --detach origin/main` |
| `Build run <id> ended with status Failed` | Read the printed `az acr task logs ...` command output |
| `Could not identify the build run` | `az acr task list-runs --registry <acr> --subscription <id> --top 5`; rerun with a new `-Prefix` |
| `... already has revision ...` | Choose a new `-Prefix` |
| Health or header check fails | Inspect the revision (`az containerapp revision show ...`) and logs; for production use the printed rollback |

The [release and rollback runbook](../../docs/runbooks/release-and-rollback.md) covers the operator procedure.

## Tests

`tests/Test-Release.ps1` runs fully offline. It replaces `az`, `git`, `tar`, `Invoke-WebRequest` and `Start-Sleep` with
in-process fakes (like `scripts/infra/tests`) and uses the synthetic `scripts/infra/tests/fixtures/release.fixture.json`.
`scripts/verify-release.ps1` runs it as the `release-script-offline-tests` stage.

```powershell
pwsh -NoProfile -File .\scripts\release\tests\Test-Release.ps1
```
