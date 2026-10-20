import { test } from "node:test";
import assert from "node:assert/strict";
import { DisplayAudioSource, type AudioSource, type CaptureSourceKind } from "../../src/VoiceAssistant.Web/src/capture.js";
import { CodedError } from "../../src/VoiceAssistant.Web/src/errors.js";
import { LiveController, SilenceWatch, liveLimits, type LiveClock, type LiveNetwork, type SourceHooks } from "../../src/VoiceAssistant.Web/src/live-controller.js";
import { liveMessage, type Classified } from "../../src/VoiceAssistant.Web/src/live-errors.js";
import { LiveState } from "../../src/VoiceAssistant.Web/src/live-state.js";
import { defaultOptions } from "../../src/VoiceAssistant.Web/src/options.js";
import type { ServerEvent } from "../../src/VoiceAssistant.Web/src/protocol.js";
import { maxAudioMessageBytes, type Transport } from "../../src/VoiceAssistant.Web/src/transport.js";

const flush = (): Promise<void> => new Promise(resolve => setImmediate(resolve));

class FakeClock implements LiveClock {
  time = 0;
  private next = 1;
  private readonly timers = new Map<number, { at: number; run: () => void }>();
  now(): number { return this.time; }
  setTimeout(run: () => void, ms: number): unknown { const id = this.next++; this.timers.set(id, { at: this.time + ms, run }); return id; }
  clearTimeout(handle: unknown): void { this.timers.delete(handle as number); }
  /** Moves time forward, running every timer that falls due on the way (including timers they set). */
  advance(ms: number): void {
    const end = this.time + ms;
    for (;;) {
      let due: [number, { at: number; run: () => void }] | undefined;
      for (const entry of this.timers) if (entry[1].at <= end && (!due || entry[1].at < due[1].at)) due = entry;
      if (!due) break;
      this.timers.delete(due[0]);
      this.time = due[1].at;
      due[1].run();
    }
    this.time = end;
  }
}
class FakeNetwork implements LiveNetwork {
  up = true;
  readonly listeners = new Set<() => void>();
  online(): boolean { return this.up; }
  onOnline(listener: () => void): () => void { this.listeners.add(listener); return () => { this.listeners.delete(listener); }; }
  restore(): void { this.up = true; for (const listener of [...this.listeners]) listener(); }
}
class FakeTransport implements Transport {
  onEvent: (event: ServerEvent) => void = () => {};
  onError: (error: Error) => void = () => {};
  readonly sent: { type?: string }[] = [];
  readonly binary: { at: number; bytes: Uint8Array }[] = [];
  closed = false;
  bufferedAmount = 0;
  failConnect: Error | undefined;
  constructor(private readonly clock: FakeClock) {}
  async connect(onEvent: (event: ServerEvent) => void, onError: (error: Error) => void, signal: AbortSignal): Promise<void> {
    signal.throwIfAborted();
    if (this.failConnect) throw this.failConnect;
    this.onEvent = onEvent; this.onError = onError;
  }
  send(message: object): void {
    if (this.closed) throw new CodedError("socket_not_open", "The meeting connection is not open.");
    this.sent.push(message as { type?: string });
  }
  audio(buffer: ArrayBuffer): void {
    if (this.closed) throw new CodedError("socket_not_open", "The meeting connection is not open.");
    assert.ok(buffer.byteLength > 0 && buffer.byteLength % 640 === 0 && buffer.byteLength <= maxAudioMessageBytes, String(buffer.byteLength));
    this.binary.push({ at: this.clock.now(), bytes: new Uint8Array(buffer.slice(0)) });
  }
  close(): void { this.closed = true; }
  ready(extra: Partial<ServerEvent> = {}): void { this.onEvent({ type: "session.ready", ...extra }); }
  get types(): (string | undefined)[] { return this.sent.map(message => message.type); }
  get bytes(): number { return this.binary.reduce((total, item) => total + item.bytes.byteLength, 0); }
}
class FakeSource implements AudioSource {
  prepared = 0; started = 0; stopped = 0;
  readonly paused: boolean[] = [];
  private emit: (frame: ArrayBuffer) => void = () => {};
  fail: (error: Error) => void = () => {};
  async prepare(signal: AbortSignal): Promise<void> { signal.throwIfAborted(); this.prepared++; }
  start(onAudio: (frame: ArrayBuffer) => void, onError: (error: Error) => void): void { this.started++; this.emit = onAudio; this.fail = onError; }
  pause(value: boolean): void { this.paused.push(value); }
  async stop(): Promise<void> { this.stopped++; }
  /** One 20 ms frame whose samples all carry `value`, so the receiver can check order. */
  frame(value = 1): void { this.emit(new Int16Array(320).fill(value).buffer); }
}

