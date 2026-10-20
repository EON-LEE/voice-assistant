import "./live.css";
import workletUrl from "./audio.worklet.ts?worker&url";
import { BrowserAuth } from "./auth.js";
import { CaptureError, DisplayAudioSource, MicrophoneAudioSource, type AudioSource, type CaptureErrorKind,
  type CaptureSourceKind } from "./capture.js";
import { SocketTransport, asError } from "./transport.js";
import { defaultOptions, type SessionOptions } from "./options.js";
import { CoachClient, CoachError, type CoachKind } from "./coach-client.js";
import type { Grounding, ServerEvent } from "./protocol.js";
import { LiveState, answers, translationChunks, type LiveReply, type LiveTurn } from "./live-state.js";
import { PushedKorean, pushedKoreanWaitMs } from "./reply-korean.js";
import { parseSettings, pendingUpdate, settingsKey, withDocumentSearch, withResponseMode, documentSearchOn,
  type LiveSettings, type ResponseMode } from "./live-settings.js";
import { LiveController, liveLimits, type SourceHooks } from "./live-controller.js";
import { actionLabels, captureMessage, liveHost, liveMessage, reconnectText, type RecoveryAction } from "./live-errors.js";
import { LatencyMarks, debugEnabled } from "./live-latency.js";

let liveDocument = document;
const $ = <T extends HTMLElement = HTMLElement>(id: string): T => {
  const value = document.getElementById(id) ?? liveDocument.getElementById(id);
  if (!value) throw new Error(`Missing #${id}`);
  return value as T;
};
const panel = $("live-panel"), stage = $("live-stage"), menu = $("live-menu");
const settingsDialog = $<HTMLDialogElement>("settings");
const micDevice = $<HTMLSelectElement>("mic-device"), responseMode = $<HTMLSelectElement>("response-mode");
const topic = $<HTMLInputElement>("topic");
const audioSource = $<HTMLSelectElement>("audio-source");
const microphoneSettings = $("microphone-settings"), displayHint = $("display-hint");
/** LiveCoach.exe (WebView2) or a browser tab: the share picker offers different surfaces in each. */
const host = liveHost(window);

const auth = new BrowserAuth(undefined, { persistent: true });
const coach = new CoachClient((path, init) => auth.coachRequest(path, init), () => auth.isFake);
const state = new LiveState();

let paused = false;
/** One-off notes (sign-in results, another tab took over); cleared by the next start. */
let infoMessage = "";
/** Signed in first from a Start click: sharing needs a click of its own, offered inline. */
let startOffered = false;
let translations = new AbortController();
const meetingKo = new Map<string, string>();
const failedTurns = new Map<string, string>();
const requestedTurns = new Set<string>();
const replyKo = new Map<string, string[]>();
// The server pushes a Korean line with each reply line; a line it cannot provide falls back to the translation request.
const pushes = new PushedKorean(korean => /[\uac00-\ud7a3]/u.test(korean) || auth.isFake && korean.startsWith("[fake-ko] "));
let pushing = false;
// Only a reply that began while the server was pushing Korean has a pushed line to wait for.
const pushedAtStart = new Set<string>();
// What the connected session runs with, and whether the server accepts setting changes while connected.
let applied: { responseMode: ResponseMode; replyKorean: boolean } = { responseMode: "grounded", replyKorean: false };
let startedMode: ResponseMode = "grounded";
let canUpdate = false, updating = false;
let quickNote = "";
const recentTranslatedTurns = 6;

function storage(): Storage | undefined {
  try { return localStorage; } catch { return undefined; }
}
const debug = debugEnabled(location.search, storage());
const latency = new LatencyMarks(() => performance.now(), debug ? name => { performance.mark(name); } : undefined);

