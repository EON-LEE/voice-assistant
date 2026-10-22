import { test } from "node:test";
import assert from "node:assert/strict";
import { LiveState, answers, meaningTexts, primaryMeaning, translatable, translationChunks, type LiveReply } from "../../src/VoiceAssistant.Web/src/live-state.js";
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

test("Live: respondNow is parsed, validated and kept on the completed reply", () => {
  const e = parseEvent(JSON.stringify({ type: "response.completed", turnId: "t", responseId: "r", text: "Yes.", sources: [], respondNow: true }));
  assert.equal(e.respondNow, true);
  assert.throws(() => parseEvent(JSON.stringify({ type: "response.completed", turnId: "t", responseId: "r", text: "Yes.", sources: [], respondNow: "yes" })));
  const state = new LiveState();
  state.apply(final("t", "What do you think?"));
  state.apply({ type: "response.started", turnId: "t", responseId: "r" });
  state.apply(e);
  assert.equal(state.display?.respondNow, true);
});

test("Live: response.started may say how many finals the reply answers, as a whole count of at least one", () => {
  const started = (extra: object) => JSON.stringify({ type: "response.started", turnId: "t", responseId: "r", ...extra });
  assert.deepEqual(parseEvent(started({ segments: 2 })), { type: "response.started", turnId: "t", responseId: "r", segments: 2 });
  assert.deepEqual(parseEvent(started({})), { type: "response.started", turnId: "t", responseId: "r" }, "an older server leaves it out");
  for (const bad of [0, -1, 1.5, "2", null, true, 2 ** 53])
    assert.throws(() => parseEvent(started({ segments: bad })), /segments/, JSON.stringify(bad));
  assert.deepEqual(parseEvent(JSON.stringify({ type: "response.cancelled", turnId: "t", responseId: "r" })),
    { type: "response.cancelled", turnId: "t", responseId: "r" });
});

test("Live: a reply to gathered finals answers all of them, so what it answers is every final it was gathered from", () => {
  const reply = (segments?: number) => {
    const state = new LiveState();
    state.apply(final("t1", "Quick question about the rollout."));
    state.apply({ type: "transcript.partial", turnId: "p", revision: 1, text: "um" });
    state.apply(final("t2", "When does the migration finish? "));
    state.apply(parseEvent(JSON.stringify({ type: "response.started", turnId: "t2", responseId: "r", ...(segments ? { segments } : {}) })));
    state.apply({ type: "response.completed", turnId: "t2", responseId: "r", text: "Next Friday.", sources: [] });
    return state.display!;
  };
  assert.equal(reply(2).question, "Quick question about the rollout. When does the migration finish?", "finals only, joined with single spaces");
  for (const [segments, why] of [[undefined, "an older server"], [1, "one final"], [3, "a final that is no longer known"]] as const)
    assert.equal(reply(segments).question, "When does the migration finish? ", why);
});

test("Live: a pushed Korean line parses strictly as either a meaning or a failure for one of the two reply lines", () => {
  const line = (extra: object) => JSON.stringify({ type: "response.korean", turnId: "t", responseId: "r", index: 0, ...extra });
  assert.deepEqual(parseEvent(line({ korean: "검토부터 하겠습니다." })),
    { type: "response.korean", turnId: "t", responseId: "r", index: 0, korean: "검토부터 하겠습니다." });
  assert.deepEqual(parseEvent(line({ index: 1, failed: true })),
    { type: "response.korean", turnId: "t", responseId: "r", index: 1, failed: true });
  assert.equal(parseEvent(line({ korean: "가".repeat(400) })).korean?.length, 400);
  for (const bad of [{ korean: "가", index: 2 }, { korean: "가", index: -1 }, { korean: "가", index: "0" }, { korean: "가", index: 0.5 },
    { korean: "가", index: null }, {}, { korean: "   " }, { korean: "가".repeat(401) }, { korean: 5 }, { korean: null },
    { failed: false }, { failed: "yes" }, { failed: null }, { failed: true, korean: "가" }])
    assert.throws(() => parseEvent(line(bad)), /./, JSON.stringify(bad));
  assert.throws(() => parseEvent(JSON.stringify({ type: "response.korean", turnId: "t", index: 0, korean: "가" })), /responseId/);
  assert.throws(() => parseEvent(JSON.stringify({ type: "response.korean", responseId: "r", index: 0, korean: "가" })), /turnId/);
});