function harness(createSource?: (kind: CaptureSourceKind, hooks: SourceHooks) => AudioSource,
  listen: { onEvent?(event: ServerEvent): void; onDisconnect?(reason: Classified): void } = {}) {
  const clock = new FakeClock(), network = new FakeNetwork();
  const transports: FakeTransport[] = [], events: ServerEvent[] = [], disconnects: Classified[] = [], sources: FakeSource[] = [];
  let hooks: SourceHooks | undefined;
  let nextTransport: ((transport: FakeTransport) => void) | undefined;
  const controller = new LiveController({
    createSource: (kind, given) => {
      hooks = given;
      if (createSource) return createSource(kind, given);
      const source = new FakeSource(); sources.push(source); return source;
    },
    createTransport: () => {
      const transport = new FakeTransport(clock);
      nextTransport?.(transport);
      transports.push(transport);
      return transport;
    },
    options: () => defaultOptions(),
    onEvent: event => { events.push(event); listen.onEvent?.(event); },
    onDisconnect: reason => { disconnects.push(reason); listen.onDisconnect?.(reason); },
    onChange: () => {},
    clock, network,
  });
  return {
    controller, clock, network, transports, events, disconnects, sources,
    hooks: () => hooks!,
    latest: () => transports.at(-1)!,
    onTransport: (configure: (transport: FakeTransport) => void) => { nextTransport = configure; },
  };
}
async function live(h: ReturnType<typeof harness>, kind: CaptureSourceKind = "microphone"): Promise<void> {
  h.controller.start(kind);
  await flush();
  h.latest().ready();
  assert.equal(h.controller.phase, "live");
}

/** A real DisplayAudioSource over mocked browser media: counts every getDisplayMedia call (= every picker). */
function displayMedia() {
  const calls = { getDisplayMedia: 0 };
  const tracks: { kind: string; readyState: string; muted: boolean; onended: (() => void) | null; onmute: (() => void) | null;
    onunmute: (() => void) | null; stop(): void }[] = [];
  const port = { onmessage: null as ((event: { data: unknown }) => void) | null, epoch: 0,
    postMessage(message: { type: string; epoch?: number }) { if (message.type === "pause") this.epoch = message.epoch ?? 0; }, close() {} };
  const create = (): AudioSource => new DisplayAudioSource({
    getDisplayMedia: async () => {
      calls.getDisplayMedia++;
      const made = ["video", "audio"].map(kind => ({ kind, readyState: "live", muted: false, onended: null, onmute: null, onunmute: null,
        stop() { this.readyState = "ended"; } }));
      tracks.push(...made);
      return { getTracks: () => made, getAudioTracks: () => made.filter(track => track.kind === "audio") } as unknown as MediaStream;
    },
    context: () => ({ state: "running", resume: async () => {}, close: async () => {}, audioWorklet: { addModule: async () => {} },
      createMediaStreamSource: () => ({ connect() {}, disconnect() {} }), destination: {} }) as unknown as AudioContext,
    node: () => ({ port, connect() {}, disconnect() {}, onprocessorerror: null }) as unknown as AudioWorkletNode,
    workletUrl: "fixture-worklet",
  });
  const emit = (value: number): void => port.onmessage?.({ data: { type: "audio", buffer: new Int16Array(320).fill(value).buffer, epoch: port.epoch } });
  return { calls, tracks, create, emit };
}
async function withMediaStream(run: () => Promise<void>): Promise<void> {
  const original = globalThis.MediaStream;
  Object.defineProperty(globalThis, "MediaStream", { configurable: true, value: class { constructor(_tracks: unknown) {} } });
  try { await run(); } finally { Object.defineProperty(globalThis, "MediaStream", { configurable: true, value: original }); }
}

