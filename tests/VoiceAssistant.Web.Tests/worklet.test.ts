import { test } from "node:test";
import assert from "node:assert/strict";
import { PcmConverter } from "../../src/VoiceAssistant.Web/src/pcm.js";
import { audioBufferCount, audioFrameBytes } from "../../src/VoiceAssistant.Web/src/audio-limits.js";

interface Frame { type: string; buffer?: ArrayBuffer; epoch?: number; message?: string }
class Port {
  onmessage: ((event: { data: Frame & { paused?: boolean } }) => void) | undefined;
  sent: Frame[] = [];
  postMessage(message: Frame, transfer: ArrayBuffer[] = []): void {
    this.sent.push(structuredClone(message, { transfer }));
  }
}
interface Processor { port: Port; process(inputs: Float32Array[][]): boolean }
let ProcessorClass: new () => Processor;
Reflect.set(globalThis, "sampleRate", 48000);
Reflect.set(globalThis, "AudioWorkletProcessor", class { readonly port = new Port(); });
Reflect.set(globalThis, "registerProcessor", (_name: string, processor: new () => Processor) => { ProcessorClass = processor; });
await import("../../src/VoiceAssistant.Web/src/audio.worklet.js");

function processor(credits = audioBufferCount): Processor {
  const instance = new ProcessorClass();
  for (let i = 0; i < credits; i++) instance.port.onmessage!({ data: { type: "buffer", buffer: new ArrayBuffer(640) } });
  instance.port.onmessage!({ data: { type: "pause", paused: false, epoch: 1 } });
  return instance;
}
function render(instance: Processor, blocks: number, channels = 1, value = .5): void {
  const input = Array.from({ length: channels }, () => new Float32Array(128).fill(value));
  for (let i = 0; i < blocks; i++) instance.process([input]);
}
test("Worklet exhausts bounded credits once then fails explicitly without further messages", () => {
  const instance = processor();
  render(instance, 2000);
  assert.equal(instance.port.sent.filter(m => m.type === "audio").length, audioBufferCount);
  assert.equal(instance.port.sent.filter(m => m.type === "error").length, 1);
  assert.match(instance.port.sent.at(-1)!.message!, /cannot keep up/);
});
test("A 300 ms main-thread stall retains every PCM frame and resumes without loss", () => {
  const instance = processor();
  render(instance, 113); // 301.3 ms at 48 kHz: longer than the old eight-frame/160 ms pool.
  const initial = instance.port.sent.filter(m => m.type === "audio");
  assert.ok(initial.length >= 14 && initial.length <= 16);
  assert.equal(instance.port.sent.filter(m => m.type === "error").length, 0);
  for (const message of initial) {
    assert.equal(message.buffer!.byteLength, audioFrameBytes);
    instance.port.onmessage!({ data: { type: "buffer", buffer: message.buffer } });
  }
  const before = initial.length;
  render(instance, 113);
  assert.ok(instance.port.sent.filter(m => m.type === "audio").length >= before + 14);
  assert.equal(instance.port.sent.filter(m => m.type === "error").length, 0);
});
test("Excess credits never grow the native pool beyond one second", () => {
  const instance = processor(audioBufferCount + 10);
  render(instance, 2000);
  assert.equal(instance.port.sent.filter(m => m.type === "audio").length, audioBufferCount);
  assert.equal(instance.port.sent.filter(m => m.type === "error").length, 1);
});
for (const channels of [1, 2, 6, 32]) {
  test(`Native worklet algorithm emits PCM16LE with ${channels} input channels`, () => {
    const instance = processor();
    render(instance, 16, channels);
    const frames = instance.port.sent.filter(m => m.type === "audio");
    assert.equal(frames.length, 2);
    const frame = frames[1]!;
    assert.equal(frame.buffer!.byteLength, 640);
    assert.equal(frame.epoch, 1);
    const bytes = new Uint8Array(frame.buffer!);
    assert.equal(bytes[638], 0); assert.equal(bytes[639], 64);
  });
}
test("Pause discards partial samples; epoch and converter state reset on resume", () => {
  const instance = processor();
  render(instance, 7, 1, -.5);
  assert.equal(instance.port.sent.length, 0);
  instance.port.onmessage!({ data: { type: "pause", paused: true, epoch: 2 } });
  render(instance, 30);
  assert.equal(instance.port.sent.length, 0);
  instance.port.onmessage!({ data: { type: "pause", paused: false, epoch: 3 } });
  render(instance, 8, 1, 0);
  const frame = instance.port.sent[0]!;
  assert.equal(frame.epoch, 3);
  assert.ok(new Uint8Array(frame.buffer!).every(n => n === 0));
});
test("Empty disconnected input is allowed; invalid channel shape fails visibly", () => {
  const converter = new PcmConverter(48000);
  converter.process([], () => assert.fail("No samples expected"));
  assert.throws(() => converter.process([new Float32Array(128), new Float32Array(127)], () => {}), /Mismatched/);
  assert.throws(() => converter.process(Array.from({ length: 33 }, () => new Float32Array(128)), () => {}), /channels/);
});
