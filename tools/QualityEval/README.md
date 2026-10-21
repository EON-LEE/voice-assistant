# QualityEval: synthetic evaluation of the full Live Coach path

QualityEval measures what a meeting participant actually gets. It speaks a fictional company's questions with Azure AI
Speech text-to-speech in several accents and streams them, in real time, through the real WebSocket protocol
(`contracts/websocket-v1.md`) to a Live Coach server. The server then runs its normal pipeline: Azure Speech recognition,
retrieval over the evaluation identity's materials, and the streamed reply. QualityEval scores:

- latency per stage, from the moment the question's audio ends;
- whether key terms were recognized;
- the reply's facts, forbidden claims, abstentions and readability;
- optionally, an advisory LLM judge.

It repeats every item, reports p50/p95 with question-level bootstrap 95% confidence intervals (CIs), and gates a candidate
result against a baseline.

Everything in this folder is **synthetic**. Kestrova Analytics, its people, products, prices and dates are invented.

| Path | Content |
| --- | --- |
| `corpus/corpus.json`, `corpus/docs/*.md` | 7 synthetic documents: product overview, pricing tiers, timeline/milestones, team/roles, security/compliance FAQ, support SLA, integrations/API |
| `corpus/items.json` | 36 synthetic items in 7 categories: `fact`, `number`, `asr-term`, `multi-turn`, `not-covered`, `commitment`, `general` |
| `qualityeval/` | Python standard-library implementation (Python 3.10+; `azure-identity` optional) |
| `tests/` | Offline tests with an in-process fake server; no network, Azure or .NET needed |
| `out/`, `.tts-cache/` | Git-ignored outputs and cached TTS audio. Never commit them. |

## Privacy rules

- **Synthetic content only.** The loader refuses a corpus or item file unless it is declared
  `"classification": "synthetic"`. The corpus must also be marked `"fictional": true`, and every document must carry the
  manifest's synthetic marker. Never put private scenarios, materials, audio, logs or evaluator output in this folder.
- **Outputs stay out of Git.** Results, details, compare reports and TTS audio may be written only to two places:
  - the git-ignored `out/` and `.tts-cache/` folders (checked with `git check-ignore`);
  - a path outside the repository.

  Every other repository path, including `corpus/`, is refused, and existing outputs are not overwritten.
