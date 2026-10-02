import type { BrowserAuth } from "./auth.js";
import { fileRejection, looksBinary, MaterialError, MaterialsClient, UploadQueue, type MaterialDocument } from "./materials.js";

function node<K extends keyof HTMLElementTagNameMap>(tag: K, text?: string): HTMLElementTagNameMap[K] {
  const element = document.createElement(tag); if (text) element.textContent = text; return element;
}
function table(title: string, headings: string[], body: HTMLTableSectionElement): HTMLElement {
  const wrap = node("div"); wrap.className = "table-scroll"; wrap.tabIndex = 0;
  wrap.setAttribute("role", "region"); wrap.setAttribute("aria-label", title);
  const element = node("table"); element.append(node("caption", title));
  const header = node("thead"), row = node("tr");
  for (const heading of headings) { const cell = node("th", heading); cell.scope = "col"; row.append(cell); }
  header.append(row); element.append(header, body); wrap.append(element); return wrap;
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
  private readonly documents = node("tbody");
  private readonly progress = node("tbody");
  private readonly documentSelection = new Set<string>();
  private indexed: MaterialDocument[] = [];
  private deleting = false;
  private readonly selectAll = node("input");
  private readonly deleteSelected = node("button", "Delete selected");
  private readonly uploadSummary = node("p");
  private readonly files = node("input");
  private readonly folder = node("input");
  private readonly upload = node("button", "Upload selected files");
  private readonly retry = node("button", "Retry failed only");
  private readonly title = node("input");
  private readonly notes = node("textarea");
  private readonly refreshButton = node("button", "Refresh materials");
  constructor(private readonly host: HTMLElement, auth: Pick<BrowserAuth, "knowledgeRequest" | "signedIn">,
    private readonly authChanged: () => void, private readonly confirmDelete: (message: string) => boolean = message => window.confirm(message)) {
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
    const details = node("section"); details.id = "materials-panel";
    details.append(node("h2", "Upload and manage documents"));
    details.append(node("p", "Extracted text and search embeddings are stored in your Azure subscription's Search index until you delete them; the original file is not stored; do not upload anything you are not allowed to process in Azure."));
    this.status.id = "materials-status"; this.status.setAttribute("role", "status"); details.append(this.status);
    this.actions = node("fieldset"); this.actions.append(node("legend", "Manage your materials"));
    this.files.type = this.folder.type = "file"; this.files.multiple = this.folder.multiple = true;
    this.folder.setAttribute("webkitdirectory", ""); this.files.id = "material-files"; this.folder.id = "material-folder";
    this.label(this.files, "Choose files"); this.label(this.folder, "Choose folder");
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
        }, file.size); } catch (error) { this.show(error); break; }
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
      }, new TextEncoder().encode(text).byteLength); this.title.value = this.notes.value = ""; } catch (error) { this.show(error); }
    });
    const cancel = node("button", "Cancel uploads"); cancel.id = "materials-cancel";
    cancel.addEventListener("click", () => this.queue.cancel());
    this.retry.id = "materials-retry"; this.retry.addEventListener("click", () => { if (this.enabled) this.queue.retryFailed(); });
    const clear = node("button", "Clear finished upload entries"); clear.addEventListener("click", () => this.queue.clearFinished());
    this.refreshButton.id = "materials-refresh"; this.refreshButton.addEventListener("click", () => { void this.refresh(); });
    this.progress.id = "materials-progress";
    this.uploadSummary.id = "materials-upload-summary"; this.uploadSummary.setAttribute("role", "status");
    this.usage.id = "materials-usage"; this.documents.id = "materials-documents";
    this.documentMeter.setAttribute("aria-label", "Documents used versus quota");
    this.chunkMeter.setAttribute("aria-label", "Chunks used versus quota");
    this.documentMeter.min = this.chunkMeter.min = 0;
    this.selectAll.type = "checkbox"; this.selectAll.id = "materials-select-all";
    const allLabel = node("label", " Select all indexed documents"); allLabel.htmlFor = this.selectAll.id;
    allLabel.prepend(this.selectAll); allLabel.className = "check";
    this.selectAll.addEventListener("change", () => {
      this.documentSelection.clear();
      if (this.selectAll.checked) for (const document of this.indexed) this.documentSelection.add(document.id);
      this.renderDocuments();
    });
    this.deleteSelected.id = "materials-delete-selected";
    this.deleteSelected.disabled = true;
    this.deleteSelected.addEventListener("click", () => { void this.removeDocuments([...this.documentSelection]); });
    this.actions.append(save, node("h3", "Upload progress"), cancel, this.retry, clear, this.uploadSummary,
      table("Uploads", ["File or notes", "Size", "Status", "Details", "Actions"], this.progress),
      node("h3", "Indexed documents"), this.refreshButton, this.usage,
      this.documentMeter, this.chunkMeter, allLabel, this.deleteSelected,
      table("Indexed documents", ["Select", "Title", "Chunks", "Indexed date", "Actions"], this.documents));
    details.append(this.actions); host.append(details);
    this.renderQueue(); this.setAccess(false, "Sign in in this tab to manage your meeting materials.");
    window.addEventListener("pagehide", () => this.queue.cancel());
  }
  private label(input: HTMLInputElement | HTMLTextAreaElement, text: string): void {
    const label = node("label", text); label.htmlFor = input.id; this.actions.append(label, input);
  }
  setAccess(enabled: boolean, message: string): void {
    const changed = this.enabled !== enabled || this.accessMessage !== message;
    if (this.enabled !== enabled) {
      this.generation++;
      if (!enabled) { this.queue.cancel(); this.documentSelection.clear(); this.indexed = []; this.renderDocuments(); this.usage.textContent = "";
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
      this.indexed = list.documents;
      for (const id of this.documentSelection) if (!this.indexed.some(document => document.id === id)) this.documentSelection.delete(id);
      this.renderDocuments();
    } catch (error) { if (generation === this.generation) this.show(error); }
    finally {
      this.loading = false; this.refreshButton.disabled = false;
      if (this.refreshAgain) { this.refreshAgain = false; void this.refresh(); }
    }
  }
  private renderQueue(): void {
    this.progress.replaceChildren();
    for (const item of this.queue.items) {
      const row = node("tr");
      row.append(node("td", item.name), node("td", item.size === undefined ? "Not supplied" : `${item.size.toLocaleString()} B`),
        node("td", item.state), node("td", item.message));
      const actions = node("td");
      if (item.state === "failed") {
        const retry = node("button", "Retry"); retry.setAttribute("aria-label", `Retry ${item.name}`);
        retry.addEventListener("click", () => { if (this.enabled) this.queue.retryFailed(item); }); actions.append(retry);
      }
      if (item.state === "queued" || item.state === "uploading") {
        const cancel = node("button", "Cancel"); cancel.setAttribute("aria-label", `Cancel ${item.name}`);
        cancel.addEventListener("click", () => this.queue.cancel(item)); actions.append(cancel);
      }
      row.append(actions); this.progress.append(row);
    }
    const count = (state: string): number => this.queue.items.filter(item => item.state === state).length;
    this.uploadSummary.textContent = `${count("queued")} queued · ${count("uploading")} uploading · ${count("success")} stored · ${count("failed")} failed · ${count("cancelled")} cancelled`;
    this.retry.disabled = !this.queue.items.some(item => item.state === "failed");
  }
  private renderDocuments(): void {
    this.documents.replaceChildren();
    for (const document of this.indexed) {
      const row = node("tr"), selection = node("td"), checkbox = node("input");
      checkbox.type = "checkbox"; checkbox.checked = this.documentSelection.has(document.id); checkbox.disabled = this.deleting;
      checkbox.setAttribute("aria-label", `Select ${document.title}`);
      checkbox.addEventListener("change", () => {
        if (checkbox.checked) this.documentSelection.add(document.id); else this.documentSelection.delete(document.id);
        this.updateSelection();
      });
      selection.append(checkbox);
      row.append(selection, node("td", document.title), node("td", String(document.chunks)), node("td", document.updatedAt));
      const action = node("td"), remove = node("button", "Delete");
      remove.disabled = this.deleting; remove.setAttribute("aria-label", `Delete ${document.title}`);
      remove.addEventListener("click", () => { void this.removeDocuments([document.id]); });
      action.append(remove); row.append(action); this.documents.append(row);
    }
    this.updateSelection();
  }
  private updateSelection(): void {
    this.selectAll.checked = this.indexed.length > 0 && this.documentSelection.size === this.indexed.length;
    this.selectAll.indeterminate = this.documentSelection.size > 0 && !this.selectAll.checked;
    this.selectAll.disabled = this.deleting || !this.indexed.length;
    this.deleteSelected.disabled = this.deleting || !this.documentSelection.size;
    this.deleteSelected.textContent = this.documentSelection.size ? `Delete selected (${this.documentSelection.size})` : "Delete selected";
  }
  private async removeDocuments(ids: string[]): Promise<void> {
    if (!this.enabled || this.deleting || !ids.length) return;
    if (!this.confirmDelete(`Delete ${ids.length} indexed document${ids.length === 1 ? "" : "s"} and all their stored text and embeddings? This cannot be undone.`)) return;
    this.deleting = true; this.renderDocuments();
    let deleted = 0;
    try {
      // Keep deletes sequential to avoid flooding Search or racing ownership checks.
      for (const id of ids) {
        if (!this.enabled) break;
        await this.client.delete(id); this.documentSelection.delete(id); deleted++;
      }
      this.status.textContent = `${deleted} document${deleted === 1 ? "" : "s"} deleted.`;
    } catch (error) { this.show(error); }
    finally { this.deleting = false; await this.refresh(); this.renderDocuments(); }
  }
}
