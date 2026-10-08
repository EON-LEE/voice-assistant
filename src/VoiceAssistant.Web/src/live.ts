import "./live.css";
import workletUrl from "./audio.worklet.ts?worker&url";
import { BrowserAuth } from "./auth.js";
import { MicrophoneAudioSource } from "./capture.js";
import { MeetingSession } from "./session.js";
import { SocketTransport, asError } from "./transport.js";
import { defaultOptions, type SessionOptions } from "./options.js";
import { CoachClient, CoachError, type CoachKind } from "./coach-client.js";
import type { ServerEvent } from "./protocol.js";
import { LiveState, answers, isTransientFailure, translationChunks, type LiveReply } from "./live-state.js";

const $ = <T extends HTMLElement = HTMLElement>(id: string): T => {
  const value = document.getElementById(id);
  if (!value) throw new Error(`Missing #${id}`);
  return value as T;
};
const panel = $("live-panel"), stage = $("live-stage"), menu = $("live-menu");
const settingsDialog = $<HTMLDialogElement>("settings");
const micDevice = $<HTMLSelectElement>("mic-device"), responseMode = $<HTMLSelectElement>("response-mode");
const topic = $<HTMLInputElement>("topic");

const auth = new BrowserAuth(undefined, { persistent: true });
const coach = new CoachClient((path, init) => auth.coachRequest(path, init), () => auth.isFake);
const state = new LiveState();

const maxReconnects = 20;
let session: MeetingSession | undefined;
let active = false, ready = false, paused = false, reconnecting = false;
let attempt = 0, readyAt = 0;
let reconnectTimer: ReturnType<typeof setTimeout> | undefined;
let connectionMessage = "", warning = "";
let translations = new AbortController();
const meetingKo = new Map<string, string>();
const failedTurns = new Map<string, string>();
const replyKo = new Map<string, string[]>();

interface StoredSettings { deviceId: string; responseMode: SessionOptions["responseMode"]; topic: string }
const settingsKey = "live-coach-settings";
function loadSettings(): StoredSettings {
  try {
    const value = JSON.parse(localStorage.getItem(settingsKey) ?? "{}") as Partial<StoredSettings>;
    return {
      deviceId: typeof value.deviceId === "string" ? value.deviceId : "",
      responseMode: value.responseMode === "balanced" || value.responseMode === "conversation" ? value.responseMode : "grounded",
      topic: typeof value.topic === "string" ? value.topic.slice(0, 300) : "",
    };
  } catch { return { deviceId: "", responseMode: "grounded", topic: "" }; }
}
let settings = loadSettings();
responseMode.value = settings.responseMode; topic.value = settings.topic;
function saveSettings(): void {
  settings = { deviceId: micDevice.value, responseMode: responseMode.value as StoredSettings["responseMode"], topic: topic.value.trim() };
  localStorage.setItem(settingsKey, JSON.stringify(settings));
}
micDevice.addEventListener("change", saveSettings);
responseMode.addEventListener("change", saveSettings);
topic.addEventListener("change", saveSettings);

function sessionOptions(): SessionOptions {
  return { ...defaultOptions(), responseMode: settings.responseMode, topic: settings.topic,
    endSilenceMs: 1100, semanticSegmentation: true, replySettleMs: 1000 };
}

// ---------- Live session lifecycle with automatic reconnect ----------

async function startLive(): Promise<void> {
  if (active) return;
  try {
    const config = await auth.initialize();
    // Uses the cached account first; a popup appears only when Microsoft requires sign-in again.
    if (config.mode === "Azure" && !(await auth.verify())) await auth.signIn();
  } catch (error) {
    connectionMessage = `${auth.isDemo ? "로그인" : "Microsoft 계정 연결"} 실패: ${asError(error).message}`;
    render(); return;
  }
  if (!navigator.mediaDevices?.getUserMedia) {
    connectionMessage = "이 브라우저에서는 마이크를 사용할 수 없습니다. HTTPS의 최신 Edge/Chrome을 사용하세요.";
    render(); return;
  }
  translations.abort(); translations = new AbortController();
  state.resetSession(); meetingKo.clear(); failedTurns.clear(); replyKo.clear();
  active = true; paused = false; attempt = 0; readyAt = 0; connectionMessage = ""; warning = "";
  liveChannel?.postMessage("started");
  connect();
}

