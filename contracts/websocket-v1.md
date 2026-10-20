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

Speech recovery: when the recognizer stops with a connection failure, a service time-out or service unavailable, the server recreates it in-process instead of ending the session. This happens at most 3 times per session, and a failed attempt also counts. The waits before the attempts are 0.5, 1 and 2 seconds. No event is sent. The transcript history, settings and any reply in progress are kept, but audio that arrives while the recognizer is being replaced is not transcribed. Any other cancellation, or a retryable one after the restarts are used up, ends the session with `error` `code:"speech_unavailable"` as before. A failed renewal of the Speech token is retried while the current token is still valid. Only a token that expired without a successful renewal ends the session, with `speech_authentication_failed`.

Optional top-level `session.start.options` is immutable for this socket, except `responseMode` and `replyKorean`, which `session.update` can change while the session runs (see below):

```json
{"responseMode":"balanced","profile":{"name":"Mina","role":"Software engineer","project":"Evaluating email client interoperability"},"profileConfirmed":true,"topic":"Email protocols","phrases":["JMAP","IMAP","CONDSTORE","QRESYNC"],"endSilenceMs":500}
```

The example profile is fictional test data, not the user's identity. Missing options preserve legacy `grounded` mode and700ms segmentation silence. Missing individual properties use those same defaults; the new browser explicitly sends `balanced`/500ms. Allowed modes are `balanced`, `grounded`, `conversation`. Profile name/role/project limits are100/160/300 characters; topic300. Any nonempty profile requires explicit `profileConfirmed:true`; edit/reset confirmation in the browser before starting another session. Phrase list is at most40 nonblank strings, each at most64 characters, at most2048 total characters. Silence is an integer350..1500ms inclusive and applies to silence segmentation and recognition-only sessions; it has no effect under `semanticSegmentation` (see below). Unknown options/profile fields, duplicate fields, wrong types, control characters, unconfirmed profile and oversized values fail as `invalid_start`. JSON null is not an omission. No profile/topic/phrase is persisted or logged. A server running the phrase-hint experiment (server configuration `Speech:MaterialPhraseHints`, off by default) also gives the speech recognizer up to 100 hints from the caller's own materials, after the client's phrases. Prompts and translation still use only the client's phrases, and `transcribeOnly` sessions get no hints.

Optional `transcribeOnly` (boolean, default false, JSON null is not an omission) makes the socket recognition-only for practice mode: `transcript.partial/final` are emitted as usual but there is no automatic response, no Search and no model call, and `response.request` returns `error` `code:"transcribe_only"`, `retryable:false` while the session continues. See `practice-v1.md`.

Recognition-only sessions do not create a partial-retrieval worker or compose confirmed-profile replies.
`response.cancel` remains safe and `session.stop` closes normally; phrase hints and end-silence settings still reach
Speech. The option cannot be changed after the handshake. Clients should not treat the manual-request rejection as
a terminal transport error; further spoken answers are still transcribed.

Speech receives the explicit phrase list through `PhraseListGrammar` before continuous recognition starts, plus the configured segmentation silence. Hints are recognition bias, not verified facts. Confirmed profile fields are passed as untrusted JSON facts for relevant introductions, not instructions or evidence of personal history, employer, dates or commitments.

Routing: `grounded` always retrieves. `conversation` explicitly skips Search and instructs the model to abstain from unsupported private facts. `balanced` skips only anchored clear conversational requests, allowlisted self-contained general technical questions, or introductions covered by confirmed profile fields. Company/customer/date/commitment questions and ambiguous fragments default to Search. Up to12 prior turns/24000 characters can make routing more conservative; history never turns an ambiguous fragment into a confident knowledge-free answer. The model receives bounded history even when routing chooses Search. General technical questions should get direct explanations, not irrelevant requests for personal details.

Optional `semanticSegmentation` (boolean, default false) selects Azure Speech's sentence-oriented
`Semantic` strategy for continuous meeting captions. Native meeting mode enables it by default;
the legacy/browser silence strategy is unchanged. Recognition-only practice keeps silence-based
boundaries even if the flag is present. While Semantic segmentation is active (the flag without
`transcribeOnly`), `endSilenceMs` is still validated but the server does not send it to Speech, so it
has no effect on the boundaries. Semantic segmentation aims for complete sentences, not
speaker diarization or guaranteed grammatical correction; partial text remains provisional.

