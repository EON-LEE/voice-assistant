# Meeting scenario evaluation

This toolchain turns a presentation or script into repeatable meeting-assistant checks without modifying the source file.
It supports legacy OLE PowerPoint files even when their extension is `.pptx`.

## Repeat the cycle

1. Extract text with installed PowerPoint in read-only, hidden mode. Classification defaults to `Confidential`, and
   `Confidential` or `Private` output is rejected when its destination is inside the repository:

   ```powershell
   .\tools\MeetingScenarioEval\Extract-PresentationText.ps1 `
     -InputPath 'C:\path\meeting-deck.pptx' `
     -OutputPath 'C:\private-artifacts\meeting-deck.extracted.json' `
     -Classification Confidential
   ```

2. Normalize layout text:

   ```powershell
   python .\tools\MeetingScenarioEval\scenario_tool.py normalize `
     --input C:\private-artifacts\meeting-deck.extracted.json `
     --output C:\private-artifacts\meeting-deck.normalized.json `
     --classification confidential
   ```

3. Keep customer/private plans outside the repository and mark their top-level `classification` as `confidential` or
   `private`. Only `public` or `synthetic` plans may write output inside the worktree. The committed example is synthetic:

   ```powershell
   python .\tools\MeetingScenarioEval\scenario_tool.py generate `
     --plan .\tools\MeetingScenarioEval\synthetic-market-signals.plan.json `
     --output C:\temp\synthetic-market-signals.scenarios.json
   python .\tools\MeetingScenarioEval\scenario_tool.py validate `
     --scenarios C:\temp\synthetic-market-signals.scenarios.json
   ```

4. Run the production reply prompt with those facts as grounding. This calls Azure OpenAI and needs `OPENAI_TOKEN`;
   `EVAL_OPENAI_ENDPOINT` and `EVAL_CHAT_DEPLOYMENT` are optional:

   ```powershell
   dotnet run --project .\tools\ReplyQualityEval -- `
     --scenarios C:\temp\synthetic-market-signals.scenarios.json `
     C:\temp\synthetic-market-signals.responses.json
   ```

5. Score supported facts, unsupported claims, abstention, number/negation/question-intent preservation,
   generic answers, and first-response latency:

   ```powershell
   python .\tools\MeetingScenarioEval\scenario_tool.py score `
     --scenarios C:\temp\synthetic-market-signals.scenarios.json `
     --responses .\tools\MeetingScenarioEval\synthetic-market-signals.responses.json `
     --output C:\temp\synthetic-market-signals.score.json
   ```

The committed response file is a deterministic scorer fixture, not a claim about live model quality or speed. Its response
times, including the 760ms median, are authored fixture values used to exercise latency scoring; they are not measurements
from a model, network stream, browser, or Azure service. Use a fresh path for every extraction, generation, model run, and
score; outputs are intentionally not overwritten. The local ignore rules cover
`*.confidential.json`, `*.private.json`, `private/`, and `local-private/`, but ignore rules are only a second line of defense:
the classification boundary rejects private output in the repository before writing it.

## AMI and ICSI timing-first evaluation

`ReplyQualityEval` accepts AMI and ICSI NXT word archives and now supports multi-character speaker IDs. Run timing-only before
paying for reply generation and LLM judging:

```powershell
$env:EVAL_TIMING_ONLY = '1'
dotnet run --project .\tools\ReplyQualityEval -- C:\temp\timing.json `
  'C:\corpora\ami-words.zip|ES2002a' `
  'C:\corpora\icsi-words.zip|Bdb001'
```

Questions fire at their final transcript time in this simulation, matching `MeetingSession`; statements and fragments retain
the configured settle behavior. For the broader public sample, use
`tools/ReplyQualityEval/Run-PublicTimingEval.ps1` with `public-timing-samples.json`. The manifest separates six exact prior
five-minute windows (including explicit target speakers) from 16 new windows and declares `audioAsrE2E: false`; transcript
timing simulation must never be reported as audio recognition validation.
