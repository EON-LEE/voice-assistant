import "./live.css";
import workletUrl from "./audio.worklet.ts?worker&url";
import { BrowserAuth } from "./auth.js";
import { CaptureError, DisplayAudioSource, MicrophoneAudioSource, isHostSource, type AudioSource, type CaptureErrorKind,
  type CaptureSourceKind } from "./capture.js";
import { HostAudioSource, hostKind, hostWebView, isHostAudioAvailable } from "./host-audio.js";
import { SocketTransport, asError } from "./transport.js";
import type { SessionOptions, SessionUpdate } from "./options.js";
import { CoachClient, CoachError, type CoachKind } from "./coach-client.js";
import type { Grounding, ServerEvent } from "./protocol.js";
import { LiveState, answers, leadSentence, meaningTexts, primaryMeaning, suggestionLabels, translatable, translationChunks,
  type LiveReply, type LiveTurn } from "./live-state.js";
import { PushedKorean, pushedKoreanWaitMs } from "./reply-korean.js";
import { acknowledgedSettings, deferredUpdateNote, idleStatus, keptNotes, liveStartOptions, notesChipName, parseSettings, pendingUpdate,
  settingsKey, stepAnswerSize, withDocumentSearch, withResponseMode, documentSearchOn, type AppliedSettings, type HostSources,
  type LiveSettings, type ResponseMode } from "./live-settings.js";
import { LiveController, liveLimits, type SourceHooks } from "./live-controller.js";
import { actionLabels, captureMessage, liveHost, liveMessage, reconnectText, sourceNames, warningMessage, type LiveWarning,
  type PageAction } from "./live-errors.js";
import { LatencyMarks, debugEnabled } from "./live-latency.js";

let liveDocument = document;
const $ = <T extends HTMLElement = HTMLElement>(id: string): T => {
  const value = document.getElementById(id) ?? liveDocument.getElementById(id);
  if (!value) throw new Error(`Missing #${id}`);
  return value as T;
};
const panel = $("live-panel"), stage = $("live-stage"), menu = $("live-menu");
const toggleButton = $<HTMLButtonElement>("live-toggle"), pauseButton = $<HTMLButtonElement>("live-pause");
const smallerButton = $<HTMLButtonElement>("answer-smaller"), largerButton = $<HTMLButtonElement>("answer-larger");
const settingsDialog = $<HTMLDialogElement>("settings");
const micDevice = $<HTMLSelectElement>("mic-device"), responseMode = $<HTMLSelectElement>("response-mode");
const topic = $<HTMLInputElement>("topic"), notesField = $<HTMLTextAreaElement>("my-notes");
const audioSource = $<HTMLSelectElement>("audio-source");
const microphoneSettings = $("microphone-settings"), displayHint = $("display-hint");
/** LiveCoach.exe (WebView2) or a browser tab: the share picker offers different surfaces in each. */
const host = liveHost(window);
/** What LiveCoach.exe captures natively for this page: Teams app audio, its PC (default output) audio, or nothing. */
const offered: HostSources = { teams: isHostAudioAvailable("teams"), system: isHostAudioAvailable("system") };
for (const [value, available] of [["teams", offered.teams], ["host-system", offered.system]] as const) {
  const option = audioSource.querySelector<HTMLOptionElement>(`option[value="${value}"]`);
  if (option) option.hidden = option.disabled = !available;
}

const auth = new BrowserAuth(undefined, { persistent: true });
const coach = new CoachClient((path, init) => auth.coachRequest(path, init), () => auth.isFake);
const state = new LiveState();

let paused = false;
/** One-off notes (sign-in results, another tab took over); cleared by the next start. */
let infoMessage = "";
/** Signed in first from a Start click: sharing needs a click of its own, offered inline. */
let startOffered = false;
/** Whether that offered start continues the conversation on screen. */
let offeredResume = false;
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
let applied: AppliedSettings = { responseMode: "grounded", replyKorean: false, replyOn: "all", notes: "" };
let started: Omit<AppliedSettings, "replyKorean"> = { responseMode: "grounded", replyOn: "all", notes: "" };
let canUpdate = false, updating = false;
/** The `session.update` waiting for its `session.updated`, which reports only whether notes are set. */
let sentUpdate: SessionUpdate | null = null;
let quickNote = "";
const recentTranslatedTurns = 6;

