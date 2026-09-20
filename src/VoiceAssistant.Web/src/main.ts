import "./style.css";
import workletUrl from "./audio.worklet.ts?worker&url";
import demoAudioUrl from "./assets/demo-original.wav?url";
import { BrowserAuth } from "./auth.js";
import { DemoAudioSource } from "./demo.js";
import { DisplayAudioSource, SyntheticSource } from "./capture.js";
import { MeetingSession } from "./session.js";
import { ReplyState, type Reply } from "./state.js";
import { DemoTransport, SocketTransport, asError, isLoopback } from "./transport.js";

function element<T extends HTMLElement>(id: string): T {
  const value = document.getElementById(id);
  if (!value) throw new Error(`Missing UI element: ${id}`);
  return value as T;
}
const mode = element<HTMLSelectElement>("mode");
const start = element<HTMLButtonElement>("start");
const stop = element<HTMLButtonElement>("stop");
const pause = element<HTMLInputElement>("pause");
const suggest = element<HTMLButtonElement>("suggest");
const cancel = element<HTMLButtonElement>("cancel");
const pin = element<HTMLButtonElement>("pin");
const consent = element<HTMLInputElement>("consent");
const signin = element<HTMLButtonElement>("signin");
const state = new ReplyState();
const auth = new BrowserAuth();
let session: MeetingSession | null = null;
let configReady = false;
let fake = false;
let configGeneration = 0;
for (const id of ["mode", "start", "stop", "suggest", "pause", "pin", "transcript", "reply", "status", "error", "consent", "signin"])
  element(id).dataset.testid = id;
element("pinned").dataset.testid = "pinned-reply";
if (!isLoopback(location.hostname)) mode.querySelector<HTMLOptionElement>('option[value="synthetic"]')!.remove();

