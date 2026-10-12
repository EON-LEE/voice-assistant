import { test } from "node:test";
import assert from "node:assert/strict";
import { MeetingSession } from "../../src/VoiceAssistant.Web/src/session.js";
import { DisplayAudioSource, type AudioSource, type CaptureDependencies } from "../../src/VoiceAssistant.Web/src/capture.js";
import { SocketTransport, DemoTransport, type Transport } from "../../src/VoiceAssistant.Web/src/transport.js";
import { startMessage, type ServerEvent } from "../../src/VoiceAssistant.Web/src/protocol.js";

class FakeSource implements AudioSource {
  started = false; stopped = false; paused = false;
  emit: (buffer: ArrayBuffer) => void = () => {};
  fail: (error: Error) => void = () => {};
  async prepare(signal: AbortSignal): Promise<void> { signal.throwIfAborted(); }
  start(emit: (buffer: ArrayBuffer) => void, fail: (error: Error) => void): void { this.started = true; this.emit = emit; this.fail = fail; }
  pause(value: boolean): void { this.paused = value; }
  async stop(): Promise<void> { this.stopped = true; }
}
class FakeTransport implements Transport {
  events: (event: ServerEvent) => void = () => {};
  fail: (error: Error) => void = () => {};
  messages: object[] = []; frames = 0; closed = false;
  async connect(events: (event: ServerEvent) => void, fail: (error: Error) => void): Promise<void> { this.events = events; this.fail = fail; }
  send(message: object): void { this.messages.push(message); }
  audio(_buffer: ArrayBuffer): void { this.frames++; }
  close(): void { this.closed = true; }
}
const tick = (): Promise<void> => new Promise(resolve => setImmediate(resolve));
test("Ready gates all audio and pause drops frames/cancels; stop sends command and cleans up", async () => {
  const source = new FakeSource(), transport = new FakeTransport();
  const errors: Error[] = [];
  const session = new MeetingSession(source, transport, () => {}, () => {}, error => errors.push(error));
  await session.start();
  assert.equal(source.started, false);
  assert.deepEqual(transport.messages[0], startMessage);
  transport.events({ type: "session.ready" });
  assert.equal(source.started, true);
  source.emit(new ArrayBuffer(640)); assert.equal(transport.frames, 1);
  session.pause(true); source.emit(new ArrayBuffer(640)); session.request();
  assert.equal(transport.frames, 1); assert.deepEqual(transport.messages.at(-1), { type: "response.cancel" });
  session.pause(false); source.emit(new ArrayBuffer(640)); assert.equal(transport.frames, 2);
  await session.stop(); await session.stop();
  assert.deepEqual(transport.messages.at(-1), { type: "session.stop" });
  assert.equal(source.stopped, true); assert.equal(transport.closed, true); assert.deepEqual(errors, []);
});
for (const failure of ["handshake", "source", "disconnect", "fatal"]) {
  test(`${failure} failures fail visibly and dispose audio/transport`, async () => {
    const source = new FakeSource(), transport = new FakeTransport(), errors: Error[] = [];
    const session = new MeetingSession(source, transport, () => {}, () => {}, e => errors.push(e));
    await session.start();
    if (failure === "handshake") transport.events({ type: "transcript.final", turnId: "t", revision: 1, text: "wrong order" });
    else {
      transport.events({ type: "session.ready" });
      if (failure === "source") source.fail(new Error("device ended"));
      if (failure === "disconnect") transport.fail(new Error("disconnected"));
      if (failure === "fatal") transport.events({ type: "error", code: "fatal", message: "bad", retryable: false });
    }
    await tick();
    assert.ok(errors.length); assert.equal(source.stopped, true); assert.equal(transport.closed, true);
  });
}
test("Explicit demo produces contract events without media or network", async () => {
  const demo = new DemoTransport(), events: ServerEvent[] = [];
  await demo.connect(e => events.push(e), () => {}, new AbortController().signal);
  demo.send(startMessage); demo.audio(new ArrayBuffer(640));
  demo.send({ type: "response.request" });
  await new Promise(resolve => setTimeout(resolve, 700));
  assert.ok(events.some(e => e.type === "transcript.final"));
  assert.ok(events.some(e => e.type === "response.completed" && e.text));
  demo.close();
});

