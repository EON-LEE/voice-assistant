# Browser meeting protocol v1

Same-origin browser application; microphone or user-selected tab audio is captured only with explicit browser/user permission. The API does not capture Teams audio itself and does not store audio, transcript or responses.

## Authentication

`GET /api/client-config` returns public configuration:

```json
{"clientId":"SPA-CLIENT-ID","authority":"https://login.microsoftonline.com/TENANT-ID","scope":"api://API-CLIENT-ID/Meeting.Access","mode":"Azure","webSocketPath":"/api/meeting"}
```

Use Entra authorization code + PKCE with tokens in memory. API app registration must issue v2 access tokens (`requestedAccessTokenVersion: 2`), expose delegated scope `Meeting.Access`, and grant the SPA that scope. `Authentication:Audience` must match the **access token's API aud**, not the SPA client ID. No app-only tokens are accepted.

`POST /api/session/ticket`, `Authorization: Bearer <access-token>` and browser `Origin`, returns:

```json
{"ticket":"43-character-base64url-random-value","expiresAt":"2026-09-18T00:00:30+00:00"}
```

Upgrade `wss://<same-origin>/api/meeting?ticket=<ticket>` before expiration. Tickets contain 256 random bits, are process-local, expire at 30 seconds, are atomically consumed once even on an origin mismatch, and carry the validated user's object ID and exact origin. Only configured `Security:AllowedOrigins` are accepted. No bearer access tokens in URLs. Suppress query strings in proxy/platform access logs as well as application logs.

**Deploy one process/one replica.** A rolling revision/restart can invalidate tickets; the browser should fetch a new ticket and retry. Scaling requires an atomic shared expiring ticket store (not just separate local caches) or reliable affinity that routes ticket POST and WebSocket to the same process. One simultaneous session per object ID, at most 100 sessions and 4096 outstanding tickets per process; at most 8 outstanding tickets per user.

Explicit `Provider:Mode=Fake` plus environment `Development` accepts bare WebSockets only when peer IP, Host and Origin are loopback and `Sec-Fetch-Site` is not `cross-site`. Origin is still required. It never activates automatically on Azure errors. Fake mode cannot start in Production.

## Stream

The first message must be text JSON:

```json
{"type":"session.start","protocolVersion":1,"audio":{"encoding":"pcm_s16le","sampleRate":16000,"channels":1}}
```

Wait for `{"type":"session.ready","protocolVersion":1}` before sending binary frames containing headerless PCM signed 16-bit little-endian, 16,000 samples/second, mono. Resample in the browser; never send WebM/Opus or a WAV header. Each binary message is even-length, 1-32768 bytes (zero-length rejected). The initial `session.start` is bounded to32768 UTF-8 bytes to allow escaped multibyte context; subsequent text commands remain at most4096 bytes. Fragmentation is supported and limits apply to the complete message. Send near real-time: token bucket allows 128 KB burst and refills 64 KB/s. Startup deadline15 seconds; receive idle deadline90 seconds; response deadline30 seconds. Session lifetime is configured server-side by `Session:MaxMinutes` / `Session__MaxMinutes` (integer5..180, default30; invalid values prevent startup). Send silence when capture is active but quiet.

At the maximum duration the server emits existing `error` with `code:"session_time_limit"`, `retryable:false`, and message `Session reached its time limit. Start a new session to continue.`, then closes. This is distinct from `session_ended` for other cancellation and `idle_timeout` for90-second input inactivity. The duration is not controlled by untrusted `session.start` fields.

Optional top-level `session.start.options` is immutable for this socket:

```json
{"responseMode":"balanced","profile":{"name":"Mina","role":"Software engineer","project":"Evaluating email client interoperability"},"profileConfirmed":true,"topic":"Email protocols","phrases":["JMAP","IMAP","CONDSTORE","QRESYNC"],"endSilenceMs":500}
```

The example profile is fictional test data, not the user's identity. Missing options preserve legacy `grounded` mode and700ms segmentation silence. Missing individual properties use those same defaults; the new browser explicitly sends `balanced`/500ms. Allowed modes are `balanced`, `grounded`, `conversation`. Profile name/role/project limits are100/160/300 characters; topic300. Any nonempty profile requires explicit `profileConfirmed:true`; edit/reset confirmation in the browser before starting another session. Phrase list is at most40 nonblank strings, each at most64 characters, at most2048 total characters. Silence is an integer350..1500ms inclusive. Unknown options/profile fields, duplicate fields, wrong types, control characters, unconfirmed profile and oversized values fail as `invalid_start`. JSON null is not an omission. No profile/topic/phrase is persisted or logged.

Optional `transcribeOnly` (boolean, default false, JSON null is not an omission) makes the socket recognition-only for practice mode: `transcript.partial/final` are emitted as usual but there is no automatic response, no Search and no model call, and `response.request` returns `error` `code:"transcribe_only"`, `retryable:false` while the session continues. See `practice-v1.md`.

Speech receives the explicit phrase list through `PhraseListGrammar` before continuous recognition starts, plus the configured segmentation silence. Hints are recognition bias, not verified facts. Confirmed profile fields are passed as untrusted JSON facts for relevant introductions, not instructions or evidence of personal history, employer, dates or commitments.

Routing: `grounded` always retrieves. `conversation` explicitly skips Search and instructs the model to abstain from unsupported private facts. `balanced` skips only anchored clear conversational requests, allowlisted self-contained general technical questions, or introductions covered by confirmed profile fields. Company/customer/date/commitment questions and ambiguous fragments default to Search. Up to12 prior turns/24000 characters can make routing more conservative; history never turns an ambiguous fragment into a confident knowledge-free answer. The model receives bounded history even when routing chooses Search. General technical questions should get direct explanations, not irrelevant requests for personal details.