function loadSettings(): LiveSettings {
  try { return parseSettings(localStorage.getItem(settingsKey)); } catch { return parseSettings(null); }
}
let settings = loadSettings();
responseMode.value = settings.responseMode; topic.value = settings.topic; audioSource.value = settings.audioSource;
if (host === "app") {
  // WebView2 has no browser tabs to pick; only a whole screen carries (system) audio there.
  displayHint.textContent = "라이브 시작 시 뜨는 공유 선택창에서 '전체 화면'을 고르고 '시스템 오디오 공유'를 체크하세요. "
    + "창(Window) 공유에는 소리가 없습니다. 시스템 오디오는 Windows 기본 출력 장치의 소리만 가져옵니다.";
}
function renderAudioSource(): void {
  const display = audioSource.value === "display";
  microphoneSettings.hidden = display; displayHint.hidden = !display;
}
renderAudioSource();
function persist(): void {
  try { localStorage.setItem(settingsKey, JSON.stringify(settings)); } catch { /* storage unavailable: the choice lasts until the page closes */ }
}
function saveSettings(): void {
  settings = { ...settings, deviceId: micDevice.value, topic: topic.value.trim(), audioSource: audioSource.value as LiveSettings["audioSource"] };
  persist();
}
micDevice.addEventListener("change", saveSettings);
topic.addEventListener("change", saveSettings);
audioSource.addEventListener("change", () => { renderAudioSource(); saveSettings(); });

// ---------- Quick settings: document search, captions, Korean ----------

function changeSettings(next: LiveSettings): void {
  const before = settings;
  settings = next;
  persist();
  if (before.showKorean && !next.showKorean) dropKorean();
  if (next.showKorean && (!before.showKorean || next.showCaptions && !before.showCaptions)) translateOnScreen();
  syncSession();
  render();
}
$("doc-on").addEventListener("click", () => changeSettings(withDocumentSearch(settings, true)));
$("doc-off").addEventListener("click", () => changeSettings(withDocumentSearch(settings, false)));
$("toggle-captions").addEventListener("click", () => changeSettings({ ...settings, showCaptions: !settings.showCaptions }));
$("toggle-korean").addEventListener("click", () => changeSettings({ ...settings, showKorean: !settings.showKorean }));
responseMode.addEventListener("change", () => changeSettings(withResponseMode(settings, responseMode.value as ResponseMode)));

/** Makes the connected session match the settings. A change reaches the next reply; a reply already under way keeps its own. */
function syncSession(): void {
  if (!controller.isReady) return;
  const update = pendingUpdate(applied, settings);
  if (!update) { quickNote = ""; return; }
  if (!canUpdate) {
    // Korean still works without the server: the client translates it itself. Document search needs the server.
    quickNote = update.responseMode ? "이 서버는 연결 중 문서검색 변경을 지원하지 않아요 · 다음 연결부터 적용돼요" : "";
    return;
  }
  quickNote = "";
  if (updating) return;
  controller.update(update);
  updating = true;
}
/** Forgets what the last connection accepted; the next `session.ready` says it again. */
function resetUpdate(): void { canUpdate = updating = false; quickNote = ""; }

function sessionOptions(): SessionOptions {
  // endSilenceMs is required by the options validator but has no effect under semantic segmentation.
  const options: SessionOptions = { ...defaultOptions(), responseMode: settings.responseMode, topic: settings.topic,
    endSilenceMs: 1100, semanticSegmentation: true, replySettleMs: 1000, replyKorean: settings.showKorean };
  startedMode = options.responseMode;
  return options;
}

// ---------- Capture: one source for the whole live session ----------