test("Live: session.ready reports the Korean push capability only when the server set it to true", () => {
  assert.deepEqual(parseEvent(JSON.stringify({ type: "session.ready", protocolVersion: 1 })), { type: "session.ready" });
  assert.deepEqual(parseEvent(JSON.stringify({ type: "session.ready", protocolVersion: 1, replyKorean: true })),
    { type: "session.ready", replyKorean: true });
  assert.deepEqual(parseEvent(JSON.stringify({ type: "session.ready", protocolVersion: 1, replyKorean: false })), { type: "session.ready" });
  for (const bad of ["true", 1, null])
    assert.throws(() => parseEvent(JSON.stringify({ type: "session.ready", replyKorean: bad })), /replyKorean/);
});

test("Live: session.ready reports the settings-update capability only when the server set it to true", () => {
  assert.deepEqual(parseEvent(JSON.stringify({ type: "session.ready", protocolVersion: 1, sessionUpdate: true })),
    { type: "session.ready", sessionUpdate: true });
  assert.deepEqual(parseEvent(JSON.stringify({ type: "session.ready", protocolVersion: 1, replyKorean: true, sessionUpdate: true })),
    { type: "session.ready", replyKorean: true, sessionUpdate: true });
  assert.deepEqual(parseEvent(JSON.stringify({ type: "session.ready", protocolVersion: 1, sessionUpdate: false })), { type: "session.ready" });
  for (const bad of ["true", 1, null])
    assert.throws(() => parseEvent(JSON.stringify({ type: "session.ready", sessionUpdate: bad })), /sessionUpdate/);
});

test("Live: session.ready says how many turns the server resumed, as a whole count", () => {
  assert.deepEqual(parseEvent(JSON.stringify({ type: "session.ready", protocolVersion: 1, sessionUpdate: true, resumedTurns: 3 })),
    { type: "session.ready", sessionUpdate: true, resumedTurns: 3 });
  assert.equal(parseEvent(JSON.stringify({ type: "session.ready", protocolVersion: 1 })).resumedTurns, undefined);
  for (const bad of ["3", -1, 1.5, null, true])
    assert.throws(() => parseEvent(JSON.stringify({ type: "session.ready", resumedTurns: bad })), /resumedTurns/, JSON.stringify(bad));
});

test("Live: session.updated carries the effective document-search mode and Korean flag, both required and strict", () => {
  assert.deepEqual(parseEvent(JSON.stringify({ type: "session.updated", responseMode: "conversation", replyKorean: false })),
    { type: "session.updated", responseMode: "conversation", replyKorean: false });
  assert.deepEqual(parseEvent(JSON.stringify({ type: "session.updated", responseMode: "grounded", replyKorean: true })),
    { type: "session.updated", responseMode: "grounded", replyKorean: true });
  for (const bad of [{ responseMode: "fast", replyKorean: true }, { responseMode: 3, replyKorean: true }, { replyKorean: true },
    { responseMode: "balanced" }, { responseMode: "balanced", replyKorean: "true" }, { responseMode: null, replyKorean: false }])
    assert.throws(() => parseEvent(JSON.stringify({ type: "session.updated", ...bad })), /./, JSON.stringify(bad));
  const state = new LiveState();
  state.apply(parseEvent(JSON.stringify({ type: "session.updated", responseMode: "conversation", replyKorean: false })));
  assert.equal(state.display, null, "a settings acknowledgement changes no conversation state");
});

test("Live: session.updated may carry the effective replyOn, which must then be all or questions", () => {
  for (const replyOn of ["all", "questions"])
    assert.deepEqual(parseEvent(JSON.stringify({ type: "session.updated", responseMode: "grounded", replyKorean: false, replyOn })),
      { type: "session.updated", responseMode: "grounded", replyKorean: false, replyOn });
  assert.equal(parseEvent(JSON.stringify({ type: "session.updated", responseMode: "grounded", replyKorean: false })).replyOn, undefined,
    "a server older than replyOn leaves it out");
  for (const bad of ["question", "Questions", "", null, true, 1])
    assert.throws(() => parseEvent(JSON.stringify({ type: "session.updated", responseMode: "grounded", replyKorean: false, replyOn: bad })),
      /replyOn/, JSON.stringify(bad));
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
  assert.equal(state.generating, false, "a newer final alone may never get its own gathered reply");
  state.apply({ type: "response.started", turnId: "two", responseId: "r2" });
  assert.equal(state.generating, true, "a newer reply is being produced");
  assert.equal(state.display!.id, "r1", "previous suggestions stay until the new reply completes");
  state.apply({ type: "response.delta", turnId: "one", responseId: "r1", text: "stale" });
  state.apply({ type: "response.delta", turnId: "two", responseId: "r2", text: "Alex owns it." });
  assert.equal(state.current!.text, "Alex owns it.");
  assert.equal(state.display!.text, "Review first.", "drafts must not replace a complete reading answer");
  state.pause(true);
  assert.equal(state.display!.id, "r1");
  state.resetSession();
  assert.equal(state.display, null);
});

