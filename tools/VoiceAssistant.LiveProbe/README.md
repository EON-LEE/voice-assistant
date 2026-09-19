# Explicit live Azure acceptance probe

This .NET 8 console references the **same `AzureMeetingProvider`** as the API. It sends an approved original synthetic English WAV through real Azure Speech, then submits only its recognized synthetic utterance to real Azure OpenAI streaming chat. It never substitutes Fake for Azure. It does not start an HTTP server or test browser capture, Entra API JWT/tickets, Search, source ACLs, or deployment readiness.

## Run without a login prompt

Build separately so MSBuild output is not mixed into the single-line JSON evidence:

```powershell
dotnet build .\tools\VoiceAssistant.LiveProbe\VoiceAssistant.LiveProbe.csproj -c Release
dotnet .\tools\VoiceAssistant.LiveProbe\bin\Release\net8.0\VoiceAssistant.LiveProbe.dll --help
dotnet .\tools\VoiceAssistant.LiveProbe\bin\Release\net8.0\VoiceAssistant.LiveProbe.dll --live --audio .\tests\VoiceAssistant.Web.E2E\fixtures\original-project.wav --metadata .\tests\VoiceAssistant.Web.E2E\fixtures\original-project.json --timeout-seconds 90
```

`--live` is explicit permission to submit **the approved fixture only** to the configured Azure Speech/OpenAI resources and may incur service charges. Do not use meeting recordings, company text, third-party audio, or arbitrary downloaded fixtures. The original fixture is generated locally by the browser component and its metadata must authorize live use. The probe reads but never creates or changes Azure resources.

Required environment variables are `Azure__SpeechRegion`, `Azure__SpeechResourceId`, `Azure__OpenAIEndpoint`, and `Azure__ChatDeployment`. `Azure__SpeechEndpoint` is optional (HTTPS portal endpoint). `Provider__Mode` must be absent or `Azure`; setting it to `Fake` is a **BLOCKED** configuration, not an implicit override. The probe intentionally ignores Search settings and needs no API authentication/audience/SPA configuration.

`Azure__ChatMaxOutputTokens` optionally sets the completion budget64..4096 (default2048). On reasoning models this budget also covers reasoning tokens; an exhausted budget yielding no text is an explicit failure, not success. Temperature is omitted because some reasoning deployments reject custom temperature values. The provider does not send unsupported/raw reasoning flags; response wording remains bounded by the system prompt and the8000-character stream limit.

The pinned Azure.AI.OpenAI2.1 client otherwise rewrites `MaxOutputTokenCount` to legacy `max_tokens`, which GPT-5 rejects. The API explicitly enables `max_completion_tokens` via the documented `SetNewMaxCompletionTokensPropertyEnabled` extension. Because that pinned extension requires an initialized additional-property bag, options are created with the public SDK `ModelReaderWriter` before applying the extension. The regression test captures the actual Azure client's serialized request, not merely the C# property.

Authentication uses the standard `DefaultAzureCredential` chain shared with the provider, including existing managed identity, Azure CLI, Azure PowerShell, and developer credentials. Interactive browser and broker are explicitly excluded. There is no custom token bridge, cached-token extraction, API key, interactive login retry, or check that assumes Azure CLI is the only credential. Local credential child processes have a20-second budget and overall authentication has a30-second timeout; failure produces **BLOCKED** without opening a Speech stream or calling OpenAI. Successful token acquisition is not evidence of resource permissions; subsequent Azure service failures are **FAILED**, not passed readiness.

Flags are exactly `--live`, `--audio <path>`, `--metadata <path>`, `--timeout-seconds <1..180>` (default 90), or standalone `--help`. Unknown/duplicate flags are rejected. Ctrl+C requests cancellation. No `--source-revision` argument is accepted: caller-provided provenance would not attest to the binary.

For a bounded model-only diagnosis, `--live --chat-only [--timeout-seconds 45]` sends one fixed original generic English request for a project-update sentence. It uses the same Azure configuration and standard credential preflight, but no WAV, Speech, Search, or corporate documents. Do not combine it with audio/metadata flags. Its JSON has `scope: "openai_only"` and `fullPipelineVerified:false`; a successful model-only diagnosis is **not** a full speech/model acceptance result.

`firstDeltaMs` measures entry into this model call to the first nonempty delta; `elapsedMs` measures completion. Both include any SDK-internal credential acquisition and connection setup after preflight. They are not pure model-generation latency. Reusing the API's singleton provider/client permits SDK token/connection caching; restarting this console for each measurement does not reproduce that warm-client behavior.

### Bounded cold/warm observation (2026-09-19 UTC)