- **Results hold aggregates only.** `results.json` contains numbers, synthetic item ids and synthetic key terms. It
  contains no audio, transcripts or reply text. The server origin and the identity's object ID appear only as SHA-256
  prefixes. `--write-details` adds per-run synthetic transcripts and replies in `details.jsonl` in the same folder. Use
  it for debugging, and for runs you may need to [rescore](#rescore-a-finished-run-offline) later.
- **Credentials come from environment variables only.** They are never written to disk or logs. Redirects are refused, so
  a bearer token cannot be forwarded to another host.
- **Use a dedicated synthetic identity, never your real account.** Uploading materials requires
  `--allow-materials-upload`. The run prints the identity's oid SHA-256 prefix first. Upload and cleanup touch only
  documents whose titles exactly match the synthetic corpus. To enforce the separation, set:
  - `EVAL_FORBIDDEN_OID_SHA256`: your real oid hash;
  - `EVAL_EXPECTED_OID_SHA256`: the synthetic identity's hash.
- **Deployed servers need explicit consent.** A non-loopback URL needs `--allow-remote`, and placeholder tone audio is
  refused against an Azure-mode server. Run against a staging revision only, not production.

## Offline checks

```powershell
python .\tools\QualityEval\quality_eval.py validate
python -m unittest discover -s .\tools\QualityEval -p "test_*.py"     # or: cd tools\QualityEval; python -m pytest
```

`validate` checks the following:

- there are at least 30 items and every category is present;
- each required fact can be traced to the document it should come from;
- each key term is actually spoken in its item;
- forbidden patterns are valid regular expressions;
- multi-turn, not-covered and commitment items are well formed.

Windows CI runs the same unit tests in the `quality-eval-offline-tests` stage of `scripts/verify-release.ps1`. That
stage hides every `EVAL_*` variable, so the tests cannot pick up a server URL or a credential.

## Dry run against a local Fake-mode API (no Azure)

```powershell
# Terminal 1: the API with the deterministic Fake provider (Development only, loopback only)
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:Provider__Mode = 'Fake'
$env:ASPNETCORE_URLS = 'http://localhost:5080'
dotnet run --project .\src\VoiceAssistant.Api\VoiceAssistant.Api.csproj

# Terminal 2
python .\tools\QualityEval\quality_eval.py run --base-url http://localhost:5080 `
  --audio-source synthetic-tone --repeats 1 --allow-materials-upload --cleanup-materials
```

The dry run exercises the real code paths of the evaluation itself:

- the handshake, `session.start` with the Live overlay's options, and real-time 20 ms PCM frames;
- event timing, target binding, and the materials API (upload, list, delete);
- scoring, statistics and results.

Fake recognition ignores the words. It emits a fixed question as soon as speech starts and answers with a fixed reply. As a
result, `speechEndToFinal` is negative, and fact, term and retrieval scores are meaningless in Fake mode. Use it to verify
the plumbing only.

## Real run against a deployed (staging) URL

Supply these through environment variables only:

| Variable | Purpose |
| --- | --- |
| `EVAL_BASE_URL` (or `--base-url`) | Staging app origin, for example `https://<app>.<env>.<region>.azurecontainerapps.io` |
| `EVAL_DEMO_USERNAME`, `EVAL_DEMO_PASSWORD` | Demo-login credentials of the synthetic identity, if staging enables `DemoLogin`; **or** |
| `EVAL_BEARER_TOKEN` | A pre-acquired delegated `Meeting.Access` access token of the synthetic Entra user |
| `EVAL_FORBIDDEN_OID_SHA256` | Comma-separated SHA-256 of identities that must never run evaluations (for example your own oid) |
| `EVAL_EXPECTED_OID_SHA256` | Optional: SHA-256 of the synthetic identity's oid; anything else is refused |
| `EVAL_SPEECH_ENDPOINT` | Custom-domain endpoint of a Speech resource for TTS, `https://<name>.cognitiveservices.azure.com/` (or `EVAL_SPEECH_REGION`) |
| `EVAL_SPEECH_RESOURCE_ID` | ARM ID of that Speech resource (Entra auth uses `Bearer aad#<resourceId>#<token>`) |
| `EVAL_SPEECH_TOKEN` | Optional Entra token for `https://cognitiveservices.azure.com/.default`. Otherwise `azure-identity` or the signed-in Azure CLI is used. |
| `EVAL_JUDGE_ENDPOINT`, `EVAL_JUDGE_DEPLOYMENT` | Optional advisory judge: Azure OpenAI endpoint and a deployment **different** from the one under test |
| `EVAL_JUDGE_API_VERSION`, `EVAL_JUDGE_TOKEN`, `EVAL_SUT_DEPLOYMENT` | Optional: the API version (default `2024-10-21`), a judge token, and the evaluated deployment name. The judge is refused if it matches `EVAL_SUT_DEPLOYMENT`. |

To compute a hash, run `python -c "import hashlib,sys;print(hashlib.sha256(sys.argv[1].lower().encode()).hexdigest())" <oid>`.

```powershell
# 1. Fill the TTS cache once: 3 accents, 2 voices each; billable, then reused.
python .\tools\QualityEval\quality_eval.py synthesize --allow-tts
# 2. Baseline: upload the synthetic corpus to the synthetic identity, run 36 items x 3 repeats.
python .\tools\QualityEval\quality_eval.py run --allow-remote --allow-materials-upload --repeats 3
# 3. After a change, run again on the candidate revision, then gate.
python .\tools\QualityEval\quality_eval.py compare .\tools\QualityEval\out\run-A\results.json .\tools\QualityEval\out\run-B\results.json
```

The run needs these permissions:

| Who | Needs |
| --- | --- |
| Synthetic identity | `Meeting.Access` on the staging API (or demo login). Session tickets and the materials API need nothing else. |
| Operator who runs TTS | **Cognitive Services Speech User** on the TTS Speech resource |
| Operator who runs the judge | **Cognitive Services OpenAI User** on the judge's resource |

The Origin header must be one of the server's `Security__AllowedOrigins`. To test a revision label URL whose host differs
from the allowed origin, either add that origin or pass `--origin <allowed origin>`.

Useful run options:

- `--items`, `--categories`: run a subset.
- `--accents en-US,en-GB,en-IN`: the default; `en-AU` is also configured. `--voices file.json` overrides the voices.
- `--response-mode grounded|balanced|conversation`: the Live default is `grounded`.
- `--phrases`: measure Speech phrase hints. The Live UI sends none.
- `--reply-korean`: push Korean replies, which counts the `response.korean` events.
- `--cleanup-materials`: remove the corpus afterwards.
- `--judge`: run the advisory LLM judge.
- `--write-details`: write per-run transcripts and replies to `details.jsonl`. A run needs it to be rescored later.

## Rescore a finished run (offline)

Scores depend on the scorer (`qualityeval/scoring.py`, `qualityeval/textnorm.py`) and on `corpus/items.json` at the time
of the run. After either changes, rescore the older runs, so that `compare` sees the same scoring on both sides:

```powershell
python .\tools\QualityEval\quality_eval.py rescore C:\eval\baseline\run-20261021T090000Z-0a1b2c `
  --output-dir C:\eval\rescored
python .\tools\QualityEval\quality_eval.py rescore C:\eval\candidate\run-20261021T100000Z-3d4e5f\details.jsonl `
  --output-dir C:\eval\rescored --items .\tools\QualityEval\corpus\items.json
```

- **Input:** a run folder written with `--write-details`, or its `details.jsonl`, with the run's `results.json` next to
  it. The source files are only read.
- **Scoring:** every run record is rebuilt from `details.jsonl` and scored again with the current scorer and the
  `--items` file (default: `items.json` in `--corpus-dir`). Statuses, latency and the other outcome data are taken over
  unchanged; the per-run response counts are recovered from the recorded events. Judge grades are carried over as
  recorded. Nothing is sent anywhere: no network, Azure or TTS.
- **Output:** a new folder `<source run id>-rescored-<time>-<random>` under `--output-dir` (default: the git-ignored
  `out/`; a folder outside the repository also works) with `results.json` and `details.jsonl` in the same schema. The
  run's bootstrap samples and seed are reused, so an unchanged scorer and items file reproduce the original aggregates.
  The command prints the summary and the quality rates that changed.
- **Metadata:** `items.sha256` and `scorer.sha256` describe the new scoring. The `rescore` block records the source run
  id, its tool version and its items and scorer hashes, the items and scorer hashes used, and `rescoredAt`.
- **Limits:** rescoring assumes that the items' spoken turns did not change. It refuses a run whose recorded turn count
  no longer matches an item, or whose selected items are missing from the items file.

## What is measured

The session sends `session.start` with the same options `/live.html` uses:

```json
{"responseMode":"grounded","profile":{"name":"","role":"","project":""},"profileConfirmed":false,"topic":"",
 "phrases":[],"endSilenceMs":1100,"semanticSegmentation":true,"replySettleMs":1000,"replyKorean":false}
```

After `session.ready`, it streams the item's timeline in real time as 20 ms frames of 640 bytes:

1. 400 ms of lead silence.
2. The context turns and backchannels, in two voices, with 700 ms gaps.
3. The target question.
4. Silence until the reply finishes or a timeout is reached.

If the evaluating machine stalls (for example, a busy PC or a throttled background task), the client sends at most one
second of overdue audio at once and moves the rest of the timeline later. No audio is dropped. The server accepts about
4 seconds of audio at once and twice real time after that; it ends the session with `invalid_audio` when audio arrives
faster. Latency is still measured from the moment each frame is actually sent.

It then sends `session.stop`. Every server event and every sent frame gets a monotonic timestamp. Context finals are
assigned to utterances by word overlap, falling back to timing. The evaluated reply is the last completed response whose
`turnId` belongs to a final of the target question. Replies to earlier context are counted separately.

The latency anchor is the moment the frame carrying the target's last speech sample was handed to the socket. That sample
comes from the TTS clip (threshold about -44 dBFS). The anchor is what the server perceives: it includes the upload of
that frame and excludes browser capture and rendering.

| Stage (ms, signed) | From → to |
| --- | --- |
| `speechEndToFinal` | speech end → the `transcript.final` the reply is bound to |
| `finalToStarted` | that final → `response.started` (includes `replySettleMs` and routing) |
| `startedToFirstDelta` | `response.started` → first non-empty `response.delta` (retrieval + time to first token) |
| `firstDeltaToPrimaryDone` | first delta → `response.delta` with `primaryDone: true` |
| `primaryDoneToCompleted`, `firstDeltaToCompleted` | → `response.completed` |
| `speechEndToStarted`, `speechEndToFirstDelta`, `speechEndToPrimaryDone`, `speechEndToCompleted` | cumulative from speech end |

`primaryDone: true` is an optional flag on `response.delta`. An empty delta text is accepted only together with it, and
any other empty delta is counted under `protocolAnomalies`. Servers that do not send the flag leave the `primaryDone`
stages empty.

### Quality checks (deterministic)

Matching is token-based on normalized text, so `$1,950`, `1950 dollars` and `USD 1950` all match, and so do
`99.9 percent` and `99.9%`.

| Metric | Meaning |
| --- | --- |
| `answeredRate` | A model reply was completed; a grounding-unavailable notice does not count |
| `passRate` | Answered, with every required fact, no forbidden claim, the required abstention, reading limits met and not filler |
| `factRecall`, `factRecallPrimary` | Share of required facts found in primary + alternative, or in the primary only |
| `forbiddenRate` | Share of answers that assert a forbidden claim or pattern. Negation-aware: `can't guarantee 100%` is not a guarantee. |
| `abstainRate` | Not-covered and trap items: the reply abstains, defers or states the documented alternative |
| `formatOkRate`, `genericRate` | Reading limits (primary ≤ 24 words, alternative ≤ 25, ≤ 2 sentences per line, no markup or quotes, few very long words), and empty fillers |
| `retrievalHitRate` | Materials-required items: `grounding: grounded` with an expected synthetic document among the sources |
| `termAccuracy` | Key terms (names and acronyms such as `Ruairi`, `TALLIS`, `ORN-4`) recognized in the utterance that says them; also `micro` and `byTerm` |

The optional judge (`--judge`) sends only the synthetic item, its synthetic reference documents and the reply. It
returns anchored 0/1/2 scores for **usefulness** (does the reply answer the current question so it can be said now?) and
**support** (is every claim backed by the reference, with no unsupported promise?). Judge scores are reported separately
under `judge` and are advisory.

### Statistics

- **Repeats and accents:** the default is 3 repeats. Each repeat rotates the accent per item, so with 3 accents every
  item is heard once in each.
- **Percentiles:** p50 is the median, withheld below 5 samples. p95 uses the nearest rank, withheld below 20 samples.
- **Rates:** question-level means, so every item weighs the same.
- **Confidence intervals:** 95% intervals from a cluster bootstrap that resamples whole items, with 2000 samples and a
  fixed seed. They are recorded in the results.

### Baseline and gate

`compare BASELINE CANDIDATE` exits with:

- **0** when the candidate passes;
- **1** when it regresses;
- **2** when the two results are not comparable: a different corpus or items hash, scorer, item selection, session
  options, accents or audio source. Use `--allow-mismatch` to compare anyway.

Results record a fingerprint of the scorer source (`scorer.sha256`). Results written before it existed have none and
skip that check, except against a rescored result, which is comparable only with results scored by the same scorer. To
compare runs scored at different times, [rescore](#rescore-a-finished-run-offline) both with the same items file.

| Rule | Default |
| --- | --- |
| Latency: p95 or p50 of `speechEndToFirstDelta` / `speechEndToCompleted` grows by more than 10% **and** by more than 100 ms | `--max-p95-regression 0.10`, `--max-p50-regression 0.10`, `--min-latency-delta-ms 100` |
| Quality: the candidate's point estimate falls outside the baseline's 95% CI in the bad direction | always on, for answered/pass/fact/abstain/format/retrieval/term rates and forbidden/generic rates |
| Judge usefulness/support below the baseline CI | advisory; gates only with `--gate-judge` |

Withheld percentiles are skipped, not failed. Run the baseline and the candidate with the same options and repeats. A
3-repeat run gives about 108 samples, enough for stable p95.

## Cost and time (rough; check current Azure pricing)

- **Wall time:** a full run (36 items × 3 repeats = 108 sessions) streams real-time audio. Each session takes about
  10–15 s, so a run takes about 20–30 minutes. The Fake-mode dry run of 36 sessions took about 3 minutes.
- **Speech-to-text:** about 15–20 minutes of real-time recognition per full run.
- **TTS:** 153 clips (51 per accent, about 6,300 characters across three accents), synthesized once and then cached.
- **Azure OpenAI:** per session, one reply plus the server's turn classifier, plus replies to context questions.
- **Search:** grounded mode retrieves for every reply and speculatively for partials. That means embeddings plus a few
  hundred semantic-ranker queries per full run. On the Search **free** semantic plan (1,000 queries per month), a few full
  runs exhaust the quota and later retrievals fail with HTTP 402, so use the standard plan.
- **Judge:** about 108 extra calls when enabled.

## Limitations

- The fictional corpus is small and clean. Failures on real materials (scanned PDFs, long decks, near-duplicate
  documents) are not represented.
- TTS speech is clearer than a meeting room. Expect real accuracy and recognition to be lower than this harness shows.
- The deterministic checks are lexical. A correct paraphrase without the expected key phrase fails and needs reading via
  `details.jsonl`. The judge is advisory and is not calibrated against human labels here.
- Latency is measured at the server boundary on the operator's network. It is not browser-to-screen latency.
