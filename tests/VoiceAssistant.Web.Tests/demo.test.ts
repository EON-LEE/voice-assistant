import { test } from "node:test";
import assert from "node:assert/strict";
import { DemoAudioSource } from "../../src/VoiceAssistant.Web/src/demo.js";

class Player extends EventTarget {
  src = ""; currentTime = 0; ended = false; plays = 0; pauses = 0; loads = 0;
  rejectPlay = false;
  async play(): Promise<void> { this.plays++; if (this.rejectPlay) throw new Error("blocked"); }
  pause(): void { this.pauses++; }
  load(): void { this.loads++; }
  removeAttribute(name: string): void { if (name === "src") this.src = ""; }
  finish(): void { this.ended = true; this.dispatchEvent(new Event("ended")); }
}

test("Demo starts audible playback from prepare and finishes input exactly once at recording end", async () => {
  const player = new Player(), source = new DemoAudioSource(player, "/sample.wav");
  const pending = source.prepare(new AbortController().signal);
  assert.equal(player.plays, 1);
  await pending;
  const frames: ArrayBuffer[] = [];
  source.start(buffer => frames.push(buffer), error => { throw error; });
  assert.equal(frames.length, 0);
  player.finish(); player.finish();
  assert.equal(frames.length, 1);
  assert.equal(frames[0]!.byteLength, 640);
  await source.stop();
  assert.equal(player.src, "");
  assert.equal(player.loads, 1);
});

test("Demo preserves recording completion that precedes the session-ready event", async () => {
  const player = new Player(), source = new DemoAudioSource(player, "/sample.wav");
  await source.prepare(new AbortController().signal);
  player.finish();
  let frames = 0;
  source.start(() => frames++, error => { throw error; });
  assert.equal(frames, 1);
  await source.stop();
});

test("Demo pause/resume controls the recording and stop detaches all playback callbacks", async () => {
  const player = new Player(), source = new DemoAudioSource(player, "/sample.wav");
  await source.prepare(new AbortController().signal);
  let frames = 0, errors = 0;
  source.start(() => frames++, () => errors++);
  source.pause(true);
  assert.equal(player.pauses, 1);
  source.pause(false);
  assert.equal(player.plays, 2);
  await source.stop(); await source.stop();
  player.finish(); player.dispatchEvent(new Event("error"));
  assert.equal(frames, 0); assert.equal(errors, 0);
  assert.equal(player.loads, 1);
});

test("Demo playback rejection is explicit and never becomes a successful silent demo", async () => {
  const player = new Player(), source = new DemoAudioSource(player, "/sample.wav");
  player.rejectPlay = true;
  await assert.rejects(source.prepare(new AbortController().signal), /could not play/);
  assert.equal(player.src, "");
  assert.equal(player.loads, 1);
});

test("Recording completion while paused is delivered only when the demo resumes", async () => {
  const player = new Player(), source = new DemoAudioSource(player, "/sample.wav");
  await source.prepare(new AbortController().signal);
  let frames = 0;
  source.start(() => frames++, error => { throw error; });
  source.pause(true); player.finish();
  assert.equal(frames, 0);
  source.pause(false);
  assert.equal(frames, 1);
  assert.equal(player.plays, 1);
  await source.stop();
});

test("Aborting or failing an active demo releases audio and reports failure", async () => {
  const player = new Player(), source = new DemoAudioSource(player, "/sample.wav");
  const abort = new AbortController();
  await source.prepare(abort.signal);
  const errors: Error[] = [];
  source.start(() => {}, error => errors.push(error));
  player.dispatchEvent(new Event("error"));
  assert.match(errors[0]!.message, /could not be loaded/);
  abort.abort();
  assert.equal(player.src, "");
  assert.equal(player.loads, 1);
});