function connect(): void {
  reconnectTimer = undefined;
  const source = new MicrophoneAudioSource({
    context: () => new AudioContext(),
    node: context => new AudioWorkletNode(context, "meeting-pcm", { numberOfInputs: 1, numberOfOutputs: 1, outputChannelCount: [1] }),
    workletUrl,
    getUserMedia: constraints => navigator.mediaDevices.getUserMedia(constraints),
    devices: navigator.mediaDevices,
    deviceId: settings.deviceId || undefined,
    onDevices: fillDevices,
    onWarning: message => { warning = message; render(); },
    onLevel: level => { $<HTMLMeterElement>("input-level").value = level; },
  });
  const transport = new SocketTransport(() => auth.endpoint(false));
  const current: MeetingSession = new MeetingSession(source, transport,
    event => { if (session === current) onEvent(event); },
    () => { if (session === current) render(); },
    error => { if (session === current) onFailure(error); },
    sessionOptions());
  session = current;
  render();
  void current.start();
}

function onFailure(error: Error): void {
  ready = false;
  const message = error.message;
  if (!active) { render(); return; }
  if (!isTransientFailure(message)) {
    active = false; reconnecting = false;
    connectionMessage = `세션 종료: ${message} 우클릭 → 라이브 시작으로 다시 연결하세요.`;
    render(); return;
  }
  // A session that stayed up resets the backoff; repeated quick failures back off up to 3 seconds.
  attempt = readyAt && Date.now() - readyAt > 30000 ? 1 : attempt + 1;
  readyAt = 0;
  if (attempt > maxReconnects) {
    active = false; reconnecting = false;
    connectionMessage = `연결이 계속 끊깁니다 (${message}). 네트워크를 확인하고 다시 시작하세요.`;
    render(); return;
  }
  reconnecting = true;
  if (state.current && !state.current.complete) state.cancelCurrent();
  state.clearError();
  connectionMessage = `연결이 끊겨 자동 재연결 중 (${attempt}/${maxReconnects}) · 대화와 추천은 유지됩니다`;
  render();
  reconnectTimer = setTimeout(() => { if (active) connect(); }, Math.min(3000, 300 * 2 ** Math.min(attempt - 1, 4)));
}

async function stopLive(message = ""): Promise<void> {
  active = false; reconnecting = false; ready = false;
  clearTimeout(reconnectTimer); reconnectTimer = undefined;
  const current = session; session = undefined;
  connectionMessage = message;
  render();
  await current?.stop();
  render();
}

function onEvent(e: ServerEvent): void {
  state.apply(e);
  if (e.type === "session.ready") {
    ready = true; reconnecting = false; readyAt = Date.now(); connectionMessage = "";
    if (paused) session?.pause(true);
  }
  if (e.type === "transcript.final") void translateTurn(e.turnId!, e.text!);
  const completed = state.current;
  if (e.type === "response.completed" && completed && completed.id === e.responseId && completed.complete)
    void translateReply(completed);
  schedule();
}

// ---------- Korean translation (translation-only, retried on transient failure) ----------

