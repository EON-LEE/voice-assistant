import { test } from "node:test";
import assert from "node:assert/strict";
import { createStartMessage, defaultOptions, parsePhrases, validateOptions, validateUpdate, type SessionOptions, type SessionUpdate } from "../../src/VoiceAssistant.Web/src/options.js";
import { RenderScheduler, type RenderClock } from "../../src/VoiceAssistant.Web/src/render-scheduler.js";
import { MeetingSession } from "../../src/VoiceAssistant.Web/src/session.js";
import { parseEvent, type ServerEvent } from "../../src/VoiceAssistant.Web/src/protocol.js";
import { ReplyState } from "../../src/VoiceAssistant.Web/src/state.js";
import type { AudioSource } from "../../src/VoiceAssistant.Web/src/capture.js";
import type { Transport } from "../../src/VoiceAssistant.Web/src/transport.js";

test("Web defaults are balanced500 with no assumed personal facts", () => {
  assert.deepEqual(validateOptions(defaultOptions()), {
    responseMode: "balanced", profile: { name: "", role: "", project: "" }, profileConfirmed: false,
    topic: "", phrases: [], endSilenceMs: 500,
  });
  const first = defaultOptions(); first.profile.name = "Mina";
  assert.equal(defaultOptions().profile.name, "");
});
for (const responseMode of ["balanced", "grounded", "conversation"] as const)
  for (const endSilenceMs of [450, 500, 700, 1000])
    test(`Accepts ${responseMode} at ${endSilenceMs}ms`, () => {
      assert.equal(validateOptions({ ...defaultOptions(), responseMode, endSilenceMs }).endSilenceMs, endSilenceMs);
    });