Optional `replySettleMs` (integer, `0` or `300..5000`, default `0`; JSON null is not an omission) makes the server answer the
gathered meaning of recent speech instead of every final utterance. After each final the server waits this long for more
speech, and new speech re-arms the wait, so a question split across several finals becomes one reply. Backchannels such as
`Yeah.` are discarded, a question or direct request (`Tell me about yourself.`, `Walk us through the rollout.`) is answered,
and a statement is answered once it reaches 12 words; a shorter statement keeps waiting for more context. The wait shortens to
`min(replySettleMs, 300)` ms when the gathered speech is a question or request that ends like a finished sentence (it does not
end on a preposition, auxiliary, article or conjunction), and to 300 ms once speech has been gathered for more than 12 seconds.
Gathered speech is bounded by the same 12-turn history the model sees: a waiting segment that leaves that history is dropped
from the reply target (and a remainder of only backchannels is discarded), never an error.
While the server waits it already retrieves for the latest final. `response.request` replies at once. `0` keeps one reply per
final utterance.

A server may run with the early-start experiment switched on (server configuration `Session:EarlyStartAtFinal`, off by default;
no client option). Then a final that makes the gathered speech a question or request that ends like a finished sentence starts
its reply at once instead of after the shortened wait. The wait still runs. If more speech in it changes the target (anything
but backchannels), that reply is replaced like a newer request: `response.cancelled`, then `response.started` for the whole
gathered speech. Otherwise the reply stands, and no second reply follows. A reply that is already answering an earlier window
still holds a new question back until it completes, exactly as without the switch. Clients need no change: every event is one
they already handle.

Optional `replyKorean` (boolean, default false, JSON null is not an omission) asks the server to translate every reply line into
Korean and push it as `response.korean`, so the client needs no second HTTP request after `response.completed`. `session.ready`
then carries `"replyKorean":true`. A client that did not opt in receives neither that field nor `response.korean` events, and
the server makes no translation calls for it. A server that does not know the option rejects the start as `invalid_start`.

Optional `session.update` changes the document-search mode and the Korean push of a running session, so a client can switch
either one without reconnecting:

```json
{"type":"session.update","options":{"responseMode":"conversation","replyKorean":false}}
```

`options` accepts only `responseMode` (`balanced`, `grounded`, `conversation`) and `replyKorean` (boolean); at least one must be present. Unknown or duplicate properties, wrong types, JSON null and an empty object fail as the
fatal `invalid_command`. The server answers in order with `{"type":"session.updated","responseMode":…,"replyKorean":…}` carrying the full effective values.
A reply that was already started (`response.started` sent) keeps the settings it started with: a switch to `conversation` does not stop a search that
reply already depends on, and a reply started without `replyKorean` gets no pushed Korean. The change applies from the next reply, and a pending
partial-speech retrieval is dropped when the mode changes. A client that switches `replyKorean` on translates an older or running reply itself through the
enrichment API. A server that supports it sets `"sessionUpdate":true` in `session.ready` (not on a `transcribeOnly` socket, where
`session.update` returns `error` `code:"transcribe_only"`, `retryable:false`); a client must not send `session.update` to a server that did not set it,
because such a server rejects the command as the fatal `invalid_command`.

Text commands: `{"type":"response.request"}`, `{"type":"response.cancel"}`, `{"type":"session.stop"}` and `session.update` (above). Manual request uses the latest finalized utterance; before any final it returns a retryable `no_transcript` error. At most 10 commands/second. A finalized utterance automatically starts a response when idle. While a reply is active, new partials do not cancel it and the latest final is queued; intermediate queued finals remain in recognized history. Completion/failure starts the latest queued reply. Manual regeneration/cancellation and stop invalidate obsolete generation immediately and clear queued work. Clients match active response/turn IDs, retain completed suggestions and label previous-question replies. Socket writes are serialized.

Server events:

For a clear individual/group introduction request with any confirmed profile fields, balanced/conversation mode selects a **deterministic** `profile` response: fixed English labels and verbatim supplied name/role/project values only. It does not call Search or the language model, invent history or answer an unknown first-touchpoint question. Missing fields are omitted without placeholders. Mixed customer/date/commitment/employer requests are not eligible for this shortcut; grounded mode retains retrieval. `profile` completions have `grounding:disabled`, empty sources and `retrievalPrefetched:false`, and are excluded from model response histograms. The UI should ask users to enter concise English profile wording and identify the profile reply as composed from confirmed fields, not model-generated. Caller-provided punctuation/quotes remain literal user content, not interpreted instructions.

