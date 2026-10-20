import { test } from "node:test";
import assert from "node:assert/strict";
import { HostAudioSource, ackEveryFrames, decodeFrames, describeHostStatus, hostWebView, isHostAudioAvailable,
  type HostAudioDependencies, type HostMessageEvent, type HostWebView } from "../../src/VoiceAssistant.Web/src/host-audio.js";
import { MeetingSession } from "../../src/VoiceAssistant.Web/src/session.js";
import { isTransientFailure } from "../../src/VoiceAssistant.Web/src/live-state.js";
import type { Transport } from "../../src/VoiceAssistant.Web/src/transport.js";
import type { ServerEvent } from "../../src/VoiceAssistant.Web/src/protocol.js";

const tick = (): Promise<void> => new Promise(resolve => setImmediate(resolve));
type Posted = { type: string; source?: string; id?: string; seq?: number };

/** window.chrome.webview as LiveCoach.exe provides it: JSON text both ways. */
class FakeWebView implements HostWebView {
  readonly posted: Posted[] = [];
  private readonly listeners = new Set<(event: HostMessageEvent) => void>();
  postMessage(message: string): void { this.posted.push(JSON.parse(message) as Posted); }
  addEventListener(_type: "message", listener: (event: HostMessageEvent) => void): void { this.listeners.add(listener); }
  removeEventListener(_type: "message", listener: (event: HostMessageEvent) => void): void { this.listeners.delete(listener); }
  get listening(): number { return this.listeners.size; }
  sent(type: string): Posted[] { return this.posted.filter(message => message.type === type); }
  host(message: object): void { for (const listener of [...this.listeners]) listener({ data: JSON.stringify(message) }); }
  status(id: string, state: string, detail = "", source = "teams"): void { this.host({ type: "host-audio.status", id, source, state, detail }); }
  frames(seq: number, ...markers: number[]): void {
    this.host({ type: "host-audio.frame", seq, pcm: Buffer.concat(markers.map(marker => Buffer.alloc(640, marker))).toString("base64") });
  }
}
function fixture(overrides: Partial<HostAudioDependencies> = {}) {
  const webview = new FakeWebView(), warnings: string[] = [], gaps: [number, number][] = [], audio: number[] = [], errors: Error[] = [];
  const source = new HostAudioSource({ webview, source: "teams", id: "c1", onWarning: message => warnings.push(message),
    onGap: (missing, total) => gaps.push([missing, total]), ...overrides });
  const onAudio = (buffer: ArrayBuffer): void => { assert.equal(buffer.byteLength, 640); audio.push(new Uint8Array(buffer)[0]!); };
  return { webview, source, warnings, gaps, audio, errors, onAudio, onError: (error: Error) => errors.push(error) };
}
async function prepared(overrides: Partial<HostAudioDependencies> = {}) {
  const f = fixture(overrides);
  const preparing = f.source.prepare(new AbortController().signal);
  f.webview.status("c1", "capturing", "Teams process tree (PID 42)");
  await preparing;
  return f;
}

test("Host audio is offered only by LiveCoach's WebView2 bridge with a matching capability", () => {
  const webview = new FakeWebView();
  const shell = (sources: unknown, version: unknown = 1) => ({ chrome: { webview }, __liveShell: { hostAudio: { version, sources } } });
  assert.equal(isHostAudioAvailable(undefined, {}), false);
  assert.equal(isHostAudioAvailable(undefined, { chrome: { webview } }), false);
  assert.equal(isHostAudioAvailable(undefined, { chrome: { webview: { postMessage: "x" } }, __liveShell: shell(["teams"]).__liveShell }), false);
  assert.equal(isHostAudioAvailable(undefined, shell(["teams", "system"])), true);
  assert.equal(isHostAudioAvailable("teams", shell(["teams", "system"])), true);
  assert.equal(isHostAudioAvailable("teams", shell(["system"])), false);
  assert.equal(isHostAudioAvailable("system", shell(["system"])), true);
  assert.equal(isHostAudioAvailable(undefined, shell(["teams"], 2)), false);
  assert.equal(isHostAudioAvailable(undefined, shell("teams")), false);
  assert.equal(isHostAudioAvailable(), false);
  assert.equal(hostWebView({ chrome: { webview } }), webview);
});

test("Prepare asks the host for the chosen source and waits for this capture's own status", async () => {
  const f = fixture();
  let ready = false;
  const preparing = f.source.prepare(new AbortController().signal).then(() => { ready = true; });
  assert.deepEqual(f.webview.posted, [{ type: "host-audio.start", source: "teams", id: "c1" }]);
  f.webview.status("older", "capturing");
  f.webview.frames(0, 9);
  await tick();
  assert.equal(ready, false);
  f.webview.status("c1", "capturing", "Teams process tree (PID 42)");
  await preparing;
  assert.equal(ready, true);
  assert.deepEqual(f.warnings, [""]);
  await f.source.stop();
});