/** Picker hints; a browser ignores what it does not know. The video track is only the carrier browsers require for audio. */
interface DisplayCaptureOptions extends DisplayMediaStreamOptions {
  systemAudio?: "include" | "exclude";
  surfaceSwitching?: "include" | "exclude";
  selfBrowserSurface?: "include" | "exclude";
  monitorTypeSurfaces?: "include" | "exclude";
}
function displayOptions(): DisplayCaptureOptions {
  return {
    video: host === "app" ? { frameRate: { max: 5 }, displaySurface: "monitor" } : { frameRate: { max: 5 } },
    audio: true, systemAudio: "include", surfaceSwitching: "include", selfBrowserSurface: "exclude", monitorTypeSurfaces: "include",
  };
}
function showLevel(level: number): void {
  for (const id of ["input-level", "live-level"]) {
    const meter = liveDocument.getElementById(id) ?? document.getElementById(id);
    if (meter instanceof HTMLMeterElement) meter.value = level;
  }
}
function createSource(kind: CaptureSourceKind, hooks: SourceHooks): AudioSource {
  const level = (value: number): void => { showLevel(value); hooks.level(value); };
  const warning = (_message: string, warned?: CaptureErrorKind): void => hooks.warning(warned === "muted" ? "muted" : null);
  const shared = {
    context: () => new AudioContext(),
    node: (context: AudioContext) => new AudioWorkletNode(context, "meeting-pcm", { numberOfInputs: 1, numberOfOutputs: 1, outputChannelCount: [1] }),
    workletUrl, onStall: hooks.stall, onLevel: level, onWarning: warning,
  };
  if (kind === "display") return new DisplayAudioSource({
    ...shared,
    getDisplayMedia: () => {
      if (!navigator.mediaDevices?.getDisplayMedia) throw new CaptureError("unsupported", "display", "Screen audio sharing is not available here.");
      return navigator.mediaDevices.getDisplayMedia(displayOptions());
    },
  });
  return new MicrophoneAudioSource({
    ...shared,
    getUserMedia: constraints => {
      if (!navigator.mediaDevices?.getUserMedia) throw new CaptureError("unsupported", "microphone", "Microphone capture is not available here.");
      return navigator.mediaDevices.getUserMedia(constraints);
    },
    devices: navigator.mediaDevices,
    deviceId: settings.deviceId || undefined,
    onDevices: fillDevices,
  });
}

const controller = new LiveController({
  createSource,
  createTransport: () => new SocketTransport(() => auth.endpoint(false)),
  options: sessionOptions,
  onEvent,
  onDisconnect,
  onChange: schedule,
});

// ---------- Live session lifecycle ----------

function resetConversation(): void {
  translations.abort(); translations = new AbortController();
  state.resetSession(); meetingKo.clear(); failedTurns.clear(); requestedTurns.clear(); replyKo.clear(); pushes.clear();
  pushedAtStart.clear();
}

/**
 * Starts from a click, so the share picker (or microphone prompt) opens inside it. `resume` keeps the conversation on
 * screen ("다시 공유" / "다시 시작"). Reconnects never come back here: they keep the source and only reconnect the server.
 */
function startLive(resume = false): void {
  if (controller.active) return;
  const kind = settings.audioSource;
  infoMessage = ""; startOffered = false;
  if (kind === "display" ? !navigator.mediaDevices?.getDisplayMedia : !navigator.mediaDevices?.getUserMedia) {
    infoMessage = captureMessage("unsupported", kind, host);
    render(); return;
  }
  // Without a signed-in account, sign in from this click first; a share picker then needs a click of its own.
  if (auth.configured && !auth.isFake && !auth.signedIn) { void signInFirst(resume); return; }
  if (!resume) resetConversation();
  paused = false; pushing = false; resetUpdate();
  liveChannel?.postMessage("started");
  controller.start(kind);
  render();
}

async function signInFirst(resume: boolean): Promise<void> {
  try { await auth.signIn(); }
  catch (error) {
    infoMessage = `${auth.isDemo ? "로그인" : "Microsoft 계정 연결"} 실패: ${asError(error).message}`;
    render(); return;
  }
  // A microphone needs no new click; a screen-share picker does.
  if (settings.audioSource === "microphone") { startLive(resume); return; }
  startOffered = true;
  infoMessage = "로그인되었습니다. '라이브 시작'을 눌러 공유할 화면을 고르세요.";
  render();
}