| type | Additional fields |
| --- | --- |
| `session.ready` | `protocolVersion: 1`; `replyKorean: true` only when the client opted in with `replyKorean`; `sessionUpdate: true` when the socket accepts `session.update` (every socket except `transcribeOnly`) |
| `session.updated` | `responseMode`, `replyKorean`: the effective values after a `session.update`. Sent in order with the other events, so it is known which reply started before or after it |
| `transcript.partial` / `transcript.final` | `turnId: string`, `revision: integer` (increasing within turn), `text: string` (replacement, not delta) |
| `response.started` | `responseId: string`, `turnId: string` |
| `response.delta` | `responseId`, `turnId`, `text: string` (append); `primaryDone: true` once per streamed reply when the primary line is complete (see below) |
| `response.completed` | `responseId`, `turnId`, `text: string` (complete replacement), `suggestions: string[]` (1–2 English replies, primary first), `sources: [{title,url,updatedAt}]`, `grounding`, `responseRoute: transcript\|profile\|knowledge`, `retrievalPrefetched: boolean` |
| `response.korean` | `responseId`, `turnId`, `index: 0\|1` (0 = primary reply, 1 = alternative), and either `korean: string` (1–400 characters, mainly Hangul) or `failed: true` with no `korean` property. Sent only to clients that opted in with `replyKorean` |
| `response.cancelled` | `responseId`, `turnId` |
| `error` | `code: string`, `message: string` (safe user-facing detail), `retryable: boolean` |

`updatedAt` is an ISO timestamp or null if source metadata does not provide it. `grounding` is `disabled`, `grounded`, `no_matches`, or `unavailable`. Sources are relevance-filtered reference candidates, not verified citations or a guarantee that every generated statement is supported. Each document appears once in `sources`, in relevance order (de-duplicated by `url`, or by title when a source has no URL), even when several of its chunks were given to the model as evidence. Render as text, validate http(s) URLs, and show grounding status. When a successful search yields no authorized relevant results, the real model can provide general conversational phrasing from the transcript alone (`no_matches`, empty sources), but must not invent company-specific knowledge or claim to have consulted supporting documents.

Search failure: a transient failure (HTTP 429, any 5xx, a time-out or a network error) gets one fast retry that adds at most about 800 ms inside the existing 10-second retrieval cap; a permanent failure (HTTP 400, 401, 402, 403, 404 or a configuration/credential problem) is not retried. A candidate without a reranker score is skipped as long as other candidates were scored; when no candidate was scored, ranking was partial or the service failed, retrieval has failed. A failed retrieval for a knowledge reply first emits the non-fatal `error` `grounding_unavailable` (`retryable: true`), and then the language model **is** called without documents for a brief, general and non-committal reply that states no company-specific facts, numbers, names, dates or commitments and may say that the details still need to be confirmed; its `response.completed` has `grounding: "unavailable"` and empty `sources`. If that model call fails as well, the deterministic completion follows instead (`text` and the single suggestion `Grounding is unavailable. Please try again before relying on a factual answer.`, `grounding: "unavailable"`, empty sources) without a second `error`, and a client that opted in with `replyKorean` gets `response.korean` `index: 0` `failed: true` for it. A reply that the content filter stops is withdrawn instead (`response_filtered`, below), never completed with this notice.

Model throttling and failures: a live model call (the reply, the turn classifier or a Korean line) that is throttled (HTTP 429) or fails transiently (408, 5xx or a network error) is retried at most once. The retry happens only when the service asks for a wait of at most 1 second and the call still has at least 2 seconds of its deadline left after that wait. The reply's deadline is 30 seconds from its start, so time spent retrieving documents counts against it; a Korean line's deadline is 10 seconds. Otherwise the failure surfaces at once, without waiting out a long `Retry-After`. For the reply this is the non-fatal `error` `model_busy` (HTTP 429) or `response_unavailable` (`retryable: true`), followed by `response.cancelled`. For a Korean line it is `response.korean` `failed: true`. A failed turn classifier does not fail the reply.

