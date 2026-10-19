import { test } from "node:test";
import assert from "node:assert/strict";
import { PushedKorean, pushedKoreanWaitMs } from "../../src/VoiceAssistant.Web/src/reply-korean.js";
import type { ServerEvent } from "../../src/VoiceAssistant.Web/src/protocol.js";

const hangul = (korean: string) => /[\uac00-\ud7a3]/u.test(korean);
const line = (responseId: string, index: number, korean?: string): ServerEvent =>
  korean === undefined
    ? { type: "response.korean", turnId: "t", responseId, index, failed: true }
    : { type: "response.korean", turnId: "t", responseId, index, korean };
const open = () => new AbortController().signal;
const flush = () => new Promise<void>(resolve => setImmediate(resolve));

test("A line that arrived before anyone asked is returned without waiting", async () => {
  const pushed = new PushedKorean(hangul);
  pushed.record(line("r1", 0, "검토부터 하겠습니다."));
  assert.equal(pushed.peek("r1", 0), "검토부터 하겠습니다.");
  assert.equal(pushed.peek("r1", 1), undefined);
  assert.equal(await pushed.wait("r1", 0, pushedKoreanWaitMs, open()), "검토부터 하겠습니다.");
});

test("A waiting line resolves when its own Korean arrives and ignores other lines and responses", async t => {
  t.mock.timers.enable({ apis: ["setTimeout"] });
  const pushed = new PushedKorean(hangul);
  const waiting = pushed.wait("r1", 1, pushedKoreanWaitMs, open());
  let settled = false;
  void waiting.then(() => { settled = true; });
  pushed.record(line("r1", 0, "첫째 줄입니다."));
  pushed.record(line("r2", 1, "다른 응답입니다."));
  await flush();
  assert.equal(settled, false);
  pushed.record(line("r1", 1, "함께 확인해 볼까요?"));
  assert.equal(await waiting, "함께 확인해 볼까요?");
});

test("A failed line, or Korean the client rejects, resolves to null so the caller translates it itself", async () => {
  const pushed = new PushedKorean(hangul);
  const waiting = pushed.wait("r1", 0, pushedKoreanWaitMs, open());
  pushed.record(line("r1", 0));
  assert.equal(await waiting, null);
  assert.equal(pushed.peek("r1", 0), null);
  pushed.record(line("r1", 1, "Review the plan first."));
  assert.equal(pushed.peek("r1", 1), null);
  assert.equal(await pushed.wait("r1", 1, pushedKoreanWaitMs, open()), null);
});

test("A line that is late resolves to null after exactly the wait time, and a late arrival is still remembered", async t => {
  t.mock.timers.enable({ apis: ["setTimeout"] });
  const pushed = new PushedKorean(hangul);
  const waiting = pushed.wait("r1", 0, pushedKoreanWaitMs, open());
  let settled = false;
  void waiting.then(() => { settled = true; });
  t.mock.timers.tick(pushedKoreanWaitMs - 1);
  await flush();
  assert.equal(settled, false);
  t.mock.timers.tick(1);
  assert.equal(await waiting, null);
  pushed.record(line("r1", 0, "늦게 도착했습니다."));
  assert.equal(pushed.peek("r1", 0), "늦게 도착했습니다.");
});

test("Aborting ends the wait at once, and an aborted signal never starts one", async t => {
  t.mock.timers.enable({ apis: ["setTimeout"] });
  const pushed = new PushedKorean(hangul);
  const controller = new AbortController();
  const waiting = pushed.wait("r1", 0, pushedKoreanWaitMs, controller.signal);
  controller.abort();
  assert.equal(await waiting, null);
  assert.equal(await pushed.wait("r1", 1, pushedKoreanWaitMs, controller.signal), null);
  pushed.record(line("r1", 0, "중단된 뒤에 도착했습니다."));
  assert.equal(pushed.peek("r1", 0), "중단된 뒤에 도착했습니다.");
});