async function signInAndResume(): Promise<void> {
  try { await auth.signIn(); }
  catch (error) { infoMessage = `로그인 실패: ${asError(error).message}`; render(); return; }
  infoMessage = "";
  if (controller.needsSignIn) controller.resume();
  else if (!controller.active) startLive(true);
  render();
}

async function stopLive(message = ""): Promise<void> {
  pushing = false; pushes.expire(); resetUpdate();
  infoMessage = message; startOffered = false;
  const stopped = controller.stop();
  render();
  await stopped;
  render();
}

/** The recovery step the inline button offers, if any. */
function currentAction(): RecoveryAction | "start" | undefined {
  if (startOffered && !controller.active) return "start";
  return controller.failure?.action;
}
$("live-action").addEventListener("click", () => {
  const action = currentAction();
  if (action === "signin") void signInAndResume();
  else if (action === "start") startLive(false);
  else if (action === "reshare" || action === "restart") startLive(true);
});

/** A dropped or replaced connection: replies in flight end, the next `session.ready` restates the capabilities. */
function onDisconnect(): void {
  pushing = false; pushes.expire(); resetUpdate();
  if (state.current && !state.current.complete) state.cancelCurrent();
  state.clearError();
  schedule();
}

function onEvent(e: ServerEvent): void {
  // With Korean off nothing is recorded, even while the server has not yet stopped sending it.
  if (e.type === "response.korean") { if (settings.showKorean) pushes.record(e); return; }
  state.apply(e);
  if (e.type === "session.ready") {
    pushing = e.replyKorean === true; canUpdate = e.sessionUpdate === true; updating = false;
    applied = { responseMode: startedMode, replyKorean: pushing };
    // A setting may have changed between connecting and being ready.
    syncSession();
  }
  if (e.type === "session.updated") {
    applied = { responseMode: e.responseMode ?? applied.responseMode, replyKorean: e.replyKorean === true };
    pushing = applied.replyKorean; updating = false;
    syncSession();
  }
  if (e.type === "response.started" && pushing && settings.showKorean) pushedAtStart.add(e.responseId!);
  if (e.type === "transcript.final") {
    latency.final(e.turnId!);
    if (settings.showKorean && settings.showCaptions) void translateTurn(e.turnId!, e.text!);
  }
  if (e.type === "response.completed") {
    latency.completed(e.responseId!, e.turnId!);
    const completed = state.current;
    if (settings.showKorean && completed && completed.id === e.responseId && completed.complete) void translateReply(completed);
  }
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
  requestedTurns.add(turnId);
  failedTurns.delete(turnId);
  try {
    const korean = await translate("question", text, signal);
    if (!signal.aborted) { meetingKo.set(turnId, korean); schedule(); }
  } catch (error) {
    if (signal.aborted) return;
    meetingKo.set(turnId, failureLabel(error)); failedTurns.set(turnId, text); schedule();
  }
}
async function translateReply(reply: LiveReply, retry = false): Promise<void> {
  const signal = translations.signal, values = answers(reply);
  const result = values.map(() => "한국어 번역 중…");
  replyKo.set(reply.id, result); schedule();
  await Promise.all(values.map(async (english, index) => {
    try { result[index] = await replyLine(reply.id, index, english, retry, signal); }
    catch (error) { if (!signal.aborted) result[index] = `${failureLabel(error)} · 우클릭으로 재시도`; }
    if (!signal.aborted) schedule();
  }));
  const keep = new Set([state.current?.id, state.lastCompleted?.id, state.shown?.id, state.display?.id]);
  for (const id of replyKo.keys()) if (!keep.has(id)) replyKo.delete(id);
  for (const id of pushedAtStart) if (!keep.has(id)) pushedAtStart.delete(id);
  pushes.prune(keep);
}
/** The server's pushed line when it has one in time; otherwise (or on a retry) the client translates the line itself. */
async function replyLine(responseId: string, index: number, english: string, retry: boolean, signal: AbortSignal): Promise<string> {
  const wait = !retry && pushedAtStart.has(responseId) ? pushedKoreanWaitMs : 0;
  const pushed = await pushes.wait(responseId, index, wait, signal);
  return pushed ?? translate("reply", english, signal);
}
function retryTranslations(): void {
  if (!settings.showKorean) return;
  const display = state.display;
  if (display?.complete) void translateReply(display, true);
  if (settings.showCaptions) for (const [turnId, text] of failedTurns) void translateTurn(turnId, text);
}