test("Three dropped connections reconnect only the server: one picker for the whole session, audio kept meanwhile", async () => withMediaStream(async () => {
  const media = displayMedia();
  const h = harness(() => media.create());
  await live(h, "display");
  media.emit(1);
  assert.equal(h.latest().binary.length, 1);
  for (let drop = 1; drop <= 3; drop++) {
    const before = h.latest();
    before.onError(new CodedError(drop === 2 ? "server_closed" : "socket_closed", "dropped"));
    assert.equal(before.closed, true);
    media.emit(10 + drop); // spoken while reconnecting: kept, then sent after session.ready
    h.clock.advance(3000);
    await flush();
    h.latest().ready();
    assert.equal(h.controller.phase, "live");
    assert.equal(h.latest().binary[0]!.bytes[0], 10 + drop, "the backlog is flushed first, in order");
  }
  assert.equal(media.calls.getDisplayMedia, 1, "reconnects never open the share picker");
  assert.equal(h.transports.length, 4);
  assert.ok(media.tracks.every(track => track.readyState === "live"), "the shared tracks stay open");
  assert.equal(h.controller.stats.reconnects, 3);
  assert.equal(h.controller.failure, null);
}));

test("A display track that ends stops with a re-share step and never opens a picker by itself", async () => withMediaStream(async () => {
  const media = displayMedia();
  const h = harness(() => media.create());
  await live(h, "display");
  media.tracks.find(track => track.kind === "audio")!.onended!();
  await flush();
  assert.equal(h.controller.phase, "stopped");
  assert.equal(h.controller.failure?.action, "reshare");
  assert.equal(h.controller.failure?.capture?.kind, "ended");
  assert.deepEqual(h.transports[0]!.types.slice(-1), ["session.stop"]);
  assert.match(liveMessage(h.controller.failure!, "app"), /화면 공유가 중지되었습니다. '다시 공유'/);
  h.clock.advance(120000);
  await flush();
  assert.equal(media.calls.getDisplayMedia, 1, "no automatic picker");
  assert.equal(h.transports.length, 1, "no automatic reconnect");
  h.controller.start("display"); // the user's click on 다시 공유
  await flush();
  assert.equal(media.calls.getDisplayMedia, 2);
  assert.equal(h.controller.failure, null);
}));

test("While paused, one silent 20 ms frame goes out every 20 seconds and nothing else", async () => {
  const h = harness();
  await live(h);
  const source = h.sources[0]!;
  h.controller.pause(true);
  assert.equal(source.paused.at(-1), true);
  assert.equal(h.latest().types.at(-1), "response.cancel");
  source.frame(5); // a frame already in flight when pausing is dropped
  h.clock.advance(19999);
  assert.equal(h.latest().binary.length, 0);
  h.clock.advance(1);
  assert.equal(h.latest().binary.length, 1);
  for (const minutes of [1, 2]) h.clock.advance(60000 * minutes);
  const frames = h.latest().binary;
  assert.equal(frames.length, 1 + 3 + 6, "one every 20 s");
  assert.ok(frames.every(frame => frame.bytes.byteLength === 640 && frame.bytes.every(value => value === 0)));
  assert.deepEqual(frames.slice(0, 3).map(frame => frame.at), [20000, 40000, 60000]);
  assert.equal(h.controller.stats.keepalives, 10);
  h.controller.pause(false);
  for (let i = 0; i < 1500; i++) { h.clock.advance(20); source.frame(1); } // 30 s of real audio
  assert.equal(h.controller.stats.keepalives, 10, "no keepalive while audio flows");
  assert.equal(h.transports.length, 1);
});