test("A retry or a connection without the push does not wait: it returns what is known, otherwise null", async () => {
  const pushed = new PushedKorean(hangul);
  assert.equal(await pushed.wait("r1", 0, 0, open()), null);
  pushed.record(line("r1", 0, "이미 도착했습니다."));
  assert.equal(await pushed.wait("r1", 0, 0, open()), "이미 도착했습니다.");
});

test("Every waiter of one line resolves, and expire() releases the others without inventing a value", async t => {
  t.mock.timers.enable({ apis: ["setTimeout"] });
  const pushed = new PushedKorean(hangul);
  const first = pushed.wait("r1", 0, pushedKoreanWaitMs, open());
  const again = pushed.wait("r1", 0, pushedKoreanWaitMs, open());
  const otherLine = pushed.wait("r1", 1, pushedKoreanWaitMs, open());
  const otherResponse = pushed.wait("r2", 0, pushedKoreanWaitMs, open());
  pushed.record(line("r1", 0, "둘 다 받습니다."));
  assert.deepEqual(await Promise.all([first, again]), ["둘 다 받습니다.", "둘 다 받습니다."]);
  pushed.expire();
  assert.deepEqual(await Promise.all([otherLine, otherResponse]), [null, null]);
  assert.equal(pushed.peek("r1", 0), "둘 다 받습니다.", "expire keeps what already arrived");
  assert.equal(pushed.peek("r1", 1), undefined);
});

test("The first line recorded for a reply line wins", () => {
  const pushed = new PushedKorean(hangul);
  pushed.record(line("r1", 0, "처음입니다."));
  pushed.record(line("r1", 0, "나중입니다."));
  pushed.record(line("r1", 0));
  assert.equal(pushed.peek("r1", 0), "처음입니다.");
  pushed.record(line("r1", 1));
  pushed.record(line("r1", 1, "실패한 뒤에는 바꾸지 않습니다."));
  assert.equal(pushed.peek("r1", 1), null);
});

test("Events that are not a usable pushed line are ignored", () => {
  const pushed = new PushedKorean(hangul);
  pushed.record({ type: "response.completed", turnId: "t", responseId: "r1", text: "한국어 문장입니다.", index: 0, korean: "한국어" });
  pushed.record({ type: "response.korean", turnId: "t", index: 0, korean: "응답 번호가 없습니다." });
  pushed.record({ type: "response.korean", turnId: "t", responseId: "r1", index: 2, korean: "줄 번호가 잘못되었습니다." });
  pushed.record({ type: "response.korean", turnId: "t", responseId: "r1", korean: "줄 번호가 없습니다." });
  for (const index of [0, 1, 2]) assert.equal(pushed.peek("r1", index), undefined);
});

test("prune keeps only the listed responses, and clear releases waiters and forgets everything", async t => {
  t.mock.timers.enable({ apis: ["setTimeout"] });
  const pushed = new PushedKorean(hangul);
  for (const id of ["r1", "r2", "r3"]) pushed.record(line(id, 0, `${id} 번역`));
  pushed.prune(new Set(["r2", undefined]));
  assert.deepEqual(["r1", "r2", "r3"].map(id => pushed.peek(id, 0)), [undefined, "r2 번역", undefined]);
  const waiting = pushed.wait("r4", 1, pushedKoreanWaitMs, open());
  pushed.clear();
  assert.equal(await waiting, null);
  assert.equal(pushed.peek("r2", 0), undefined);
});

test("At most 32 responses are remembered, oldest first", () => {
  const pushed = new PushedKorean(hangul);
  for (let i = 0; i < 40; i++) pushed.record(line(`r${i}`, 0, `한국어 ${i}`));
  assert.equal(pushed.peek("r0", 0), undefined);
  assert.equal(pushed.peek("r7", 0), undefined);
  assert.equal(pushed.peek("r8", 0), "한국어 8");
  assert.equal(pushed.peek("r39", 0), "한국어 39");
});