/** Turning Korean off stops all Korean work at once: running requests end and no "translating…" line stays behind. */
function dropKorean(): void {
  translations.abort(); translations = new AbortController();
  meetingKo.clear(); failedTurns.clear(); requestedTurns.clear(); replyKo.clear();
  pushes.clear(); pushedAtStart.clear();
}
/** Turning Korean (or captions) on translates what is already on screen, so the meanings do not wait for the next reply. */
function translateOnScreen(): void {
  const display = state.display;
  if (display?.complete && !replyKo.has(display.id)) void translateReply(display, true);
  if (!settings.showCaptions) return;
  for (const turn of state.turns.filter(value => value.final).slice(-recentTranslatedTurns))
    if (!requestedTurns.has(turn.id)) void translateTurn(turn.id, turn.text);
}
/** The Korean line shown under a heard turn; `undefined` means the turn has none (Korean off, or never requested). */
function turnKorean(turn: LiveTurn): string | undefined {
  if (!settings.showKorean) return undefined;
  if (!turn.final) return "듣는 중…";
  return meetingKo.get(turn.id) ?? (requestedTurns.has(turn.id) ? "한국어 번역 중…" : undefined);
}

// ---------- Focused reading view ----------

let pending = false;
/** One render per frame of the window that shows the panel; a timer covers a window that does not paint (hidden opener). */
function schedule(): void {
  if (pending) return;
  pending = true;
  const view = liveDocument.defaultView && !liveDocument.defaultView.closed ? liveDocument.defaultView : window;
  const run = (): void => { if (!pending) return; pending = false; render(); };
  view.requestAnimationFrame(run);
  view.setTimeout(run, 100);
}
let renderedConversation = "";

function write(element: HTMLElement, text: string): void {
  if (element.textContent !== text) element.textContent = text;
}
function setText(id: string, text: string): void { write($(id), text); }

/** What the error line says, most important first. */
function statusMessage(): string {
  const failure = controller.failure;
  if (failure) return liveMessage(failure, host);
  if (controller.phase === "reconnecting" && !controller.rollingOver)
    return reconnectText(controller.attempt, liveLimits.maxReconnects, controller.offline);
  if (infoMessage) return infoMessage;
  if (controller.warning) return captureMessage(controller.warning, controller.source, host);
  return controller.notice ? liveMessage(controller.notice, host) : "";
}

function render(): void {
  pending = false;
  const phase = controller.phase, live = phase === "live", reconnecting = phase === "reconnecting";
  const source = controller.active ? controller.source : settings.audioSource;
  const sourceLabel = source === "display" ? "탭/시스템 오디오" : "마이크";
  panel.dataset.capture = reconnecting ? "reconnecting" : live ? paused ? "paused" : "on" : "off";
  setText("capture-status", reconnecting
    ? controller.rollingOver ? `새 세션으로 이어서 듣는 중 · ${sourceLabel} 계속 켜짐`
      : controller.needsSignIn ? `로그인 필요 · ${sourceLabel} 계속 켜짐`
      : controller.offline ? `네트워크 대기 중 · ${sourceLabel} 계속 켜짐` : `재연결 중 · ${sourceLabel} 계속 켜짐`
    : live && paused ? `${sourceLabel} 일시정지 · 오디오 전송 중지`
    : live ? "듣는 중 · 영어 · 이전 대화 맥락 연결"
    : phase === "starting" ? source === "display" ? "공유할 화면 선택 중…" : "마이크 준비 중…"
    : phase === "connecting" ? "연결 중…" : `${sourceLabel} 꺼짐 · 우클릭으로 설정 및 시작`);
  setHidden("live-level", !controller.isCapturing);
  const message = statusMessage();
  setText("session-error", message); setHidden("session-error", !message);
  const action = currentAction();
  setHidden("live-action", !action);
  if (action) setText("live-action", actionLabels[action]);
  renderQuick();
  renderConversation();
  renderSuggestions();
  renderMenu();
}

