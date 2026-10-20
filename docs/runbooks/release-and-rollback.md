# Release and rollback

Operator procedure for shipping a new image of the single Live Coach Container App. Tooling:
[`scripts/release/Release.ps1`](../../scripts/release/README.md). Every command below is scoped with `--subscription`;
never run `az account set`.

## Before you start

- CI (**Build and test**) is green for the commit you will release.
- `infra/release.local.json` exists (copy of `infra/release.example.json`; git-ignored) with the subscription, tenant,
  resource group, registry, production app and staging app.
- `az login` to the approved tenant; `az account show --subscription <id>` works.
- Pick a time outside meetings. The app has one replica and in-memory WebSocket tickets, so a revision switch ends
  live sessions; users reconnect with Start.
- Choose a new revision prefix (for example `w2`). Revision suffixes are single-use.

## Release

1. Check out exactly what you release:

   ```powershell
   git fetch origin
   git switch --detach origin/main
   ```

2. Dry run and read the plan (no Azure command runs):

   ```powershell
   .\scripts\release\Release.ps1 -Prefix w2
   ```

3. Build and deploy to **staging**; add your smoke test if you have one:

   ```powershell
   .\scripts\release\Release.ps1 -Prefix w2 -Apply -SmokeTest C:\ops\smoke.ps1
   ```

   The run ends with the pinned image (`<registry>.azurecr.io/voice-assistant@sha256:...`). Keep that digest.
4. Optional manual check on staging: open `/live.html`, run the audio demo, ask one question with document search on.
   For a measured gate, run the [quality baseline](quality-baseline.md) comparison against staging.
5. Promote the **same digest** to production:

   ```powershell
   .\scripts\release\Release.ps1 -Prefix w2 -Apply -Production -ImageDigest sha256:<digest from step 3>
   ```

   Before touching production the script prints the current production revision, its image and the rollback commands.
   Copy them into your release notes (outside this repository).
6. After release, for about 15 minutes: `/health/ready` returns 200, the Live page connects, and the console logs show no
   new `Meeting provider failed` or `Meeting retrieval unavailable` warnings.

Steps 3 and 5 can be one run (`-Apply -Production`), but promoting with `-ImageDigest` keeps "tested on staging" and
"deployed to production" provably the same image.

## Roll back

Roll back when production health or headers fail, errors rise, or replies regress. Use the commands the script
printed; they already contain the names.

**A. Redeploy the previous image (single-revision mode; preferred).** Creates a new revision with the old digest;
traffic switches only when it is ready.

```powershell
az containerapp update --name <app> --resource-group <group> --subscription <id> `
  --image <previous image> --revision-suffix rb-<previous suffix>
```

**B. Reactivate the retained revision.** Single-revision mode deactivates the previous revision but keeps it in the
revision list, so it can be activated again. Traffic weights apply only in multiple-revision mode.

```powershell
az containerapp revision activate --name <app> --resource-group <group> --subscription <id> --revision <previous revision>
az containerapp ingress traffic set --name <app> --resource-group <group> --subscription <id> --revision-weight <previous revision>=100
```

If the printout is lost, list revisions with their images:

```powershell
az containerapp revision list --name <app> --resource-group <group> --subscription <id> --all `
  --query "[].{name:name, active:properties.active, created:properties.createdTime, image:properties.template.containers[0].image}" -o table
```

After a rollback, confirm `https://<fqdn>/health/ready` returns 200 and the Live page works. Keep the bad digest in
ACR for analysis, and fix forward with a new prefix.

## Notes

- `Release.ps1` changes only the image (and revision suffix). `scripts/infra/Deploy.ps1 -Apply` redeploys the whole
  template, which replaces the container's environment variables; re-apply manual settings such as `DemoLogin__*`
  afterwards.
- In Azure mode `/health/ready` waits once for the credential, Azure OpenAI and Speech warm-up (Search is best-effort),
  so a cold revision gets no traffic. It does not prove grounded answers; use the smoke test or the quality baseline.
- Script failures and their fixes: see the troubleshooting table in
  [scripts/release/README.md](../../scripts/release/README.md#troubleshooting).