function storage(): Storage | undefined {
  try { return localStorage; } catch { return undefined; }
}
const debug = debugEnabled(location.search, storage());
const latency = new LatencyMarks(() => performance.now(), debug ? name => { performance.mark(name); } : undefined);

function loadSettings(): LiveSettings {
  try { return parseSettings(localStorage.getItem(settingsKey), offered); } catch { return parseSettings(null, offered); }
}
let settings = loadSettings();
responseMode.value = settings.responseMode; topic.value = settings.topic; audioSource.value = settings.audioSource;
notesField.value = settings.notes;
if (host === "app") {
  // WebView2 has no browser tabs to pick; only a whole screen carries (system) audio there.
  displayHint.textContent = "라이브 시작 시 뜨는 공유 선택창에서 '전체 화면'을 고르고 '시스템 오디오 공유'를 체크하세요. "
    + "창(Window) 공유에는 소리가 없습니다. 시스템 오디오는 Windows 기본 출력 장치의 소리만 가져옵니다."
    + (offered.teams ? " Teams 데스크톱 앱이면 'Teams 앱 소리'가 더 정확합니다." : "");
}
function renderAudioSource(): void {
  const kind = audioSource.value;
  microphoneSettings.hidden = kind !== "microphone"; displayHint.hidden = kind !== "display";
  $("teams-hint").hidden = kind !== "teams"; $("host-system-hint").hidden = kind !== "host-system";
}
renderAudioSource();
function persist(): void {
  try { localStorage.setItem(settingsKey, JSON.stringify(settings)); } catch { /* storage unavailable: the choice lasts until the page closes */ }
}
function selectedSource(): CaptureSourceKind {
  const value = audioSource.value;
  return value === "display" || value === "teams" || value === "host-system" ? value : "microphone";
}
function saveSettings(): void {
  settings = { ...settings, deviceId: micDevice.value, topic: topic.value.trim(), audioSource: selectedSource(),
    notes: keptNotes(notesField.value) };
  // The field shows the notes as they are kept and sent: on one line, without extra spaces.
  if (notesField.value !== settings.notes) notesField.value = settings.notes;
  persist();
}
micDevice.addEventListener("change", saveSettings);
topic.addEventListener("change", saveSettings);
// Notes reach a connected session at once; the next reply follows them.
notesField.addEventListener("change", () => { saveSettings(); syncSession(); render(); });
audioSource.addEventListener("change", () => {
  renderAudioSource(); saveSettings();
  // Choosing the source in use again also cancels a switch that is still being prepared.
  if (controller.active && (settings.audioSource !== controller.source || controller.switching)) changeLiveSource(settings.audioSource);
});

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
$("reply-all").addEventListener("click", () => changeSettings({ ...settings, replyOn: "all" }));
$("reply-questions").addEventListener("click", () => changeSettings({ ...settings, replyOn: "questions" }));
$("toggle-captions").addEventListener("click", () => changeSettings({ ...settings, showCaptions: !settings.showCaptions }));
$("toggle-korean").addEventListener("click", () => changeSettings({ ...settings, showKorean: !settings.showKorean }));
$("notes-chip").addEventListener("click", () => openSettings(notesField));
responseMode.addEventListener("change", () => changeSettings(withResponseMode(settings, responseMode.value as ResponseMode)));

/** A− / A+ step the English answer size. At the smallest or largest size the pressed button turns off, so focus moves to the other. */
function resizeAnswer(step: -1 | 1): void {
  const button = step < 0 ? smallerButton : largerButton;
  const focused = button.ownerDocument.activeElement === button;
  changeSettings({ ...settings, answerSize: stepAnswerSize(settings.answerSize, step) });
  if (focused && button.disabled) (step < 0 ? largerButton : smallerButton).focus({ preventScroll: true });
}
smallerButton.addEventListener("click", () => resizeAnswer(-1));
largerButton.addEventListener("click", () => resizeAnswer(1));