function setPressed(id: string, pressed: boolean): void {
  const element = $(id), value = String(pressed);
  if (element.getAttribute("aria-pressed") !== value) element.setAttribute("aria-pressed", value);
}
function setHidden(id: string, hidden: boolean): void {
  const element = $(id);
  if (element.hidden !== hidden) element.hidden = hidden;
}
/** Quick settings and what they show or hide. Hidden parts keep their content, so switching one on shows it at once. */
function renderQuick(): void {
  const search = documentSearchOn(settings);
  setPressed("doc-on", search); setPressed("doc-off", !search);
  setPressed("toggle-captions", settings.showCaptions); setPressed("toggle-korean", settings.showKorean);
  setHidden("caption-block", !settings.showCaptions); setHidden("conversation-section", !settings.showCaptions);
  setHidden("answer-meaning", !settings.showKorean); setHidden("alternative-meaning", !settings.showKorean);
  setText("details-summary", settings.showCaptions ? "대화 · 대안 · 상세 근거" : "대안 · 상세 근거");
  if (responseMode.value !== settings.responseMode) responseMode.value = settings.responseMode;
  setText("quick-note", quickNote);
  setHidden("quick-note", !quickNote);
}

function renderConversation(): void {
  // Hidden captions are not rebuilt; switching them on renders the latest turns at once.
  if (!settings.showCaptions) return;
  const recent = state.turns.slice(-12);
  const signature = JSON.stringify(recent.map(turn => [turn.id, turn.revision, turn.final, turnKorean(turn) ?? null]));
  if (signature === renderedConversation) return;
  renderedConversation = signature;
  const conversation = $("conversation");
  if (!recent.length) {
    const waiting = document.createElement("p"); waiting.className = "live-waiting";
    waiting.textContent = "영어 대화를 기다리고 있어요.";
    conversation.replaceChildren(waiting); return;
  }
  conversation.replaceChildren(...recent.map((turn, index) => {
    const line = document.createElement("div");
    line.className = index === recent.length - 1 ? "live-line current" : "live-line";
    const en = document.createElement("p");
    en.className = "en"; en.lang = "en"; en.textContent = turn.text;
    line.append(en);
    const korean = turnKorean(turn);
    if (korean !== undefined) {
      const ko = document.createElement("p");
      ko.className = "ko"; ko.lang = "ko"; ko.textContent = korean;
      line.append(ko);
    }
    return line;
  }));
  conversation.scrollTop = conversation.scrollHeight;
}

const groundingBadges: Readonly<Record<Grounding, string>> = {
  grounded: "문서 인용 있음 · 내용 확인 필요", no_matches: "문서 근거 없음 · 사실 확인 필요",
  unavailable: "자료 확인 불가 · 사실 답변에 의존하지 마세요", disabled: "문서 검색 미사용 · 대화 맥락만 사용",
};
const groundingDetails: Readonly<Record<Grounding, string>> = {
  grounded: "근거 있음 · 내 업로드 문서", no_matches: "문서 근거 없음 · 사실 확인 필요",
  unavailable: "자료 확인 불가 · 문서 검색이 실패해 일반적인 답변만 보여 줍니다", disabled: "이번 답변은 문서 검색을 사용하지 않음",
};