function error(message: string): void {
  element("error").textContent = message;
  element("error").hidden = !message;
}
function canSuggest(): boolean {
  return !!session?.isReady && !pause.checked && state.turns.at(-1)?.final === true;
}
function sourceList(id: string, reply: Reply | null): void {
  const list = element(id);
  list.replaceChildren();
  if (reply?.complete) {
    const line = document.createElement("li");
    const grounding = { grounded: "References available", disabled: "Reference search disabled",
      unavailable: "Reference search unavailable", no_matches: "No matching references found" };
    line.textContent = reply.grounding ? grounding[reply.grounding] : "Reference status not supplied";
    list.append(line);
  }
  for (const source of reply?.sources ?? []) {
    const item = document.createElement("li");
    item.textContent = `${source.title} — ${source.url}${source.updatedAt ? ` · updated ${source.updatedAt}` : ""}`;
    list.append(item);
  }
}
function render(): void {
  const transcript = element("transcript");
  transcript.replaceChildren();
  if (!state.turns.length) {
    const empty = document.createElement("p"); empty.className = "empty"; empty.textContent = "The conversation will appear here after you start."; transcript.append(empty);
  }
  for (const turn of state.turns) {
    const item = document.createElement("p");
    const label = document.createElement("span"); label.className = "turn-label"; label.textContent = turn.final ? "FINAL" : "PARTIAL";
    item.append(label, document.createTextNode(turn.text)); transcript.append(item);
  }
  element("reply").textContent = state.current?.text || "Ask for a suggestion after a final transcript arrives.";
  element("reply-status").textContent = state.current ? state.current.complete ? "Complete" : "Streaming…" : "Ready when you are";
  element("pinned").textContent = state.pinned?.text || "Keep a useful answer here. New suggestions will not replace it.";
  sourceList("sources", state.current); sourceList("pinned-sources", state.pinned);
  suggest.disabled = !canSuggest();
  cancel.disabled = !session?.isReady;
  pause.disabled = !session?.isReady;
  pin.disabled = !state.current?.text;
  element<HTMLButtonElement>("unpin").disabled = !state.pinned;
  start.disabled = !!session || mode.value !== "demo" && !configReady || mode.value === "live" && (!consent.checked || !auth.signedIn);
  stop.disabled = !session;
  mode.disabled = !!session;
  consent.disabled = !!session;
  signin.disabled = !!session || !configReady || fake;
  if (configReady && !fake && !auth.signedIn)
    element("auth-status").textContent = "Sign in first, then click Share meeting audio.";
}
async function configureMode(): Promise<void> {
  const generation = ++configGeneration;
  error("");
  configReady = false;
  element("live-options").hidden = mode.value !== "live";
  element("demo-options").hidden = mode.value !== "demo";
  start.textContent = mode.value === "demo" ? "Start demo" : mode.value === "synthetic" ? "Start synthetic test" : "Share meeting audio";
  element("mode-help").textContent = mode.value === "demo"
    ? "AUDIO DEMO — hear a prerecorded English sample, then see its scripted transcript and reply. No sign-in, capture, or Azure AI calls."
    : mode.value === "synthetic" ? "LOCAL FAKE SERVICE — synthetic silence through a real WebSocket. No media permission or cloud calls."
    : "LIVE — explicitly share a tab or screen with audio. No audio sharing starts until you click Share.";
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
consent.addEventListener("change", render);
signin.addEventListener("click", () => {
  signin.disabled = true; error("");
  void auth.signIn().then(() => { element("auth-status").textContent = "Signed in. You may now share meeting audio."; })
    .catch(e => error(asError(e).message)).finally(render);
});
start.addEventListener("click", () => {
  if (session || start.disabled) return;
  error(""); state.reset(); pause.checked = false;
  const selectedMode = mode.value;
  const source = selectedMode === "live"
    ? new DisplayAudioSource({
      getDisplayMedia: () => {
        if (!navigator.mediaDevices?.getDisplayMedia) throw new Error("This browser does not support audio sharing. Use current Edge or Chrome over HTTPS.");
        return navigator.mediaDevices.getDisplayMedia({ video: true, audio: true });
      },
      context: () => new AudioContext(),
      node: context => new AudioWorkletNode(context, "meeting-pcm", { numberOfInputs: 1, numberOfOutputs: 1, outputChannelCount: [1] }),
      workletUrl,
    }) : selectedMode === "demo"
      ? new DemoAudioSource(element<HTMLAudioElement>("demo-audio"), demoAudioUrl)
      : new SyntheticSource();
  const transport = selectedMode === "demo" ? new DemoTransport() : new SocketTransport(() => auth.endpoint(selectedMode === "synthetic"));
  session = new MeetingSession(source, transport,
    e => {
      state.apply(e); if (state.error) error(state.error); render();
      if (selectedMode === "demo" && e.type === "transcript.final" && canSuggest()) {
        state.request(); session?.request(); render();
      }
    },
    status => {
      element("status").textContent = selectedMode === "demo" && status.startsWith("Connected")
        ? "DEMO · sample playback and scripted responses (not live AI)"
        : (selectedMode === "demo" ? "DEMO · " : fake && selectedMode !== "demo" ? "LOCAL FAKE · " : "") + status;
      if (status.startsWith("Stopped")) { session = null; pause.checked = false; consent.checked = false; state.pause(true); }
      render();
    }, e => error(e.message));
  // No await before this call: getDisplayMedia and AudioContext.resume need this gesture.
  void session.start();
  render();
});
stop.addEventListener("click", () => { void session?.stop(); });
pause.addEventListener("change", () => { state.pause(pause.checked); session?.pause(pause.checked); render(); });
suggest.addEventListener("click", () => {
  if (!canSuggest()) return;
  state.request(); error(""); session?.request(); render();
});
cancel.addEventListener("click", () => { state.cancel(); session?.cancel(); render(); });
pin.addEventListener("click", () => { state.pin(); render(); });
element("unpin").addEventListener("click", () => { state.pinned = null; render(); });
window.addEventListener("pagehide", () => { void session?.stop(); });
void configureMode();