/** Makes the connected session match the settings. A change reaches the next reply; a reply already under way keeps its own. */
function syncSession(): void {
  if (!controller.isReady) return;
  const update = pendingUpdate(applied, settings);
  if (!update) { quickNote = ""; return; }
  if (!canUpdate) {
    // Korean still works without the server: the client translates it itself. Document search and replyOn need the server.
    quickNote = deferredUpdateNote(update);
    return;
  }
  quickNote = "";
  if (updating) return;
  controller.update(update);
  updating = true; sentUpdate = update;
}
/** Forgets what the last connection accepted; the next `session.ready` says it again. */
function resetUpdate(): void { canUpdate = updating = false; sentUpdate = null; quickNote = ""; }

function sessionOptions(): SessionOptions {
  const options = liveStartOptions(settings);
  started = { responseMode: options.responseMode, replyOn: options.replyOn ?? "all", notes: options.notes ?? "" };
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
  if (isHostSource(kind)) {
    const webview = hostWebView();
    if (!webview) throw new CaptureError("unsupported", kind, "LiveCoach audio is not available here.");
    return new HostAudioSource({ webview, source: hostKind(kind), onWarning: hooks.warning, onLevel: hooks.level,
      onGap: missing => hooks.stall(missing) });
  }
  const warning = (_message: string, warned?: CaptureErrorKind): void => hooks.warning(warned === "muted" ? "muted" : null);
  const shared = {
    context: () => new AudioContext(),
    node: (context: AudioContext) => new AudioWorkletNode(context, "meeting-pcm", { numberOfInputs: 1, numberOfOutputs: 1, outputChannelCount: [1] }),
    workletUrl, onStall: hooks.stall, onLevel: hooks.level, onWarning: warning,
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
  onLevel: showLevel,
});

// ---------- Live session lifecycle ----------

function resetConversation(): void {
  translations.abort(); translations = new AbortController();
  state.resetSession(); meetingKo.clear(); failedTurns.clear(); requestedTurns.clear(); replyKo.clear(); pushes.clear();
  pushedAtStart.clear();
}

/** Whether this page can capture `kind` here at all: a browser API, or LiveCoach.exe offering it. */
function sourceAvailable(kind: CaptureSourceKind): boolean {
  switch (kind) {
    case "display": return !!navigator.mediaDevices?.getDisplayMedia;
    case "microphone": return !!navigator.mediaDevices?.getUserMedia;
    case "teams": case "host-system": return isHostAudioAvailable(hostKind(kind));
  }
}

/**
 * Starts from a click, so the share picker (or microphone prompt) opens inside it. `resume` keeps the conversation on
 * screen ("다시 공유" / "다시 시작"). Reconnects never come back here: they keep the source and only reconnect the server.
 */
function startLive(resume = false): void {
  if (controller.active) return;
  const kind = settings.audioSource;
  infoMessage = ""; startOffered = false;
  if (!sourceAvailable(kind)) {
    infoMessage = captureMessage("unsupported", kind, host);
    render(); return;
  }
  // Without a signed-in account, sign in from this click first; a share picker then needs a click of its own.
  if (auth.configured && !auth.isFake && !auth.signedIn) { void signInFirst(resume); return; }
  // Continuing keeps the conversation, but an old pause or an unfinished reply would hide every new reply.
  if (resume) state.restart(true); else resetConversation();
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
  offerStart(resume, "로그인되었습니다. '라이브 시작'을 눌러 공유할 화면을 고르세요.");
}

/** After an await, only a share picker needs the user's next click; the microphone and LiveCoach audio start by themselves. */
function offerStart(resume: boolean, message: string): void {
  if (settings.audioSource !== "display") { startLive(resume); return; }
  startOffered = true; offeredResume = resume;
  infoMessage = message;
  render();
}