Content filter: when Azure OpenAI's content filter stops a reply's stream (`finish_reason: "content_filter"`), the server withdraws that reply. It sends the non-fatal `error` `response_filtered` (`retryable: true`), then `response.cancelled` for the reply, and never `response.completed` or `response.korean` for it; no translation call is made for it, because translation starts only after a stream ends normally. The error carries no `responseId`: it concerns the active reply, whose `response.cancelled` follows immediately. Deltas of the reply, up to its whole primary line, may already have been sent: when the deployment uses Azure OpenAI's asynchronous streaming filter, text is streamed before it is checked, and the stop is only guaranteed within about 1,000 characters of the violating text. Clients must remove any text already shown for that response, including a held or pinned copy, instead of keeping it as an interrupted line, and treat its `response.cancelled` as already handled. The session continues.

`suggestions` is additive: older clients may ignore it and continue reading `text`.
The first suggestion equals `text`; `response.delta` still appends only primary plain text.
Azure generates two independently speakable concise English wordings from the same transcript/evidence
in **one** streamed model request, not an additional retrieval or model round trip. The internal newline
separator and second wording are never streamed to the primary text. The prompt targets at most two
short sentences / 18 words per option, with an A2-B1 Korean-speaking IT-engineer reading persona.
The persona sets vocabulary difficulty, not evidence of employment or personal history. The second
reply uses a distinct conversational move rather than synonym-only paraphrasing.
A missing, duplicate, multiline, oversized or over-25-word
alternative is discarded; deterministic profile replies and the fixed grounding-failure notice have only one suggestion.
Suggestions share completion-level grounding/sources; they are not individually verified citations.
Korean translations are client enrichment, not part of this English API field; a client that opted in with `replyKorean`
receives them as separate `response.korean` events (below). Neither primary nor
alternative is inserted into conversation history: only actual finalized recognition segments are context.
No suggestion implies the participant spoke it or committed to an action.

`primaryDone: true` on `response.delta` marks that the primary line (the first suggestion) is complete, so a client can show
it before `response.completed`. It is sent exactly once per streamed reply: on the delta whose `text` ends the primary line,
or, when the end is only known after the last primary text was sent (for example a single-line reply, which ends with the
stream), on one extra delta with empty `text`. Empty `text` occurs only together with `primaryDone: true`, no further delta
of that reply follows it, and deltas without the marker omit the property. The concatenated `text` of a reply's deltas equals
`response.completed.text` and `suggestions[0]`; the server does not post-process the primary line. Profile replies carry the
marker on their single delta. One exception: when a reply that was asked for without documents after a Search failure fails
after it started streaming, the deterministic completion above replaces it, so clients must always replace the streamed text
with `response.completed.text` when it arrives. A reply that the content filter stops never completes, and its streamed text
must be removed (`response_filtered`, above). `response.completed` timing is unchanged, and clients that ignore the
property keep working.

`response.korean` (only for clients that set `replyKorean`) delivers exactly one Korean line per suggestion line of every
completed reply: `index` 0 for the primary, 1 for the alternative. Each is either a translation or `failed:true` when none could
be produced, and the client then translates that line itself. Translation runs beside the answer and never delays it. Both
lines are translated only once the stream has ended normally, so a reply that the content filter stops is never translated;
each line's event can arrive before or after `response.completed`. A reply
cancelled while it was still active publishes no Korean afterwards; a completed reply keeps its Korean even after a newer reply
starts. The server translates with the meeting topic plus the configured phrases and retrieved evidence titles as context,
keeps those names as written, and reads a work-meeting `owner` as the responsible person (담당), not legal ownership. Each
translation call is limited to 10 seconds. Fake emits `[fake-ko] ` followed by the English line.

Fake: >=640 nonzero audio bytes emit deterministic partial+final once per utterance. Further nonzero audio is ignored until >=16000 consecutive zero bytes (500 ms silence). All-zero audio never fabricates speech. Transcript: `Could you briefly explain the next steps?` Answer: `Let's confirm the goal, agree on the next action, and assign an owner.` Response uses three timed primary deltas; completion includes the synthetic alternative `Let's agree on the goal and next step, then choose an owner.` Fake is synthetic test behavior, not recognition.

## Grounding

The additive `responseRoute` describes the selected context path, not answer correctness: `transcript` and `profile` skip Search and use `grounding:disabled` with empty sources; `knowledge` may result in any existing grounding state. `retrievalPrefetched:true` means a retrieval that had already started for this utterance, from a stable partial or from its final transcript (including an in-flight or failed one), was selected for the final text, not a latency or relevance guarantee. It remains false for transcript/profile routes and when the reply has to start its own retrieval.

