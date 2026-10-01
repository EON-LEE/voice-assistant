import { test } from "node:test";
import assert from "node:assert/strict";
import { MicrophoneAudioSource, type MicrophoneDependencies, microphoneError } from "../../src/VoiceAssistant.Web/src/capture.js";
import { MeetingSession } from "../../src/VoiceAssistant.Web/src/session.js";
import type { ServerEvent } from "../../src/VoiceAssistant.Web/src/protocol.js";
import { SocketTransport } from "../../src/VoiceAssistant.Web/src/transport.js";

const tick = (): Promise<void> => new Promise(resolve => setImmediate(resolve));
function fixture() {
  const track = {
    kind: "audio", readyState: "live", muted: false, onended: null as (() => void) | null,
    onmute: null as (() => void) | null, onunmute: null as (() => void) | null, stops: 0,
    getSettings: () => ({ deviceId: "mic-1" }), stop() { this.readyState = "ended"; this.stops++; },
  };
  const stream = { getTracks: () => [track], getAudioTracks: () => [track] };
  const devices = new EventTarget();
  let available = [{ kind: "audioinput", deviceId: "mic-1", label: "Fixture microphone" }] as MediaDeviceInfo[];
  const port = { messages: [] as { type: string; epoch?: number; paused?: boolean }[],
    onmessage: null as ((event: { data: { type: string; buffer: ArrayBuffer; epoch: number } }) => void) | null,
    postMessage(data: { type: string; epoch?: number; paused?: boolean }) { this.messages.push(data); }, close() {} };
  const context = {
    state: "running", closes: 0, resume: async () => {},
    close: async () => { context.state = "closed"; context.closes++; },
    audioWorklet: { addModule: async () => {} },
    createMediaStreamSource: () => ({ connect() {}, disconnect() {} }), destination: {},
    createAnalyser: () => ({ fftSize: 256, getFloatTimeDomainData: (samples: Float32Array) => samples.fill(.25), disconnect() {} }),
  };
  const warnings: string[] = [], levels: number[] = [], errors: Error[] = [];
  let constraints: MediaStreamConstraints | undefined;
  const deps: MicrophoneDependencies = {
    getUserMedia: async options => { constraints = options; return stream as unknown as MediaStream; },
    devices: { enumerateDevices: async () => available,
      addEventListener: devices.addEventListener.bind(devices), removeEventListener: devices.removeEventListener.bind(devices) },
    context: () => context as unknown as AudioContext,
    node: () => ({ port, connect() {}, disconnect() {}, onprocessorerror: null }) as unknown as AudioWorkletNode,
    workletUrl: "fixture-worklet", deviceId: "mic-1", onDevices: () => {},
    onWarning: message => warnings.push(message), onLevel: level => levels.push(level),
  };
  return { deps, track, stream, context, port, warnings, levels, errors, devices,
    remove: () => { available = []; devices.dispatchEvent(new Event("devicechange")); }, constraints: () => constraints };
}
async function withStream(run: () => Promise<void>): Promise<void> {
  const original = globalThis.MediaStream;
  Object.defineProperty(globalThis, "MediaStream", { configurable: true, value: class { constructor(_tracks: unknown) {} } });
  try { await run(); } finally { Object.defineProperty(globalThis, "MediaStream", { configurable: true, value: original }); }
}
test("Microphone requests explicit mono processing constraints, measures level and releases on stop", async () => withStream(async () => {
  const f = fixture(); const source = new MicrophoneAudioSource(f.deps);
  await source.prepare(new AbortController().signal); source.start(() => {}, e => f.errors.push(e));
  assert.deepEqual(f.constraints(), { audio: { channelCount: 1, echoCancellation: true, noiseSuppression: true,
    autoGainControl: true, deviceId: { exact: "mic-1" } }, video: false });
  await new Promise(resolve => setTimeout(resolve, 120));
  assert.ok(f.levels.includes(.25));
  await source.stop(); await source.stop();
  assert.equal(f.track.stops, 1); assert.equal(f.context.closes, 1); assert.equal(f.levels.at(-1), 0);
}));
for (const name of ["NotAllowedError", "NotFoundError", "NotReadableError", "OverconstrainedError", "SecurityError"]) {
  test(`Microphone ${name} produces actionable safe errors and closes context`, async () => {
    const f = fixture();
    f.deps.getUserMedia = async () => { throw new DOMException("private driver details", name); };
    await assert.rejects(new MicrophoneAudioSource(f.deps).prepare(new AbortController().signal),
      error => error instanceof Error && !error.message.includes("private") && /microphone/i.test(error.message));
    assert.equal(f.context.closes, 1);
    assert.notEqual(microphoneError(new DOMException("private", name)).message, "private");
  });
}
test("Microphone mute is visible and recoverable; pause epochs never send muted or stale audio", async () => withStream(async () => {
  const f = fixture(); const source = new MicrophoneAudioSource(f.deps); let frames = 0;
  await source.prepare(new AbortController().signal); source.start(() => frames++, e => f.errors.push(e));
  const send = (epoch: number) => f.port.onmessage!({ data: { type: "audio", buffer: new ArrayBuffer(640), epoch } });
  const initial = f.port.messages.at(-1)!.epoch!;
  send(initial); assert.equal(frames, 1);
  f.track.onmute!(); send(initial);
  assert.match(f.warnings.at(-1)!, /muted by the system/);
  assert.equal(f.track.readyState, "live"); assert.equal(frames, 1);
  source.pause(true); f.track.onunmute!();
  assert.equal(f.warnings.at(-1), "");
  assert.equal(f.port.messages.at(-1)!.paused, true);
  source.pause(false); const resumed = f.port.messages.at(-1)!.epoch!;
  send(initial); assert.equal(frames, 1);
  send(resumed); assert.equal(frames, 2);
  await source.stop();
}));
for (const event of ["ended", "removed"] as const) {
  test(`Microphone ${event} terminates capture and closes context`, async () => withStream(async () => {
    const f = fixture(); const source = new MicrophoneAudioSource(f.deps);
    await source.prepare(new AbortController().signal); source.start(() => {}, e => f.errors.push(e));
    if (event === "ended") f.track.onended!(); else f.remove();
    await tick(); assert.equal(f.track.readyState, "ended"); assert.equal(f.context.closes, 1);
    assert.match(f.errors[0]!.message, /disconnected|ended/);
  }));
}
test("Abort during microphone permission disposes a late grant and never starts processing", async () => {
  const f = fixture(); let grant: (stream: MediaStream) => void = () => {};
  f.deps.getUserMedia = () => new Promise(resolve => { grant = resolve; });
  const abort = new AbortController(), source = new MicrophoneAudioSource(f.deps);
  const prepare = source.prepare(abort.signal); abort.abort(); grant(f.stream as unknown as MediaStream);
  await assert.rejects(prepare); assert.equal(f.track.readyState, "ended"); assert.equal(f.context.closes, 1);
});
test("Session limit event maps to explicit stopped status and releases microphone", async () => {
  let receive: (event: ServerEvent) => void = () => {}; let stopped = false;
  const statuses: string[] = [];
  const session = new MeetingSession({ prepare: async () => {}, start() {}, pause() {}, stop: async () => { stopped = true; } },
    { connect: async fn => { receive = fn; }, send() {}, audio() {}, close() {} }, () => {}, s => statuses.push(s), () => {});
  await session.start(); receive({ type: "session.ready" });
  receive({ type: "error", code: "session_time_limit", message: "limit", retryable: false });
  await tick(); assert.equal(stopped, true); assert.match(statuses.at(-1)!, /Session time limit reached/);
});
test("Normal server close is distinct from unexpected disconnect without trusting close reason", async () => {
  for (const code of [1000, 1011]) {
    const socket = { onopen: null as (() => void) | null, onclose: null as ((event: { code: number }) => void) | null, close() {} };
    const errors: Error[] = [];
    const transport = new SocketTransport(async () => new URL("ws://localhost/api/meeting"), () => socket as unknown as WebSocket);
    const connected = transport.connect(() => {}, e => errors.push(e), new AbortController().signal);
    await tick(); socket.onopen!(); await connected; socket.onclose!({ code });
    assert.match(errors[0]!.message, code === 1000 ? /server ended/ : /unexpectedly/);
    transport.close();
  }
});