Text commands: `{"type":"response.request"}`, `{"type":"response.cancel"}`, `{"type":"session.stop"}`. Manual request uses the latest finalized utterance; before any final it returns a retryable `no_transcript` error. At most 10 commands/second. A finalized utterance automatically starts a response. A new partial utterance cancels the previous response; a new final/manual request supersedes pending generation. After cancellation, obsolete deltas/completions are discarded. Socket writes are serialized.

Server events:

For a clear individual/group introduction request with any confirmed profile fields, balanced/conversation mode selects a **deterministic** `profile` response: fixed English labels and verbatim supplied name/role/project values only. It does not call Search or the language model, invent history or answer an unknown first-touchpoint question. Missing fields are omitted without placeholders. Mixed customer/date/commitment/employer requests are not eligible for this shortcut; grounded mode retains retrieval. `profile` completions have `grounding:disabled`, empty sources and `retrievalPrefetched:false`, and are excluded from model response histograms. The UI should ask users to enter concise English profile wording and identify the profile reply as composed from confirmed fields, not model-generated. Caller-provided punctuation/quotes remain literal user content, not interpreted instructions.

| type | Additional fields |
| --- | --- |
| `session.ready` | `protocolVersion: 1` |
| `transcript.partial` / `transcript.final` | `turnId: string`, `revision: integer` (increasing within turn), `text: string` (replacement, not delta) |
| `response.started` | `responseId: string`, `turnId: string` |
| `response.delta` | `responseId`, `turnId`, `text: string` (append) |
| `response.completed` | `responseId`, `turnId`, `text: string` (complete replacement), `sources: [{title,url,updatedAt}]`, `grounding`, `responseRoute: transcript\|profile\|knowledge`, `retrievalPrefetched: boolean` |
| `response.cancelled` | `responseId`, `turnId` |
| `error` | `code: string`, `message: string` (safe user-facing detail), `retryable: boolean` |

`updatedAt` is an ISO timestamp or null if source metadata does not provide it. `grounding` is `disabled`, `grounded`, `no_matches`, or `unavailable`. Sources are relevance-filtered reference candidates, not verified citations or a guarantee that every generated statement is supported. Render as text, validate http(s) URLs, and show grounding status. Search failure emits `error` with `grounding_unavailable` followed by a deterministic refusal-like completion (`unavailable`, empty sources); the language model is **not** called. When a successful search yields no authorized relevant results, the real model can provide general conversational phrasing from the transcript alone (`no_matches`, empty sources), but must not invent company-specific knowledge or claim to have consulted supporting documents.

Fake: >=640 nonzero audio bytes emit deterministic partial+final once per utterance. Further nonzero audio is ignored until >=16000 consecutive zero bytes (500 ms silence). All-zero audio never fabricates speech. Transcript: `Could you briefly explain the next steps?` Answer: `Let's confirm the goal, agree on the next action, and assign an owner.` Response uses three timed deltas. Fake is synthetic test behavior, not recognition.

## Grounding

The additive `responseRoute` describes the selected context path, not answer correctness: `transcript` and `profile` skip Search and use `grounding:disabled` with empty sources; `knowledge` may result in any existing grounding state. `retrievalPrefetched:true` means an already-started partial retrieval (including an in-flight or failed one) was selected for the exact final utterance, not a latency or relevance guarantee. It remains false for transcript/profile routes and fresh final retrieval.

For knowledge routes only, partial text with at least20 characters and4 whitespace-separated words is eligible after250ms without a changed normalized partial. At most3 speculative retrievals start per utterance. There is one active speculative worker/candidate per socket/identity; revisions cancel obsolete work, and a noncooperative provider occupies that worker rather than spawning unbounded replacements. Reuse requires the **same turn and exact lowercase/whitespace-normalized text**. Punctuation, apostrophes, decimals, minus signs, dates, numbers and negations are preserved; a punctuation-only final change can intentionally miss the cache. No cross-user or cross-session cache exists; ACL uses the ticket-bound identity. Explicit cancel/stop invalidates speculative work. Matched retrieval failure remains visible as `grounding_unavailable`, never a success-shaped fallback. Unmatched speculative results are discarded and final retrieval runs normally.

`search-index.json` is the minimum index schema, including the `meeting-semantic` title/content configuration. Optional additional fields are allowed. Populate `contentVector` with the same deployment/model as query embeddings: `text-embedding-3-small`, 1536 dimensions. The API performs hybrid text + vector retrieval over up to 50 candidates, semantically reranks them, and passes at most 5 accepted results to the model. The configurable minimum reranker score defaults to 2.0 on the service's 0-4 scale; it needs corpus-specific evaluation and is not a correctness guarantee. Missing/invalid scores, partial semantic results, or ranker failures produce `grounding_unavailable`, never an unfiltered fallback. Candidates remain prefiltered by:

```text
allowedPrincipalIds/any(p: p eq '<validated-oid-GUID>')
```

ACLs contain canonical lowercase **user object IDs from the configured Entra tenant**. Group expansion, wildcard/public access, user-supplied ACL filters and cross-tenant ACLs are not supported. Ingestion must copy current source permissions and remove/reindex revoked access before exposing the index; this API cannot infer source permissions. It does not query Blob Storage. Documents are untrusted evidence, never executable instructions. Only 5 chunks of at most 6000 characters each enter the model; transcript context is at most 12 turns / 24000 characters.