function renderSuggestions(): void {
  const display = state.display, values = answers(display);
  const shown = state.shown;
  if (shown) latency.shownReply(shown.id, shown.turnId, shown.complete);
  const ko = display ? replyKo.get(display.id) ?? [] : [];
  setText("answer-text", values[0] ?? "답변이 완성되면 여기에 표시됩니다.");
  if (settings.showKorean) {
    setText("answer-korean", ko[0] ?? (display ? "한국어 번역 준비 중…" : "영어 추천 답변의 한국어 뜻이 함께 표시됩니다."));
    setText("alternative-korean", ko[1] ?? "한국어 번역 준비 중…");
  }
  setHidden("alternative-card", values.length < 2);
  setText("alternative-text", values[1] ?? "");
  const generating = controller.isReady && state.generating;
  const finishing = !!display && generating && state.current?.id === display.id;
  const previous = !!display && (state.hasNewerReply || generating && state.current?.id !== display.id);
  const myTurn = !previous && !!display?.respondNow;
  setText("primary-label", previous ? "이전 질문용 답변" : myTurn ? "지금 말할 추천" : "추천 영어 답변");
  panel.querySelector(".live-card.primary")!.classList.toggle("my-turn", myTurn);
  const answersSection = panel.querySelector<HTMLElement>(".live-answers")!;
  if (answersSection.dataset.previous !== String(previous)) answersSection.dataset.previous = String(previous);
  setText("question-text", display?.question ?? state.turns.find(turn => turn.id === display?.turnId)?.text
    ?? (display ? "이전 대화" : state.latest?.text ?? "영어 대화를 기다리고 있어요."));
  setText("reading-status", state.readingHeld
    ? state.hasNewerReply ? "새 답변 도착 · 고정을 해제하면 표시합니다"
      : generating ? "화면 고정됨 · 새 답변 준비 중"
        : "화면 고정됨 · 오디오 전송 상태는 위에서 확인하세요"
    : finishing ? "추천 문장입니다 · 대안과 근거를 확인하는 중"
      : generating ? display ? "새 답변 준비 중 · 아래는 이전 질문용 답변" : "답변 준비 중 · 완성되면 표시합니다"
        : display?.interrupted ? "답변 생성이 중단되었습니다 · 근거를 확인하지 못했습니다"
          : display ? "추천 문장입니다 · 내용 확인 후 사용하세요"
            : controller.active ? "듣는 중 · 답변이 완성되면 표시합니다" : "우클릭으로 설정 / 라이브 시작");
  const hold = $<HTMLButtonElement>("hold-answer");
  hold.disabled = !display;
  setPressed("hold-answer", state.readingHeld);
  write(hold, state.readingHeld ? state.hasNewerReply ? "새 답변 보기" : "고정 해제" : "답변 고정");
  setText("continuity", "추천은 실제로 말한 내용이 아닙니다. 화면 고정은 오디오 전송을 중지하지 않습니다.");
  const badge = $("grounding-badge");
  if (badge.dataset.grounding !== (display?.grounding ?? "")) badge.dataset.grounding = display?.grounding ?? "";
  write(badge, display?.grounding ? groundingBadges[display.grounding]
    : display?.interrupted ? "답변 중단 · 근거 확인 안 됨" : generating ? "근거 확인 중" : "근거 상태 대기");
  setText("grounding-text", display?.grounding ? groundingDetails[display.grounding] : "문서 근거 상태는 답변 완료 후 표시됩니다");
  const sources = display?.sources ?? [];
  setText("sources-text", sources.slice(0, 3).map(source => source.title).join(" · ") +
    (sources.length > 3 ? ` · 외 ${sources.length - 3}개` : ""));
}

function toggleReadingHold(): void {
  if (state.readingHeld) state.releaseDisplay(); else state.holdDisplay();
  render();
}
$("hold-answer").addEventListener("click", toggleReadingHold);

// ---------- Right-click session controls ----------