function retryable(error: unknown): boolean {
  if (error instanceof CoachError) return ["busy", "provider_unavailable", "provider_timeout"].includes(error.code);
  return error instanceof TypeError || (error instanceof Error && error.name === "TimeoutError");
}
function failureLabel(error: unknown): string {
  const code = error instanceof CoachError ? error.code : "";
  return code === "busy" ? "번역 요청 제한 · 잠시 후 재시도" : code === "provider_timeout" ? "한국어 번역 시간 초과"
    : code === "unauthorized" || code === "forbidden" ? "한국어 번역 인증 오류" : "한국어 번역 실패";
}
async function translate(kind: CoachKind, text: string, signal: AbortSignal): Promise<string> {
  const parts: string[] = [];
  for (const chunk of translationChunks(text)) {
    for (let tries = 1; ; tries++) {
      try { parts.push(await coach.translate(kind, chunk, signal)); break; }
      catch (error) {
        if (tries >= 3 || signal.aborted || !retryable(error)) throw error;
        await new Promise(resolve => setTimeout(resolve, 300 * tries));
      }
    }
  }
  return parts.join(" ");
}
async function translateTurn(turnId: string, text: string): Promise<void> {
  const signal = translations.signal;
  failedTurns.delete(turnId);
  try {
    const korean = await translate("question", text, signal);
    if (!signal.aborted) { meetingKo.set(turnId, korean); schedule(); }
  } catch (error) {
    if (signal.aborted) return;
    meetingKo.set(turnId, failureLabel(error)); failedTurns.set(turnId, text); schedule();
  }
}
async function translateReply(reply: LiveReply): Promise<void> {
  const signal = translations.signal, values = answers(reply);
  const result = values.map(() => "한국어 번역 중…");
  replyKo.set(reply.id, result); schedule();
  await Promise.all(values.map(async (english, index) => {
    try { result[index] = await translate("reply", english, signal); }
    catch (error) { if (!signal.aborted) result[index] = `${failureLabel(error)} · 우클릭으로 재시도`; }
    if (!signal.aborted) schedule();
  }));
  const keep = new Set([state.current?.id, state.lastCompleted?.id]);
  for (const id of replyKo.keys()) if (!keep.has(id)) replyKo.delete(id);
}
function retryTranslations(): void {
  const display = state.display;
  if (display?.complete) void translateReply(display);
  for (const [turnId, text] of failedTurns) void translateTurn(turnId, text);
}

// ---------- Rendering (same wording and layout rules as the desktop overlay) ----------

let frame = 0;
function schedule(): void { if (!frame) frame = requestAnimationFrame(() => { frame = 0; render(); }); }
let renderedConversation = "";

function render(): void {
  const capture = reconnecting ? "reconnecting" : ready ? paused ? "paused" : "on" : "off";
  panel.dataset.capture = capture;
  $("capture-status").textContent = reconnecting ? "재연결 중 · 마이크 자동 재개"
    : ready && paused ? "마이크 일시정지 · 오디오 전송 중지"
    : ready ? "듣는 중 · 영어 · 이전 대화 맥락 연결"
    : active ? "연결 중…" : "마이크 꺼짐 · 우클릭으로 설정 및 시작";
  const error = connectionMessage || state.error || warning;
  const errorLine = $("session-error");
  errorLine.textContent = error; errorLine.hidden = !error;
  renderConversation();
  renderSuggestions();
  renderMenu();
}

function renderConversation(): void {
  const recent = state.turns.slice(-12);
  const signature = JSON.stringify(recent.map(turn => [turn.id, turn.revision, turn.final, meetingKo.get(turn.id) ?? ""]));
  if (signature === renderedConversation) return;
  renderedConversation = signature;
  const host = $("conversation");
  if (!recent.length) {
    const waiting = document.createElement("p"); waiting.className = "live-waiting";
    waiting.textContent = "영어 대화를 기다리고 있어요.";
    host.replaceChildren(waiting); return;
  }
  host.replaceChildren(...recent.map((turn, index) => {
    const line = document.createElement("div");
    line.className = index === recent.length - 1 ? "live-line current" : "live-line";
    const en = document.createElement("p"), ko = document.createElement("p");
    en.className = "en"; en.lang = "en"; en.textContent = turn.text;
    ko.className = "ko"; ko.lang = "ko";
    ko.textContent = !turn.final ? "듣는 중…" : meetingKo.get(turn.id) ?? "한국어 번역 중…";
    line.append(en, ko);
    return line;
  }));
  host.scrollTop = host.scrollHeight;
}