test("Teams not running and capture errors are warnings; capturing clears them", async () => {
  const statuses: string[] = [];
  const f = fixture({ onStatus: status => statuses.push(status.state) });
  const preparing = f.source.prepare(new AbortController().signal);
  f.webview.status("c1", "teams-not-running", "Waiting for Microsoft Teams (ms-teams.exe) to start.");
  await preparing;
  f.source.start(f.onAudio, f.onError);
  assert.match(f.warnings.at(-1)!, /Teams is not running/);
  f.webview.status("c1", "capturing");
  assert.equal(f.warnings.at(-1), "");
  f.webview.status("c1", "error", "Teams is playing sound, but Windows gives Live Coach only silence for the Teams app.");
  assert.match(f.warnings.at(-1)!, /^Teams audio: Teams is playing sound/);
  f.webview.status("c1", "error", "The audio device was removed or changed (0x88890004).", "system");
  assert.match(f.warnings.at(-1)!, /^System audio: The audio device was removed/);
  assert.deepEqual(f.errors, []);
  assert.deepEqual(statuses, ["teams-not-running", "capturing", "error", "error"]);
  await f.source.stop();
});

test("An unsupported Windows build, a silent host and abort reject prepare and release the host capture", async () => {
  const unsupported = fixture();
  const preparing = unsupported.source.prepare(new AbortController().signal);
  unsupported.webview.status("c1", "unsupported-os", "Capturing only Teams needs Windows 10 build 20348 or later; this PC runs Windows build 19045.");
  await assert.rejects(preparing, (error: Error) => /unsupported.*20348/.test(error.message) && !isTransientFailure(error.message));
  assert.equal(unsupported.webview.sent("host-audio.stop").length, 1);
  assert.equal(unsupported.webview.listening, 0);

  const silent = fixture({ answerTimeoutMs: 10 });
  await assert.rejects(silent.source.prepare(new AbortController().signal),
    (error: Error) => /did not answer/.test(error.message) && !isTransientFailure(error.message));
  assert.deepEqual(silent.webview.sent("host-audio.stop"), [{ type: "host-audio.stop", id: "c1" }]);

  const aborted = fixture(), abort = new AbortController();
  const cancelled = aborted.source.prepare(abort.signal);
  abort.abort();
  await assert.rejects(cancelled, /cancelled/);
  assert.equal(aborted.webview.sent("host-audio.stop").length, 1);
  await assert.rejects(fixture().source.prepare(abort.signal));

  const closed = fixture();
  closed.webview.postMessage = () => { throw new Error("WebView closed"); };
  await assert.rejects(closed.source.prepare(new AbortController().signal), /WebView closed/);
  assert.equal(closed.webview.listening, 0);
  await tick();
});

test("Frames decode to 640-byte buffers, flow only after start and never while paused", async () => {
  const f = await prepared();
  f.webview.frames(0, 1);
  f.source.start(f.onAudio, f.onError);
  f.webview.frames(1, 2, 3, 4);
  f.source.pause(true);
  f.webview.frames(4, 5);
  f.source.pause(false);
  f.webview.frames(5, 6);
  assert.deepEqual(f.audio, [2, 3, 4, 6]);
  assert.deepEqual(f.gaps, []);
  const frames = decodeFrames(Buffer.concat([Buffer.alloc(640, 7), Buffer.alloc(640, 8)]).toString("base64"));
  assert.deepEqual(frames.map(frame => [frame.byteLength, new Uint8Array(frame)[639]]), [[640, 7], [640, 8]]);
  for (const bad of ["", "%%%", Buffer.alloc(641).toString("base64"), Buffer.alloc(640 * 6).toString("base64")])
    assert.throws(() => decodeFrames(bad), /invalid audio/);
  await f.source.stop();
});

test("Seq gaps are counted and reported; repeated or older frames are ignored", async () => {
  const f = await prepared();
  f.source.start(f.onAudio, f.onError);
  f.webview.frames(0, 10);
  f.webview.frames(1, 11);
  f.webview.frames(4, 14, 15);
  f.webview.frames(3, 13);
  f.webview.frames(5, 15);
  f.webview.frames(9, 19);
  assert.deepEqual(f.audio, [10, 11, 14, 15, 19]);
  assert.deepEqual(f.gaps, [[2, 2], [3, 5]]);
  await f.source.stop();
});

