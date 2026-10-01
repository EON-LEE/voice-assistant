import type { BrowserAuth } from "./auth.js";
import { fileRejection, looksBinary, MaterialError, MaterialsClient, UploadQueue } from "./materials.js";

function node<K extends keyof HTMLElementTagNameMap>(tag: K, text?: string): HTMLElementTagNameMap[K] {
  const element = document.createElement(tag); if (text) element.textContent = text; return element;
}
export class MaterialsPanel {
  private readonly client: MaterialsClient;
  private readonly queue: UploadQueue;
  private enabled = false;
  private loading = false;
  private refreshAgain = false;
  private generation = 0;
  private scanning = false;
  private accessMessage = "";
  private selected: File[] = [];
  private readonly skipped = new Map<string, number>();
  private readonly actions: HTMLFieldSetElement;
  private readonly status = node("p");
  private readonly summary = node("p");
  private readonly usage = node("p");
  private readonly documentMeter = node("meter");
  private readonly chunkMeter = node("meter");
  private readonly documents = node("ul");
  private readonly progress = node("ul");
  private readonly files = node("input");
  private readonly folder = node("input");
  private readonly upload = node("button", "Upload selected files");
  private readonly retry = node("button", "Retry failed only");
  private readonly title = node("input");
  private readonly notes = node("textarea");
  private readonly refreshButton = node("button", "Refresh materials");
  constructor(private readonly host: HTMLElement, auth: BrowserAuth, private readonly authChanged: () => void) {
    this.client = new MaterialsClient(async (path, init) => {
      try {
        const response = await auth.knowledgeRequest(path, init);
        if (response.status === 401 || response.status === 403) {
          this.authChanged();
          this.setAccess(false, "Sign in again to access your meeting materials.");
        }
        return response;
      } catch (error) {
        if (!auth.signedIn) { this.setAccess(false, "Sign in again to access your meeting materials."); this.authChanged(); }
        throw error;
      }
    });
    this.queue = new UploadQueue(() => { this.renderQueue(); });
    const details = node("details"); details.id = "materials-panel";
    details.append(node("summary", "My meeting materials"));
    details.append(node("p", "Extracted text and search embeddings are stored in your Azure subscription's Search index until you delete them; the original file is not stored; do not upload anything you are not allowed to process in Azure."));
    this.status.id = "materials-status"; this.status.setAttribute("role", "status"); details.append(this.status);
    this.actions = node("fieldset"); this.actions.append(node("legend", "Manage your materials"));
    this.files.type = this.folder.type = "file"; this.files.multiple = this.folder.multiple = true;
    this.folder.setAttribute("webkitdirectory", ""); this.files.id = "material-files"; this.folder.id = "material-folder";
    this.label(this.files, "Select files"); this.label(this.folder, "Select a folder");
    this.files.addEventListener("change", () => { void this.select([...this.files.files ?? []]); });
    this.folder.addEventListener("change", () => { void this.select([...this.folder.files ?? []]); });
    const drop = node("div", "Drop files or folders here, or use the labelled pickers above.");
    drop.className = "drop-zone"; drop.id = "materials-drop";
    drop.addEventListener("dragover", e => { e.preventDefault(); });
    drop.addEventListener("drop", event => {
      event.preventDefault(); if (!this.enabled || this.scanning || !event.dataTransfer) return;
      const items = [...event.dataTransfer.items];
      const fallback = [...event.dataTransfer.files];
      void this.drop(items, fallback);
    });
    this.actions.append(drop, this.summary, this.upload);
    this.upload.id = "materials-upload";
    this.upload.addEventListener("click", () => {
      if (!this.enabled || this.scanning) return;
      for (const file of this.selected) {
        try { this.queue.add(file.name, async signal => {
          const result = await this.client.upload(file, signal); void this.refresh(); return result;
        }); } catch (error) { this.show(error); break; }
      }
      this.selected = []; this.summary.textContent = ""; this.upload.disabled = true;
      this.files.value = this.folder.value = "";
    });
    this.title.id = "material-title"; this.title.maxLength = 200; this.title.autocomplete = "off";
    this.notes.id = "material-notes"; this.notes.maxLength = 400000; this.notes.rows = 5;
    this.label(this.title, "Notes title"); this.label(this.notes, "Paste notes (up to 400,000 characters)");
    const save = node("button", "Upload notes"); save.id = "materials-save-notes";
    save.addEventListener("click", () => {
      if (!this.enabled) return;
      const title = this.title.value.trim(), text = this.notes.value;
      if (!title || !text.trim()) { this.show(new MaterialError("invalid_request")); return; }
      try { this.queue.add(title, async signal => {
        const result = await this.client.notes(title, text, signal); void this.refresh(); return result;
      }); this.title.value = this.notes.value = ""; } catch (error) { this.show(error); }
    });
    const cancel = node("button", "Cancel uploads"); cancel.id = "materials-cancel";
    cancel.addEventListener("click", () => this.queue.cancel());
    this.retry.id = "materials-retry"; this.retry.addEventListener("click", () => { if (this.enabled) this.queue.retryFailed(); });
    const clear = node("button", "Clear finished upload entries"); clear.addEventListener("click", () => this.queue.clearFinished());
    this.refreshButton.id = "materials-refresh"; this.refreshButton.addEventListener("click", () => { void this.refresh(); });
    this.progress.id = "materials-progress"; this.progress.setAttribute("aria-live", "polite");
    this.usage.id = "materials-usage"; this.documents.id = "materials-documents";
    this.documentMeter.setAttribute("aria-label", "Documents used versus quota");
    this.chunkMeter.setAttribute("aria-label", "Chunks used versus quota");
    this.documentMeter.min = this.chunkMeter.min = 0;
    this.actions.append(save, cancel, this.retry, clear, this.progress, this.refreshButton, this.usage,
      this.documentMeter, this.chunkMeter, this.documents);
    details.append(this.actions); host.append(details);
    details.addEventListener("toggle", () => { if (details.open && this.enabled) void this.refresh(); });
    this.renderQueue(); this.setAccess(false, "Materials are disabled in Demo. Choose Live and sign in to manage your own materials.");
    window.addEventListener("pagehide", () => this.queue.cancel());
  }
  private label(input: HTMLInputElement | HTMLTextAreaElement, text: string): void {
    const label = node("label", text); label.htmlFor = input.id; this.actions.append(label, input);
  }
  setAccess(enabled: boolean, message: string): void {
    const changed = this.enabled !== enabled || this.accessMessage !== message;
    if (this.enabled !== enabled) {
      this.generation++;
      if (!enabled) { this.queue.cancel(); this.documents.replaceChildren(); this.usage.textContent = "";
        this.documentMeter.value = this.chunkMeter.value = 0; }
    }
    this.enabled = enabled; this.actions.disabled = !enabled;
    this.host.hidden = false;
    if (changed) this.status.textContent = message;
    this.accessMessage = message;
  }
  private show(error: unknown): void {
    if (error instanceof MaterialError && ["unauthorized", "forbidden"].includes(error.code)) {
      this.authChanged();
      this.enabled = false; this.actions.disabled = true; this.queue.cancel();
    }
    this.status.textContent = error instanceof MaterialError ? error.message
      : error instanceof Error && error.message.startsWith("Queue limit") ? error.message : "Materials could not be processed. Try again.";
  }
  private async select(files: File[]): Promise<void> {
    if (!this.enabled || this.scanning) return;
    this.scanning = true; this.upload.disabled = this.files.disabled = this.folder.disabled = true; this.selected = []; this.skipped.clear();
    const generation = this.generation;
    try {
      for (let i = 0; i < files.length; i++) {
        const file = files[i]!;
        if (generation !== this.generation) return;
        const reason = this.selected.length >= 300 ? "selection limit (300 files)" : fileRejection(file, file.webkitRelativePath || file.name)
          ?? (await looksBinary(file) ? "binary/non-UTF-8 text" : null);
        if (reason) this.skipped.set(reason, (this.skipped.get(reason) ?? 0) + 1);
        else this.selected.push(file);
      }
      this.summary.textContent = `${this.selected.length} files ready. Skipped: ${[...this.skipped].map(([reason, count]) => `${count} ${reason}`).join(", ") || "0"}.`;
    } catch { this.selected = []; this.show(new MaterialError("invalid_request")); }
    finally {
      this.scanning = false; this.files.disabled = this.folder.disabled = false;
      this.upload.disabled = !this.enabled || this.selected.length === 0;
    }
  }
  private async drop(items: DataTransferItem[], fallback: File[]): Promise<void> {
    const entries = items.map(item => item.webkitGetAsEntry?.()).filter((entry): entry is FileSystemEntry => !!entry);
    if (!entries.length) { await this.select(fallback); return; }
    this.scanning = true; this.upload.disabled = true;
    const files: File[] = []; let visited = 0, skipped = 0;
    const generation = this.generation;
    const visit = async (entry: FileSystemEntry, path: string): Promise<void> => {
      if (++visited > 2000 || generation !== this.generation) { skipped++; return; }
      if (entry.isDirectory) {
        if (fileRejection({ name: "check.txt", size: 0 }, `${path}/check.txt`)) { skipped++; return; }
        const reader = (entry as FileSystemDirectoryEntry).createReader();
        while (visited <= 2000 && generation === this.generation) {
          const children = await new Promise<FileSystemEntry[]>((resolve, reject) => reader.readEntries(resolve, reject));
          if (!children.length) break;
          for (const child of children) await visit(child, `${path}/${child.name}`);
        }
      } else {
        const file = await new Promise<File>((resolve, reject) => (entry as FileSystemFileEntry).file(resolve, reject));
        if (fileRejection(file, path)) skipped++; else files.push(file);
      }
    };
    try {
      for (const entry of entries) await visit(entry, entry.name);
      this.scanning = false; await this.select(files);
      if (skipped) this.summary.textContent += ` Additionally skipped ${skipped} folder entries (excluded paths, types, or traversal limit).`;
    } catch { this.show(new MaterialError("invalid_request")); }
    finally { this.scanning = false; this.upload.disabled = !this.enabled || this.selected.length === 0; }
  }
  async refresh(): Promise<void> {
    if (!this.enabled) return;
    if (this.loading) { this.refreshAgain = true; return; }
    this.loading = true; this.refreshButton.disabled = true; const generation = this.generation;
    try {
      const list = await this.client.list();
      if (!this.enabled || generation !== this.generation) return;
      this.usage.textContent = `${list.usage.documents} / ${list.limits.maxDocuments} documents · ${list.usage.chunks} / ${list.limits.maxChunks} chunks`;
      this.documentMeter.max = Math.max(1, list.limits.maxDocuments); this.documentMeter.value = list.usage.documents;
      this.chunkMeter.max = Math.max(1, list.limits.maxChunks); this.chunkMeter.value = list.usage.chunks;
      this.documents.replaceChildren();
      for (const document of list.documents) {
        const item = node("li");
        item.append(node("span", `${document.title} · ${document.chunks} chunks · ${document.updatedAt}`));
        const remove = node("button", "Delete"); remove.setAttribute("aria-label", `Delete ${document.title}`);
        remove.addEventListener("click", () => {
          if (!this.enabled) return;
          remove.disabled = true;
          void this.client.delete(document.id).then(() => this.refresh()).catch(error => { this.show(error); remove.disabled = false; });
        });
        item.append(remove); this.documents.append(item);
      }
    } catch (error) { if (generation === this.generation) this.show(error); }
    finally {
      this.loading = false; this.refreshButton.disabled = false;
      if (this.refreshAgain) { this.refreshAgain = false; void this.refresh(); }
    }
  }
  private renderQueue(): void {
    this.progress.replaceChildren();
    for (const item of this.queue.items) this.progress.append(node("li", `${item.name}: ${item.message}`));
    this.retry.disabled = !this.queue.items.some(item => item.state === "failed");
  }
}
