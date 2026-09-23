import { test } from "node:test";
import assert from "node:assert/strict";
import { createStartMessage, defaultOptions, parsePhrases, validateOptions, type SessionOptions } from "../../src/VoiceAssistant.Web/src/options.js";
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
  for (const character of ["한", "\u0001"]) {
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