const item = (action: string): HTMLButtonElement => menu.querySelector<HTMLButtonElement>(`[data-action="${action}"]`)!;
function renderMenu(): void {
  item("start").disabled = controller.active;
  item("stop").disabled = !controller.active;
  item("pause").disabled = item("retry").disabled = !controller.isReady;
  write(item("pause"), paused ? "다시 듣기" : "내가 말할 동안 일시정지");
  item("hold").disabled = !state.display;
  write(item("hold"), state.readingHeld
    ? state.hasNewerReply ? "새 답변 보기 / 화면 고정 해제" : "화면 고정 해제"
    : "읽는 동안 답변 고정 (오디오는 계속 듣기)");
  item("translate").disabled = !settings.showKorean || !state.display?.complete && failedTurns.size === 0;
  item("float").hidden = !("documentPictureInPicture" in window);
  write(item("float"), pipWindow ? "작은 창 닫고 페이지로 돌아가기" : "항상 위 작은 창으로 띄우기");
}
function positionMenu(x: number, y: number): void {
  const bounds = panel.getBoundingClientRect();
  const left = Math.max(4, Math.min(x, bounds.width - menu.offsetWidth - 4));
  const top = Math.max(4, Math.min(y, bounds.height - menu.offsetHeight - 4));
  menu.style.left = `${left}px`; menu.style.top = `${top}px`;
}
new ResizeObserver(() => {
  if (!menu.hidden) positionMenu(parseFloat(menu.style.left), parseFloat(menu.style.top));
}).observe(panel);
function openMenu(x: number, y: number): void {
  renderMenu();
  menu.hidden = false;
  const bounds = panel.getBoundingClientRect();
  positionMenu(x - bounds.left, y - bounds.top);
  menu.querySelector<HTMLButtonElement>("button:not(:disabled)")?.focus();
  const owner = panel.ownerDocument;
  const close = (event: Event): void => {
    if (event.type === "keydown" && "key" in event && event.key !== "Escape") return;
    if (event.type === "pointerdown" && menu.contains(event.target as Node)) return;
    closeMenu();
    owner.removeEventListener("pointerdown", close, true); owner.removeEventListener("keydown", close, true);
  };
  owner.addEventListener("pointerdown", close, true); owner.addEventListener("keydown", close, true);
}
/** Keyboard focus returns to the panel, so the next Shift+F10 or Tab starts from there. */
function closeMenu(): void {
  const hadFocus = menu.contains(panel.ownerDocument.activeElement);
  menu.hidden = true;
  if (hadFocus) panel.focus({ preventScroll: true });
}
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
  if (action === "start") startLive();
  if (action === "stop") void stopLive();
  if (action === "pause") { paused = !paused; state.pause(paused); controller.pause(paused); render(); }
  if (action === "hold") toggleReadingHold();
  if (action === "retry") { state.beginRequest(); controller.request(); render(); }
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
  liveDocument = floating.document;
  pipWindow = floating;
  floating.addEventListener("pagehide", () => {
    stage.append(panel); liveDocument = document; pipWindow = undefined; closeMenu(); pending = false; render();
  });
  render();
}

// ---------- One live tab at a time (prevents two tabs from replacing each other's session) ----------

const liveChannel = "BroadcastChannel" in window ? new BroadcastChannel("live-coach") : undefined;
liveChannel?.addEventListener("message", event => {
  if (event.data === "started" && controller.active) void stopLive("다른 탭에서 라이브를 시작해 이 탭은 멈췄습니다.");
});
window.addEventListener("pagehide", () => { void controller.stop(); });

if (debug) Object.assign(window, { __liveCoachDebug: { latency: latency.records, stats: controller.stats } });

// Tests and hosts wait for this flag: after it, the page only changes in response to events.
void auth.initialize().then(() => render(), error => {
  infoMessage = `서비스 설정을 불러오지 못했습니다: ${asError(error).message}`; render();
}).finally(() => { document.documentElement.dataset.liveReady = "1"; });
render();