function renderSuggestions(): void {
  const display = state.display, values = answers(display);
  const ko = display ? replyKo.get(display.id) ?? [] : [];
  $("answer-text").textContent = values[0] ?? "대화가 인식되면 추천 답변이 여기에 표시됩니다.";
  $("answer-korean").textContent = ko[0] ?? (display ? "한국어 번역 준비 중…" : "영어 추천 답변의 한국어 뜻이 함께 표시됩니다.");
  $("alternative-card").hidden = values.length < 2;
  $("alternative-text").textContent = values[1] ?? "";
  $("alternative-korean").textContent = ko[1] ?? "한국어 번역 준비 중…";
  const generating = ready && state.generating;
  // "Previous" only while a newer reply is being produced; ongoing speech (maybe the user answering) keeps the hint current.
  const previous = !!display && generating && state.current?.id !== display.id;
  const myTurn = !previous && !!display?.respondNow;
  $("primary-label").textContent = previous ? "01 · 이전 질문용 답변" : myTurn ? "01 · 지금 답할 차례" : "01 · 바로 답하기";
  document.querySelector(".live-card.primary")!.classList.toggle("my-turn", myTurn);
  const question = state.turns.find(turn => turn.id === display?.turnId)?.text ?? (display ? "이전 대화" : "");
  $("continuity").textContent = !display
    ? generating ? "내 문서에서 근거를 찾아 답변 준비 중…" : active ? "듣는 중 · 질문이 끝나면 추천 답변을 만듭니다" : "우클릭으로 설정 / 라이브 시작"
    : `${previous ? "이전" : "현재"} 답변 기준: ${question}` +
      (generating ? "\n새 추천 답변 생성 중 · 기존 답변 유지" : "\n추천은 실제로 말한 내용이 아닙니다.");
  $("grounding-badge").textContent = ({ grounded: "맥락 + 내 문서 근거", no_matches: "문서 근거 없음",
    unavailable: "문서 검색 실패", disabled: "대화 맥락" } as Record<string, string>)[display?.grounding ?? ""]
    ?? (generating ? "근거 확인 중" : "맥락 연결");
  $("grounding-text").textContent = ({ grounded: "근거 있음 · 내 업로드 문서", no_matches: "문서 근거 없음 · 사실 확인 필요",
    unavailable: "문서 검색 실패 · 사실 답변에 의존하지 마세요", disabled: "이번 답변은 문서 검색을 사용하지 않음" } as Record<string, string>)
    [display?.grounding ?? ""] ?? "문서 근거 상태는 답변 완료 후 표시됩니다";
  const sources = display?.sources ?? [];
  $("sources-text").textContent = sources.slice(0, 3).map(source => source.title).join(" · ") +
    (sources.length > 3 ? ` · 외 ${sources.length - 3}개` : "");
}

// ---------- Right-click menu (the overlay itself has no buttons) ----------

const item = (action: string): HTMLButtonElement => menu.querySelector<HTMLButtonElement>(`[data-action="${action}"]`)!;
function renderMenu(): void {
  item("start").disabled = active;
  item("stop").disabled = !active;
  item("pause").disabled = item("retry").disabled = !ready;
  item("pause").textContent = paused ? "다시 듣기" : "내가 말할 동안 일시정지";
  item("translate").disabled = !state.display?.complete && failedTurns.size === 0;
  item("float").hidden = !("documentPictureInPicture" in window);
  item("float").textContent = pipWindow ? "작은 창 닫고 페이지로 돌아가기" : "항상 위 작은 창으로 띄우기";
}
function openMenu(x: number, y: number): void {
  renderMenu();
  menu.hidden = false;
  const bounds = panel.getBoundingClientRect();
  const left = Math.max(4, Math.min(x - bounds.left, bounds.width - menu.offsetWidth - 4));
  const top = Math.max(4, Math.min(y - bounds.top, bounds.height - menu.offsetHeight - 4));
  menu.style.left = `${left}px`; menu.style.top = `${top}px`;
  menu.querySelector<HTMLButtonElement>("button:not(:disabled)")?.focus();
  const owner = panel.ownerDocument;
  const close = (event: Event): void => {
    if (event instanceof KeyboardEvent && event.key !== "Escape") return;
    if (event.type === "pointerdown" && menu.contains(event.target as Node)) return;
    closeMenu();
    owner.removeEventListener("pointerdown", close, true); owner.removeEventListener("keydown", close, true);
  };
  owner.addEventListener("pointerdown", close, true); owner.addEventListener("keydown", close, true);
}
function closeMenu(): void { menu.hidden = true; }
panel.addEventListener("contextmenu", event => { event.preventDefault(); openMenu(event.clientX, event.clientY); });
menu.addEventListener("keydown", event => {
  if (event.key !== "ArrowDown" && event.key !== "ArrowUp") return;
  event.preventDefault();
  const items = [...menu.querySelectorAll<HTMLButtonElement>("button:not(:disabled):not([hidden])")];
  const index = items.indexOf(panel.ownerDocument.activeElement as HTMLButtonElement);
  items[(index + (event.key === "ArrowDown" ? 1 : items.length - 1)) % items.length]?.focus();
});
menu.addEventListener("click", event => {
  const action = (event.target as HTMLElement).closest<HTMLButtonElement>("button[data-action]")?.dataset.action;
  if (!action) return;
  closeMenu();
  if (action === "settings") openSettings();
  if (action === "start") void startLive();
  if (action === "stop") void stopLive();
  if (action === "pause") { paused = !paused; state.pause(paused); session?.pause(paused); render(); }
  if (action === "retry") { state.beginRequest(); session?.request(); render(); }
  if (action === "translate") retryTranslations();
  if (action === "float") void toggleFloat();
});

