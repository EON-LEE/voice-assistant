import { test } from "node:test";
import assert from "node:assert/strict";
import { CaptureError } from "../../src/VoiceAssistant.Web/src/capture.js";
import { HostAudioSource, ackEveryFrames, decodeFrames, describeHostStatus, hostKind, hostWebView, isHostAudioAvailable, pageSource,
  type HostAudioDependencies, type HostAudioStatus, type HostAudioWarning } from "../../src/VoiceAssistant.Web/src/host-audio.js";
import { classifyError } from "../../src/VoiceAssistant.Web/src/live-errors.js";
import { MeetingSession } from "../../src/VoiceAssistant.Web/src/session.js";
import type { Transport } from "../../src/VoiceAssistant.Web/src/transport.js";
import type { ServerEvent } from "../../src/VoiceAssistant.Web/src/protocol.js";
import { FakeWebView } from "./fake-webview.js";

const tick = (): Promise<void> => new Promise(resolve => setImmediate(resolve));
const sleep = (ms: number): Promise<void> => new Promise(resolve => setTimeout(resolve, ms));

function fixture(overrides: Partial<HostAudioDependencies> = {}) {
  const webview = new FakeWebView(), warnings: (HostAudioWarning | null)[] = [], details: string[] = [];
  const gaps: [number, number][] = [], audio: number[] = [], errors: Error[] = [];
  const source = new HostAudioSource({ webview, source: "teams", id: "c1",
    onWarning: (warning, detail) => { warnings.push(warning); details.push(detail); },
    onGap: (missing, total) => gaps.push([missing, total]), ...overrides });
  const onAudio = (buffer: ArrayBuffer): void => { assert.equal(buffer.byteLength, 640); audio.push(new Uint8Array(buffer)[0]!); };
  return { webview, source, warnings, details, gaps, audio, errors, onAudio, onError: (error: Error) => errors.push(error) };
}
async function prepared(overrides: Partial<HostAudioDependencies> = {}) {
  const f = fixture(overrides);
  const preparing = f.source.prepare(new AbortController().signal);
  f.webview.status("c1", "capturing", "Teams process tree (PID 42)");
  await preparing;
  return f;
}
/** A capture failure of this kind, for the Teams source, that the live page stops on. */
function fatalCapture(kind: string, message: RegExp) {
  return (error: unknown): boolean => error instanceof CaptureError && error.kind === kind && error.source === "teams" &&
    message.test(error.message) && classifyError(error).disposition === "fatal";
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
  assert.deepEqual([pageSource("teams"), pageSource("system"), hostKind("teams"), hostKind("host-system")],
    ["teams", "host-system", "teams", "system"]);
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
  assert.deepEqual(f.warnings, [null]);
  await f.source.stop();
});

test("Teams not running and capture errors are typed warnings; capturing clears them", async () => {
  const statuses: HostAudioStatus[] = [];
  const f = fixture({ onStatus: status => statuses.push(status) });
  const preparing = f.source.prepare(new AbortController().signal);
  f.webview.status("c1", "teams-not-running", "Waiting for Microsoft Teams (ms-teams.exe) to start.");
  await preparing;
  f.source.start(f.onAudio, f.onError);
  assert.equal(f.warnings.at(-1), "teams-not-running");
  f.webview.status("c1", "capturing");
  assert.equal(f.warnings.at(-1), null);
  f.webview.status("c1", "error", "Teams is playing sound, but Windows gives Live Coach only silence for the Teams app.", "teams", "teams-silent");
  assert.equal(f.warnings.at(-1), "teams-silent");
  f.webview.status("c1", "error", "The audio device was removed or changed (0x88890004).", "system");
  assert.equal(f.warnings.at(-1), "capture-error");
  assert.equal(f.details.at(-1), "The audio device was removed or changed (0x88890004).");
  assert.deepEqual(f.errors, []);
  assert.deepEqual(statuses.map(status => [status.state, status.code]),
    [["teams-not-running", undefined], ["capturing", undefined], ["error", "teams-silent"], ["error", undefined]]);
  await f.source.stop();
});

