export const opacityKey = "voice-assistant.overlay-opacity";
export function validOpacity(value: unknown): number {
  const number = typeof value === "number" ? value : typeof value === "string" && value.trim() ? Number(value) : NaN;
  return Number.isFinite(number) && number >= 35 && number <= 100 ? Math.round(number) : 80;
}
export function readOpacity(storage: Pick<Storage, "getItem">): number {
  try { return validOpacity(storage.getItem(opacityKey)); } catch { return 80; }
}
export function saveOpacity(storage: Pick<Storage, "setItem">, value: number): boolean {
  try { storage.setItem(opacityKey, String(validOpacity(value))); return true; } catch { return false; }
}
export interface PictureInPictureApi { requestWindow(options: { width: number; height: number }): Promise<Window> }
type SheetName = "settings" | "practice" | "appearance";
const focusable = 'button:not(:disabled),input:not(:disabled),select:not(:disabled),textarea:not(:disabled),a[href],summary,[tabindex="0"]';

/** Owns layout and windows only. Media, sockets and all application state remain in the opener. */
export class Overlay {
  readonly card: HTMLElement;
  private readonly home: Document;
  private readonly marker: Comment;
  private readonly sheets = new Map<SheetName, HTMLElement>();
  private sheet: HTMLElement | undefined;
  private returnFocus: HTMLElement | undefined;
  private pip: Window | undefined;
  private opening = false;
  private generation = 0;
  private readonly api: PictureInPictureApi | undefined;
  private readonly live: HTMLElement;
  private readonly content: HTMLElement;
  private readonly toolbar: HTMLElement;
  private readonly bar: HTMLElement;
  private readonly floatButton: HTMLButtonElement;
  private readonly opacity: HTMLInputElement;
  private readonly opacityValue: HTMLOutputElement;
  private readonly moved = new Set<() => void>();
  private readonly ids: Map<string, HTMLElement>;
  private practiceMode = false;
  private alpha = 80;