// ---------- Settings dialog ----------

function fillDevices(devices: MediaDeviceInfo[]): void {
  const selected = settings.deviceId;
  micDevice.replaceChildren(new Option("브라우저 기본 마이크", ""));
  for (const device of devices.filter(value => value.kind === "audioinput"))
    micDevice.add(new Option(device.label || "마이크", device.deviceId));
  micDevice.value = [...micDevice.options].some(option => option.value === selected) ? selected : "";
}
function renderAccount(): void {
  const label = auth.isDemo ? "로그인" : "Microsoft 계정 연결";
  $("account-status").textContent = auth.isFake ? "로컬 테스트 서버 (로그인 불필요)"
    : auth.signedIn ? `로그인됨: ${auth.accountName} · 이 브라우저에서 로그인이 유지됩니다`
    : auth.isDemo ? "아직 로그인하지 않았습니다." : "Microsoft 계정이 아직 연결되지 않았습니다.";
  $("signin").hidden = auth.isFake;
  $("signin").textContent = auth.signedIn ? (auth.isDemo ? "다시 로그인" : "다른 계정으로 연결") : label;
}
function openSettings(): void {
  if (pipWindow) window.focus();
  renderAccount();
  void navigator.mediaDevices?.enumerateDevices().then(fillDevices).catch(() => undefined);
  settingsDialog.showModal();
}
$("signin").addEventListener("click", () => {
  void auth.signIn().then(renderAccount, error => { $("account-status").textContent = `로그인 실패: ${asError(error).message}`; });
});
settingsDialog.addEventListener("close", () => { saveSettings(); render(); });

// ---------- Always-on-top floating window (Document Picture-in-Picture, Edge/Chrome) ----------

interface DocumentPictureInPicture { requestWindow(options: { width: number; height: number }): Promise<Window> }
let pipWindow: Window | undefined;
async function toggleFloat(): Promise<void> {
  if (pipWindow) { pipWindow.close(); return; }
  const api = (window as unknown as { documentPictureInPicture?: DocumentPictureInPicture }).documentPictureInPicture;
  if (!api) return;
  const floating = await api.requestWindow({ width: 550, height: 860 });
  for (const sheet of [...document.styleSheets]) {
    try {
      const style = floating.document.createElement("style");
      style.textContent = [...sheet.cssRules].map(rule => rule.cssText).join("\n");
      floating.document.head.append(style);
    } catch {
      if (sheet.href) {
        const link = floating.document.createElement("link"); link.rel = "stylesheet"; link.href = sheet.href;
        floating.document.head.append(link);
      }
    }
  }
  floating.document.documentElement.lang = "ko";
  floating.document.title = "Live Coach";
  floating.document.body.className = "live-body live-floating";
  const floatingStage = floating.document.createElement("main");
  floatingStage.className = "live-stage";
  floatingStage.append(panel);
  floating.document.body.append(floatingStage);
  pipWindow = floating;
  floating.addEventListener("pagehide", () => { stage.append(panel); pipWindow = undefined; closeMenu(); render(); });
  render();
}

// ---------- One live tab at a time (prevents two tabs from replacing each other's session) ----------

const liveChannel = "BroadcastChannel" in window ? new BroadcastChannel("live-coach") : undefined;
liveChannel?.addEventListener("message", event => {
  if (event.data === "started" && active) void stopLive("다른 탭에서 라이브를 시작해 이 탭은 중지되었습니다.");
});
window.addEventListener("pagehide", () => { void session?.stop(); });

void auth.initialize().then(() => render(), error => {
  connectionMessage = `서비스 설정을 불러오지 못했습니다: ${asError(error).message}`; render();
});
render();
