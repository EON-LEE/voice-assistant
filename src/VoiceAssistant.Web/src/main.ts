import "./style.css";
import "./coach.css";
import "./overlay.css";
import workletUrl from "./audio.worklet.ts?worker&url";
import demoAudioUrl from "./assets/demo-original.wav?url";
import { BrowserAuth } from "./auth.js";
import { DemoAudioSource } from "./demo.js";
import { DisplayAudioSource, MicrophoneAudioSource, SyntheticSource } from "./capture.js";
import { MeetingSession } from "./session.js";
import { ReplyState, type Reply } from "./state.js";
import { DemoTransport, SocketTransport, asError, isLoopback } from "./transport.js";
import { createStartMessage, parsePhrases, validateOptions, type SessionOptions } from "./options.js";
import { OwnerWindowClock, RenderScheduler } from "./render-scheduler.js";
import { Overlay } from "./overlay.js";
import { referenceText } from "./materials.js";
import { CoachClient, type Rate } from "./coach-client.js";
import { CoachAudio } from "./coach-audio.js";
import { EnrichmentRequests, renderEnrichment } from "./enrichment.js";
import { PracticeUI } from "./practice-ui.js";

const overlay = new Overlay(document.querySelector<HTMLElement>(".coach-shell")!);
function element<T extends HTMLElement>(id: string): T { return overlay.get<T>(id); }
const mode = element<HTMLSelectElement>("mode");
const start = element<HTMLButtonElement>("start");
const stop = element<HTMLButtonElement>("stop");
const pause = element<HTMLInputElement>("pause");
const suggest = element<HTMLButtonElement>("suggest");
const cancel = element<HTMLButtonElement>("cancel");
const pin = element<HTMLButtonElement>("pin");
const consent = element<HTMLInputElement>("consent");
const signin = element<HTMLButtonElement>("signin");
const audioSource = element<HTMLSelectElement>("audio-source");
const micDevice = element<HTMLSelectElement>("microphone-device");
const state = new ReplyState();
const auth = new BrowserAuth();
let session: MeetingSession | null = null;
let configReady = false;
let fake = false;
let configGeneration = 0;
let transcriptDirty = true;
let renderedReply: Reply | null | undefined;
let renderedPinned: Reply | null | undefined;
const scheduler = new RenderScheduler(renderContent, new OwnerWindowClock(() => overlay.ownerWindow));
let coachMode: "meeting" | "practice" = "meeting";
let elapsedStarted = 0;
let elapsedTimer: number | undefined;
let elapsedWindow = overlay.ownerWindow;
let clockActive = false;
let assistTurn: string | undefined;
const coachClient = new CoachClient((path, init) => auth.coachRequest(path, init), () => auth.isFake);
const clip = new CoachAudio(coachClient, new Audio(), audio => {
  element("audio-status").textContent = audio.message;
  element<HTMLButtonElement>("stop-listening").disabled = !["loading", "playing"].includes(audio.state);
  element<HTMLButtonElement>("reply-listen").textContent = audio.key === "meeting-reply" && audio.state === "loading"
    ? "Preparing audio…" : audio.key === "meeting-reply" && audio.state === "playing" ? "Playing · 재생 중" : "◖ Click to listen · 듣기";
});
const enrichment = new EnrichmentRequests(coachClient, (kind, value) => {
  if (kind === "question") renderEnrichment(element("question-ko"), null, value);
  else {
    renderEnrichment(element("reply-ko"), element("reply-pronunciation"), value);
    element("pronunciation-note").hidden = !value?.pronunciation?.length;
  }
});
const practice = new PracticeUI(auth, clip, workletUrl, (active, status) => {
  if (coachMode === "practice") {
    const starting = active && !clockActive;
    updateCoachActivity(active, status);
    if (starting) overlay.close();
  }
}, overlay.card);
function tickElapsed(): void {
  const seconds = Math.floor((Date.now() - elapsedStarted) / 1000);
  element("elapsed").textContent = `${String(Math.floor(seconds / 60)).padStart(2, "0")}:${String(seconds % 60).padStart(2, "0")}`;
}
overlay.onWindowChanged(() => {
  scheduler.flush();
  if (elapsedTimer !== undefined) elapsedWindow.clearInterval(elapsedTimer);
  elapsedWindow = overlay.ownerWindow;
  elapsedTimer = clockActive ? elapsedWindow.setInterval(tickElapsed, 1000) : undefined;
  if (clockActive) tickElapsed();
});
function updateCoachActivity(active: boolean, status: string): void {
  const toggle = element<HTMLButtonElement>("coach-toggle");
  toggle.textContent = active ? "■" : "▶"; toggle.setAttribute("aria-label", active ? "Stop session" : "Start session");
  element("coach-status").textContent = status;
  if (active && !clockActive) {
    elapsedStarted = Date.now(); element("elapsed").textContent = "00:00";
    elapsedWindow = overlay.ownerWindow; elapsedTimer = elapsedWindow.setInterval(tickElapsed, 1000);
  } else if (!active && elapsedTimer !== undefined) { elapsedWindow.clearInterval(elapsedTimer); elapsedTimer = undefined; }
  clockActive = active;
  overlay.card.classList.toggle("session-active", active);
}
for (const id of ["mode", "start", "stop", "suggest", "pause", "pin", "transcript", "reply", "status", "error", "consent", "signin"])
  element(id).dataset.testid = id;