test("session_time_limit rolls over at once to a new session with the same source and no stop message", async () => {
  const h = harness();
  await live(h);
  const source = h.sources[0]!, first = h.latest();
  h.events.length = 0;
  first.onEvent({ type: "error", code: "session_time_limit", message: "Session reached its time limit.", retryable: false });
  assert.equal(h.transports.length, 2, "no backoff before the next session");
  assert.equal(first.closed, true);
  assert.equal(h.controller.rollingOver, true);
  assert.equal(h.controller.phase, "reconnecting");
  assert.equal(h.controller.failure, null);
  assert.equal(h.disconnects.at(-1)?.disposition, "rollover");
  assert.ok(!h.events.some(event => event.type === "error"), "the page never sees the limit as an error");
  source.frame(7);
  first.onError(new CodedError("server_closed", "The server ended this session.")); // the old socket's close is ignored
  await flush();
  assert.equal(h.transports.length, 2);
  h.latest().ready();
  assert.equal(h.controller.phase, "live");
  assert.equal(h.controller.rollingOver, false);
  assert.equal(h.latest().binary[0]!.bytes[0], 7);
  assert.equal(source.prepared, 1); assert.equal(source.started, 1); assert.equal(source.stopped, 0);
  assert.equal(h.controller.stats.rollovers, 1);
  assert.equal(h.controller.attempt, 0);
});

test("grounding_unavailable is a notice: the session stays connected and the reply still arrives", async () => {
  const h = harness();
  await live(h);
  const transport = h.latest();
  transport.onEvent({ type: "transcript.final", turnId: "t", revision: 1, text: "What does the contract say?" });
  transport.onEvent({ type: "response.started", turnId: "t", responseId: "r" });
  transport.onEvent({ type: "error", code: "grounding_unavailable", message: "Grounding is unavailable.", retryable: true });
  transport.onEvent({ type: "response.completed", turnId: "t", responseId: "r", text: "I need to check that first.", sources: [], grounding: "unavailable" });
  h.clock.advance(10000);
  await flush();
  assert.equal(h.controller.phase, "live");
  assert.equal(h.transports.length, 1);
  assert.equal(h.controller.notice?.code, "grounding_unavailable");
  assert.equal(liveMessage(h.controller.notice!, "browser"), "");
  assert.deepEqual(h.events.map(event => event.type), ["session.ready", "transcript.final", "response.started", "response.completed"]);
  transport.onEvent({ type: "response.started", turnId: "t", responseId: "r2" });
  assert.equal(h.controller.notice, null, "the next reply clears the notice");
});

/**
 * The server's audio budget (MeetingSession: 128 KB burst, refilled at 64 KB/s, charged on ARRIVAL) replayed against what
 * the client sent. Everything sent within `bunchMs` of the flush start arrives together at its end, as a stalled network
 * would deliver it. Returns the lowest budget seen; below zero the server would reject the audio as `invalid_audio`.
 */
function lowestServerBudget(sends: { at: number; bytes: Uint8Array }[], start: number, bunchMs: number): number {
  let budget = 128000, previous = start, lowest = budget;
  for (const send of sends) {
    const arrival = Math.max(send.at, start + bunchMs);
    budget = Math.min(128000, budget + (arrival - previous) * 64);
    previous = arrival;
    budget -= send.bytes.byteLength;
    lowest = Math.min(lowest, budget);
  }
  return lowest;
}

