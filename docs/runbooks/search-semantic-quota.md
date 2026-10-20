# Search semantic ranker quota (HTTP 402)

Grounded replies use Azure AI Search with semantic reranking (`queryType=semantic`, configuration
`meeting-semantic`). The API never falls back to unranked results, so when the semantic ranker refuses requests,
every grounded reply loses its documents.

## Symptoms

- Replies carry the label **자료 확인 불가** (sources unavailable) and answer from the conversation only.
- Console logs show content-free warnings such as:

  ```text
  Meeting retrieval unavailable: quota, transient False, RequestFailedException, HTTP status 402, search code ..., retried False.
  ```

- An evaluation run shows the retrieval-hit and fact rates of grounded items dropping to near zero (a past evaluation
  run failed this way).

Kind `quota` with HTTP 402 means the semantic ranker's **free plan** monthly allowance is used up. Other kinds point
elsewhere: `auth` (401/403) to the runtime identity's **Search Index Data Contributor** role, `throttled` (429) or
`server_error` (5xx) to service load, and `bad_request` (400) often to a missing `meeting-semantic` configuration
(fix with `scripts/infra/Update-SemanticIndex.ps1`, see [infra/README.md](../../infra/README.md)).

## Check

```powershell
az search service show --name <search> --resource-group <group> --subscription <id> --query semanticSearch -o tsv
```

`free` can hit this quota, `standard` bills per request after the free allowance, `disabled` fails every semantic
query. With diagnostics enabled, count the failures in Log Analytics (the table depends on the diagnostic setting's
destination type, so both are queried):

```kusto
union isfuzzy=true
  (ContainerAppConsoleLogs | project TimeGenerated, Message = Log),
  (AzureDiagnostics | where Category == "ContainerAppConsoleLogs" | project TimeGenerated, Message = column_ifexists("Log_s", ""))
| where Message has "Meeting retrieval unavailable"
| extend Kind = extract(@"unavailable: (\w+)", 1, Message), Status = extract(@"HTTP status (\d+)", 1, Message)
| summarize Failures = count() by bin(TimeGenerated, 1h), Kind, Status
```

## Fix

1. **Infrastructure as code (preferred).** `infra/main.bicep` defaults `searchSemanticSearch` to `standard`. Make sure
   your `infra/parameters.local.json` does not override it with `free`, then preview and apply:

   ```powershell
   .\scripts\infra\Deploy.ps1 -ParametersFile .\infra\parameters.local.json -BicepPath C:\tools\bicep.exe -ResourceGroup <group> -WhatIf
   .\scripts\infra\Deploy.ps1 -ParametersFile .\infra\parameters.local.json -BicepPath C:\tools\bicep.exe -ResourceGroup <group> -Apply
   ```

   A template apply replaces the container's environment variables; re-apply manual settings such as `DemoLogin__*`.
2. **Immediate switch** (when a deployment is not possible right now):

   ```powershell
   az search service update --name <search> --resource-group <group> --subscription <id> --semantic-search standard
   ```

   Then align `parameters.local.json`, so the next template deployment does not switch it back.

The standard plan charges per semantic request beyond the free allowance; check current Azure AI Search pricing.
No index, document or app restart is needed.

## Verify

- `az search service show ... --query semanticSearch` returns `standard`.
- Ask a question about an uploaded document with document search on: the reply lists its source and no new
  `HTTP status 402` warnings appear.
- For a measured check, run the [quality baseline](quality-baseline.md) against staging; grounded items should recover
  their retrieval-hit rate.

## Prevent

- `scripts/infra/tests/Test-Offline.ps1` (CI) fails if the template default leaves `standard`.
- Run the quality baseline after infrastructure changes and before releases.