For knowledge routes only, partial text with at least20 characters and4 whitespace-separated words is eligible after250ms without a changed normalized partial. At most 3 speculative retrievals start per utterance. A **final** transcript that is worth a reply is retrieved at once, with no debounce, and does not count toward that limit; it also promotes an identical candidate that is still waiting out the debounce. There is one active speculative worker/candidate per socket/identity; revisions and speech of a later turn cancel obsolete speculative work, but never a retrieval that a reply is already waiting for: that one finishes on its own and cannot be reused afterwards. A noncooperative provider occupies that worker rather than spawning unbounded replacements, and a retrieval that has not started yet is never handed to a reply. Reuse requires the **same turn and the same text after folding case, whitespace and punctuation at the edges of words** (a speech partial and its final differ in exactly these). Apostrophes, decimals, minus signs, dates, numbers and negations inside words are preserved, so `can` / `cannot` / `can't` and `10` / `100` never match. No cross-user or cross-session cache exists; ACL uses the ticket-bound identity. Explicit cancel/stop invalidates speculative work. Matched retrieval failure remains visible as `grounding_unavailable` followed by a general reply labeled `grounding: "unavailable"`, never a document-grounded-looking success. Unmatched speculative results are discarded and final retrieval runs normally.

Retrieval is context-aware by default for knowledge routes. Without another model call, one shared
query builder combines up to four preceding actual recognition segments plus up to three older
recognition excerpts (350 normalized characters each) with the current segment (1536 normalized
characters), in chronological order, within4096 characters total. The older query anchors select
the earliest, middle and latest available segments preceding the four recent segments.
Oversized segments preserve their beginning and end with an omission marker. This supplies topic words for
follow-ups such as “When can we ship it?”; it is bounded context, not guaranteed pronoun resolution.
Prefetch (from a partial or from the final itself) uses the same builder and may be reused only when **both**
the final text, compared as described above, and the bounded context query match. Changed/pruned context invalidates reuse. A manual retry uses the
same actual recognition history; generated suggestions are never spoken-history substitutes.
Search owner prefiltering and no-cross-session/identity isolation are unchanged.

For longer meetings, the existing recent transcript window remains12 turns /24000 characters.
When a segment leaves it, the session keeps a300-character actual-speech excerpt: the opening
evicted segment plus the three most recently evicted segments, at most1200 extra characters.
These precede recent segments for routing, retrieval and model context, yielding at most16 segments
/25200 characters total. Excerpts are **not a rolling semantic summary** and may omit important
middle details or later topic changes; there is no full-meeting memory, persistence or summary LLM call.
Only newly recognized speech can enter either window. Starting a new session clears both.

`search-index.json` is the minimum index schema, including the `meeting-semantic` title/content configuration. Optional additional fields are allowed. Populate `contentVector` with the same deployment/model as query embeddings: `text-embedding-3-small`, 1536 dimensions. The API performs hybrid text + vector retrieval over up to 50 candidates, semantically reranks them, and passes at most 5 accepted results to the model. The configurable minimum reranker score defaults to 2.0 on the service's 0-4 scale when unset; the deployment template sets 1.5, chosen with tools/QualityEval (on the synthetic suite it raised the retrieval hit rate from 0.84 to 0.99 and the pass rate from 0.88 to 0.98 with no forbidden claims). It still needs corpus-specific evaluation and is not a correctness guarantee. A candidate without a valid reranker score is skipped while others were scored; no scored candidate, partial semantic results or ranker failures produce `grounding_unavailable`, never an unfiltered fallback. Candidates remain prefiltered by:

```text
allowedPrincipalIds/any(p: p eq '<validated-oid-GUID>')
```

ACLs contain canonical lowercase **user object IDs from the configured Entra tenant**. Group expansion, wildcard/public access, user-supplied ACL filters and cross-tenant ACLs are not supported. Ingestion must copy current source permissions and remove/reindex revoked access before exposing the index; this API cannot infer source permissions. It does not query Blob Storage. Documents are untrusted evidence, never executable instructions. Only 5 chunks of at most 6000 characters each enter the model; recent transcript context is at most 12 turns / 24000 characters, plus at most 4 earlier actual-speech excerpts / 1200 characters as described above.