test("Reconnect backlog keeps the newest four seconds and flushes gently: at most 64 KB at once, then at most 48 KB/s, in order", async () => {
  const h = harness();
  await live(h);
  const source = h.sources[0]!;
  h.latest().onError(new CodedError("socket_closed", "dropped"));
  for (let i = 0; i < 300; i++) source.frame(i % 250 + 1); // six seconds spoken while disconnected
  assert.equal(h.controller.stats.droppedFrames, 100);
  h.clock.advance(300);
  await flush();
  const transport = h.latest(), start = h.clock.now();
  transport.ready();
  const burst = transport.binary.filter(item => item.at === start).reduce((total, item) => total + item.bytes.byteLength, 0);
  assert.ok(burst <= 64000, `first burst ${burst}`);
  for (let i = 0; i < 300; i++) { h.clock.advance(20); source.frame(251); } // live audio keeps coming meanwhile
  const frames = transport.binary.flatMap(item => Array.from({ length: item.bytes.byteLength / 640 }, (_, i) => item.bytes[i * 640]!));
  assert.equal(frames.length, 500);
  assert.deepEqual(frames.slice(0, 200), Array.from({ length: 200 }, (_, i) => (i + 100) % 250 + 1), "newest four seconds first, in order");
  assert.ok(frames.slice(200).every(value => value === 251), "then live audio, in order");
  assert.ok(transport.binary.every(item => item.bytes.byteLength <= maxAudioMessageBytes));
  for (let second = 1; second * 1000 <= h.clock.now() - start; second++) {
    const sent = transport.binary.filter(item => item.at <= start + second * 1000).reduce((total, item) => total + item.bytes.byteLength, 0);
    assert.ok(sent <= 64000 + 48000 * second, `${sent} bytes within ${second} s`);
  }
  for (const bunchMs of [0, 150, 300]) {
    const lowest = lowestServerBudget(transport.binary, start, bunchMs);
    assert.ok(lowest >= 0, `server budget ${lowest} with ${bunchMs} ms of bunching`);
  }
  // The previous 120 KB / 60 KB/s client bucket left so little room that 150 ms of bunching broke the server's limit.
  const old = [{ at: start, bytes: new Uint8Array(120000) }, { at: start + 150, bytes: new Uint8Array(9000) }];
  assert.ok(lowestServerBudget(old, start, 150) < 0);
});

test("An audio-rate rejection right after a backlog flush reconnects with the same source; without a flush it stops", async () => {
  const h = harness();
  await live(h);
  const source = h.sources[0]!;
  h.latest().onError(new CodedError("socket_closed", "dropped"));
  for (let i = 0; i < 50; i++) source.frame(1);
  h.clock.advance(300); await flush();
  h.latest().ready(); // starts by flushing the backlog
  h.latest().onEvent({ type: "error", code: "invalid_audio", message: "Audio must be bounded, even-length PCM16LE at real-time speed.", retryable: true });
  assert.equal(h.controller.phase, "live", "the close that follows decides");
  h.latest().onError(new CodedError("server_closed", "The server ended this session."));
  assert.deepEqual(h.disconnects.at(-1), { code: "invalid_audio", disposition: "transient" });
  assert.equal(h.controller.phase, "reconnecting");
  h.clock.advance(3000); await flush();
  h.latest().ready();
  assert.equal(h.controller.phase, "live");
  assert.equal(source.prepared, 1); assert.equal(source.stopped, 0);
  h.latest().onEvent({ type: "error", code: "invalid_audio", message: "x", retryable: true });
  assert.equal(h.controller.phase, "stopped", "a connection that sent no backlog has no such excuse");
  assert.equal(h.controller.failure?.code, "invalid_audio");
});

test("A rollover whose next connection fails shows the attempts, the network wait or the sign-in instead", async () => {
  const h = harness();
  await live(h);
  const limit = (): void => h.latest().onEvent({ type: "error", code: "session_time_limit", message: "limit", retryable: false });
  limit();
  assert.equal(h.controller.rollingOver, true);
  h.latest().onError(new CodedError("socket_error", "failed"));
  assert.equal(h.controller.rollingOver, false);
  assert.equal(h.controller.attempt, 1);
  h.clock.advance(300); await flush(); h.latest().ready();
  limit();
  h.network.up = false;
  h.latest().onError(new CodedError("socket_closed", "failed"));
  assert.equal(h.controller.offline, true);
  assert.equal(h.controller.rollingOver, false);
  h.network.restore(); await flush(); h.latest().ready();
  h.onTransport(transport => { transport.failConnect = new CodedError("signin_required", "Sign-in expired."); });
  limit();
  await flush();
  assert.equal(h.controller.needsSignIn, true);
  assert.equal(h.controller.rollingOver, false);
});

