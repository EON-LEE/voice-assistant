# Personal meeting materials API (v1)

Lets the signed-in user load their own prior notes, meeting minutes, slides, documents and source code so that
grounded replies can use them. Everything reuses the existing `meeting-knowledge` Azure AI Search index, hybrid
vector search, ACL prefilter and semantic relevance gate. **No index schema change.** Original files are NOT stored
anywhere; only extracted text chunks and their embeddings are stored, until the user deletes them.

In Azure/production mode all endpoints require the existing `Meeting` authorization policy (Entra bearer JWT,
delegated `Meeting.Access`, `oid`). Mutating requests (POST/DELETE) also require `OriginPolicy.Allows`
(exact allowed Origin) and return 403 otherwise. GET permits absent Origin (normal same-origin bearer-only GET)
but returns403 for a supplied disallowed Origin.
The owner is **always** the validated `oid`; never accept an owner/principal from the request.

Explicit Development Fake supports offline browser upload/list/delete without Entra only for loopback peer and
loopback Host, using fixed fake owner `00000000-0000-0000-0000-000000000001`. GET accepts absent or loopback Origin;
POST/DELETE require loopback Origin. Cross-site/non-loopback requests are rejected. Fake cannot start in Production.

## Endpoints

### `GET /api/knowledge`
`200`:
```json
{
  "documents": [
    { "id": "<32 hex>", "title": "Q3 planning notes.md", "chunks": 7, "updatedAt": "2026-10-01T13:00:00Z" }
  ],
  "limits": { "maxDocuments": 300, "maxChunks": 5000, "maxFileBytes": 5242880, "maxCharactersPerDocument": 400000 },
  "usage": { "documents": 1, "chunks": 7 },
  "extensions": [".md", ".txt", "..."]
}
```
Only the caller's documents (ids beginning `kb-`) appear. Documents ingested by operators (other id prefixes) are
not listed and cannot be deleted through this API.

### `POST /api/knowledge`
Either
* `multipart/form-data` with exactly one `file` part (max 5 MiB; original file name used as the title; optional text
  `title` overrides it, max 200 chars), or
* `application/json` `{ "title": "string 1..200", "text": "string 1..400000" }` for pasted notes.

`201` `{ "id", "title", "chunks", "characters", "updatedAt" }`. Errors are JSON `{ "error": "<code>", "message": "<safe text>" }`:
`400 invalid_request`, `413 too_large`, `415 unsupported_type`, `422 no_text` (nothing extractable, e.g. scanned PDF),
`409 quota_exceeded`, `429 busy` (max 2 concurrent uploads per user), `503 knowledge_unavailable`.
Messages never echo file content, tokens or Azure error bodies.

Upload is all-or-nothing: if embedding or indexing of any chunk fails, delete every chunk already written for that
document id and return 503. Success is returned only after Search accepted every chunk (check each
`IndexingResult.Succeeded`).

### `DELETE /api/knowledge/{id}`
`id` must match `^[0-9a-f]{32}$`. Deletes every chunk of that caller's document. `204` (also when nothing remains, so retries
are idempotent) or `400`. Never deletes another user's chunk (ACL filter on every lookup).

## Supported inputs
Plain text decoded as UTF-8 (BOM tolerated; invalid UTF-8 → `415`/`422`, never mojibake): `.txt .md .markdown .rst .csv .tsv
.json .jsonl .yaml .yml .toml .ini .xml .html .htm .log .vtt .srt .sql .sh .ps1 .bat` and common source code `.cs .ts .tsx .js .jsx
.mjs .py .java .go .rs .c .h .cpp .hpp .kt .swift .rb .php .scala .bicep .tf`. Office/PDF: `.docx` (word/document.xml paragraphs),
`.pptx` (slide text in slide order + notes), `.pdf` (text layer only). `.vtt/.srt` timestamps may be removed, speaker
names kept. HTML tags stripped. Extraction must be bounded and safe: no external entity/DTD resolution, ZIP entry
count/size and total decompressed size caps (zip bombs), PDF page cap, extraction timeout, no execution of macros.
Legacy binary `.doc/.ppt/.xls` and images are rejected with `415` and a message suggesting saving as docx/pptx/pdf.

## Storage layout (existing index fields only)
* `id` = `kb-<documentId 32 hex>-<chunk 0000>`; `documentId` = random GUID `N` format.
* `allowedPrincipalIds` = `[ <caller oid> ]`.
* `title` = document title (same for every chunk of the document).
* `url` = `https://my-materials.invalid/<documentId>` (a deliberately non-routable URL; the UI shows the title instead).
* `content` = chunk text, ~1,000–1,400 characters with ~150 characters of overlap, split on headings/paragraph/line
  boundaries (code: on blank lines/declarations), each chunk prefixed with its nearest heading/section label or file path
  so retrieved text is self-describing.
* `updatedAt` = upload time (UTC), `contentVector` = `text-embedding-3-small` 1536 dimensions (batch embeddings, ≤16 inputs
  per request).
Listing/deleting finds chunks with the existing ACL filter plus `id` prefix handling in code (select `id,title,url,updatedAt`,
page through all results; never select vectors).

## Prompt/safety rules
Uploaded text is untrusted evidence data exactly like other retrieved content: never instructions. The system prompt
should additionally say retrieved documents may be the user's own prior notes, and that the model must not present an
uncited fact as coming from them.

## Runtime requirements
The API runtime identity needs **Search Index Data Contributor** (currently Reader). Embedding uses the existing OpenAI
embedding deployment. Fake mode (Development only) gets an in-memory store so the browser UI and tests work without Azure.