test("replyKorean is an optional boolean that is sent only when it was set", () => {
  assert.equal("replyKorean" in validateOptions(defaultOptions()), false);
  assert.equal(validateOptions({ ...defaultOptions(), replyKorean: true }).replyKorean, true);
  assert.equal(validateOptions({ ...defaultOptions(), replyKorean: false }).replyKorean, false);
  for (const bad of ["true", 1, null])
    assert.throws(() => validateOptions({ ...defaultOptions(), replyKorean: bad as unknown as boolean }), /replyKorean/);
  const start = createStartMessage({ ...defaultOptions(), replyKorean: true });
  assert.equal("options" in start && start.options.replyKorean, true);
});
test("resumeConversation is an optional boolean that is sent only when it was set", () => {
  assert.equal("resumeConversation" in validateOptions(defaultOptions()), false);
  assert.equal(validateOptions({ ...defaultOptions(), resumeConversation: true }).resumeConversation, true);
  assert.equal(validateOptions({ ...defaultOptions(), resumeConversation: false }).resumeConversation, false);
  for (const bad of ["true", 1, null])
    assert.throws(() => validateOptions({ ...defaultOptions(), resumeConversation: bad as unknown as boolean }), /resumeConversation/);
  const start = createStartMessage({ ...defaultOptions(), resumeConversation: true });
  assert.equal("options" in start && start.options.resumeConversation, true);
});
test("A settings update carries only the known settings, at least one, and is copied", () => {
  assert.deepEqual(validateUpdate({ responseMode: "conversation" }), { responseMode: "conversation" });
  assert.deepEqual(validateUpdate({ replyKorean: false }), { replyKorean: false });
  assert.deepEqual(validateUpdate({ responseMode: "balanced", replyKorean: true }), { responseMode: "balanced", replyKorean: true });
  const input = { responseMode: "grounded" as const };
  assert.notEqual(validateUpdate(input), input);
  for (const bad of [{}, { responseMode: undefined }, { responseMode: "fast" }, { responseMode: 3 }, { replyKorean: "true" },
    { replyKorean: 1 }, { replyKorean: null }, { topic: "x" }, { responseMode: "balanced", topic: "x" }, null, [], "conversation"])
    assert.throws(() => validateUpdate(bad as unknown as SessionUpdate), /./, JSON.stringify(bad));
});
test("A running session sends a validated settings update only while it is ready and not recognition-only", async () => {
  const sent: { type?: string; options?: object }[] = [], errors: Error[] = [];
  let receive: (e: ServerEvent) => void = () => {};
  let broken = false;
  const source: AudioSource = { prepare: async () => {}, start: () => {}, pause: () => {}, stop: async () => {} };
  const transport: Transport = {
    connect: async listener => { receive = listener; },
    send: message => { if (broken) throw new Error("The meeting connection is not open."); sent.push(message); },
    audio: () => {}, close: () => {},
  };
  const session = new MeetingSession(source, transport, () => {}, () => {}, e => errors.push(e), defaultOptions());
  session.update({ replyKorean: true });
  await session.start();
  session.update({ replyKorean: true });
  assert.deepEqual(sent.map(message => message.type), ["session.start"], "nothing is sent before session.ready");
  receive({ type: "session.ready", sessionUpdate: true });
  session.update({ responseMode: "conversation", replyKorean: false });
  assert.deepEqual(sent.at(-1), { type: "session.update", options: { responseMode: "conversation", replyKorean: false } });
  assert.throws(() => session.update({}), /at least one/);
  assert.throws(() => session.update({ responseMode: "fast" } as unknown as SessionUpdate), /Choose/);
  assert.equal(sent.length, 2, "an invalid update never reaches the server");
  broken = true;
  session.update({ replyKorean: true });
  assert.match(errors[0]!.message, /not open/, "a dead connection fails the session like any other command");
  await session.stop();
  broken = false;
  session.update({ replyKorean: true });
  assert.equal(sent.length, 2, "a stopped session sends nothing");

  const recognitionOnly = new MeetingSession(source, transport, () => {}, () => {}, e => errors.push(e),
    { ...defaultOptions(), transcribeOnly: true });
  await recognitionOnly.start();
  receive({ type: "session.ready" });
  const before = sent.length;
  recognitionOnly.update({ replyKorean: true });
  assert.equal(sent.length, before, "a recognition-only session has no replies to change");
  await recognitionOnly.stop();
});
test("Profile requires explicit confirmation and validation copies normalized values", () => {
  const input = { ...defaultOptions(), profile: { name: " Mina ", role: "Tester", project: "Fictional Lumen" }, phrases: [" Lumen "] };
  assert.throws(() => validateOptions(input), /Confirm/);
  input.profileConfirmed = true;
  const options = validateOptions(input);
  input.profile.name = "Changed"; input.phrases[0] = "Changed";
  assert.equal(options.profile.name, "Mina");
  assert.deepEqual(options.phrases, ["Lumen"]);
  assert.deepEqual(parsePhrases(" Lumen \r\n\n   \nAPI"), ["Lumen", "API"]);
});
test("Exact context length and phrase-count boundaries are enforced", () => {
  const input = { ...defaultOptions(), profileConfirmed: true,
    profile: { name: "n".repeat(100), role: "r".repeat(160), project: "p".repeat(300) },
    topic: "t".repeat(300), phrases: Array.from({ length: 32 }, () => "a".repeat(64)) };
  assert.equal(validateOptions(input).phrases.length, 32);
  for (const [key, limit] of [["name", 100], ["role", 160], ["project", 300]] as const)
    assert.throws(() => validateOptions({ ...input, profile: { ...input.profile, [key]: "x".repeat(limit + 1) } }), /at most/);
  assert.throws(() => validateOptions({ ...input, topic: "x".repeat(301) }), /300/);
  assert.throws(() => validateOptions({ ...input, phrases: [...input.phrases, "x"] }), /2048/);
  assert.throws(() => validateOptions({ ...input, phrases: ["x".repeat(65)] }), /64/);
  assert.equal(validateOptions({ ...input, phrases: Array(40).fill("a") }).phrases.length, 40);
  assert.throws(() => validateOptions({ ...input, phrases: Array(41).fill("a") }), /40/);
  assert.throws(() => validateOptions({ ...input, phrases: [" "] }), /empty/);
});
test("Rejects malformed choices instead of silently selecting defaults", () => {
  assert.throws(() => validateOptions({ ...defaultOptions(), responseMode: "fast" as SessionOptions["responseMode"] }));
  for (const endSilenceMs of [NaN, 0, 349, 1501, 450.5]) assert.throws(() => validateOptions({ ...defaultOptions(), endSilenceMs }));
  assert.equal(validateOptions({ ...defaultOptions(), endSilenceMs: 350 }).endSilenceMs, 350);
  assert.equal(validateOptions({ ...defaultOptions(), endSilenceMs: 1500 }).endSilenceMs, 1500);
});
test("Maximum Unicode and escaped context fit the explicit32KiB startup contract", () => {
  for (const character of ["한", "\\"]) {
    const options = { ...defaultOptions(), profileConfirmed: true,
      profile: { name: character.repeat(100), role: character.repeat(160), project: character.repeat(300) },
      topic: character.repeat(300), phrases: Array.from({ length: 32 }, () => character.repeat(64)) };
    const payload = JSON.stringify(createStartMessage(options));
    assert.ok(new TextEncoder().encode(payload).byteLength > 4096);
    assert.ok(new TextEncoder().encode(payload).byteLength <= 32768);
    assert.equal(JSON.parse(payload).options.profile.name, options.profile.name);
  }
  assert.equal("options" in createStartMessage(), false);
});
test("Control characters are rejected before capture rather than failing the server handshake", () => {
  for (const character of ["\u0001", "\u007f", "\u0085", "\n", "\t"]) {
    assert.throws(() => validateOptions({ ...defaultOptions(), topic: `topic${character}text` }), /control characters/);
    assert.throws(() => validateOptions({ ...defaultOptions(), phrases: [`term${character}text`] }), /control characters/);
    assert.throws(() => validateOptions({ ...defaultOptions(), profileConfirmed: true,
      profile: { name: `name${character}text`, role: "", project: "" } }), /control characters/);
  }
});
test("Start validates before capture and sends a stable snapshot of supplied options", async () => {
  let prepared = 0;
  const messages: object[] = [], errors: Error[] = [];
  let receive: (e: ServerEvent) => void = () => {};
  const options = { ...defaultOptions(), profile: { name: "Mina", role: "", project: "" }, profileConfirmed: true };
  const source: AudioSource = {
    prepare: async () => { prepared++; options.profile.name = "changed during setup"; },
    start: () => {}, pause: () => {}, stop: async () => {},
  };
  const transport: Transport = {
    connect: async listener => { receive = listener; }, send: message => { messages.push(message); },
    audio: () => {}, close: () => {},
  };
  const session = new MeetingSession(source, transport, () => {}, () => {}, e => errors.push(e), options);
  await session.start(); receive({ type: "session.ready" });
  assert.equal(prepared, 1);
  assert.equal((messages[0] as { options: SessionOptions }).options.profile.name, "Mina");
  await session.stop();
  const invalid = new MeetingSession(source, transport, () => {}, () => {}, e => errors.push(e),
    { ...defaultOptions(), profile: { name: "Mina", role: "", project: "" } });
  await invalid.start();
  assert.equal(prepared, 1);
  assert.match(errors[0]!.message, /Confirm/);
});