test("Live: the first reading answer waits for completion without hiding generation progress", () => {
  const state = new LiveState();
  state.holdDisplay();
  assert.equal(state.readingHeld, false);
  state.apply(final("t", "Who will check?"));
  state.apply({ type: "response.started", turnId: "t", responseId: "r" });
  state.apply({ type: "response.delta", turnId: "t", responseId: "r", text: "I will" });
  assert.equal(state.generating, true);
  const draftDisplay = state.display;
  assert.equal(draftDisplay, null);
  state.apply({ type: "response.completed", turnId: "t", responseId: "r", text: "I will check.", sources: [], grounding: "disabled" });
  assert.equal(state.display?.text, "I will check.");
  assert.equal(state.display?.question, "Who will check?");
  assert.equal(state.generating, false);
});

test("Live: reading hold preserves a complete answer and its question while newer replies finish", () => {
  const state = new LiveState();
  state.apply(final("one", "What is the next step?"));
  state.apply({ type: "response.started", turnId: "one", responseId: "r1" });
  state.apply({ type: "response.completed", turnId: "one", responseId: "r1", text: "Review the plan.", sources: [],
    suggestions: ["Review the plan.", "Could we review it together?"] });
  state.holdDisplay();
  assert.equal(state.readingHeld, true);
  assert.equal(state.hasNewerReply, false);
  for (let i = 2; i <= 70; i++) {
    state.apply(final(`t${i}`, `Question ${i}?`));
    state.apply({ type: "response.started", turnId: `t${i}`, responseId: `r${i}` });
    state.apply({ type: "response.completed", turnId: `t${i}`, responseId: `r${i}`, text: `Answer ${i}.`, sources: [] });
  }
  assert.equal(state.display?.question, "What is the next step?", "a held target survives the bounded transcript history");
  assert.equal(state.display?.text, "Review the plan.");
  assert.deepEqual(answers(state.display), ["Review the plan.", "Could we review it together?"]);
  assert.equal(state.hasNewerReply, true);
  assert.equal(state.lastCompleted?.id, "r70", "holding must not suppress incoming replies");
  state.releaseDisplay();
  assert.equal(state.display?.id, "r70", "release selects the latest complete reply, not an intermediate reply");
  assert.equal(state.readingHeld, false);
  assert.equal(state.hasNewerReply, false);
  state.holdDisplay();
  state.resetSession();
  assert.equal(state.readingHeld, false);
  assert.equal(state.display, null);
});