test("Frames from before this capture's first status are ignored, and a capture's own seq starts at 0", async () => {
  const f = fixture();
  const preparing = f.source.prepare(new AbortController().signal);
  f.webview.frames(700, 1);
  f.webview.status("c1", "capturing");
  await preparing;
  f.source.start(f.onAudio, f.onError);
  f.webview.frames(2, 3);
  assert.deepEqual(f.audio, [3]);
  assert.deepEqual(f.gaps, [[2, 2]]);
  await f.source.stop();
});

test("Every few frames are acknowledged with the capture id, also while paused", async () => {
  const f = await prepared();
  f.source.pause(true);
  for (let seq = 0; seq < ackEveryFrames + 2; seq++) f.webview.frames(seq, seq);
  f.webview.frames(ackEveryFrames + 2, 1, 2, 3, 4, 5);
  assert.deepEqual(f.webview.sent("host-audio.ack"), [
    { type: "host-audio.ack", seq: ackEveryFrames - 1, id: "c1" },
    { type: "host-audio.ack", seq: ackEveryFrames + 6, id: "c1" },
  ]);
  await f.source.stop();
});

test("Invalid audio, an unexpected host stop and audio callback errors are reported", async () => {
  const invalid = await prepared();
  invalid.source.start(invalid.onAudio, invalid.onError);
  invalid.webview.host({ type: "host-audio.frame", seq: 0, pcm: Buffer.alloc(100).toString("base64") });
  assert.match(invalid.errors[0]!.message, /invalid audio/);
  assert.equal(invalid.webview.sent("host-audio.stop").length, 1);
  invalid.webview.host({ type: "host-audio.frame", seq: -1, pcm: "" });
  assert.equal(invalid.errors.length, 1);

  const stopped = await prepared();
  stopped.source.start(stopped.onAudio, stopped.onError);
  stopped.webview.status("c1", "stopped", "Capture stopped.");
  assert.match(stopped.errors[0]!.message, /stopped capturing/);
  assert.equal(isTransientFailure(stopped.errors[0]!.message), false);
  assert.throws(() => stopped.source.start(stopped.onAudio, stopped.onError), /stopped capturing/);

  const throwing = await prepared();
  throwing.source.start(() => { throw new Error("transport closed"); }, throwing.onError);
  throwing.webview.frames(0, 1, 2);
  assert.deepEqual(throwing.errors.map(error => error.message), ["transport closed"]);
  await throwing.source.stop();
});

test("Stop is idempotent, releases the host once and ignores later messages", async () => {
  const f = await prepared();
  f.source.start(f.onAudio, f.onError);
  await f.source.stop();
  await f.source.stop();
  assert.deepEqual(f.webview.sent("host-audio.stop"), [{ type: "host-audio.stop", id: "c1" }]);
  assert.equal(f.webview.listening, 0);
  assert.equal(f.warnings.at(-1), "");
  f.webview.frames(0, 1);
  assert.deepEqual(f.audio, []);
  assert.throws(() => f.source.start(f.onAudio, f.onError), /no longer available/);
  await assert.rejects(f.source.prepare(new AbortController().signal), /stopped/);
});

test("Host status wording keeps fatal errors out of the automatic reconnect", () => {
  assert.deepEqual(describeHostStatus({ state: "capturing", detail: "x", source: "teams" }), { fatal: false, message: "" });
  assert.equal(describeHostStatus({ state: "teams-not-running", detail: "", source: "teams" }).fatal, false);
  for (const state of ["unsupported-os", "stopped"] as const) {
    const effect = describeHostStatus({ state, detail: "", source: "teams" });
    assert.equal(effect.fatal, true);
    assert.equal(isTransientFailure(effect.message), false);
  }
});

test("A meeting session streams host frames to the transport only after session.ready", async () => {
  const webview = new FakeWebView();
  let receive: (event: ServerEvent) => void = () => {};
  const sent: object[] = [];
  let frames = 0, closed = false;
  const transport: Transport = { connect: async onEvent => { receive = onEvent; }, send: message => { sent.push(message); },
    audio: buffer => { assert.equal(buffer.byteLength, 640); frames++; }, close: () => { closed = true; } };
  const source = new HostAudioSource({ webview, source: "teams", id: "s1", onWarning: () => {} });
  const session = new MeetingSession(source, transport, () => {}, () => {}, () => {});
  const starting = session.start();
  assert.equal(webview.sent("host-audio.start").length, 1);
  webview.status("s1", "capturing");
  await starting;
  webview.frames(0, 1);
  assert.equal(frames, 0);
  receive({ type: "session.ready" });
  webview.frames(1, 1, 1);
  assert.equal(frames, 2);
  session.pause(true);
  webview.frames(3, 1);
  assert.equal(frames, 2);
  await session.stop();
  assert.equal(closed, true);
  assert.equal(webview.sent("host-audio.stop").length, 1);
  assert.ok(sent.some(message => (message as { type?: string }).type === "session.stop"));
});