function captureMocks(audio: boolean) {
  const videoTrack = { kind: "video", readyState: "live", onended: null, onmute: null, stops: 0, stop() { this.stops++; this.readyState = "ended"; } };
  const audioTrack = { ...videoTrack, kind: "audio" };
  const tracks = audio ? [videoTrack, audioTrack] : [videoTrack];
  const stream = { getTracks: () => tracks, getAudioTracks: () => audio ? [audioTrack] : [] };
  const context = { state: "running", closes: 0, resume: async () => {}, close: async () => { context.closes++; context.state = "closed"; },
    audioWorklet: { addModule: async () => {} }, createMediaStreamSource: () => ({ connect() {}, disconnect() {} }), destination: {} };
  const port = { postMessage() {}, close() {}, onmessage: null };
  const node = { port, connect() {}, disconnect() {}, onprocessorerror: null };
  const deps: CaptureDependencies = {
    getDisplayMedia: async () => stream as unknown as MediaStream,
    context: () => context as unknown as AudioContext,
    node: () => node as unknown as AudioWorkletNode, workletUrl: "test.js",
  };
  return { deps, tracks, context, stream };
}
test("Video-only browser share fails and stops all tracks/context", async () => {
  const mock = captureMocks(false);
  const capture = new DisplayAudioSource(mock.deps);
  await assert.rejects(capture.prepare(new AbortController().signal), /No shared audio/);
  assert.ok(mock.tracks.every(t => t.stops >= 1));
  assert.equal(mock.context.closes, 1);
});
test("Permission rejection closes AudioContext and never requests microphone", async () => {
  const mock = captureMocks(false);
  mock.deps.getDisplayMedia = async () => { throw new Error("Permission denied"); };
  const capture = new DisplayAudioSource(mock.deps);
  await assert.rejects(capture.prepare(new AbortController().signal), /Permission denied/);
  assert.equal(mock.context.closes, 1);
});
test("Stopping while browser picker is pending stops late-granted tracks", async () => {
  const mock = captureMocks(true);
  let grant: (stream: MediaStream) => void = () => {};
  mock.deps.getDisplayMedia = () => new Promise(resolve => { grant = resolve; });
  const capture = new DisplayAudioSource(mock.deps), abort = new AbortController();
  const pending = capture.prepare(abort.signal);
  abort.abort(); grant(mock.stream as unknown as MediaStream);
  await assert.rejects(pending);
  assert.ok(mock.tracks.every(t => t.stops >= 1));
  assert.equal(mock.context.closes, 1);
});
test("Context creation failure still stops late browser tracks", async () => {
  const mock = captureMocks(true);
  mock.deps.context = () => { throw new Error("Unavailable"); };
  await assert.rejects(new DisplayAudioSource(mock.deps).prepare(new AbortController().signal), /Unavailable/);
  await tick(); assert.ok(mock.tracks.every(t => t.stops >= 1));
});
test("Source end stops every audio/video track and context", async () => {
  const original = globalThis.MediaStream;
  Object.defineProperty(globalThis, "MediaStream", { configurable: true, value: class { constructor(_tracks: unknown) {} } });
  try {
    const mock = captureMocks(true), errors: Error[] = [];
    const capture = new DisplayAudioSource(mock.deps);
    await capture.prepare(new AbortController().signal);
    capture.start(() => {}, e => errors.push(e));
    (mock.tracks[0]!.onended as (() => void) | null)?.();
    await tick();
    assert.ok(mock.tracks.every(t => t.stops >= 1)); assert.equal(mock.context.closes, 1);
    assert.match(errors[0]!.message, /Sharing ended/);
  } finally { Object.defineProperty(globalThis, "MediaStream", { configurable: true, value: original }); }
});
test("WebSocket buffering fails at bound and incoming binary fails explicitly", async () => {
  const socket = {
    readyState: 1, bufferedAmount: 31999, onopen: null as (() => void) | null,
    onmessage: null as ((event: { data: unknown }) => void) | null, onclose: null, onerror: null, send() {}, close() {},
  };
  const errors: Error[] = [];
  const transport = new SocketTransport(async () => new URL("ws://localhost/api/meeting"), () => socket as unknown as WebSocket);
  const pending = transport.connect(() => {}, e => errors.push(e), new AbortController().signal);
  await tick(); socket.onopen!(); await pending;
  assert.throws(() => transport.audio(new ArrayBuffer(640)), /cannot keep up/);
  assert.throws(() => transport.audio(new ArrayBuffer(2)), /20 ms/);
  socket.onmessage!({ data: new ArrayBuffer(4) });
  assert.match(errors[0]!.message, /must be text/);
  transport.close();
});
