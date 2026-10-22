import { test } from "node:test";
import assert from "node:assert/strict";
import { acknowledgedSettings, answerSizes, deferredUpdateNote, documentSearchOn, keptNotes, liveStartOptions, noHostSources, notesChipName,
  parseSettings, pendingUpdate, resolveAudioSource, settingsKey, stepAnswerSize, withDocumentSearch, withResponseMode, type AppliedSettings,
  type HostSources, type LiveSettings } from "../../src/VoiceAssistant.Web/src/live-settings.js";
import { createStartMessage, validateUpdate } from "../../src/VoiceAssistant.Web/src/options.js";
import { parseEvent } from "../../src/VoiceAssistant.Web/src/protocol.js";
import type { CaptureSourceKind } from "../../src/VoiceAssistant.Web/src/capture.js";

const defaults: LiveSettings = { deviceId: "", responseMode: "grounded", documentMode: "grounded", topic: "", audioSource: "microphone",
  showCaptions: false, showKorean: false, answerSize: 100, replyOn: "all", notes: "" };

test("Live settings: nothing stored means document search on, captions and Korean off, replies to all speech and no notes", () => {
  assert.equal(settingsKey, "live-coach-settings");
  assert.deepEqual(parseSettings(null), defaults);
  assert.deepEqual(parseSettings("{}"), defaults);
});

test("Live settings: settings stored by the previous version keep their choices and get the new ones off", () => {
  const stored = JSON.stringify({ deviceId: "mic-1", responseMode: "balanced", topic: "Azure sync", audioSource: "display" });
  assert.deepEqual(parseSettings(stored), { deviceId: "mic-1", responseMode: "balanced", documentMode: "balanced", topic: "Azure sync",
    audioSource: "display", showCaptions: false, showKorean: false, answerSize: 100, replyOn: "all", notes: "" });
  const off = parseSettings(JSON.stringify({ responseMode: "conversation" }));
  assert.equal(off.responseMode, "conversation");
  assert.equal(off.documentMode, "grounded", "an older version that stored only conversation turns back on as the default mode");
});

