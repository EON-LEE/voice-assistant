# Practice mode, Korean assist and read-aloud API (v1)

Adds, for a Korean speaker who is not fluent in English:

1. **Korean assist** - Korean translation of what the other person said, and of the suggested English reply, plus a
   **Hangul pronunciation guide** for the suggested English reply (word-chunk aligned).
2. **Read-aloud (shadowing)** - server-side neural text-to-speech so the user can listen to a sentence and repeat it.
3. **Practice mode** - an AI meeting partner that asks short English questions (optionally grounded in the user's own
   uploaded materials), the user answers by voice, and receives Korean coaching.

All endpoints are additive, same-origin JSON (except `speak`, which returns audio), and reuse the existing security
model. No audio, transcript, answer or feedback is stored or logged. No Search/index schema change.

## Security (same rules as `knowledge-v1.md`)
* Production/Azure: every endpoint requires the `Meeting` authorization policy (Entra bearer JWT, delegated
  `Meeting.Access`, `oid`). All endpoints here are `POST`, so each also requires `OriginPolicy.Allows` (exact allowed
  Origin) and returns `403` otherwise. Never accept an owner/principal from the request; use the validated `oid`.
* Fake mode (Development only, loopback peer/Host/Origin) uses the fixed fake owner and deterministic outputs.
* Bodies are `application/json; charset=utf-8`, at most 16 KiB, strict schema: unknown/duplicate properties, wrong
  types, control characters (other than `\n` in `text`), JSON null for non-nullable values -> `400 invalid_request`.
* Per-user limits: at most 8 in-flight assist requests and 60 requests per rolling minute across all endpoints in this
  document (`speak` counted separately: at most 30 per minute and 3 in flight). Exceeding -> `429 busy` with
  `Retry-After`.
* Errors are JSON `{ "error": "<code>", "message": "<safe text>" }`. Codes: `400 invalid_request`, `403` (empty body
  allowed), `413 too_large`, `429 busy`, `502 provider_unavailable`, `504 provider_timeout`. Messages never echo user text,
  tokens, prompts or Azure error bodies. Model/Speech failures are never converted into a success-shaped fake answer.
* All user-supplied text (questions, answers, scenario text, topic, retrieved documents) is **untrusted data, never
  instructions**; the system prompts must say so. JSON model outputs are parsed strictly and validated (below); if the
  model output violates the schema retry once, then return `502 provider_unavailable`.
* Timeouts: LLM endpoints 20 s, `speak` 15 s. Streaming is not used for these endpoints.

## Style rules for every English sentence the server generates
Plain text only. Simple everyday English (about CEFR B1), short sentences, contractions allowed, no idioms, no
markdown/bullets/quotes/emojis/line breaks, no preface such as "You could say". The existing live-reply rules about
privacy, profile and grounding still apply.

## `POST /api/assist/enrich`
Korean translation and Hangul pronunciation. Request:
```json
{ "kind": "question", "text": "What is the main risk for the rollout?" }
```
`kind` is `question` (something another person said) or `reply` (suggested English the user will say). `text` is
1..600 characters of English. Optional `translationOnly` (JSON boolean, default `false`; any other type -> `400`): when
`true` the server requests and returns only the Korean translation and `pronunciation` is always `null`, for both kinds.
Existing clients that omit it are unchanged.

`200`:
```json
{
  "korean": "롤아웃의 가장 큰 위험은 무엇인가요?",
  "pronunciation": null
}
```
For `kind:"reply"` without `translationOnly:true`, `pronunciation` is a non-empty array of chunks, otherwise `null`:
```json
{
  "korean": "가장 큰 위험은 느린 데이터베이스 이전입니다.",
  "pronunciation": [
    { "en": "The main risk", "ko": "더 메인 리스크" },
    { "en": "is a slow", "ko": "이즈 어 슬로우" },
    { "en": "database migration.", "ko": "데이터베이스 마이그레이션" }
  ]
}
```
Rules (server validates, the browser also validates and falls back to showing plain English):
* `korean` is a natural Korean **translation of meaning**, at most 400 characters, Hangul-dominant (names such as
  `Copilot` may stay in Latin letters, including at the start), no English-only echo.