  constructor(card: HTMLElement, api?: PictureInPictureApi) {
    this.card = card; this.home = card.ownerDocument; this.marker = this.home.createComment("overlay-home");
    card.before(this.marker); card.id = "overlay-card";
    this.api = api ?? (this.home.defaultView as (Window & { documentPictureInPicture?: PictureInPictureApi }) | null)?.documentPictureInPicture;
    this.ids = new Map([...card.querySelectorAll<HTMLElement>("[id]")].map(element => [element.id, element]));
    this.get("reply").setAttribute("aria-live", "polite"); this.get("reply").setAttribute("aria-atomic", "false");
    card.querySelector(".coach-heading")?.remove();
    const privacy = card.querySelector("footer")!; privacy.className = "privacy-help";
    this.toolbar = card.querySelector<HTMLElement>(".coach-strip")!;
    this.toolbar.setAttribute("aria-label", "Floating meeting coach");
    const handle = this.make("div", "", "overlay-handle"); handle.setAttribute("aria-hidden", "true"); card.prepend(handle);
    this.toolbar.append(this.icon("settings-toggle", "⚙", "Settings · 설정", () => this.open("settings")),
      this.floatButton = this.icon("float-toggle", "↗", "Float window · 항상 위", () => { void this.toggleFloat(); }),
      this.icon("opacity-toggle", "◐", "Opacity and compact layout", () => this.open("appearance")));
    this.floatButton.disabled = !this.api;
    this.floatButton.title = this.api ? "Open an always-on-top window" : "Floating windows require Chrome or Edge with Document Picture-in-Picture. Use Compact instead.";
    this.content = this.make("div", "", "overlay-content"); this.content.tabIndex = 0; this.content.setAttribute("aria-label", "Conversation");
    this.live = this.make("p", "", "overlay-notice"); this.live.setAttribute("role", "status");
    const audio = this.get("audio-status");
    this.content.append(this.get("meeting-view"), this.get("practice-view"));
    this.bar = this.make("div", "", "overlay-bottom");
    const actions = this.get("suggest").closest<HTMLElement>(".actions")!;
    actions.id = "meeting-actions"; this.ids.set(actions.id, actions); this.bar.append(actions);
    const practiceActions = this.make("div", "", "practice-actions"); practiceActions.className = "actions"; practiceActions.hidden = true;
    practiceActions.append(this.get("practice-stop"), this.get("practice-next"),
      this.icon("practice-setup-toggle", "⚙", "Practice setup", () => this.open("practice")));
    this.ids.set(practiceActions.id, practiceActions); this.bar.append(practiceActions);
    const line = this.make("p", "P: pause · Audio streams only after consent · Stop releases capture.", "overlay-privacy");
    this.bar.append(line);
    const settings = this.newSheet("settings", "Settings · 설정");
    const validation = this.make("p", "", "settings-validation-error"); validation.className = "error"; validation.hidden = true;
    validation.setAttribute("role", "alert"); settings.append(validation);
    const controls = this.get("session-title").closest<HTMLElement>(".controls")!;
    const error = this.get("error"); this.content.prepend(error, this.get("microphone-warning"));
    settings.append(controls, this.get("manage-materials").closest<HTMLElement>(".materials-link")!, privacy);
    const help = this.make("p", "Float opens an always-on-top, resizable Chrome/Edge window. Browser windows cannot be see-through to other apps. Opacity affects only the card inside the window or page; Compact helps when you snap the browser beside Teams.");
    help.className = "fine"; settings.append(help);
    const practice = this.newSheet("practice", "Practice setup · 연습 설정");
    const setup = this.get("practice-view").querySelector<HTMLElement>(".card")!;
    const practiceStatus = this.get("practice-status"), practiceError = this.get("practice-error");
    const practiceWarning = this.get("practice-warning"), retry = this.get("practice-retry");
    this.get("practice-view").prepend(practiceStatus, practiceError, practiceWarning, retry);
    practice.append(setup);
    const appearance = this.newSheet("appearance", "Appearance · 화면");
    const label = this.make("label", "Card opacity (35–100%)"); label.htmlFor = "overlay-opacity";
    this.opacity = this.make("input", "", "overlay-opacity"); this.opacity.type = "range"; this.opacity.min = "35"; this.opacity.max = "100"; this.opacity.step = "1";
    this.opacityValue = this.make("output", "", "overlay-opacity-value"); this.opacityValue.htmlFor = this.opacity.id;
    const compactLabel = this.make("label", " Compact width (~420px) for snapping beside your meeting");
    const compact = this.make("input", "", "overlay-compact"); compact.type = "checkbox"; compactLabel.prepend(compact); compactLabel.className = "check";
    compact.addEventListener("change", () => card.classList.toggle("compact", compact.checked));
    appearance.append(label, this.opacity, this.opacityValue, compactLabel, help.cloneNode(true));
    this.opacity.addEventListener("input", () => {
      this.setOpacity(Number(this.opacity.value));
      let saved = false;
      try { saved = saveOpacity(this.home.defaultView!.localStorage, this.alpha); } catch { /* Storage can be denied by browser policy. */ }
      if (!saved) this.live.textContent = "Opacity applies for this tab; browser storage is unavailable.";
    });
    try { this.setOpacity(readOpacity(this.home.defaultView!.localStorage)); } catch { this.setOpacity(80); }
    const transcript = this.get("transcript").closest<HTMLElement>(".transcript-panel")!;
    const history = this.make("details", "", "transcript-disclosure");
    history.append(this.make("summary", "Transcript · 대화 기록"), transcript); this.get("meeting-view").append(history);
    const pinned = this.get("pinned").closest<HTMLElement>(".pinned-panel")!;
    pinned.classList.add("overlay-pinned");
    card.append(this.live, audio, this.content, this.bar, ...this.sheets.values());
    card.addEventListener("keydown", event => this.key(event));
    this.home.defaultView?.addEventListener("pagehide", () => { this.generation++; this.pip?.close(); });
    const placeholder = this.make("p", "The coach is in its floating window. Close that window to return it here.", "float-placeholder");
    placeholder.hidden = true; card.after(placeholder); this.ids.set(placeholder.id, placeholder);
    this.close(false);
  }
  get<T extends HTMLElement = HTMLElement>(id: string): T {
    const element = this.ids.get(id) ?? this.card.querySelector<HTMLElement>(`#${id}`);
    if (!element) throw new Error(`Missing overlay element: ${id}`);
    return element as T;
  }
  get ownerWindow(): Window { return this.card.ownerDocument.defaultView ?? this.home.defaultView!; }
  onWindowChanged(callback: () => void): void { this.moved.add(callback); }
  setMode(practice: boolean): void {
    this.practiceMode = practice; this.get("meeting-actions").hidden = practice; this.get("practice-actions").hidden = !practice;
    this.get("overlay-privacy").textContent = practice
      ? "Practice · Mic stays paused until Answer by voice · Stop clears this round."
      : "P: pause · Audio streams only after consent · Stop releases capture.";
    this.close(); if (practice) this.open("practice");
  }
  setOpacity(value: number): void {
    this.alpha = validOpacity(value); this.card.style.setProperty("--overlay-alpha", String(this.alpha / 100));
    this.opacity.value = String(this.alpha); this.opacityValue.value = `${this.alpha}%`;
  }
  open(name: SheetName): void {
    const sheet = this.sheets.get(name)!;
    if (this.sheet === sheet) return;
    if (!this.sheet) this.returnFocus = this.card.ownerDocument.activeElement as HTMLElement | undefined;
    for (const item of this.sheets.values()) item.hidden = item !== sheet;
    this.sheet = sheet; this.content.inert = this.toolbar.inert = this.bar.inert = true;
    this.get("settings-meeting-only").hidden = this.practiceMode;
    sheet.querySelector<HTMLElement>("[data-close-sheet]")?.focus();
  }
  close(restoreFocus = true): void {
    for (const sheet of this.sheets.values()) sheet.hidden = true;
    const hadSheet = !!this.sheet; this.sheet = undefined; this.content.inert = this.toolbar.inert = this.bar.inert = false;
    if (restoreFocus && hadSheet) {
      if (this.returnFocus?.isConnected && !this.returnFocus.closest("[hidden]")) this.returnFocus.focus();
      else this.get("coach-toggle").focus();
    }
  }
  revealMeetingSettings(): void { this.open("settings"); this.live.textContent = "Choose audio, sign in if needed, and confirm permission in Settings before starting."; }
  reportError(message: string): void {
    const validation = this.get("settings-validation-error"); validation.textContent = message; validation.hidden = !message;
  }
  async toggleFloat(): Promise<void> {
    if (this.pip) { this.pip.close(); return; }
    if (!this.api || this.opening) return;
    this.opening = true; this.floatButton.disabled = true; const generation = ++this.generation;
    try {
      const win = await this.api.requestWindow({ width: 460, height: 680 });
      if (generation !== this.generation) { win.close(); return; }
      this.pip = win;
      const base = win.document.createElement("base"); base.href = this.home.baseURI; win.document.head.append(base);
      win.document.title = "Meeting coach";
      for (const original of this.home.querySelectorAll('link[rel="stylesheet"],style')) win.document.head.append(original.cloneNode(true));
      win.document.documentElement.lang = this.home.documentElement.lang;
      win.document.body.className = "coach-theme pip-window";
      this.close(false); win.document.body.append(this.card);
      this.get("float-placeholder").hidden = false;
      this.floatButton.setAttribute("aria-label", "Return to main window"); this.floatButton.title = "Close the floating window and return to the tab";
      win.addEventListener("pagehide", () => this.restore(), { once: true });
      this.moved.forEach(callback => callback()); this.get("coach-toggle").focus();
      this.live.textContent = "Floating above other windows. Keep the original tab open to maintain the session.";
    } catch {
      if (this.pip) { const failed = this.pip; this.restore(); failed.close(); }
      this.live.textContent = "The floating window could not open. Use Chrome/Edge, or use Compact and snap this window beside your meeting.";
    } finally { this.opening = false; this.floatButton.disabled = !this.api; }
  }
  private restore(): void {
    if (!this.pip) return;
    this.close(false); this.marker.after(this.card); this.pip = undefined;
    this.get("float-placeholder").hidden = true;
    this.floatButton.setAttribute("aria-label", "Float window · 항상 위"); this.floatButton.title = "Open an always-on-top window";
    this.moved.forEach(callback => callback()); this.floatButton.focus(); this.live.textContent = "Returned to the meeting tab. Session state is unchanged.";
  }
  private key(event: KeyboardEvent): void {
    if (!this.sheet) return;
    if (event.key === "Escape") { event.preventDefault(); event.stopPropagation(); this.close(); }
    if (event.key !== "Tab") return;
    const nodes = [...this.sheet.querySelectorAll<HTMLElement>(focusable)].filter(element => element.getClientRects().length > 0);
    const first = nodes[0], last = nodes.at(-1);
    const active = this.card.ownerDocument.activeElement;
    if (event.shiftKey && active === first) { event.preventDefault(); last?.focus(); }
    else if (!event.shiftKey && active === last) { event.preventDefault(); first?.focus(); }
  }
  private newSheet(name: SheetName, title: string): HTMLElement {
    const sheet = this.make("section", "", `${name}-sheet`); sheet.className = "overlay-sheet"; sheet.hidden = true;
    sheet.setAttribute("role", "dialog"); sheet.setAttribute("aria-modal", "true"); sheet.setAttribute("aria-label", title);
    const heading = this.make("div"); heading.className = "sheet-heading"; heading.append(this.make("h2", title),
      this.icon(`${name}-close`, "×", `Close ${title}`, () => this.close()));
    heading.querySelector("button")!.setAttribute("data-close-sheet", "");
    sheet.append(heading); this.sheets.set(name, sheet);
    if (name === "settings") {
      // Meeting controls are already a group; retain their original IDs while making mode-specific content clear.
      const controls = this.get("session-title").closest<HTMLElement>(".controls")!; controls.id = "settings-meeting-only"; this.ids.set(controls.id, controls);
    }
    return sheet;
  }
  private make<K extends keyof HTMLElementTagNameMap>(tag: K, text = "", id?: string): HTMLElementTagNameMap[K] {
    const node = this.home.createElement(tag); node.textContent = text; if (id) { node.id = id; this.ids.set(id, node); } return node;
  }
  private icon(id: string, text: string, label: string, click: () => void): HTMLButtonElement {
    const button = this.make("button", text, id); button.type = "button"; button.className = "icon-button"; button.setAttribute("aria-label", label);
    button.title = label; button.addEventListener("click", click); return button;
  }
}