test("Live settings: stored values are used only when they are valid", () => {
  const stored = parseSettings(JSON.stringify({ responseMode: "conversation", documentMode: "balanced", showCaptions: true, showKorean: true }));
  assert.deepEqual([stored.responseMode, stored.documentMode, stored.showCaptions, stored.showKorean], ["conversation", "balanced", true, true]);
  const invalid = parseSettings(JSON.stringify({ deviceId: 3, responseMode: "fast", documentMode: "conversation", topic: 5, audioSource: "tab",
    showCaptions: "true", showKorean: 1, answerSize: "115", replyOn: "question", notes: ["Prefer plan B."] }));
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

test("Live settings: a name stored by an earlier version is ignored, and what is stored loads back unchanged", () => {
  const earlier = parseSettings(JSON.stringify({ myName: "Eon, Eon Lee", replyOn: "questions", topic: "Plan" }));
  assert.deepEqual(earlier, { ...defaults, replyOn: "questions", topic: "Plan" });
  assert.equal("myName" in earlier, false, "the next save stores the settings without it");
  const chosen: LiveSettings = { ...defaults, replyOn: "questions", topic: "Plan", showKorean: true, notes: "Prefer plan B." };
  assert.deepEqual(parseSettings(JSON.stringify(chosen)), chosen, "the stored settings come back as they were saved");
});

test("Live settings: notes are kept on one line, without control characters, at most 500 characters", () => {
  assert.equal(keptNotes("  B안 선호.\n\n예산 5천 달러 이하.\t3분기 전에는 약속하지 않음.  "), "B안 선호. 예산 5천 달러 이하. 3분기 전에는 약속하지 않음.");
  assert.equal(keptNotes("Prefer\u00a0plan\u3000B.\r\nBudget\u2028fixed."), "Prefer plan B. Budget fixed.", "Unicode spaces count as spaces");
  assert.equal(keptNotes("Prefer \u0007plan\u0000 B."), "Prefer plan B.", "other control characters are dropped");
  assert.equal(keptNotes(" \n\t "), "");
  for (const stored of [undefined, null, 5, true, ["Prefer plan B."], { notes: "x" }]) assert.equal(keptNotes(stored), "", JSON.stringify(stored));
  assert.equal(keptNotes("a".repeat(500)), "a".repeat(500));
  assert.equal(keptNotes("a".repeat(600)), "a".repeat(500), "a longer stored copy is cut to the limit");
  assert.equal(keptNotes(`${"a".repeat(499)} b`), "a".repeat(499), "a cut never leaves a space at the end");
  assert.equal(keptNotes(`${"a".repeat(499)}😀`), "a".repeat(499), "a cut never leaves half a character");
  assert.equal(keptNotes(`${"a".repeat(300)}${" ".repeat(400)}${"b".repeat(199)}`).length, 500, "spacing never counts against the limit");
  const stored = parseSettings(JSON.stringify({ notes: " Prefer plan B.\n Budget under 5,000 dollars. " }));
  assert.equal(stored.notes, "Prefer plan B. Budget under 5,000 dollars.");
  assert.deepEqual(parseSettings(JSON.stringify(stored)), stored, "kept notes load back unchanged");
});

test("Live settings: the 내 메모 chip says in its name whether notes are set, because opening them toggles nothing", () => {
  assert.equal(notesChipName(""), "내 메모");
  assert.equal(notesChipName("Prefer plan B."), "내 메모 (설정됨)");
  assert.equal(notesChipName(parseSettings(JSON.stringify({ notes: " \n " })).notes), "내 메모", "notes of only spaces are none");
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
  const applied: AppliedSettings = { responseMode: "grounded", replyKorean: false, replyOn: "all", notes: "" };
  assert.equal(pendingUpdate(applied, defaults), null);
  assert.deepEqual(pendingUpdate(applied, { ...defaults, responseMode: "conversation" }), { responseMode: "conversation" });
  assert.deepEqual(pendingUpdate(applied, { ...defaults, showKorean: true }), { replyKorean: true });
  assert.deepEqual(pendingUpdate(applied, { ...defaults, replyOn: "questions" }), { replyOn: "questions" });
  assert.deepEqual(pendingUpdate(applied, { ...defaults, notes: "Prefer plan B." }), { notes: "Prefer plan B." });
  assert.deepEqual(pendingUpdate(applied, { responseMode: "balanced", showKorean: true, replyOn: "questions", notes: "Prefer plan B." }),
    { responseMode: "balanced", replyKorean: true, replyOn: "questions", notes: "Prefer plan B." });
  assert.equal(pendingUpdate({ responseMode: "conversation", replyKorean: true, replyOn: "questions", notes: "Prefer plan B." },
    { responseMode: "conversation", showKorean: true, replyOn: "questions", notes: "Prefer plan B." }), null);
  assert.deepEqual(pendingUpdate({ ...applied, replyOn: "questions" }, defaults), { replyOn: "all" }, "switching back is sent too");
  assert.deepEqual(pendingUpdate({ ...applied, notes: "Prefer plan B." }, defaults), { notes: "" }, "cleared notes are sent as empty");
  assert.deepEqual(validateUpdate(pendingUpdate(applied, { ...defaults, replyOn: "questions", notes: "Prefer plan B." })!),
    { replyOn: "questions", notes: "Prefer plan B." }, "the update is a valid session.update");
});

test("Live settings: session.updated reports whether notes are set, so the acknowledged update says which notes", () => {
  const applied: AppliedSettings = { responseMode: "grounded", replyKorean: false, replyOn: "all", notes: "" };
  const updated = (extra: object) => parseEvent(JSON.stringify({ type: "session.updated", responseMode: "conversation", replyKorean: true,
    replyOn: "questions", ...extra }));
  assert.deepEqual(acknowledgedSettings(applied, updated({ notes: true }), { notes: "Prefer plan B." }),
    { responseMode: "conversation", replyKorean: true, replyOn: "questions", notes: "Prefer plan B." });
  const noted = { ...applied, notes: "Prefer plan B." };
  assert.equal(acknowledgedSettings(noted, updated({ notes: true }), { replyOn: "questions" }).notes, "Prefer plan B.",
    "an update without notes keeps them");
  assert.equal(acknowledgedSettings(noted, updated({ notes: false }), { notes: "" }).notes, "");
  assert.equal(acknowledgedSettings(noted, updated({ notes: false }), { replyOn: "questions" }).notes, "", "the server has none");
  assert.equal(acknowledgedSettings(noted, updated({}), null).notes, "Prefer plan B.", "an older server says nothing about notes");
  assert.throws(() => updated({ notes: "Prefer plan B." }), /notes/, "the server never sends the text back");
});

test("Live settings: a server that cannot change settings while connected names what waits for the next connection", () => {
  const later = "변경을 지원하지 않아요 · 다음 연결부터 적용돼요";
  assert.equal(deferredUpdateNote({ responseMode: "conversation" }), `이 서버는 연결 중 문서검색 ${later}`);
  assert.equal(deferredUpdateNote({ replyOn: "questions" }), `이 서버는 연결 중 추천 ${later}`);
  assert.equal(deferredUpdateNote({ notes: "Prefer plan B." }), `이 서버는 연결 중 내 메모 ${later}`);
  assert.equal(deferredUpdateNote({ notes: "" }), `이 서버는 연결 중 내 메모 ${later}`, "clearing them waits as well");
  assert.equal(deferredUpdateNote({ responseMode: "grounded", replyOn: "all", replyKorean: true }), `이 서버는 연결 중 문서검색·추천 ${later}`);
  assert.equal(deferredUpdateNote({ responseMode: "grounded", replyOn: "all", notes: "x" }), `이 서버는 연결 중 문서검색·추천·내 메모 ${later}`);
  assert.equal(deferredUpdateNote({ replyKorean: true }), "", "the page translates Korean by itself, so Korean needs no note");
});

test("Live settings: the start options carry the reply choice and the notes next to the fixed live settings", () => {
  const chosen: LiveSettings = { ...defaults, responseMode: "conversation", topic: "Azure sync", showKorean: true, replyOn: "questions",
    notes: "Prefer plan B." };
  const options = liveStartOptions(chosen);
  assert.deepEqual(options, { responseMode: "conversation", profile: { name: "", role: "", project: "" }, profileConfirmed: false,
    topic: "Azure sync", phrases: [], endSilenceMs: 1100, semanticSegmentation: true, replySettleMs: 1000, replyKorean: true,
    replyOn: "questions", notes: "Prefer plan B." });
  assert.equal(liveStartOptions(defaults).replyOn, "all", "the default is sent explicitly");
  assert.equal(liveStartOptions(defaults).notes, "", "no notes are sent as empty");
  const start = JSON.parse(JSON.stringify(createStartMessage(options)));
  assert.equal(start.options.replyOn, "questions");
  assert.equal(start.options.notes, "Prefer plan B.");
});