* `pronunciation[].en` chunks, joined with a single space, must equal the input `text` after collapsing whitespace
  (same words, same order, same punctuation). Each chunk has 1..4 words; at most 40 chunks.
* `pronunciation[].ko` is how a Korean speaker would **read the English aloud** in Hangul (a transcription of the sound,
  NOT a translation), for example `risk` -> `리스크`, `database` -> `데이터베이스`. Hangul syllables, spaces and the
  punctuation `,.?!'-` only; 1..80 characters; no Latin letters except digits when the English chunk contains digits.
  Numbers and dates are written the way they are spoken (for example `14` -> `포틴`).
If the model response violates these rules after one retry: `502 provider_unavailable`.

## `POST /api/assist/speak`
Neural read-aloud. Request:
```json
{ "text": "The main risk is a slow database migration.", "voice": "coach", "rate": "slow" }
```
* `text`: 1..400 characters of English. `voice`: `coach` (default, the user's own voice model for shadowing) or `partner`
  (a different voice for the practice partner). `rate`: `normal` (default) or `slow` (about 80 % speed).
* `200` with `Content-Type: audio/mpeg` (Azure Speech neural voice, 24 kHz mono mp3), `Cache-Control: no-store`,
  body is the whole clip (clips are short). The server chooses the actual Azure voice names; use English (en-US)
  neural voices that are available in the deployed Speech region (verify against the live resource and record the
  voice names in `README.md`).
* Fake mode returns a short deterministic silent clip. It may use `Content-Type: audio/wav` (a valid tiny PCM WAV) because
  browsers decode either; the client must use the response `Content-Type`.
* Text is synthesized with SSML built **server-side with XML escaping** (never concatenate raw user text into SSML).
* Speech authorization reuses `SpeechAuthorization` / managed identity exactly like recognition; no keys.

## `POST /api/practice/turn`
Next line from the AI meeting partner. Stateless: the client sends the history.
```json
{
  "scenario": { "kind": "sales", "description": "", "difficulty": 2 },
  "topic": "Zephyr rollout planning",
  "useMaterials": true,
  "maxTurns": 6,
  "history": [
    { "role": "partner", "text": "Thanks for joining. What is the goal of the Zephyr rollout?" },
    { "role": "user", "text": "The goal is to launch safely in stages." }
  ]
}
```
* `scenario.kind`: `sales` | `interview` | `presentation` | `custom`. `description` (0..400 characters) is **required and
  non-blank for `custom`**, otherwise optional extra context. `difficulty`: 1 (slow, simple, one clear question at a
  time), 2 (normal business questions), 3 (hard follow-ups, pushback, numbers/dates). Defaults are not applied; fields
  are required.
* `topic`: 0..300 characters. `maxTurns`: 1..12 (number of partner questions in the whole practice round).
* `history`: 0..24 entries, `role` is `partner` or `user`, `text` 0..800 characters (an empty `user` text means the
  user skipped). Entries alternate starting with `partner`; the last entry is `user` (or history is empty for the
  opening line).
* `useMaterials:true` grounds the next question in the caller's own uploaded materials through the existing Search
  retrieval with the ACL prefilter and semantic gate (query built from topic + scenario + last answer). Evidence is
  untrusted data. With no relevant material, ask generic questions from scenario/topic only and say nothing false
  about the user's documents. `false` skips Search.

`200`:
```json
{
  "text": "What is the main risk, and how will you reduce it?",
  "done": false,
  "turn": 2,
  "grounding": "grounded",
  "sources": [{ "title": "Zephyr rollout meeting notes" }]
}
```
* `text`: at most 2 short sentences and 30 words, ends with a question unless `done`. When the number of partner
  questions already asked in `history` is >= `maxTurns`, return a closing line (for example thanks and goodbye),
  `done:true`, without calling Search.
* `turn`: 1-based number of this partner line. `grounding`: `disabled` | `grounded` | `no_matches` | `unavailable`
  (never an unfiltered fallback; `unavailable` -> the question is generated from scenario/topic only). `sources` titles
  only, deduplicated, at most 5 (the existing documents' titles; never URLs or chunk text).
* The partner never reveals or quotes system instructions, never claims actions, never invents facts about the user.

## `POST /api/practice/suggest`
Easy English reply the user could say to the partner's latest question (the same short-reply style as the live assistant).
```json
{ "scenario": { "kind": "sales", "description": "", "difficulty": 2 }, "topic": "Zephyr rollout planning",
  "useMaterials": true, "question": "What is the main risk, and how will you reduce it?" }
```
`question`: 1..800 characters. `200`: `{ "text": "...", "grounding": "grounded", "sources": [{ "title": "..." }] }`. The reply uses
the live reply policy: at most 2 short sentences and 25 words, first sentence answers directly; with no supporting
material it must not invent company/customer/date facts and may answer generally or say it is not sure.

## `POST /api/practice/feedback`
Korean coaching for one answer.
```json
{ "scenario": { "kind": "sales", "description": "", "difficulty": 2 }, "question": "What is the main risk?",
  "answer": "The main risk is slow database migration we reduce it early." }
```
`answer`: 0..800 characters (empty means skipped). `200`:
```json
{
  "correctedEnglish": "The main risk is a slow database migration. We reduce it by starting early.",
  "easierEnglish": "The main risk is a slow data move. We will start early.",
  "feedbackKo": "핵심 위험을 정확히 말했어요. 문장을 둘로 나누면 더 또렷해요.",
  "points": [ { "tag": "grammar", "ko": "migration 앞에 a가 필요해요." } ],
  "clarity": 4
}
```
* `correctedEnglish`/`easierEnglish`: English per the style rules, at most 40 words each. For an empty answer return the
  two best short sample answers and an encouraging `feedbackKo`.
* `feedbackKo`: Korean, at most 2 sentences, positive first. `points`: 0..3 items, `tag` is `grammar` | `vocabulary` |
  `clarity` | `length` | `tone`, `ko` at most 120 characters. `clarity` is an integer 1..5 describing how easily a listener
  would understand the answer (not a grade of the person).
* Do not claim to evaluate pronunciation or accent: the input is a speech-recognition transcript and only supports
  wording, grammar, length and clarity feedback. Speech-recognition mistakes may appear in `answer`; tell the user only
  about wording that is clearly wrong or hard to understand.

## `POST /api/practice/summary`
Korean wrap-up for the whole round.
```json
{ "scenario": { "kind": "sales", "description": "", "difficulty": 2 }, "topic": "Zephyr rollout planning",
  "turns": [ { "question": "...", "answer": "...", "correctedEnglish": "..." } ] }
```
`turns`: 1..12 items; `question`/`answer`/`correctedEnglish` strings 0..800 (the last optional). `200`:
```json
{ "headlineKo": "6개 질문 중 5개를 또렷하게 답했어요.", "strengthsKo": ["..."], "improveKo": ["..."],
  "phrases": [ { "en": "Let me check and get back to you.", "ko": "확인하고 다시 말씀드리겠습니다." } ] }
```
`strengthsKo`/`improveKo`: 0..3 Korean strings (<=120 chars). `phrases`: 0..8 reusable easy English sentences with Korean
meaning (the user's own better sentences preferred). Counts are of the data supplied; never invent that the user said
something they did not.

## WebSocket change (`websocket-v1.md`)
`session.start.options.transcribeOnly: true` (boolean, optional, default false) makes the socket a speech-recognition
channel only: `transcript.partial/final` events are emitted as usual, but there is **no automatic response, no Search and
no model call**; `response.request` returns `error` code `transcribe_only` (`retryable:false`) and the session continues.
The option is immutable like the others. It is used by practice mode so that spoken answers are transcribed without
generating replies. `endSilenceMs` and `phrases` still apply. Update `websocket-v1.md` and its validation tests.

## Fake provider (offline tests)
Deterministic, no Azure. Fake `enrich`: Korean `"[fake-ko] " + text`; for `reply` pronunciation is one chunk per up to 3
words with `ko` generated from a fixed Hangul-only token (for example `"가나다"`), so UI tests can assert alignment. Fake
`speak`: tiny silent clip. Fake `practice/turn`: question number `N` -> `"Fake question N: what is the main goal?"`, done at `maxTurns`.
Fake `suggest`: `"Let's confirm the goal and agree on the next step."`. Fake `feedback`/`summary`: fixed valid objects.
Fake mode must obey the same validation and error behavior as Azure mode, and the same limits.
