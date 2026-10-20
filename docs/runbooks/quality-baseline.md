# Quality baseline: QualityEval against staging

QualityEval ([`tools/QualityEval`](../../tools/QualityEval/README.md), the full reference) speaks a fictional company's
questions with Azure AI Speech, streams them through the real WebSocket protocol of a deployed Live Coach, and scores
replies (required facts, forbidden claims, abstention, readability, recognized key terms) and per-stage latency. A
baseline plus `compare` turns that into a release gate.

## Reference numbers (staging, October 2026)

108 synthetic runs (36 items x 3 repeats, three accents) against staging with the template's reranker threshold 1.5,
scored after the #30 scorer fix (declined commitments count as abstaining):

| Metric | Value |
| --- | --- |
| `passRate` | 0.98 |
| `factRecall` | 0.98 |
| `abstainRate` (not-covered and commitment items) | 1.00 |
| `forbiddenRate` | 0 |
| `retrievalHitRate` (materials-required items) | 0.99 |

| Latency from speech end | p50 | p95 |
| --- | --- | --- |
| Final transcript (`speechEndToFinal`) | about 1.0 s | about 1.2 s |
| First line | about 2.43 s | about 2.84 s |
| Completed reply (`speechEndToCompleted`) | about 2.48 s | about 2.88 s |

The same items at the previous threshold 2.0 scored `passRate` 0.88 and `retrievalHitRate` 0.84, which is why the
template now sets 1.5. These rounded numbers show the expected range; gate on `compare` against a full baseline file
that you keep outside the repository.

## Rules

- **Staging only.** Never point it at production.
- **Dedicated synthetic identity.** Never your real account: the run uploads the synthetic corpus to that identity's
  materials. Set `EVAL_FORBIDDEN_OID_SHA256` (hash of your real object ID) and `EVAL_EXPECTED_OID_SHA256` (hash of
  the evaluation identity) so a wrong sign-in stops before any upload.
- **Synthetic content only.** The corpus is fictional; results hold aggregates only. Outputs go to the git-ignored
  `tools/QualityEval/out/` or a folder outside the repository. Never commit them.
- **Credentials only in environment variables** of the current shell, never in files.

## One-time setup

1. Give the staging app a sign-in for the evaluation identity: the [demo login](../../infra/README.md#optional-demo-login-post-apidemologin)
   with a dedicated `DemoLogin__ObjectId`, or an Entra test account whose delegated `Meeting.Access` token you supply.
2. In a PowerShell session at the repository root:

   ```powershell
   $env:EVAL_BASE_URL = 'https://<staging-fqdn>'
   $env:EVAL_DEMO_USERNAME = '<eval user>'; $env:EVAL_DEMO_PASSWORD = '<eval password>'   # or $env:EVAL_BEARER_TOKEN
   $env:EVAL_FORBIDDEN_OID_SHA256 = '<sha256 of your real oid>'
   $env:EVAL_EXPECTED_OID_SHA256 = '<sha256 of the evaluation oid>'
   $env:EVAL_SPEECH_ENDPOINT = 'https://<speech-resource>.cognitiveservices.azure.com/'   # TTS with Entra auth
   $env:EVAL_SPEECH_RESOURCE_ID = '<Speech resource ID>'
   ```

3. Validate, synthesize the audio once (cached in the git-ignored `.tts-cache/`), and upload the synthetic corpus:

   ```powershell
   python .\tools\QualityEval\quality_eval.py validate
   python .\tools\QualityEval\quality_eval.py synthesize --allow-tts
   python .\tools\QualityEval\quality_eval.py materials upload --allow-remote --allow-materials-upload
   ```

## Baseline and candidate

```powershell
# Baseline: the revision currently on staging (36 items x 3 repeats, three accents; about 20-30 minutes).
# --write-details keeps the synthetic replies next to the results, so the run can be rescored later.
python .\tools\QualityEval\quality_eval.py run --allow-remote --repeats 3 --write-details `
  --output-dir C:\eval\baseline-2026-10-21

# Deploy the candidate to staging (scripts/release/Release.ps1 -Apply), then measure it the same way.
python .\tools\QualityEval\quality_eval.py run --allow-remote --repeats 3 --write-details `
  --output-dir C:\eval\candidate-w2

python .\tools\QualityEval\quality_eval.py compare C:\eval\baseline-2026-10-21\<run>\results.json `
  C:\eval\candidate-w2\<run>\results.json --report C:\eval\compare-w2.json
```

`run` prints the results path. `compare` exits **0** (pass), **1** (regression) or **2** (incompatible runs, for
example different items or options). It fails when:

- p50 or p95 of speech end to first reply text, or of speech end to completed reply, regresses by more than 10% and
  more than 100 ms;
- a quality rate (answered, pass, facts, abstention, format, retrieval, recognized terms; forbidden or generic claims
  in the other direction) moves outside the baseline's 95% confidence interval.

The optional LLM judge (`run --judge`, `EVAL_JUDGE_ENDPOINT`/`EVAL_JUDGE_DEPLOYMENT` on a deployment different from the
one under test) is advisory unless `compare --gate-judge`. Promote with `Release.ps1 -Production -ImageDigest ...` only
when `compare` passes, or when every regression is understood and recorded.

Keep the baseline results outside the repository. Re-baseline after an intended behavior change, never to hide a
regression.

## After a scorer or items change

Quality rates depend on the scorer and `items.json` of the moment, so `compare` exits 2 (`items differs`, `scorer
differs`) for results scored differently. Older results carry no scorer fingerprint; rescore them as well. Rescoring
works offline (no Azure calls) on runs made with `--write-details`, recomputes only the deterministic checks and keeps
latency and statuses unchanged:

```powershell
python .\tools\QualityEval\quality_eval.py rescore C:\eval\baseline-2026-10-21\<run> --output-dir C:\eval\rescored
python .\tools\QualityEval\quality_eval.py rescore C:\eval\candidate-w2\<run> --output-dir C:\eval\rescored
python .\tools\QualityEval\quality_eval.py compare C:\eval\rescored\<baseline run>-rescored-<time>\results.json `
  C:\eval\rescored\<candidate run>-rescored-<time>\results.json
```

## Quick smoke test in a release

A three-item, single-accent run takes about a minute and fails when any session does not complete:

```powershell
$smoke = {
    param($c)
    python .\tools\QualityEval\quality_eval.py run --base-url $c.BaseUrl --allow-remote --repeats 1 --accents en-US `
        --items fact-security-contact,number-ga-date,multi-plan-fit --output-dir "C:\eval\smoke-$($c.Revision)"
    if ($LASTEXITCODE -ne 0) { throw "QualityEval smoke run failed ($LASTEXITCODE)." }
}
.\scripts\release\Release.ps1 -Prefix w2 -Apply -SmokeTest $smoke
```

## Cost

Each full run streams about 15-20 minutes of audio to Speech, makes one reply (plus classifier) call per session, and
issues a few hundred semantic-ranker queries; on the Search **free** semantic plan a few runs exhaust the monthly
allowance (HTTP 402, see [search-semantic-quota.md](search-semantic-quota.md)). TTS is paid once per clip and cached.
The judge adds about one call per session.