element("pinned").dataset.testid = "pinned-reply";
if (!isLoopback(location.hostname)) mode.querySelector<HTMLOptionElement>('option[value="synthetic"]')!.remove();

function error(message: string): void {
  element("error").textContent = message;
  element("error").hidden = !message;
  overlay.reportError(message);
}
function canSuggest(): boolean {
  return !!session?.isReady && !pause.checked && state.turns.at(-1)?.final === true;
}
function readOptions(): SessionOptions {
  return validateOptions({
    responseMode: element<HTMLSelectElement>("response-mode").value as SessionOptions["responseMode"],
    profile: { name: element<HTMLInputElement>("profile-name").value, role: element<HTMLInputElement>("profile-role").value,
      project: element<HTMLTextAreaElement>("profile-project").value },
    profileConfirmed: element<HTMLInputElement>("profile-confirmed").checked,
    topic: element<HTMLTextAreaElement>("meeting-topic").value,
    phrases: parsePhrases(element<HTMLTextAreaElement>("meeting-phrases").value),
    endSilenceMs: Number(element<HTMLSelectElement>("end-silence").value),
  });
}
function sourceList(id: string, reply: Reply | null): void {
  const list = element(id);
  list.replaceChildren();
  if (reply?.complete) {
    const line = document.createElement("li");
    const grounding = { grounded: "Relevant reference candidates - verify support before relying on them",
      disabled: reply.responseRoute ? "Reference search disabled" : "Reference search disabled - reply uses the transcript only",
      unavailable: "Reference search unavailable", no_matches: "No relevant references - reply uses the transcript only" };
    line.textContent = reply.grounding ? grounding[reply.grounding] : "Reference status not supplied";
    list.append(line);
    if (reply.responseRoute) {
      const route = document.createElement("li");
      route.textContent = reply.responseRoute === "knowledge" && reply.grounding === "no_matches"
        ? "Knowledge lookup completed without supporting reference candidates"
        : { transcript: "Reply context: meeting transcript", profile: "Composed from confirmed profile (not model-generated)",
          knowledge: "Reply context: knowledge retrieval" }[reply.responseRoute];
      list.append(route);
    }
    if (reply.retrievalPrefetched !== undefined) {
      const prefetch = document.createElement("li");
      prefetch.textContent = reply.retrievalPrefetched ? "Retrieval was prefetched" : "Retrieval was not prefetched";
      list.append(prefetch);
    }
  }
  for (const source of reply?.sources ?? []) {
    const item = document.createElement("li");
    item.textContent = referenceText(source);
    list.append(item);
  }
}
function renderContent(): void {
  if (transcriptDirty) {
    transcriptDirty = false;
    const transcript = element("transcript");
    const fragment = document.createDocumentFragment();
    if (!state.turns.length) {
      const empty = document.createElement("p"); empty.className = "empty"; empty.textContent = "The conversation will appear here after you start."; fragment.append(empty);
    }
    for (const turn of state.turns) {
      const item = document.createElement("p");
      const label = document.createElement("span"); label.className = "turn-label"; label.textContent = turn.final ? "FINAL" : "PARTIAL";
      item.append(label, document.createTextNode(turn.text)); fragment.append(item);
    }
    transcript.replaceChildren(fragment);
    element("coach-question").textContent = state.turns.at(-1)?.text ?? "The latest question will appear here.";
  }
  if (renderedReply !== state.current) {
    element("reply").textContent = state.current?.text || "Ask for a suggestion after a final transcript arrives.";
    element("reply-status").textContent = state.current ? state.current.complete ? "Complete" : "Streaming…" : "Ready when you are";
    if (state.current?.complete || renderedReply?.complete || !state.current) sourceList("sources", state.current);
    renderedReply = state.current;
  }
  if (renderedPinned !== state.pinned) {
    element("pinned").closest<HTMLElement>(".pinned-panel")!.hidden = !state.pinned;
    element("pinned").textContent = state.pinned?.text || "Keep a useful answer here. New suggestions will not replace it.";
    sourceList("pinned-sources", state.pinned);
    renderedPinned = state.pinned;
  }
}
function renderControls(): void {
  element("meeting-view").classList.toggle("running", !!session);
  suggest.disabled = !canSuggest();
  cancel.disabled = !session?.isReady;
  pause.disabled = !session?.isReady;
  pin.disabled = !state.current?.text;
  element<HTMLButtonElement>("unpin").disabled = !state.pinned;
  start.disabled = !!session || mode.value !== "demo" && !configReady || mode.value === "live" && (!consent.checked || !auth.signedIn);
  stop.disabled = !session;
  mode.disabled = !!session;
  consent.disabled = !!session;
  audioSource.disabled = micDevice.disabled = !!session;
  signin.disabled = !!session || !configReady || fake;
  element<HTMLFieldSetElement>("meeting-fields").disabled = !!session;
  if (configReady && !fake && !auth.signedIn)
    element("auth-status").textContent = "Sign in first, then click Share meeting audio.";
  element<HTMLButtonElement>("reply-listen").disabled = mode.value === "demo" || !state.current?.complete ||
    !state.current.text || state.current.text.length > 400 || !auth.signedIn;
  if (coachMode === "meeting") updateCoachActivity(!!session, element("status").textContent ?? "Ready");
}
function renderSource(): void {
  const microphone = audioSource.value === "microphone";
  element("microphone-options").hidden = !microphone;
  element("display-options").hidden = microphone;
  element("consent-label").textContent = microphone
    ? "I have permission to capture and send this room's audio, and participants are informed under company/customer policy."
    : "I have permission from the participants to capture and send this meeting audio.";
  start.textContent = mode.value === "demo" ? "Start demo" : mode.value === "synthetic" ? "Start synthetic test"
    : microphone ? "Start microphone" : "Share meeting audio";
}
function render(): void { renderControls(); scheduler.flush(); }
async function configureMode(): Promise<void> {
  const generation = ++configGeneration;
  error("");
  configReady = false;
  element("live-options").hidden = mode.value !== "live";
  element("demo-options").hidden = mode.value !== "demo";
  element("meeting-options").hidden = mode.value === "demo";
  renderSource();
  element("mode-help").textContent = mode.value === "demo"
    ? "AUDIO DEMO — hear a prerecorded English sample, then see its scripted transcript and reply. No sign-in, capture, or Azure AI calls."
    : mode.value === "synthetic" ? "LOCAL FAKE SERVICE — synthetic silence through a real WebSocket. No media permission or cloud calls."
    : "LIVE — choose tab/system audio or the microphone explicitly. Audio only starts when you click Start and grant permission.";
  render();
  if (mode.value === "demo") return;
  try {
    const config = await auth.initialize();
    if (generation !== configGeneration) return;
    fake = config.mode === "Fake";
    if (mode.value === "synthetic" && !fake) throw new Error("Synthetic mode requires the explicit localhost Fake backend.");
    configReady = true;
    element("auth-status").textContent = fake ? "LOCAL FAKE BACKEND — no Entra or real AI. Shared audio is sent only to localhost."
      : auth.signedIn ? "Signed in. You may now share meeting audio." : "Sign in first, then click Share meeting audio.";
  } catch (e) { if (generation === configGeneration) error(asError(e).message); }
  if (generation === configGeneration) render();
}
mode.addEventListener("change", () => { consent.checked = false; void configureMode(); });
for (const id of ["meeting-chip", "practice-chip"]) element(id).addEventListener("click", () => {
  const next = id === "meeting-chip" ? "meeting" : "practice";
  if (coachMode === next) return;
  error("");
  clip.stop(); enrichment.reset(); assistTurn = undefined;
  if (next === "practice") { void session?.stop(); scheduler.flush(); }
  else void practice.stop();
  coachMode = next;
  overlay.setMode(next === "practice");
  element("meeting-view").hidden = next !== "meeting";
  element("practice-view").hidden = next !== "practice";
  element("meeting-chip").setAttribute("aria-pressed", String(next === "meeting"));
  element("practice-chip").setAttribute("aria-pressed", String(next === "practice"));
  updateCoachActivity(false, next === "practice" ? "Practice ready" : "Meeting ready");
  if (next === "practice") void practice.configure();
});
element("coach-toggle").addEventListener("click", () => {
  if (coachMode === "practice") {
    if (practice.active) void practice.stop(); else overlay.open("practice");
  } else if (session) stop.click();
  else if (start.disabled) overlay.revealMeetingSettings();
  else start.click();
});
element("reply-listen").addEventListener("click", () => {
  if (!state.current?.complete || element<HTMLButtonElement>("reply-listen").disabled) return;
  const text = state.current.text;
  // Avoid feeding the read-aloud into either microphone or system-loopback recognition.
  if (session?.isReady && !pause.checked) {
    pause.checked = true; state.pause(true, true); session.pause(true); render();
  }
  void clip.play(text, "coach", element<HTMLSelectElement>("speech-rate").value as Rate, "meeting-reply");
});
element("stop-listening").addEventListener("click", () => clip.stop());
consent.addEventListener("change", render);
audioSource.addEventListener("change", () => { consent.checked = false; renderSource(); render(); });
for (const id of ["profile-name", "profile-role", "profile-project"])
  element(id).addEventListener("input", () => { element<HTMLInputElement>("profile-confirmed").checked = false; });
