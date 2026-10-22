import { test } from "node:test";
import assert from "node:assert/strict";
import { answerSizes, deferredUpdateNote, documentSearchOn, liveStartOptions, noHostSources, parseSettings, pendingUpdate, resolveAudioSource,
  settingsKey, stepAnswerSize, withDocumentSearch, withResponseMode, type AppliedSettings, type HostSources,
  type LiveSettings } from "../../src/VoiceAssistant.Web/src/live-settings.js";
import { createStartMessage, validateUpdate } from "../../src/VoiceAssistant.Web/src/options.js";
import type { CaptureSourceKind } from "../../src/VoiceAssistant.Web/src/capture.js";

const defaults: LiveSettings = { deviceId: "", responseMode: "grounded", documentMode: "grounded", topic: "", audioSource: "microphone",
  showCaptions: false, showKorean: false, answerSize: 100, replyOn: "all", myName: "" };

test("Live settings: nothing stored means document search on, captions and Korean off, replies to all speech and no name", () => {
  assert.equal(settingsKey, "live-coach-settings");
  assert.deepEqual(parseSettings(null), defaults);
  assert.deepEqual(parseSettings("{}"), defaults);
});

test("Live settings: settings stored by the previous version keep their choices and get the new ones off", () => {
  const stored = JSON.stringify({ deviceId: "mic-1", responseMode: "balanced", topic: "Azure sync", audioSource: "display" });
  assert.deepEqual(parseSettings(stored), { deviceId: "mic-1", responseMode: "balanced", documentMode: "balanced", topic: "Azure sync",
    audioSource: "display", showCaptions: false, showKorean: false, answerSize: 100, replyOn: "all", myName: "" });
  const off = parseSettings(JSON.stringify({ responseMode: "conversation" }));
  assert.equal(off.responseMode, "conversation");
  assert.equal(off.documentMode, "grounded", "an older version that stored only conversation turns back on as the default mode");
});

test("Live settings: stored values are used only when they are valid", () => {
  const stored = parseSettings(JSON.stringify({ responseMode: "conversation", documentMode: "balanced", showCaptions: true, showKorean: true }));
  assert.deepEqual([stored.responseMode, stored.documentMode, stored.showCaptions, stored.showKorean], ["conversation", "balanced", true, true]);
  const invalid = parseSettings(JSON.stringify({ deviceId: 3, responseMode: "fast", documentMode: "conversation", topic: 5, audioSource: "tab",
    showCaptions: "true", showKorean: 1, answerSize: "115", replyOn: "question", myName: ["Eon"] }));
  assert.deepEqual(invalid, defaults);
  assert.equal(parseSettings(JSON.stringify({ topic: "x".repeat(400) })).topic.length, 300);
  for (const broken of ["not json", "[]", "null", "7", "\"text\""]) assert.deepEqual(parseSettings(broken), defaults, broken);
});

test("Live settings: the reply choice is all speech or questions only, and only a valid stored choice is kept", () => {
  assert.equal(parseSettings(JSON.stringify({ replyOn: "questions" })).replyOn, "questions");
  assert.equal(parseSettings(JSON.stringify({ replyOn: "all" })).replyOn, "all");
  for (const stored of ["Questions", "question", "none", "", true, 1, null, ["questions"]])
    assert.equal(parseSettings(JSON.stringify({ replyOn: stored })).replyOn, "all", JSON.stringify(stored));
});

test("Live settings: the name is kept sanitized, and what is stored loads back unchanged", () => {
  assert.equal(parseSettings(JSON.stringify({ myName: "  Eon ,  Eon   Lee , E,  " })).myName, "Eon, Eon Lee");
  assert.equal(parseSettings(JSON.stringify({ myName: "Ann, Bob, Cid, Dan" })).myName, "Ann, Bob, Cid", "at most three names");
  assert.equal(parseSettings(JSON.stringify({ myName: `${"x".repeat(41)}, Mina` })).myName, "Mina", "each name has at most 40 characters");
  assert.ok(parseSettings(JSON.stringify({ myName: `${"a".repeat(39)}, ${"b".repeat(39)}, Eon` })).myName.length <= 80);
  for (const stored of [7, null, true, { name: "Eon" }]) assert.equal(parseSettings(JSON.stringify({ myName: stored })).myName, "");
  const chosen: LiveSettings = { ...defaults, replyOn: "questions", myName: "Eon, Eon Lee", topic: "Plan", showKorean: true };
  assert.deepEqual(parseSettings(JSON.stringify(chosen)), chosen, "the stored settings come back as they were saved");
});

test("Live settings: a document mode and its remembered choice never disagree", () => {
  for (const mode of ["grounded", "balanced"] as const)
    assert.equal(parseSettings(JSON.stringify({ responseMode: mode, documentMode: mode === "grounded" ? "balanced" : "grounded" })).documentMode, mode);
});

test("Live settings: the answer size is one of the A−/A+ steps, the default one unless another step was stored", () => {
  assert.deepEqual(answerSizes, [85, 100, 115, 130, 150]);
  for (const size of answerSizes) assert.equal(parseSettings(JSON.stringify({ answerSize: size })).answerSize, size);
  for (const stored of [120, 0, -100, 1.15, 1e9, null, "130", [130]])
    assert.equal(parseSettings(JSON.stringify({ answerSize: stored })).answerSize, 100, JSON.stringify(stored));
});

test("Live settings: A−/A+ step one size at a time and stop at the smallest and largest", () => {
  assert.deepEqual(answerSizes.map(size => stepAnswerSize(size, 1)), [100, 115, 130, 150, 150]);
  assert.deepEqual(answerSizes.map(size => stepAnswerSize(size, -1)), [85, 85, 100, 115, 130]);
});