test("Live: cancelling a newer draft never replaces or releases a held reading answer", () => {
  const state = new LiveState();
  state.apply(final("t", "Question?"));
  state.apply({ type: "response.started", turnId: "t", responseId: "r" });
  state.apply({ type: "response.completed", turnId: "t", responseId: "r", text: "Check first.", sources: [] });
  state.holdDisplay();
  state.apply({ type: "response.started", turnId: "t", responseId: "r2" });
  state.apply({ type: "response.delta", turnId: "t", responseId: "r2", text: "Incomplete" });
  state.apply({ type: "response.cancelled", turnId: "t", responseId: "r2" });
  assert.equal(state.display?.text, "Check first.");
  assert.equal(state.readingHeld, true);
  state.releaseDisplay();
  assert.equal(state.display?.text, "Check first.");
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

test("Live: primaryDone parses strictly, and an empty delta is valid only when it marks the primary line done", () => {
  const delta = (extra: object) => JSON.stringify({ type: "response.delta", turnId: "t", responseId: "r", text: "Yes.", ...extra });
  assert.deepEqual(parseEvent(delta({})), { type: "response.delta", turnId: "t", responseId: "r", text: "Yes." });
  assert.deepEqual(parseEvent(delta({ primaryDone: true })), { type: "response.delta", turnId: "t", responseId: "r", text: "Yes.", primaryDone: true });
  assert.deepEqual(parseEvent(delta({ text: "", primaryDone: true })), { type: "response.delta", turnId: "t", responseId: "r", text: "", primaryDone: true });
  assert.deepEqual(parseEvent(delta({ primaryDone: false })), { type: "response.delta", turnId: "t", responseId: "r", text: "Yes." });
  for (const bad of [{ text: "" }, { text: "", primaryDone: false }, { primaryDone: "true" }, { primaryDone: 1 }, { primaryDone: null }])
    assert.throws(() => parseEvent(delta(bad)), /./, JSON.stringify(bad));
});

test("Live: a finished primary line is readable before completion, and completion fills in the rest", () => {
  const state = new LiveState();
  state.apply(final("one", "Old question?"));
  state.apply({ type: "response.started", turnId: "one", responseId: "r1" });
  state.apply({ type: "response.completed", turnId: "one", responseId: "r1", text: "Old answer.", sources: [] });
  state.apply(final("two", "Who owns the launch?"));
  state.apply({ type: "response.started", turnId: "two", responseId: "r2" });
  state.apply({ type: "response.delta", turnId: "two", responseId: "r2", text: "Alex owns" });
  assert.equal(state.display?.id, "r1", "a draft never replaces the answer on screen");
  state.apply({ type: "response.delta", turnId: "two", responseId: "r2", text: " it.", primaryDone: true });
  assert.equal(state.display?.id, "r2");
  assert.equal(state.display?.text, "Alex owns it.");
  assert.equal(state.display?.complete, false);
  assert.equal(state.display?.question, "Who owns the launch?");
  assert.deepEqual(answers(state.display), ["Alex owns it."], "the alternative waits for completion");
  assert.equal(state.generating, true);
  state.apply({ type: "response.completed", turnId: "two", responseId: "r2", text: "Alex owns it.", sources: [{ title: "Plan", url: "https://example.invalid/plan" }],
    grounding: "grounded", suggestions: ["Alex owns it.", "I think Alex owns that."] });
  assert.equal(state.display?.complete, true);
  assert.deepEqual(answers(state.display), ["Alex owns it.", "I think Alex owns that."]);
  assert.equal(state.display?.grounding, "grounded");
  assert.equal(state.lastCompleted?.id, "r2");
});

test("Live: an empty primaryDone delta shows the streamed line, and completed text replaces a line that differs", () => {
  const state = new LiveState();
  state.apply(final("t", "When is the review?"));
  state.apply({ type: "response.started", turnId: "t", responseId: "r" });
  state.apply({ type: "response.delta", turnId: "t", responseId: "r", text: "On Friday." });
  assert.equal(state.display?.id, undefined, "a line is not readable before primaryDone");
  state.apply({ type: "response.delta", turnId: "t", responseId: "r", text: "", primaryDone: true });
  assert.equal(state.display?.text, "On Friday.");
  state.apply({ type: "response.completed", turnId: "t", responseId: "r", text: "On Friday morning.", sources: [], grounding: "unavailable" });
  assert.equal(state.display?.text, "On Friday morning.");
  assert.equal(state.display?.grounding, "unavailable");
});

test("Live: hold keeps an older answer while a newer primary line waits as the latest", () => {
  const state = new LiveState();
  state.apply(final("one", "First?"));
  state.apply({ type: "response.started", turnId: "one", responseId: "r1" });
  state.apply({ type: "response.completed", turnId: "one", responseId: "r1", text: "First answer.", sources: [] });
  state.holdDisplay();
  state.apply(final("two", "Second?"));
  state.apply({ type: "response.started", turnId: "two", responseId: "r2" });
  state.apply({ type: "response.delta", turnId: "two", responseId: "r2", text: "Second answer.", primaryDone: true });
  assert.equal(state.display?.id, "r1");
  assert.equal(state.hasNewerReply, true);
  state.releaseDisplay();
  assert.equal(state.display?.id, "r2", "release shows the newer primary line");
  assert.equal(state.display?.complete, false);
  state.holdDisplay();
  assert.equal(state.readingHeld, true, "a primary line can be held before it completes");
  state.apply({ type: "response.completed", turnId: "two", responseId: "r2", text: "Second answer.", sources: [], suggestions: ["Second answer.", "Another way."] });
  assert.equal(state.hasNewerReply, false);
  assert.deepEqual(answers(state.display), ["Second answer.", "Another way."], "the held reply gains its alternative");
});

test("Live: a reply cancelled after its primary line keeps the line readable, marked interrupted", () => {
  const state = new LiveState();
  state.apply(final("t", "Status?"));
  state.apply({ type: "response.started", turnId: "t", responseId: "r" });
  state.apply({ type: "response.delta", turnId: "t", responseId: "r", text: "All green.", primaryDone: true });
  state.apply({ type: "response.cancelled", turnId: "t", responseId: "r" });
  assert.equal(state.current, null);
  assert.equal(state.display?.text, "All green.");
  assert.equal(state.display?.interrupted, true);
  state.apply({ type: "response.started", turnId: "t", responseId: "r2" });
  state.apply({ type: "response.delta", turnId: "t", responseId: "r2", text: "Still", primaryDone: false });
  state.cancelCurrent();
  assert.equal(state.display?.id, "r", "a draft without a finished line never replaces it");
});

const filtered = { type: "error", code: "response_filtered", message: "The content filter stopped this response, so it was withdrawn.", retryable: true };

test("Live: a primary line the content filter stops leaves the screen and is never kept as interrupted", () => {
  const state = new LiveState();
  state.apply(final("one", "Old question?"));
  state.apply({ type: "response.started", turnId: "one", responseId: "r1" });
  state.apply({ type: "response.completed", turnId: "one", responseId: "r1", text: "Old answer.", sources: [] });
  state.apply(final("two", "Status?"));
  state.apply({ type: "response.started", turnId: "two", responseId: "r2" });
  state.apply({ type: "response.delta", turnId: "two", responseId: "r2", text: "Withheld line.", primaryDone: true });
  assert.equal(state.display?.id, "r2");
  state.apply(filtered);
  assert.equal(state.display?.id, undefined, "the stopped line is gone, and the answer it replaced does not come back as current");
  assert.equal(state.generating, false);
  state.apply({ type: "response.cancelled", turnId: "two", responseId: "r2" });
  assert.equal(state.display?.id, undefined, "its cancellation is not shown as an interrupted line");
  state.apply(final("three", "Next?"));
  state.apply({ type: "response.started", turnId: "three", responseId: "r3" });
  state.apply({ type: "response.delta", turnId: "three", responseId: "r3", text: "Next answer.", primaryDone: true });
  assert.equal(state.display?.text, "Next answer.", "listening goes on");
  state.apply({ type: "response.cancelled", turnId: "three", responseId: "r3" });
  assert.equal(state.display?.text, "Next answer.", "a normal cancellation still keeps the finished line");
  assert.equal(state.display?.interrupted, true);
});

test("Live: a held reply the content filter stops is withdrawn; an older held reply stays", () => {
  const state = new LiveState();
  state.apply(final("t", "Status?"));
  state.apply({ type: "response.started", turnId: "t", responseId: "r" });
  state.apply({ type: "response.delta", turnId: "t", responseId: "r", text: "Withheld line.", primaryDone: true });
  state.holdDisplay();
  assert.equal(state.readingHeld, true);
  state.apply(filtered);
  state.apply({ type: "response.cancelled", turnId: "t", responseId: "r" });
  assert.equal(state.readingHeld, false, "the hold of the stopped reply ends with it");
  assert.equal(state.display, null);

  const older = new LiveState();
  older.apply(final("one", "First?"));
  older.apply({ type: "response.started", turnId: "one", responseId: "r1" });
  older.apply({ type: "response.completed", turnId: "one", responseId: "r1", text: "First answer.", sources: [] });
  older.holdDisplay();
  older.apply(final("two", "Second?"));
  older.apply({ type: "response.started", turnId: "two", responseId: "r2" });
  older.apply({ type: "response.delta", turnId: "two", responseId: "r2", text: "Withheld line.", primaryDone: true });
  assert.equal(older.hasNewerReply, true);
  older.apply(filtered);
  older.apply({ type: "response.cancelled", turnId: "two", responseId: "r2" });
  assert.equal(older.display?.text, "First answer.", "a different, older held reply is untouched");
  assert.equal(older.readingHeld, true);
  assert.equal(older.hasNewerReply, false, "the stopped reply is no newer reply waiting behind the hold");
  older.releaseDisplay();
  assert.equal(older.display, null);
});

test("Live: a stopped reply this page had already ended (a pause) is still removed by its cancellation", () => {
  const state = new LiveState();
  state.apply(final("t", "Status?"));
  state.apply({ type: "response.started", turnId: "t", responseId: "r" });
  state.apply({ type: "response.delta", turnId: "t", responseId: "r", text: "Withheld line.", primaryDone: true });
  state.pause(true);
  assert.equal(state.display?.interrupted, true, "a pause keeps the finished line, interrupted");
  state.apply(filtered);
  state.apply({ type: "response.cancelled", turnId: "t", responseId: "r" });
  assert.equal(state.display?.id, undefined);
  state.pause(false);
  state.apply(final("u", "Next?"));
  state.apply({ type: "response.started", turnId: "u", responseId: "r2" });
  state.apply({ type: "response.delta", turnId: "u", responseId: "r2", text: "Next answer.", primaryDone: true });
  state.apply({ type: "response.cancelled", turnId: "u", responseId: "r2" });
  assert.equal(state.display?.interrupted, true, "the withdrawal ended with its own cancellation");
});

test("Live: continuing after a stop clears an old pause and an unfinished reply but keeps the conversation", () => {
  const state = new LiveState();
  state.apply(final("one", "First?"));
  state.apply({ type: "response.started", turnId: "one", responseId: "r1" });
  state.apply({ type: "response.completed", turnId: "one", responseId: "r1", text: "First answer.", sources: [] });
  state.apply(final("two", "Second?"));
  state.apply({ type: "response.started", turnId: "two", responseId: "r2" });
  state.apply({ type: "response.delta", turnId: "two", responseId: "r2", text: "Half" });
  state.pause(true);
  state.restart(true);
  assert.equal(state.generating, false);
  assert.equal(state.display?.id, "r1", "the conversation stays");
  assert.equal(state.turns.length, 2);
  state.apply(final("three", "Third?"));
  state.apply({ type: "response.started", turnId: "three", responseId: "r3" });
  state.apply({ type: "response.delta", turnId: "three", responseId: "r3", text: "Third answer.", primaryDone: true });
  assert.equal(state.display?.id, "r3", "replies are no longer suppressed by the old pause");
  state.restart(false);
  assert.equal(state.display, null);
  assert.equal(state.turns.length, 0);
});

test("Live: ending the reply of a lost connection keeps a finished line and drops a draft", () => {
  const state = new LiveState();
  state.apply(final("t", "Status?"));
  state.apply({ type: "response.started", turnId: "t", responseId: "r" });
  state.apply({ type: "response.delta", turnId: "t", responseId: "r", text: "Draft" });
  state.endReply();
  assert.equal(state.generating, false);
  assert.equal(state.display?.id, undefined, "a draft is never shown");
  state.apply({ type: "response.started", turnId: "t", responseId: "r2" });
  assert.equal(state.current?.id, "r2", "ending a reply suppresses nothing that comes later");
  state.apply({ type: "response.delta", turnId: "t", responseId: "r2", text: "All green.", primaryDone: true });
  state.endReply();
  assert.equal(state.display?.text, "All green.");
  assert.equal(state.display?.interrupted, true);
});

test("Live: the Korean line under an unfinished or interrupted primary line never waits forever", () => {
  const reply = (extra: Partial<LiveReply>): LiveReply => ({ id: "r", turnId: "t", text: "Ship on Friday.", complete: false, sources: [], ...extra });
  assert.equal(primaryMeaning(null, undefined, undefined), meaningTexts.waiting);
  assert.equal(primaryMeaning(reply({ primaryDone: true }), undefined, undefined), meaningTexts.preparing, "the completion will translate it");
  assert.equal(primaryMeaning(reply({ primaryDone: true }), undefined, "금요일에 출시합니다."), "금요일에 출시합니다.", "a pushed line shows early");
  assert.equal(primaryMeaning(reply({ primaryDone: true, interrupted: true }), undefined, "금요일에 출시합니다."), "금요일에 출시합니다.");
  assert.equal(primaryMeaning(reply({ primaryDone: true, interrupted: true }), undefined, undefined), meaningTexts.interrupted);
  assert.equal(primaryMeaning(reply({ primaryDone: true, interrupted: true }), undefined, null), meaningTexts.interrupted, "a failed push");
  assert.equal(primaryMeaning(reply({ complete: true }), undefined, "푸시"), meaningTexts.preparing, "a completed reply waits for its own translation");
  assert.equal(primaryMeaning(reply({ interrupted: true }), "번역됨", undefined), "번역됨");
  assert.doesNotMatch(meaningTexts.interrupted, /준비 중/);
  assert.equal(translatable(reply({ primaryDone: true })), false, "an unfinished reply translates at completion");
  assert.equal(translatable(reply({ primaryDone: true, interrupted: true })), true);
  assert.equal(translatable(reply({ complete: true })), true);
  assert.equal(translatable(null), false);
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