class Clock implements RenderClock {
  callbacks = new Map<number, () => void>();
  next = 0;
  set(callback: () => void, delay: number): number {
    assert.equal(delay, 50); this.callbacks.set(++this.next, callback); return this.next;
  }
  clear(handle: unknown): void { this.callbacks.delete(handle as number); }
  tick(): void { const callbacks = [...this.callbacks.values()]; this.callbacks.clear(); callbacks.forEach(callback => callback()); }
}
test("A streaming flood schedules one bounded render using latest state", () => {
  const clock = new Clock(); let latest = 0, displayed = 0, renders = 0;
  const scheduler = new RenderScheduler(() => { displayed = latest; renders++; }, clock);
  for (let i = 1; i <= 10000; i++) { latest = i; scheduler.schedule(); }
  assert.equal(clock.callbacks.size, 1); assert.equal(renders, 0);
  clock.tick(); assert.equal(renders, 1); assert.equal(displayed, 10000);
});
test("Completion/control flush is immediate and cancels stale pending callbacks", () => {
  const clock = new Clock(); let renders = 0;
  const scheduler = new RenderScheduler(() => renders++, clock);
  scheduler.schedule(); scheduler.flush();
  assert.equal(renders, 1); assert.equal(clock.callbacks.size, 0);
  clock.tick(); assert.equal(renders, 1);
  scheduler.schedule(); scheduler.cancel(); clock.tick();
  assert.equal(renders, 1);
  scheduler.schedule(); clock.tick(); assert.equal(renders, 2);
});
test("Optional routing metadata is validated, stored and pinned independently", () => {
  const state = new ReplyState();
  state.apply({ type: "transcript.final", turnId: "t", revision: 1, text: "Q" });
  state.apply({ type: "response.started", turnId: "t", responseId: "r" });
  const completion = { type: "response.completed", turnId: "t", responseId: "r", text: "A", sources: [],
    grounding: "disabled", responseRoute: "profile", retrievalPrefetched: false };
  state.apply(parseEvent(JSON.stringify(completion))); state.pin();
  assert.equal(state.pinned!.responseRoute, "profile"); assert.equal(state.pinned!.retrievalPrefetched, false);
  for (const grounding of ["disabled", "grounded", "unavailable", "no_matches"])
    assert.equal(parseEvent(JSON.stringify({ ...completion, grounding })).grounding, grounding);
  assert.throws(() => parseEvent(JSON.stringify({ ...completion, responseRoute: "<script>" })), /route/);
  assert.throws(() => parseEvent(JSON.stringify({ ...completion, retrievalPrefetched: "yes" })), /prefetch/);
  const legacy = { ...completion, responseRoute: undefined, retrievalPrefetched: undefined };
  assert.equal(parseEvent(JSON.stringify(legacy)).responseRoute, undefined);
});
