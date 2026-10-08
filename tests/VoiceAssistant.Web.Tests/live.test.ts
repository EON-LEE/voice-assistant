import { test } from "node:test";
import assert from "node:assert/strict";
import { LiveState, answers, isTransientFailure, translationChunks } from "../../src/VoiceAssistant.Web/src/live-state.js";
import { parseEvent } from "../../src/VoiceAssistant.Web/src/protocol.js";

const final = (turnId: string, text: string) => ({ type: "transcript.final", turnId, revision: 1, text });

test("Live: two suggestions parse and the first must equal text", () => {
  const e = parseEvent(JSON.stringify({ type: "response.completed", turnId: "t", responseId: "r", text: "Review first.",
    sources: [], suggestions: ["Review first.", "Could we check the owner?"] }));
  assert.deepEqual(e.suggestions, ["Review first.", "Could we check the owner?"]);
  assert.equal(parseEvent(JSON.stringify({ type: "response.completed", turnId: "t", responseId: "r", text: "x", sources: [] })).suggestions, undefined);
  for (const bad of [[], ["wrong"], ["x", "a", "b"], ["x", ""]])
    assert.throws(() => parseEvent(JSON.stringify({ type: "response.completed", turnId: "t", responseId: "r", text: "x", sources: [], suggestions: bad })));
});

test("Live: continuous speech never discards a started reply and keeps last suggestions visible", () => {
  const state = new LiveState();
  state.apply(final("one", "Launch?"));
  state.apply({ type: "response.started", turnId: "one", responseId: "r1" });
  state.apply({ type: "transcript.partial", turnId: "two", revision: 1, text: "And" });
  state.apply({ type: "response.delta", turnId: "one", responseId: "r1", text: "Review first." });
  state.apply(final("two", "And who owns launch?"));
  state.apply({ type: "response.completed", turnId: "one", responseId: "r1", text: "Review first.", sources: [],
    suggestions: ["Review first.", "Could we review the findings together?"] });
  assert.equal(answers(state.display).length, 2);
  assert.equal(state.generating, true, "the newer final question is still waiting for its reply");
  state.apply({ type: "response.started", turnId: "two", responseId: "r2" });
  assert.equal(state.display!.id, "r1", "previous suggestions stay until the new reply streams text");
  state.apply({ type: "response.delta", turnId: "one", responseId: "r1", text: "stale" });
  state.apply({ type: "response.delta", turnId: "two", responseId: "r2", text: "Alex owns it." });
  assert.equal(state.display!.text, "Alex owns it.");
  state.pause(true);
  assert.equal(state.display!.id, "r1");
  state.resetSession();
  assert.equal(state.display, null);
});

test("Live: duplicate, unknown-turn and cancelled replies are ignored safely", () => {
  const state = new LiveState();
  state.apply({ type: "response.started", turnId: "ghost", responseId: "g" });
  assert.equal(state.current, null);
  state.apply(final("t", "Q?"));
  state.apply({ type: "response.started", turnId: "t", responseId: "r" });
  state.apply({ type: "response.cancelled", turnId: "t", responseId: "r" });
  assert.equal(state.current, null);
  state.apply({ type: "response.started", turnId: "t", responseId: "r" });
  assert.equal(state.current, null, "a seen response id cannot restart");
  state.apply({ type: "error", code: "model_busy", message: "busy", retryable: true });
  assert.match(state.error, /model_busy/);
  state.apply({ type: "response.started", turnId: "t", responseId: "r3" });
  assert.equal(state.error, "", "a new reply clears the previous error");
});

test("Live: only network/service failures reconnect automatically", () => {
  for (const message of ["Server disconnected unexpectedly. Audio has stopped; click Start to reconnect.",
    "WebSocket connection failed. Check sign-in and server availability.", "Connection timed out. Start again to reconnect.",
    "Server did not become ready within 15 seconds.", "The server ended this session. Click Start to continue.",
    "Meeting authorization failed (503). Sign in again.", "Failed to fetch"])
    assert.equal(isTransientFailure(message), true, message);
  for (const message of ["Sign-in expired. Stop and sign in again before sharing.", "Meeting authorization failed (401). Sign in again.",
    "Sign in before sharing audio.",
    "session_superseded: Live started in another window or device, so this session was closed.",
    "session_time_limit: Session reached its time limit.", "invalid_start: bad", "Microphone permission was denied."])
    assert.equal(isTransientFailure(message), false, message);
});

test("Live: long speech is translated in bounded chunks without losing words", () => {
  const text = Array.from({ length: 40 }, () => "Copilot helps teams prepare a launch plan.").join(" ");
  const chunks = translationChunks(text);
  assert.ok(chunks.length > 1);
  assert.ok(chunks.every(chunk => chunk.length >= 1 && chunk.length <= 600));
  assert.equal(chunks.join(" "), text);
  assert.equal(translationChunks("x".repeat(601)).length, 2);
  assert.deepEqual(translationChunks("   "), []);
});
