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

Wait for `{"type":"session.ready","protocolVersion":1}` before sending binary frames containing headerless PCM signed 16-bit little-endian, 16,000 samples/second, mono. Resample in the browser; never send WebM/Opus or a WAV header. Each binary message is even-length, 1-32768 bytes (zero-length rejected); text is at most 4096 UTF-8 bytes. Fragmentation is supported and limits apply to the complete message. Send near real-time: token bucket allows 128 KB burst and refills 64 KB/s. Startup deadline 15 seconds; receive idle deadline 90 seconds; response deadline 30 seconds; session lifetime 30 minutes. Send silence when capture is active but quiet.

Text commands: `{"type":"response.request"}`, `{"type":"response.cancel"}`, `{"type":"session.stop"}`. Manual request uses the latest finalized utterance; before any final it returns a retryable `no_transcript` error. At most 10 commands/second. A finalized utterance automatically starts a response. A new partial utterance cancels the previous response; a new final/manual request supersedes pending generation. After cancellation, obsolete deltas/completions are discarded. Socket writes are serialized.

Server events:

| type | Additional fields |
| --- | --- |
| `session.ready` | `protocolVersion: 1` |
| `transcript.partial` / `transcript.final` | `turnId: string`, `revision: integer` (increasing within turn), `text: string` (replacement, not delta) |
| `response.started` | `responseId: string`, `turnId: string` |
| `response.delta` | `responseId`, `turnId`, `text: string` (append) |
| `response.completed` | `responseId`, `turnId`, `text: string` (complete replacement), `sources: [{title,url,updatedAt}]`, `grounding` |
| `response.cancelled` | `responseId`, `turnId` |
| `error` | `code: string`, `message: string` (safe user-facing detail), `retryable: boolean` |

`updatedAt` is an ISO timestamp or null if source metadata does not provide it. `grounding` is `disabled`, `grounded`, `no_matches`, or `unavailable`. Sources are retrieved evidence, not a guarantee that every generated statement is supported. Render as text, validate http(s) URLs, and show grounding status. Search failure emits `error` with `grounding_unavailable` followed by a deterministic refusal-like completion (`unavailable`, empty sources); the language model is **not** called. No authorized results produce a clarification rather than invented facts.

Fake: >=640 nonzero audio bytes emit deterministic partial+final once per utterance. Further nonzero audio is ignored until >=16000 consecutive zero bytes (500 ms silence). All-zero audio never fabricates speech. Transcript: `Could you briefly explain the next steps?` Answer: `Let's confirm the goal, agree on the next action, and assign an owner.` Response uses three timed deltas. Fake is synthetic test behavior, not recognition.

## Grounding

`search-index.json` is the minimum index schema. Optional additional fields are allowed. Populate `contentVector` with the same deployment/model as query embeddings: `text-embedding-3-small`, 1536 dimensions. The API performs hybrid text + vector retrieval (5 neighbors/results), prefiltered by:

```text
allowedPrincipalIds/any(p: p eq '<validated-oid-GUID>')
```

ACLs contain canonical lowercase **user object IDs from the configured Entra tenant**. Group expansion, wildcard/public access, user-supplied ACL filters and cross-tenant ACLs are not supported. Ingestion must copy current source permissions and remove/reindex revoked access before exposing the index; this API cannot infer source permissions. It does not query Blob Storage. Documents are untrusted evidence, never executable instructions. Only 5 chunks of at most 6000 characters each enter the model; transcript context is at most 12 turns / 24000 characters.