async function signInAndResume(): Promise<void> {
  try { await auth.signIn(); }
  catch (error) { infoMessage = `로그인 실패: ${asError(error).message}`; render(); return; }
  infoMessage = "";
  if (controller.needsSignIn) controller.resume();
  else if (!controller.active) { offerStart(true, "로그인되었습니다. '라이브 시작'을 눌러 다시 공유하세요."); return; }
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

/** Warnings that 'PC 전체 소리' can fix while Teams app audio is used. */
const switchableWarnings: ReadonlySet<LiveWarning> = new Set(["teams-silent", "capture-error", "silent"]);
/** The step the inline button offers, if any: a recovery step, a start that needs its own click, or the switch to PC audio. */
function currentAction(): PageAction | undefined {
  if (startOffered && !controller.active) return "start";
  if (controller.failure) return controller.failure.action;
  if (controller.active && controller.source === "teams" && offered.system && !controller.switching && controller.warning
    && switchableWarnings.has(controller.warning)) return "switch-system";
  return undefined;
}
$("live-action").addEventListener("click", () => {
  const action = currentAction();
  if (action === "signin") void signInAndResume();
  else if (action === "start") startLive(offeredResume);
  else if (action === "reshare" || action === "restart") startLive(true);
  else if (action === "switch-system") {
    settings = { ...settings, audioSource: "host-system" }; persist();
    audioSource.value = "host-system"; renderAudioSource();
    changeLiveSource("host-system");
  }
});

/**
 * A source chosen while live replaces the running one on the same connection. A share needs a picker, which only a
 * click may open, so that choice waits for the next start (the settings dialog says so).
 */
function changeLiveSource(kind: CaptureSourceKind): void {
  if (kind !== "display") controller.switchSource(kind);
  render();
}

/** A dropped or replaced connection: replies in flight end, the next `session.ready` restates the capabilities. */
function onDisconnect(): void {
  pushing = false; pushes.expire(); resetUpdate();
  state.endReply();
  state.clearError();
  schedule();
}

function onEvent(e: ServerEvent): void {
  // With Korean off nothing is recorded, even while the server has not yet stopped sending it.
  if (e.type === "response.korean") { if (settings.showKorean) { pushes.record(e); schedule(); } return; }
  state.apply(e);
  if (e.type === "session.ready") {
    pushing = e.replyKorean === true; canUpdate = e.sessionUpdate === true; updating = false;
    applied = { ...started, replyKorean: pushing };
    // A setting may have changed between connecting and being ready.
    syncSession();
  }
  if (e.type === "session.updated") {
    applied = acknowledgedSettings(applied, e, sentUpdate);
    pushing = applied.replyKorean; updating = false; sentUpdate = null;
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
  if (translatable(display)) void translateReply(display, true);
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
  if (translatable(display) && !replyKo.has(display.id)) void translateReply(display, true);
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
/** 추천 1 with its first sentence set apart (see leadSentence); the element's text stays exactly the line. */
function writeAnswer(element: HTMLElement, text: string, reply: boolean): void {
  if (element.textContent === text) return;
  if (!reply) { element.textContent = text; return; }
  const span = (className: string, value: string): HTMLSpanElement => {
    const part = element.ownerDocument.createElement("span");
    part.className = className; part.textContent = value;
    return part;
  };
  const [lead, rest] = leadSentence(text);
  element.replaceChildren(span("live-lead", lead), ...rest ? [span("live-rest", rest)] : []);
}
/** The reply whose arrival on screen the first card last marked. */
let markedReply: string | undefined;

/** What the error line says, most important first. */
function statusMessage(): string {
  const failure = controller.failure;
  if (failure) return liveMessage(failure, host);
  if (controller.phase === "reconnecting" && !controller.rollingOver)
    return reconnectText(controller.attempt, liveLimits.maxReconnects, controller.offline);
  if (infoMessage) return infoMessage;
  if (controller.warning) return warningMessage(controller.warning, controller.source, host, controller.warningDetail);
  return controller.notice ? liveMessage(controller.notice, host) : "";
}

function render(): void {
  pending = false;
  const phase = controller.phase, live = phase === "live", reconnecting = phase === "reconnecting";
  const source = controller.active ? controller.source : settings.audioSource;
  const sourceLabel = sourceNames[source], switching = controller.switching;
  panel.dataset.capture = reconnecting ? "reconnecting" : live ? paused ? "paused" : "on" : "off";
  setText("capture-status", reconnecting
    ? controller.rollingOver ? `새 세션으로 이어서 듣는 중 · ${sourceLabel} 계속 켜짐`
      : controller.needsSignIn ? `로그인 필요 · ${sourceLabel} 계속 켜짐`
      : controller.offline ? `네트워크 대기 중 · ${sourceLabel} 계속 켜짐` : `재연결 중 · ${sourceLabel} 계속 켜짐`
    : live && switching ? `${sourceNames[switching]}로 전환 중…`
    : live && paused ? `${sourceLabel} 일시정지 · 오디오 전송 중지`
    : live ? `듣는 중 · ${sourceLabel} · 영어`
    : phase === "starting" ? source === "display" ? "공유할 화면 선택 중…" : isHostSource(source) ? `${sourceLabel} 연결 중…` : "마이크 준비 중…"
    : phase === "connecting" ? "연결 중…" : `${sourceLabel} 꺼짐 · 시작을 누르세요`);
  setHidden("live-level", !controller.isCapturing);
  setHidden("source-note", !(controller.active && settings.audioSource === "display" && controller.source !== "display"));
  const message = statusMessage();
  setText("session-error", message); setHidden("session-error", !message);
  const action = currentAction();
  setHidden("live-action", !action);
  if (action) setText("live-action", actionLabels[action]);
  renderQuick();
  renderConversation();
  renderSuggestions();
  renderControls();
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
  setPressed("reply-all", settings.replyOn === "all"); setPressed("reply-questions", settings.replyOn === "questions");
  setPressed("toggle-captions", settings.showCaptions); setPressed("toggle-korean", settings.showKorean);
  // 내 메모 opens the notes and toggles nothing: whether they are set is in its name and its ✓ (data-set), not aria-pressed.
  const notesChip = $("notes-chip"), notesSet = String(settings.notes.length > 0), notesName = notesChipName(settings.notes);
  if (notesChip.dataset.set !== notesSet) notesChip.dataset.set = notesSet;
  if (notesChip.getAttribute("aria-label") !== notesName) notesChip.setAttribute("aria-label", notesName);
  setHidden("caption-block", !settings.showCaptions); setHidden("conversation-section", !settings.showCaptions);
  setHidden("answer-meaning", !settings.showKorean); setHidden("alternative-meaning", !settings.showKorean);
  setText("details-summary", settings.showCaptions ? "대화 · 상세 근거" : "상세 근거");
  if (responseMode.value !== settings.responseMode) responseMode.value = settings.responseMode;
  // The default size sets no property, so the answer keeps exactly its stylesheet size.
  const scale = settings.answerSize === 100 ? "" : String(settings.answerSize / 100);
  if (panel.style.getPropertyValue("--answer-scale") !== scale) panel.style.setProperty("--answer-scale", scale);
  smallerButton.disabled = stepAnswerSize(settings.answerSize, -1) === settings.answerSize;
  largerButton.disabled = stepAnswerSize(settings.answerSize, 1) === settings.answerSize;
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
  const answerText = $("answer-text");
  writeAnswer(answerText, values[0] ?? "답변이 완성되면 여기에 표시됩니다.", values[0] !== undefined);
  // A new answer on screen glows once, so a glance tells it from the one read before; no motion if the user asked for less.
  if (display && display.id !== markedReply) {
    markedReply = display.id;
    const card = answerText.closest<HTMLElement>(".live-card"), view = answerText.ownerDocument.defaultView;
    if (card && view && !view.matchMedia("(prefers-reduced-motion: reduce)").matches)
      card.animate([{ boxShadow: "0 0 0 3px rgba(143, 193, 255, .9)" }, { boxShadow: "0 0 0 0 rgba(143, 193, 255, 0)" }],
        { duration: 1000, easing: "ease-out" });
  }
  if (settings.showKorean) {
    setText("answer-korean", primaryMeaning(display, ko[0], display ? pushes.peek(display.id, 0) : undefined));
    setText("alternative-korean", ko[1] ?? meaningTexts.preparing);
  }
  setHidden("alternative-card", values.length < 2);
  setText("alternative-text", values[1] ?? "");
  const generating = controller.isReady && state.generating;
  const finishing = !!display && generating && state.current?.id === display.id;
  const previous = state.showsEarlierAnswer(controller.isReady);
  // Nothing emphasizes either answer: the server's respondNow guess is not shown.
  const labels = suggestionLabels(previous);
  setText("primary-label", labels.primary);
  setText("alternative-label", labels.alternative);
  const answersSection = panel.querySelector<HTMLElement>(".live-answers")!;
  if (answersSection.dataset.previous !== String(previous)) answersSection.dataset.previous = String(previous);
  // What the reply answers: every final gathered into it (see LiveState), or the latest speech before any reply.
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
            : controller.active ? "듣는 중 · 답변이 완성되면 표시합니다" : idleStatus(settings));
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

// ---------- Session controls: header buttons and the right-click menu ----------

const item = (action: string): HTMLButtonElement => menu.querySelector<HTMLButtonElement>(`[data-action="${action}"]`)!;
function setTitle(element: HTMLElement, title: string): void {
  if (element.title !== title) element.title = title;
}
/** The menu, and the header's start/stop and pause buttons, which run the same actions with the same states as their items. */
function renderControls(): void {
  const active = controller.active, ready = controller.isReady;
  item("start").disabled = active;
  item("stop").disabled = !active;
  item("pause").disabled = item("retry").disabled = !ready;
  write(item("pause"), paused ? "다시 듣기" : "내가 말할 동안 일시정지");
  const command = active ? "stop" : "start";
  if (toggleButton.dataset.command !== command) toggleButton.dataset.command = command;
  write(toggleButton, active ? "종료" : "시작");
  setTitle(toggleButton, item(command).textContent ?? "");
  pauseButton.disabled = !ready;
  if (pauseButton.dataset.paused !== String(paused)) pauseButton.dataset.paused = String(paused);
  write(pauseButton, paused ? "다시 듣기" : "일시정지");
  setTitle(pauseButton, item("pause").textContent ?? "");
  item("hold").disabled = !state.display;
  write(item("hold"), state.readingHeld
    ? state.hasNewerReply ? "새 답변 보기 / 화면 고정 해제" : "화면 고정 해제"
    : "읽는 동안 답변 고정 (오디오는 계속 듣기)");
  item("translate").disabled = !settings.showKorean || !translatable(state.display) && failedTurns.size === 0;
  item("float").hidden = !("documentPictureInPicture" in window);
  write(item("float"), pipWindow ? "작은 창 닫고 페이지로 돌아가기" : "항상 위 작은 창으로 띄우기");
}
/** What a menu item or a header button does. Start runs inside the click, so a share picker may open. */
function runAction(action: string): void {
  if (action === "settings") openSettings();
  if (action === "start") startLive();
  if (action === "stop") void stopLive();
  if (action === "pause") { paused = !paused; state.pause(paused); controller.pause(paused); render(); }
  if (action === "hold") toggleReadingHold();
  if (action === "retry") { state.beginRequest(); controller.request(); render(); }
  if (action === "translate") retryTranslations();
  if (action === "float") void toggleFloat();
}
// The toggle runs what its label says, even if the session changed in the frame before the label caught up.
toggleButton.addEventListener("click", () => runAction(toggleButton.dataset.command === "stop" ? "stop" : "start"));
pauseButton.addEventListener("click", () => runAction("pause"));

/** Whether the element can hold keyboard focus now: still in the panel, shown and enabled. */
function canFocus(element: Element | null): element is HTMLElement {
  return element instanceof HTMLElement && panel.contains(element) && !element.matches(":disabled") && element.getClientRects().length > 0;
}
/**
 * Focus goes back to the element that opened the menu or the settings dialog, or to the panel when that element is gone.
 * Only closing them calls this: streaming updates never move focus, which may be in Teams or PowerPoint.
 */
function returnFocus(opener: Element | null): void {
  (canFocus(opener) ? opener : panel).focus({ preventScroll: true });
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
/** The items that can take focus: enabled and shown (the WebView2 host hides the floating-window item with CSS). */
function menuItems(): HTMLButtonElement[] {
  return [...menu.querySelectorAll<HTMLButtonElement>("button")].filter(button => !button.disabled && button.getClientRects().length > 0);
}
/** While the menu is open: the element it was opened from, and how to stop listening for what closes it. */
let openedMenu: { opener: Element | null; release(): void } | undefined;
function openMenu(x: number, y: number): void {
  renderControls();
  menu.hidden = false;
  const bounds = panel.getBoundingClientRect();
  positionMenu(x - bounds.left, y - bounds.top);
  if (!openedMenu) {
    const owner = panel.ownerDocument, opener = owner.activeElement;
    const onKey = (event: KeyboardEvent): void => { if (event.key === "Escape") closeMenu(); };
    const onPointer = (event: PointerEvent): void => {
      if (menu.contains(event.target as Node)) return;
      closeMenu();
      // A click on another control focuses that control; a click on anything else would leave focus nowhere.
      owner.defaultView?.setTimeout(() => {
        const active = owner.activeElement;
        if (menu.hidden && (!active || active === owner.body || active === panel)) returnFocus(opener);
      });
    };
    owner.addEventListener("pointerdown", onPointer, true); owner.addEventListener("keydown", onKey, true);
    openedMenu = { opener, release: () => {
      owner.removeEventListener("pointerdown", onPointer, true); owner.removeEventListener("keydown", onKey, true);
    } };
  }
  menuItems()[0]?.focus();
}
/** Hides the menu; focus returns to where it was opened from (Escape, a choice or a click outside), unless `refocus` is false. */
function closeMenu(refocus = true): void {
  menu.hidden = true;
  const opened = openedMenu;
  if (!opened) return;
  openedMenu = undefined;
  opened.release();
  if (refocus) returnFocus(opened.opener);
}
// Right-click, or Shift+F10 / the menu key on the focused panel or one of its buttons. Inside the open menu it stays as it is.
panel.addEventListener("contextmenu", event => {
  event.preventDefault();
  if (!menu.contains(event.target as Node)) openMenu(event.clientX, event.clientY);
});
menu.addEventListener("keydown", event => {
  // Tab leaves the menu from where it was opened.
  if (event.key === "Tab") { closeMenu(); return; }
  const items = menuItems(), last = items.length - 1;
  const index = items.indexOf(panel.ownerDocument.activeElement as HTMLButtonElement);
  const next = event.key === "ArrowDown" ? index < last ? index + 1 : 0
    : event.key === "ArrowUp" ? index > 0 ? index - 1 : last
    : event.key === "Home" ? 0 : event.key === "End" ? last : undefined;
  if (next === undefined) return;
  event.preventDefault();
  items[next]?.focus();
});
menu.addEventListener("click", event => {
  const button = (event.target as HTMLElement).closest<HTMLButtonElement>("button");
  if (!button) return;
  const opener = openedMenu?.opener ?? null;
  // Every item closes the menu; the items LiveCoach.exe adds (opacity, minimize, exit) run their own action.
  closeMenu();
  if (button.dataset.action) runAction(button.dataset.action);
  // A choice can turn off the button the menu was opened from (stop turns off pause): focus then goes to the panel.
  const focused = panel.ownerDocument.activeElement;
  if (focused === opener ? !canFocus(opener) : !focused || focused === panel.ownerDocument.body) returnFocus(opener);
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
/** The element the settings dialog was opened from; focus returns to it when the dialog closes. */
let settingsOpener: Element | null = null;
/** Opens the settings dialog, with focus on `field` when one is given (the "내 메모" chip opens the notes). */
function openSettings(field?: HTMLElement): void {
  settingsOpener = panel.ownerDocument.activeElement;
  if (pipWindow) window.focus();
  renderAccount();
  void navigator.mediaDevices?.enumerateDevices().then(fillDevices).catch(() => undefined);
  settingsDialog.showModal();
  field?.focus();
}
$("signin").addEventListener("click", () => {
  void auth.signIn().then(renderAccount, error => { $("account-status").textContent = `로그인 실패: ${asError(error).message}`; });
});
settingsDialog.addEventListener("close", () => { saveSettings(); syncSession(); render(); returnFocus(settingsOpener); });

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
    stage.append(panel); liveDocument = document; pipWindow = undefined; closeMenu(false); pending = false; render();
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