test("A stop in the middle of a reply ends that reply, so nothing looks as if it were still being generated", async () => withMediaStream(async () => {
  const media = displayMedia(), state = new LiveState();
  const h = harness(() => media.create(), { onEvent: event => state.apply(event), onDisconnect: () => state.endReply() });
  await live(h, "display");
  const transport = h.latest();
  transport.onEvent({ type: "transcript.final", turnId: "t1", revision: 1, text: "When can we ship?" });
  transport.onEvent({ type: "response.started", turnId: "t1", responseId: "r1" });
  transport.onEvent({ type: "response.delta", turnId: "t1", responseId: "r1", text: "Ready by Friday.", primaryDone: true });
  assert.equal(state.generating, true);
  transport.onEvent({ type: "error", code: "session_superseded", message: "Live started elsewhere.", retryable: false });
  assert.equal(h.controller.phase, "stopped");
  assert.equal(state.generating, false);
  assert.equal(state.display?.text, "Ready by Friday.", "the finished line stays");
  assert.equal(state.display?.interrupted, true);
  // The same through a share that ends while a draft is still coming.
  h.controller.start("display");
  await flush();
  h.latest().ready();
  h.latest().onEvent({ type: "transcript.final", turnId: "t2", revision: 1, text: "Who owns it?" });
  h.latest().onEvent({ type: "response.started", turnId: "t2", responseId: "r2" });
  h.latest().onEvent({ type: "response.delta", turnId: "t2", responseId: "r2", text: "Alex" });
  media.tracks.filter(track => track.kind === "audio").at(-1)!.onended!();
  await flush();
  assert.equal(h.controller.failure?.capture?.kind, "ended");
  assert.equal(state.generating, false);
  assert.equal(state.current, null);
}));

test("Paused, stopped by the share ending, then 다시 공유: new replies show again", async () => withMediaStream(async () => {
  const media = displayMedia(), state = new LiveState();
  const h = harness(() => media.create(), { onEvent: event => state.apply(event), onDisconnect: () => state.endReply() });
  await live(h, "display");
  h.latest().onEvent({ type: "transcript.final", turnId: "t1", revision: 1, text: "First?" });
  h.latest().onEvent({ type: "response.started", turnId: "t1", responseId: "r1" });
  h.latest().onEvent({ type: "response.completed", turnId: "t1", responseId: "r1", text: "First answer.", sources: [] });
  state.pause(true); h.controller.pause(true); // what the page does on "내가 말할 동안 일시정지"
  media.tracks.find(track => track.kind === "audio")!.onended!();
  await flush();
  assert.equal(h.controller.phase, "stopped");
  state.restart(true); h.controller.start("display"); // what the page does on 다시 공유
  await flush();
  h.latest().ready();
  assert.equal(h.controller.paused, false);
  h.latest().onEvent({ type: "transcript.final", turnId: "t2", revision: 1, text: "Second?" });
  h.latest().onEvent({ type: "response.started", turnId: "t2", responseId: "r2" });
  h.latest().onEvent({ type: "response.delta", turnId: "t2", responseId: "r2", text: "Second answer.", primaryDone: true });
  assert.equal(state.display?.id, "r2");
  h.latest().onEvent({ type: "response.completed", turnId: "t2", responseId: "r2", text: "Second answer.", sources: [] });
  assert.equal(state.display?.complete, true);
  assert.equal(state.turns.length, 2, "the conversation continues");
}));

test("Time offline does not count as reconnect attempts", async () => {
  const h = harness();
  await live(h);
  h.network.up = false;
  h.latest().onError(new CodedError("socket_closed", "dropped"));
  assert.equal(h.controller.offline, true);
  h.clock.advance(10 * 60000);
  await flush();
  assert.equal(h.transports.length, 1, "no connection attempts while offline");
  assert.equal(h.controller.attempt, 0);
  assert.equal(h.controller.phase, "reconnecting");
  h.network.restore();
  await flush();
  assert.equal(h.transports.length, 2);
  assert.equal(h.controller.offline, false);
  h.latest().onError(new CodedError("socket_error", "dropped"));
  assert.equal(h.controller.attempt, 1);
});

