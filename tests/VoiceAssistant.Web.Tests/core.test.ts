import { test } from "node:test";
import assert from "node:assert/strict";
import { PcmConverter } from "../../src/VoiceAssistant.Web/src/pcm.js";
import { ReplyState } from "../../src/VoiceAssistant.Web/src/state.js";
import { parseEvent } from "../../src/VoiceAssistant.Web/src/protocol.js";

for (const rate of [16000, 44100, 48000, 96000, 192000]) {
  test(`PCM ${rate}Hz yields exactly 16000 mono samples per second`, () => {
    const converter = new PcmConverter(rate);
    const output: number[] = [];
    converter.process([new Float32Array(rate).fill(.5), new Float32Array(rate).fill(.5)], sample => output.push(sample));
    assert.equal(output.length, 16000);
    assert.ok(output.slice(1000).every(sample => Math.abs(sample - 16384) <= 1));
    const bytes = new ArrayBuffer(2); new DataView(bytes).setInt16(0, output.at(-1)!, true);
    assert.deepEqual([...new Uint8Array(bytes)], [0, 64]);
  });
}
test("Fractional resampling continuity across arbitrary chunks", () => {
  const input = Float32Array.from({ length: 44100 }, (_, i) => .3 * Math.sin(i * .1));
  const expected: number[] = [], actual: number[] = [];
  new PcmConverter(44100).process([input], sample => expected.push(sample));
  const converter = new PcmConverter(44100);
  for (let offset = 0; offset < input.length; offset += 137) converter.process([input.subarray(offset, offset + 137)], sample => actual.push(sample));
  assert.deepEqual(actual, expected);
});
test("Stereo downmix, clipping, nonfinite input and reset", () => {
  const converter = new PcmConverter(48000);
  let samples: number[] = [];
  converter.process([new Float32Array(4800).fill(.8), new Float32Array(4800).fill(-.8)], s => samples.push(s));
  assert.ok(samples.every(s => s === 0));
  samples = []; converter.process([new Float32Array(4800).fill(Infinity)], s => samples.push(s));
  assert.ok(samples.every(s => s === 0));
  samples = []; converter.process([new Float32Array(4800).fill(-3)], s => samples.push(s));
  assert.equal(samples.at(-1), -32768);
  converter.reset(); samples = []; converter.process([new Float32Array(4800)], s => samples.push(s));
  assert.ok(samples.every(s => s === 0));
  assert.throws(() => new PcmConverter(8000));
});
test("Anti-alias filter preserves voice band and rejects 12kHz", () => {
  const rms = (frequency: number): number => {
    const output: number[] = [];
    new PcmConverter(48000).process([Float32Array.from({ length: 48000 }, (_, i) => .5 * Math.sin(2 * Math.PI * frequency * i / 48000))], s => output.push(s));
    return Math.sqrt(output.slice(1000).reduce((sum, n) => sum + (n / 32768) ** 2, 0) / 15000);
  };
  assert.ok(rms(1000) > .34 && rms(1000) < .36);
  assert.ok(rms(12000) < .001);
});
test("Final transcript does not regress and history is bounded", () => {
  const state = new ReplyState();
  state.apply({ type: "transcript.partial", turnId: "a", revision: 3, text: "new" });
  state.apply({ type: "transcript.partial", turnId: "a", revision: 2, text: "old" });
  assert.equal(state.turns[0]!.text, "new");
  state.apply({ type: "transcript.final", turnId: "a", revision: 3, text: "final" });
  state.apply({ type: "transcript.partial", turnId: "a", revision: 4, text: "late" });
  assert.equal(state.turns[0]!.text, "final");
  for (let i = 0; i < 100; i++) state.apply({ type: "transcript.final", turnId: String(i), revision: 1, text: "x" });
  assert.equal(state.turns.length, 64);
});
test("Pin snapshot is immutable across streaming replacement cancellation and reset", () => {
  const state = new ReplyState();
  state.apply({ type: "transcript.final", turnId: "a", revision: 1, text: "Q" });
  state.apply({ type: "response.started", turnId: "a", responseId: "r1" });
  state.apply({ type: "response.delta", turnId: "a", responseId: "r1", text: "keep" });
  state.pin();
  state.apply({ type: "response.delta", turnId: "a", responseId: "r1", text: " more" });
  state.apply({ type: "response.started", turnId: "a", responseId: "r2" });
  state.apply({ type: "response.delta", turnId: "a", responseId: "r1", text: "stale" });
  assert.equal(state.current!.text, "");
  state.apply({ type: "response.completed", turnId: "a", responseId: "r2", text: "new", sources: [], grounding: "unavailable" });
  state.apply({ type: "response.delta", turnId: "a", responseId: "r2", text: "late" });
  assert.equal(state.current!.text, "new");
  assert.equal(state.current!.grounding, "unavailable");
  assert.equal(state.pinned!.text, "keep");
  state.reset(); assert.equal(state.pinned!.text, "keep");
});
test("New turns, pause and explicit cancellation reject obsolete response events", () => {
  const state = new ReplyState();
  state.apply({ type: "transcript.final", turnId: "old", revision: 1, text: "Q" });
  state.apply({ type: "transcript.final", turnId: "new", revision: 1, text: "Q2" });
  state.apply({ type: "response.started", turnId: "old", responseId: "old" });
  assert.equal(state.current, null);
  state.pause(true);
  state.apply({ type: "response.started", turnId: "new", responseId: "paused" });
  assert.equal(state.current, null);
  state.pause(false);
  state.apply({ type: "response.started", turnId: "new", responseId: "new" });
  state.cancel();
  state.apply({ type: "response.delta", turnId: "new", responseId: "new", text: "late" });
  state.apply({ type: "response.started", turnId: "new", responseId: "late" });
  assert.equal(state.current, null);
  state.request();
  state.apply({ type: "response.started", turnId: "new", responseId: "retry" });
  assert.equal(state.current!.id, "retry");
});
test("Strict event contract and 64KiB message cap", () => {
  assert.deepEqual(parseEvent('{"type":"session.ready"}'), { type: "session.ready" });
  assert.throws(() => parseEvent(new ArrayBuffer(2)));
  assert.throws(() => parseEvent("x".repeat(65537)));
  assert.throws(() => parseEvent('{"type":"transcript.final","turnId":"t","revision":-1,"text":"x"}'));
  assert.throws(() => parseEvent('{"type":"response.completed","turnId":"t","responseId":"r","text":"x"}'));
  assert.throws(() => parseEvent('{"type":"error","code":"bad","message":"bad","retryable":"true"}'));
  const result = parseEvent('{"type":"response.completed","turnId":"t","responseId":"r","text":"x","sources":[],"grounding":"no_matches"}');
  assert.equal(result.grounding, "no_matches");
});
