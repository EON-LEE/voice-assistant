export interface MaterialDocument { id: string; title: string; chunks: number; updatedAt: string }
export interface MaterialList {
  documents: MaterialDocument[];
  limits: { maxDocuments: number; maxChunks: number; maxFileBytes: number; maxCharactersPerDocument: number };
  usage: { documents: number; chunks: number };
  extensions: string[];
}
export const allowedExtensions = new Set((".txt .md .markdown .rst .csv .tsv .json .jsonl .yaml .yml .toml .ini .xml .html .htm .log .vtt .srt .sql .sh .ps1 .bat .cs .ts .tsx .js .jsx .mjs .py .java .go .rs .c .h .cpp .hpp .kt .swift .rb .php .scala .bicep .tf .docx .pptx .pdf").split(" "));
const skippedDirectories = new Set(["node_modules", ".git", "bin", "obj", "dist", "build", ".venv", "__pycache__"]);
export function fileRejection(file: Pick<File, "name" | "size">, path = file.name): string | null {
  const parts = path.replaceAll("\\", "/").split("/");
  if (parts.some(part => part.startsWith(".") || skippedDirectories.has(part.toLowerCase())))
    return "hidden/build folders";
  if (file.size > 5242880) return "over 5 MiB";
  const extension = file.name.slice(file.name.lastIndexOf(".")).toLowerCase();
  if (!allowedExtensions.has(extension)) return "unsupported file types";
  return null;
}
export async function looksBinary(file: File): Promise<boolean> {
  const extension = file.name.slice(file.name.lastIndexOf(".")).toLowerCase();
  if ([".pdf", ".docx", ".pptx"].includes(extension)) return false;
  const reader = file.stream().getReader();
  const decoder = new TextDecoder("utf-8", { fatal: true });
  try {
    while (true) {
      const { value, done } = await reader.read();
      if (done) { decoder.decode(); return false; }
      if (value.some(byte => byte < 9 || byte > 13 && byte < 32)) return true;
      decoder.decode(value, { stream: true });
    }
  } catch { return true; }
  finally { await reader.cancel(); reader.releaseLock(); }
}
export const materialMessages: Record<string, string> = {
  invalid_request: "Check the title and file or notes format.",
  too_large: "This file or extracted text is too large. Use a smaller document (file limit 5 MiB).",
  unsupported_type: "Unsupported file type. Save as UTF-8 text, DOCX, PPTX, or a text-based PDF.",
  no_text: "No readable text was found. Scanned PDFs need text recognition before upload.",
  quota_exceeded: "Your materials quota is full. Delete some documents before uploading.",
  busy: "The service is busy with uploads. Retry this item after other uploads finish.",
  knowledge_unavailable: "Meeting materials are temporarily unavailable. Retry later.",
  unauthorized: "Sign in again to access your meeting materials.",
  forbidden: "Access is not permitted. Sign in with the correct account or contact your administrator.",
};
export class MaterialError extends Error {
  constructor(readonly code: string) { super(materialMessages[code] ?? "The materials request failed. Retry later."); }
}
export type MaterialFetch = (path: string, init?: RequestInit) => Promise<Response>;
export class MaterialsClient {
  constructor(private readonly request: MaterialFetch) {}
  private async execute(path: string, init: RequestInit, signal?: AbortSignal): Promise<Response> {
    const timeout = AbortSignal.timeout(120000);
    const combined = signal ? AbortSignal.any([signal, timeout]) : timeout;
    let response: Response;
    try { response = await this.request(path, { ...init, signal: combined }); }
    catch (error) {
      if (signal?.aborted) throw new DOMException("Cancelled", "AbortError");
      if (error instanceof Error && /sign.in/i.test(error.message)) throw new MaterialError("unauthorized");
      throw new MaterialError("knowledge_unavailable");
    }
    if (!response.ok) {
      if (response.status === 401) throw new MaterialError("unauthorized");
      if (response.status === 403) throw new MaterialError("forbidden");
      let code = "";
      try { const body: unknown = await response.json(); if (body && typeof body === "object" && "error" in body && typeof body.error === "string") code = body.error; }
      catch { /* Never expose raw proxy or server error bodies. */ }
      throw new MaterialError(code);
    }
    return response;
  }
  async list(signal?: AbortSignal): Promise<MaterialList> {
    const body: unknown = await (await this.execute("/api/knowledge", {}, signal)).json();
    if (!body || typeof body !== "object") throw new MaterialError("knowledge_unavailable");
    const b = body as Record<string, unknown>;
    if (!Array.isArray(b.documents) || b.documents.length > 300 || !Array.isArray(b.extensions) ||
        !b.extensions.every(value => typeof value === "string") || !validCounts(b.limits, ["maxDocuments", "maxChunks", "maxFileBytes", "maxCharactersPerDocument"]) ||
        !validCounts(b.usage, ["documents", "chunks"])) throw new MaterialError("knowledge_unavailable");
    for (const document of b.documents) validateDocument(document);
    return body as MaterialList;
  }
  async upload(file: File, signal?: AbortSignal): Promise<MaterialDocument> {
    if (fileRejection(file)) throw new MaterialError(file.size > 5242880 ? "too_large" : "unsupported_type");
    const form = new FormData(); form.set("file", file);
    return this.created(await this.execute("/api/knowledge", { method: "POST", body: form }, signal));
  }
  async notes(title: string, text: string, signal?: AbortSignal): Promise<MaterialDocument> {
    if (!title.trim() || title.length > 200 || !text.trim() || text.length > 400000) throw new MaterialError("invalid_request");
    return this.created(await this.execute("/api/knowledge", { method: "POST", headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ title: title.trim(), text }) }, signal));
  }
  private async created(response: Response): Promise<MaterialDocument> {
    if (response.status !== 201) throw new MaterialError("knowledge_unavailable");
    const body: unknown = await response.json(); validateDocument(body); return body as MaterialDocument;
  }
  async delete(id: string, signal?: AbortSignal): Promise<void> {
    if (!/^[0-9a-f]{32}$/.test(id)) throw new MaterialError("invalid_request");
    const response = await this.execute(`/api/knowledge/${id}`, { method: "DELETE" }, signal);
    if (response.status !== 204) throw new MaterialError("knowledge_unavailable");
  }
}
function validCounts(value: unknown, fields: string[]): boolean {
  if (!value || typeof value !== "object") return false;
  const record = value as Record<string, unknown>;
  return fields.every(field => Number.isSafeInteger(record[field]) && (record[field] as number) >= 0);
}
function validateDocument(value: unknown): void {
  if (!value || typeof value !== "object") throw new MaterialError("knowledge_unavailable");
  const d = value as Record<string, unknown>;
  if (typeof d.id !== "string" || !/^[0-9a-f]{32}$/.test(d.id) || typeof d.title !== "string" ||
      d.title.length > 200 || !Number.isSafeInteger(d.chunks) || (d.chunks as number) < 0 || typeof d.updatedAt !== "string")
    throw new MaterialError("knowledge_unavailable");
}
export type UploadState = "queued" | "uploading" | "success" | "failed" | "cancelled";
export interface UploadItem { name: string; run(signal: AbortSignal): Promise<unknown>; state: UploadState; message: string; controller?: AbortController }
export class UploadQueue {
  readonly items: UploadItem[] = [];
  private active = 0;
  constructor(private readonly changed: () => void) {}
  add(name: string, run: UploadItem["run"]): void {
    if (this.items.length >= 300) throw new Error("Queue limit reached (300 items). Clear finished items before adding more.");
    this.items.push({ name, run, state: "queued", message: "Queued" }); this.changed(); this.pump();
  }
  retryFailed(): void {
    for (const item of this.items) if (item.state === "failed") { item.state = "queued"; item.message = "Queued for retry"; }
    this.changed(); this.pump();
  }
  cancel(): void {
    for (const item of this.items) {
      if (item.state === "queued") { item.state = "cancelled"; item.message = "Cancelled before upload"; }
      if (item.state === "uploading") item.controller?.abort();
    }
    this.changed();
  }
  clearFinished(): void {
    for (let i = this.items.length - 1; i >= 0; i--)
      if (!["queued", "uploading"].includes(this.items[i]!.state)) this.items.splice(i, 1);
    this.changed();
  }
  private pump(): void {
    while (this.active < 2) {
      const item = this.items.find(value => value.state === "queued");
      if (!item) return;
      this.active++; item.state = "uploading"; item.message = "Uploading and processing…";
      item.controller = new AbortController(); this.changed();
      void Promise.resolve().then(() => { item.controller!.signal.throwIfAborted(); return item.run(item.controller!.signal); }).then(() => {
        item.state = "success"; item.message = "Stored successfully";
      }).catch(error => {
        item.state = item.controller!.signal.aborted && !(error instanceof MaterialError) ? "cancelled" : "failed";
        item.message = item.state === "cancelled" ? "Cancelled locally; refresh the list to check whether storage completed."
          : error instanceof MaterialError ? error.message : "Upload failed. Retry this item.";
      }).finally(() => { this.active--; item.controller = undefined; this.changed(); this.pump(); });
    }
  }
}
export function referenceText(source: { title: string; url: string; updatedAt?: string | null }): string {
  let personal = false;
  try { personal = new URL(source.url).hostname.toLowerCase() === "my-materials.invalid"; } catch { /* Invalid references remain inert text. */ }
  return `${source.title}${personal ? " (My meeting materials)" : ` — ${source.url}`}${source.updatedAt ? ` · updated ${source.updatedAt}` : ""}`;
}