test("An unsupported Windows build, a silent host and abort reject prepare with capture errors and release the host capture", async () => {
  const unsupported = fixture();
  const preparing = unsupported.source.prepare(new AbortController().signal);
  unsupported.webview.status("c1", "unsupported-os", "Capturing only Teams needs Windows 10 build 20348 or later; this PC runs Windows build 19045.");
  await assert.rejects(preparing, fatalCapture("unsupported", /unsupported.*20348/));
  assert.equal(unsupported.webview.sent("host-audio.stop").length, 1);
  assert.equal(unsupported.webview.listening, 0);

  const silent = fixture({ answerTimeoutMs: 10 });
  await assert.rejects(silent.source.prepare(new AbortController().signal), fatalCapture("unsupported", /did not answer/));
  assert.deepEqual(silent.webview.sent("host-audio.stop"), [{ type: "host-audio.stop", id: "c1" }]);

  const aborted = fixture(), abort = new AbortController();
  const cancelled = aborted.source.prepare(abort.signal);
  abort.abort();
  await assert.rejects(cancelled, fatalCapture("cancelled", /cancelled/));
  assert.equal(aborted.webview.sent("host-audio.stop").length, 1);
  await assert.rejects(fixture().source.prepare(abort.signal));

  const closed = fixture();
  closed.webview.postMessage = () => { throw new Error("WebView closed"); };
  await assert.rejects(closed.source.prepare(new AbortController().signal), fatalCapture("ended", /WebView closed/));
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

test("The level meter reports the peak of each 100 ms of host audio, zero while paused or when nothing arrives", async () => {
  const levels: number[] = [];
  const f = await prepared({ onLevel: level => levels.push(level) });
  f.source.start(f.onAudio, f.onError);
  const until = async (check: () => boolean): Promise<void> => {
    for (let waited = 0; !check(); waited += 10) { assert.ok(waited < 3000, "no level report"); await sleep(10); }
  };
  f.webview.samples(0, -16384);
  f.webview.samples(1, 8192);
  await until(() => levels.includes(0.5));
  levels.length = 0;
  await until(() => levels.length > 0);
  assert.equal(levels[0], 0, "a 100 ms without frames reads as silence, so a stalled host also ends in the silence warning");
  f.source.pause(true);
  f.webview.samples(2, 30000);
  levels.length = 0;
  await until(() => levels.length > 0);
  assert.equal(levels[0], 0);
  await f.source.stop();
  assert.equal(levels.at(-1), 0);
  const reports = levels.length;
  await sleep(250);
  assert.equal(levels.length, reports, "stop ends the meter");
});

test("Invalid audio, an unexpected host stop and audio callback errors are reported", async () => {
  const invalid = await prepared();
  invalid.source.start(invalid.onAudio, invalid.onError);
  invalid.webview.host({ type: "host-audio.frame", seq: 0, pcm: Buffer.alloc(100).toString("base64") });
  assert.ok(fatalCapture("ended", /invalid audio/)(invalid.errors[0]));
  assert.equal(classifyError(invalid.errors[0]).action, "restart");
  assert.equal(invalid.webview.sent("host-audio.stop").length, 1);
  invalid.webview.host({ type: "host-audio.frame", seq: -1, pcm: "" });
  assert.equal(invalid.errors.length, 1);

  const stopped = await prepared();
  stopped.source.start(stopped.onAudio, stopped.onError);
  stopped.webview.status("c1", "stopped", "Capture stopped.");
  assert.ok(fatalCapture("ended", /stopped capturing/)(stopped.errors[0]));
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
  assert.equal(f.warnings.at(-1), null);
  f.webview.frames(0, 1);
  assert.deepEqual(f.audio, []);
  assert.throws(() => f.source.start(f.onAudio, f.onError), /no longer available/);
  await assert.rejects(f.source.prepare(new AbortController().signal), /stopped/);
});

test("Host statuses map by state and code: warnings keep the meeting, unsupported and stopped end it", () => {
  const status = (state: HostAudioStatus["state"], code?: string, source: HostAudioStatus["source"] = "teams"): HostAudioStatus =>
    ({ state, detail: "English detail", source, ...(code ? { code } : {}) });
  assert.deepEqual(describeHostStatus(status("capturing")), { fatal: false, warning: null });
  assert.deepEqual(describeHostStatus(status("teams-not-running")), { fatal: false, warning: "teams-not-running" });
  assert.deepEqual(describeHostStatus(status("error", "teams-silent")), { fatal: false, warning: "teams-silent" });
  assert.deepEqual(describeHostStatus(status("error")), { fatal: false, warning: "capture-error" });
  assert.deepEqual(describeHostStatus(status("error", "something-new")), { fatal: false, warning: "capture-error" });
  for (const [state, kind, action] of [["unsupported-os", "unsupported", undefined], ["stopped", "ended", "restart"]] as const) {
    const effect = describeHostStatus(status(state, undefined, "system"));
    assert.ok(effect.fatal);
    assert.deepEqual([effect.error.kind, effect.error.source], [kind, "host-system"]);
    const classified = classifyError(effect.error);
    assert.deepEqual([classified.disposition, classified.action], ["fatal", action]);
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