signin.addEventListener("click", () => {
  signin.disabled = true; error("");
  void auth.signIn().then(() => { element("auth-status").textContent = "Signed in. You may now share meeting audio."; })
    .catch(e => error(asError(e).message)).finally(render);
});
start.addEventListener("click", () => {
  if (session || start.disabled) return;
  const selectedMode = mode.value;
  let options: SessionOptions | undefined;
  if (selectedMode !== "demo") {
    try { options = readOptions(); createStartMessage(options); }
    catch (e) { error(asError(e).message); overlay.revealMeetingSettings(); return; }
  }
  error(""); scheduler.cancel(); clip.stop(); enrichment.reset(); assistTurn = undefined; state.reset(); transcriptDirty = true; pause.checked = false;
  const shared = {
    context: () => new AudioContext(),
    node: (context: AudioContext) => new AudioWorkletNode(context, "meeting-pcm", { numberOfInputs: 1, numberOfOutputs: 1, outputChannelCount: [1] }),
    workletUrl,
  };
  const microphone = selectedMode === "live" && audioSource.value === "microphone";
  if (microphone && !navigator.mediaDevices?.getUserMedia) {
    error("Microphone access requires HTTPS and a supported browser."); return;
  }
  const source = selectedMode === "live"
    ? microphone ? new MicrophoneAudioSource({
      ...shared,
      getUserMedia: constraints => {
        if (!navigator.mediaDevices?.getUserMedia) throw new Error("Microphone access requires HTTPS and a supported browser.");
        return navigator.mediaDevices.getUserMedia(constraints);
      },
      devices: navigator.mediaDevices,
      deviceId: micDevice.value || undefined,
      onDevices: devices => {
        const selected = micDevice.value;
        micDevice.replaceChildren(new Option("Browser default microphone", ""));
        for (const device of devices) micDevice.add(new Option(device.label || "Microphone", device.deviceId));
        if (devices.some(device => device.deviceId === selected)) micDevice.value = selected;
      },
      onWarning: message => { element("microphone-warning").textContent = message; element("microphone-warning").hidden = !message; },
      onLevel: level => { element<HTMLMeterElement>("input-level").value = level; },
    }) : new DisplayAudioSource({
      ...shared,
      getDisplayMedia: () => {
        if (!navigator.mediaDevices?.getDisplayMedia) throw new Error("This browser does not support audio sharing. Use current Edge or Chrome over HTTPS.");
        return navigator.mediaDevices.getDisplayMedia({ video: true, audio: true });
      },
    }) : selectedMode === "demo"
      ? new DemoAudioSource(element<HTMLAudioElement>("demo-audio"), demoAudioUrl)
      : new SyntheticSource();
  const transport = selectedMode === "demo" ? new DemoTransport() : new SocketTransport(() => auth.endpoint(selectedMode === "synthetic"));
  session = new MeetingSession(source, transport,
    e => {
      state.apply(e);
      if ((e.type === "transcript.partial" || e.type === "transcript.final") && state.turns.at(-1)?.id === e.turnId && assistTurn !== e.turnId) {
        assistTurn = e.turnId; enrichment.reset(); clip.stop();
      }
      if (e.type === "response.started" && state.current?.id === e.responseId) { enrichment.clear("reply"); clip.stop(); }
      if (e.type === "transcript.partial" || e.type === "transcript.final") transcriptDirty = true;
      if (state.error) error(state.error);
      renderControls();
      if (e.type === "transcript.partial" || e.type === "response.delta") scheduler.schedule();
      else scheduler.flush();
      // Never await optional assistance on the streaming path. English is already rendered.
      if (selectedMode !== "demo" && e.type === "transcript.final" && state.turns.at(-1)?.id === e.turnId)
        void enrichment.load("question", e.text!);
      if (selectedMode !== "demo" && e.type === "response.completed" && state.current?.id === e.responseId)
        void enrichment.load("reply", e.text!);
      if (selectedMode === "demo" && e.type === "transcript.final" && canSuggest()) {
        state.request(); session?.request(); render();
      }
    },
    status => {
      element("status").textContent = selectedMode === "demo" && status.startsWith("Connected")
        ? "DEMO · sample playback and scripted responses (not live AI)"
        : (selectedMode === "demo" ? "DEMO · " : fake && selectedMode !== "demo" ? "LOCAL FAKE · " : "") + status
          + (microphone && status.startsWith("Connected") ? fake ? " · microphone room audio sent to localhost" : " · microphone room audio sent to Azure" : "");
      if (status.startsWith("Stopped")) { session = null; pause.checked = false; consent.checked = false; state.pause(true); clip.stop(); enrichment.reset(); }
      render();
    }, e => error(e.message), options);
  // No await before this call: getDisplayMedia and AudioContext.resume need this gesture.
  void session.start();
  overlay.close();
  render();
});
stop.addEventListener("click", () => { clip.stop(); enrichment.reset(); scheduler.flush(); void session?.stop(); render(); });
pause.addEventListener("change", () => { state.pause(pause.checked); session?.pause(pause.checked); render(); });
overlay.card.addEventListener("keydown", event => {
  if (coachMode !== "meeting" || event.key.toLowerCase() !== "p" || event.repeat || event.ctrlKey || event.metaKey || event.altKey || pause.disabled) return;
  const target = event.target;
  if (target && "closest" in target && ((target as HTMLElement).closest("input,textarea,select,button") || (target as HTMLElement).isContentEditable)) return;
  event.preventDefault(); pause.checked = !pause.checked; pause.dispatchEvent(new Event("change"));
});
suggest.addEventListener("click", () => {
  if (!canSuggest()) return;
  state.request(); error(""); session?.request(); render();
});
cancel.addEventListener("click", () => { clip.stop(); enrichment.clear("reply"); state.cancel(); session?.cancel(); render(); });
pin.addEventListener("click", () => { state.pin(); render(); });
element("unpin").addEventListener("click", () => { state.pinned = null; render(); });
window.addEventListener("pagehide", () => { if (elapsedTimer !== undefined) elapsedWindow.clearInterval(elapsedTimer); clip.stop(); enrichment.reset(); scheduler.cancel(); void practice.stop(); void session?.stop(); });
void configureMode();