test("Live settings: LiveCoach audio is used only where LiveCoach offers it, and inside it Teams app audio replaces a share", () => {
  const app: HostSources = { teams: true, system: true }, olderWindows: HostSources = { teams: false, system: true };
  const table: [stored: unknown, offered: HostSources, expected: CaptureSourceKind][] = [
    [undefined, noHostSources, "microphone"],
    [undefined, app, "teams"],
    [undefined, olderWindows, "microphone"],
    ["microphone", app, "microphone"],
    ["display", noHostSources, "display"],
    ["display", app, "teams"],
    ["display", olderWindows, "display"],
    ["teams", app, "teams"],
    ["teams", olderWindows, "display"],
    ["teams", noHostSources, "display"],
    ["host-system", app, "host-system"],
    ["host-system", olderWindows, "host-system"],
    ["host-system", noHostSources, "display"],
    ["tab", app, "teams"],
    [7, noHostSources, "microphone"],
  ];
  for (const [stored, offered, expected] of table) {
    const label = `${String(stored)} with ${JSON.stringify(offered)}`;
    assert.equal(resolveAudioSource(stored, offered), expected, label);
    assert.equal(parseSettings(JSON.stringify({ topic: "Plan", audioSource: stored }), offered).audioSource, expected, label);
  }
  assert.equal(parseSettings(JSON.stringify({ audioSource: "teams" })).audioSource, "display", "a browser never offers LiveCoach audio");
});

test("Live settings: switching document search off and on returns to the mode that was chosen", () => {
  const balanced = withResponseMode(defaults, "balanced");
  assert.equal(documentSearchOn(balanced), true);
  const off = withDocumentSearch(balanced, false);
  assert.deepEqual([off.responseMode, off.documentMode, documentSearchOn(off)], ["conversation", "balanced", false]);
  const on = withDocumentSearch(off, true);
  assert.deepEqual([on.responseMode, on.documentMode, documentSearchOn(on)], ["balanced", "balanced", true]);
  assert.equal(withDocumentSearch(defaults, true).responseMode, "grounded");
  assert.equal(withResponseMode(off, "grounded").documentMode, "grounded");
  assert.equal(withResponseMode(off, "conversation").documentMode, "balanced");
});

test("Live settings: changing a setting returns a new object and leaves the others alone", () => {
  const before = { ...defaults, showKorean: true, topic: "Plan" };
  const after = withDocumentSearch(before, false);
  assert.notEqual(after, before);
  assert.deepEqual({ ...after, responseMode: "grounded" }, before);
});

test("Live settings: only what differs from the running session is sent", () => {
  const applied: AppliedSettings = { responseMode: "grounded", replyKorean: false, replyOn: "all" };
  assert.equal(pendingUpdate(applied, defaults), null);
  assert.deepEqual(pendingUpdate(applied, { ...defaults, responseMode: "conversation" }), { responseMode: "conversation" });
  assert.deepEqual(pendingUpdate(applied, { ...defaults, showKorean: true }), { replyKorean: true });
  assert.deepEqual(pendingUpdate(applied, { ...defaults, replyOn: "questions" }), { replyOn: "questions" });
  assert.deepEqual(pendingUpdate(applied, { responseMode: "balanced", showKorean: true, replyOn: "questions" }),
    { responseMode: "balanced", replyKorean: true, replyOn: "questions" });
  assert.equal(pendingUpdate({ responseMode: "conversation", replyKorean: true, replyOn: "questions" },
    { responseMode: "conversation", showKorean: true, replyOn: "questions" }), null);
  assert.deepEqual(pendingUpdate({ ...applied, replyOn: "questions" }, defaults), { replyOn: "all" }, "switching back is sent too");
  const named: LiveSettings = { ...defaults, replyOn: "questions", myName: "Eon" };
  assert.deepEqual(validateUpdate(pendingUpdate(applied, named)!), { replyOn: "questions" }, "an update is valid and never carries the name");
});

test("Live settings: a server that cannot change settings while connected names what waits for the next connection", () => {
  const later = "변경을 지원하지 않아요 · 다음 연결부터 적용돼요";
  assert.equal(deferredUpdateNote({ responseMode: "conversation" }), `이 서버는 연결 중 문서검색 ${later}`);
  assert.equal(deferredUpdateNote({ replyOn: "questions" }), `이 서버는 연결 중 추천 ${later}`);
  assert.equal(deferredUpdateNote({ responseMode: "grounded", replyOn: "all", replyKorean: true }), `이 서버는 연결 중 문서검색·추천 ${later}`);
  assert.equal(deferredUpdateNote({ replyKorean: true }), "", "the page translates Korean by itself, so Korean needs no note");
});

test("Live settings: the start options carry the reply choice next to the fixed live settings, and never the name", () => {
  const chosen: LiveSettings = { ...defaults, responseMode: "conversation", topic: "Azure sync", showKorean: true, replyOn: "questions",
    myName: "Eon, Eon Lee" };
  const options = liveStartOptions(chosen);
  assert.deepEqual(options, { responseMode: "conversation", profile: { name: "", role: "", project: "" }, profileConfirmed: false,
    topic: "Azure sync", phrases: [], endSilenceMs: 1100, semanticSegmentation: true, replySettleMs: 1000, replyKorean: true,
    replyOn: "questions" });
  assert.equal(liveStartOptions(defaults).replyOn, "all", "the default is sent explicitly");
  const start = JSON.stringify(createStartMessage(options));
  assert.equal(JSON.parse(start).options.replyOn, "questions");
  assert.doesNotMatch(start, /Eon/, "the name stays in the browser");
});