Three sequential original-generic-prompt requests to the existing GPT-5.4-mini deployment, with **one reused AzureMeetingProvider/client**, omitted reasoning effort and the standard already-preflighted credential, produced:

| Sample | First delta | Completion | SDK credential calls | Credential acquisition |
| --- | ---: | ---: | ---: | ---: |
| 1, cold client | 7297 ms | 7324 ms | 1 | 5308 ms |
| 2, reused client | 864 ms | 917 ms | 0 | 0 ms |
| 3, reused client | 1773 ms | 1805 ms | 0 | 0 ms |

All three streamed12 deltas; no generated text or tokens were recorded. Credential time was measured by a pass-through wrapper around the standard DefaultAzureCredential, not by replacing authentication or exporting tokens. These n=3 development-host observations show cold developer-credential overhead, **not** a statistically established latency target, Azure-hosted managed-identity result, or complete meeting pipeline benchmark.

The [official GPT-5.4-mini model page](https://developers.openai.com/api/docs/models/gpt-5.4-mini) lists `none` as the default reasoning effort; [Azure reasoning guidance](https://learn.microsoft.com/azure/foundry/openai/how-to/reasoning) says `minimal` is unsupported for GPT-5.1 and later. No reasoning-effort setting or SDK upgrade was added merely to repeat the existing default. Measure the deployed concurrent API with real STT/input-ground-truth boundaries before claiming a low-latency meeting target.

### Windows PowerShell credential diagnosis

Standalone `--diagnose-auth` acquires a Cognitive Services token through the official `AzurePowerShellCredential`; `--diagnose-default-auth` checks the complete standard `DefaultAzureCredential` chain. These bounded30-second operations print only fixed error categories or `AUTHENTICATED`, scope `authentication_only`, and `serviceAcceptanceVerified:false`. They make **no Speech/OpenAI service calls**, and never print tokens, account IDs or raw SDK/process exceptions. Diagnostic success is not live acceptance.

Azure.Identity1.17 invokes `pwsh -NoProfile -NonInteractive -EncodedCommand` and, if pwsh is absent on Windows, falls back to `powershell` with the same arguments. It does **not** override execution policy. A Windows host where every execution-policy scope is Undefined has effective Restricted policy, which can prevent even an installed/signed Az.Accounts module's format files from loading. `NoAzAccountModule` can therefore mean unloadable, not simply uninstalled. The error may be localized.

If organizational policy permits it, launch the probe from a process with **process-only RemoteSigned** (signed downloaded modules remain required), not a persistent LocalMachine/CurrentUser change:

```powershell
Get-ExecutionPolicy -List
Set-ExecutionPolicy -Scope Process -ExecutionPolicy RemoteSigned -Force
dotnet .\tools\VoiceAssistant.LiveProbe\bin\Release\net8.0\VoiceAssistant.LiveProbe.dll --diagnose-default-auth
# In this same process, run the explicit --live command once config and fixture are approved.
```

This standard setting is inherited by the SDK's child PowerShell via `PSExecutionPolicyPreference`; Group Policy remains authoritative. The probe never changes execution policy itself. Do not weaken mandated policy, install untrusted modules, copy tokens to environment variables, or replace the SDK credential with manual token extraction. In the observed Windows environment, the direct SDK credential failed under default Restricted policy and succeeded under process-only RemoteSigned using the existing login; this does not prove another host's credentials or resource roles.

## Fixture validation

Only RIFF/WAVE integer PCM signed16 little-endian, 16000 Hz, one channel, block alignment2, byte rate32000 is accepted. `fmt` must be standard16 bytes or PCM18 bytes with zero extension. Unknown bounded chunks and RIFF padding are supported; duplicates, truncation, inconsistent headers, odd audio length, >30-second audio and >1,025,536-byte files are rejected. At least one nonzero sample and one full second of trailing zero PCM are required to give the recognizer time to finalize. WAV headers are never sent to Speech.

The <=8192-byte JSON metadata must include:

```json
{
  "schemaVersion": 1,
  "synthetic": true,
  "approvedForLiveUse": true,
  "language": "en-US",
  "sha256": "<64 hexadecimal SHA256 characters covering the entire WAV file>",
  "speechEndSample": null
}
```

`speechEndSample`, if known from independently annotated/generated ground truth, is the **exclusive** sample index of speech end, not the end of the file, Speech SDK final time, VAD estimate, or synthesis API completion time. Null/omitted means unknown. For the local SAPI fixture, exact semantic speech-end ground truth is unavailable: keep it null. Extra `synthesisEndSample` metadata is informational and is deliberately **not** used as speech end. Boolean approval and SHA256 are an operator attestation/integrity check, not a content classifier or cryptographic proof of authorship.

## Evidence and meaning

Stdout is one bounded JSON object with no tokens, identities, endpoints, input paths, raw audio, recognized text, response text, source documents, or raw exception messages. Exit status:

| Exit | JSON `status` | Meaning |
| --- | --- | --- |
| 0 | `SUCCESS` | Exactly one finalized synthetic utterance, counting callbacks through completed Speech shutdown, and at least one real model delta, with successful cleanup |
| 1 | `FAILED` | Provider/runtime/fixture-utterance/deadline/cleanup failure after preflight |
| 2 | `BLOCKED` | No explicit live opt-in, invalid config/arguments/approved fixture, or unavailable credentials |
| 3 | `CANCELLED` | User/caller cancellation (cleanup failure instead returns FAILED) |

Standalone `--help` exits0 but is not acceptance evidence. The orchestration gate must inspect both exit code and JSON status/provider. Offline injected test providers are always `provider: "TestDouble"` even if their pipeline succeeds. `provider: "Azure"` on **BLOCKED** identifies the requested provider, not a completed call; counts are zero and timings null.

Schema1 fields: `status`, `reason` (fixed content-free code), `provider`, `startedAt` (UTC), `elapsedMs` (monotonic), `partialEvents`, `finalEvents`, `deltaEvents`, `timings`, `scope: "speech_openai_only"`, `speechEndBoundary`, `buildSourceRevision`, `provenance`, and `exitCode`. `buildSourceRevision` is the assembly's build-time informational source revision, or null; it does **not** attest to uncommitted files. Rebuild from a clean committed checkout and retain independent source/working-tree evidence for reproducibility.

Optional `failure` reports fixed `stage` (`speech`/`openai`), numeric `httpStatus`, allowlisted service `code`, and allowlisted `parameter` (`max_tokens`, `max_completion_tokens`, `temperature`, `messages`, `messages[0].role`, `reasoning_effort`). Unrecognized values become `unknown`/null. Error bodies, freeform messages, endpoints and identities are never echoed. This distinguishes HTTP401/403 permission failures,429 throttling and400 parameter incompatibility without exposing content.

`timings` contains:

| Field | Origin |
| --- | --- |
| `audioStartMs` | Monotonic start of file pacing relative to provider-run start |
| `sourceSpeechEndOffsetMs` | Annotated `speechEndSample / 16000`, or null |
| `inputSpeechEndMs` | Actual submission timestamp of the 20ms frame containing the annotated last speech sample, or null |
| `finalSttMs` | First final transcript callback timestamp |
| `speechEndToFinalSttMs` | Final callback minus annotated-sample frame submission, or null |
| `finalSttToFirstDeltaMs` | Final callback to first nonempty model delta |
| `finalSttToCompletedMs` | Final callback to model stream completion |
| `maxFrameLatenessMs` | Largest difference from ideal sample-end pacing |

All duration clocks are monotonic, independent of UTC clock changes. Audio is supplied in 640-byte/20ms frames (last frame may be shorter) at sample-end availability; late frames are not burst to catch up. `inputSpeechEndMs` is an annotated **file input delivery** boundary at <=20ms resolution, never real microphone/acoustic end latency. Negative speech-end-to-final values, if observed, are preserved rather than clamped into misleading evidence.

This probe intentionally sends the full fixture including its silence tail and stops Speech before requesting OpenAI. Therefore final-STT-to-model timings **include remaining fixture playback and recognizer shutdown** and are not interchangeable with the concurrent WebSocket API metrics. `elapsedMs`/`startedAt` for a provider run exclude configuration and credential preflight; BLOCKED evidence describes preflight elapsed time. Deadline bounds provider execution (startup additionally15s, final-STT wait15s); cleanup can add up to10s. The underlying SDK honors cancellation; the OS/network/native runtime is still an external dependency.

No success threshold is invented. Retain actual live evidence before claiming Azure recognition/model quality, resource readiness or latency. A deterministic offline suite only validates harness logic, not Azure measurements.

## Offline tests

```powershell
dotnet test .\tests\VoiceAssistant.LiveProbe.Tests\VoiceAssistant.LiveProbe.Tests.csproj
dotnet test .\tests\VoiceAssistant.Api.Tests\VoiceAssistant.Api.Tests.csproj
```

Tests cover headers/hash/provenance gates, sample/frame pacing, unknown ground truth, monotonic timing math, cancellation/deadlines, startup/recognition/model/cleanup failures, content redaction, non-Azure test labels, config/auth blocking, Speech credential renewal, and obsolete generation suppression. They do not make live service calls.