test("Twenty quick failures in a row stop with a restart step; a stable connection resets the count", async () => {
  const h = harness();
  await live(h);
  h.clock.advance(liveLimits.stableMs + 1);
  h.onTransport(transport => { transport.failConnect = new TypeError("Failed to fetch"); });
  h.latest().onError(new CodedError("socket_closed", "dropped"));
  assert.equal(h.controller.attempt, 1);
  for (let i = 0; i < 25 && h.controller.phase === "reconnecting"; i++) { h.clock.advance(3000); await flush(); }
  assert.equal(h.controller.phase, "stopped");
  assert.equal(h.controller.failure?.code, "reconnect_exhausted");
  assert.equal(h.controller.failure?.action, "restart");
  assert.equal(h.transports.length, 1 + liveLimits.maxReconnects);
  assert.equal(h.sources[0]!.stopped, 1);
});

test("A sign-in that needs a click keeps capturing and resumes the same source after sign-in", async () => {
  const h = harness();
  h.onTransport(transport => { transport.failConnect = new CodedError("signin_required", "Sign-in expired."); });
  h.controller.start("microphone");
  await flush();
  assert.equal(h.controller.phase, "reconnecting");
  assert.equal(h.controller.needsSignIn, true);
  assert.equal(h.controller.failure?.action, "signin");
  h.clock.advance(60000);
  await flush();
  assert.equal(h.transports.length, 1, "no retries until the user signs in");
  h.sources[0]!.frame(3);
  h.onTransport(() => {});
  h.controller.resume();
  await flush();
  h.latest().ready();
  assert.equal(h.controller.phase, "live");
  assert.equal(h.latest().binary[0]!.bytes[0], 3);
  assert.equal(h.sources[0]!.prepared, 1);
});

test("Fatal server codes stop; transient codes reconnect on the close that follows", async () => {
  const h = harness();
  await live(h);
  h.latest().onEvent({ type: "error", code: "idle_timeout", message: "Session idle timeout.", retryable: true });
  assert.equal(h.controller.phase, "live", "a transient code waits for the close");
  h.latest().onError(new CodedError("server_closed", "The server ended this session."));
  assert.equal(h.disconnects.at(-1)?.code, "idle_timeout");
  h.clock.advance(300); await flush(); h.latest().ready();
  h.latest().onEvent({ type: "error", code: "session_superseded", message: "Live started elsewhere.", retryable: false });
  assert.equal(h.controller.phase, "stopped");
  assert.equal(h.controller.failure?.code, "session_superseded");
  assert.equal(h.sources[0]!.stopped, 1);
});

test("Silence for 15 seconds warns, sound clears it, and a pause or mute never counts as silence", async () => {
  const h = harness();
  await live(h);
  const hooks = h.hooks();
  for (let i = 0; i <= 150; i++) { hooks.level(0); h.clock.advance(100); }
  assert.equal(h.controller.warning, "silent");
  hooks.level(.2);
  assert.equal(h.controller.warning, null);
  hooks.warning("muted");
  assert.equal(h.controller.warning, "muted");
  for (let i = 0; i <= 200; i++) { hooks.level(0); h.clock.advance(100); }
  assert.equal(h.controller.warning, "muted");
  hooks.warning(null);
  h.controller.pause(true);
  for (let i = 0; i <= 200; i++) { hooks.level(0); h.clock.advance(100); }
  assert.equal(h.controller.warning, null);
  const watch = new SilenceWatch(15000, .002);
  assert.equal(watch.update(0, 0, true), false);
  assert.equal(watch.update(0, 15000, true), true);
  assert.equal(watch.silent, true);
  assert.equal(watch.update(0, 16000, false), true);
  assert.equal(watch.silent, false);
});

test("A stalled upload is counted, and four seconds of backlog while connected reconnects instead of failing", async () => {
  const h = harness();
  await live(h);
  const transport = h.latest(), source = h.sources[0]!;
  transport.bufferedAmount = 96000; // the socket cannot take more
  for (let i = 0; i < 10; i++) { h.clock.advance(20); source.frame(1); }
  assert.equal(h.controller.stats.uploadStalls, 1);
  assert.equal(transport.binary.length, 0);
  for (let i = 0; i < 200; i++) { h.clock.advance(20); source.frame(1); }
  assert.equal(transport.closed, true);
  assert.equal(h.disconnects.at(-1)?.code, "upload_backpressure");
  assert.equal(h.controller.phase, "reconnecting");
  assert.equal(source.stopped, 0);
});
