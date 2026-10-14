# Meeting scenario evaluation

This toolchain turns a presentation or script into repeatable meeting-assistant checks without modifying the source file.
It supports legacy OLE PowerPoint files even when their extension is `.pptx`.

## Repeat the cycle

1. Extract text with installed PowerPoint in read-only, hidden mode:

   ```powershell
   .\tools\MeetingScenarioEval\Extract-PresentationText.ps1 `
     -InputPath 'C:\path\meeting-deck.pptx' `
     -OutputPath 'C:\temp\meeting-deck.extracted.json'
   ```

2. Normalize layout text:

   ```powershell
   python .\tools\MeetingScenarioEval\scenario_tool.py normalize `
     --input C:\temp\meeting-deck.extracted.json `
     --output C:\temp\meeting-deck.normalized.json
   ```

3. Update a declarative `*.plan.json` with grounded facts, unknowns, and scripts, then expand and validate it:

   ```powershell
   python .\tools\MeetingScenarioEval\scenario_tool.py generate `
     --plan .\tools\MeetingScenarioEval\intent-intelligence-us.plan.json `
     --output C:\temp\intent-intelligence-us.scenarios.json
   python .\tools\MeetingScenarioEval\scenario_tool.py validate `
     --scenarios C:\temp\intent-intelligence-us.scenarios.json
   ```

4. Run the production reply prompt with those facts as grounding. This calls Azure OpenAI and needs `OPENAI_TOKEN`;
   `EVAL_OPENAI_ENDPOINT` and `EVAL_CHAT_DEPLOYMENT` are optional:

   ```powershell
   dotnet run --project .\tools\ReplyQualityEval -- `
     --scenarios C:\temp\intent-intelligence-us.scenarios.json `
     C:\temp\intent-intelligence-us.responses.json
   ```

5. Score supported facts, unsupported claims, abstention, number/negation/question-intent preservation,
   generic answers, and first-response latency:

   ```powershell
   python .\tools\MeetingScenarioEval\scenario_tool.py score `
     --scenarios C:\temp\intent-intelligence-us.scenarios.json `
     --responses C:\temp\intent-intelligence-us.responses.json `
     --output C:\temp\intent-intelligence-us.score.json
   ```

The committed response file is a deterministic scorer fixture, not a claim about live model quality. Use a fresh path for
every extraction, generation, model run, and score; outputs are intentionally not overwritten.

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
the configured settle behavior.
