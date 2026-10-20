import { test } from "node:test";
import assert from "node:assert/strict";
import { documentSearchOn, noHostSources, parseSettings, pendingUpdate, resolveAudioSource, settingsKey, withDocumentSearch, withResponseMode,
  type HostSources, type LiveSettings } from "../../src/VoiceAssistant.Web/src/live-settings.js";
import type { CaptureSourceKind } from "../../src/VoiceAssistant.Web/src/capture.js";

const defaults: LiveSettings = { deviceId: "", responseMode: "grounded", documentMode: "grounded", topic: "", audioSource: "microphone",
  showCaptions: false, showKorean: false };

test("Live settings: nothing stored means document search on, captions and Korean off", () => {
  assert.equal(settingsKey, "live-coach-settings");
  assert.deepEqual(parseSettings(null), defaults);
  assert.deepEqual(parseSettings("{}"), defaults);
});

test("Live settings: settings stored by the previous version keep their choices and get the new ones off", () => {
  const stored = JSON.stringify({ deviceId: "mic-1", responseMode: "balanced", topic: "Azure sync", audioSource: "display" });
  assert.deepEqual(parseSettings(stored), { deviceId: "mic-1", responseMode: "balanced", documentMode: "balanced", topic: "Azure sync",
    audioSource: "display", showCaptions: false, showKorean: false });
  const off = parseSettings(JSON.stringify({ responseMode: "conversation" }));
  assert.equal(off.responseMode, "conversation");
  assert.equal(off.documentMode, "grounded", "an older version that stored only conversation turns back on as the default mode");
});

test("Live settings: stored values are used only when they are valid", () => {
  const stored = parseSettings(JSON.stringify({ responseMode: "conversation", documentMode: "balanced", showCaptions: true, showKorean: true }));
  assert.deepEqual([stored.responseMode, stored.documentMode, stored.showCaptions, stored.showKorean], ["conversation", "balanced", true, true]);
  const invalid = parseSettings(JSON.stringify({ deviceId: 3, responseMode: "fast", documentMode: "conversation", topic: 5, audioSource: "tab",
    showCaptions: "true", showKorean: 1 }));
  assert.deepEqual(invalid, defaults);
  assert.equal(parseSettings(JSON.stringify({ topic: "x".repeat(400) })).topic.length, 300);
  for (const broken of ["not json", "[]", "null", "7", "\"text\""]) assert.deepEqual(parseSettings(broken), defaults, broken);
});

test("Live settings: a document mode and its remembered choice never disagree", () => {
  for (const mode of ["grounded", "balanced"] as const)
    assert.equal(parseSettings(JSON.stringify({ responseMode: mode, documentMode: mode === "grounded" ? "balanced" : "grounded" })).documentMode, mode);
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
  const applied = { responseMode: "grounded" as const, replyKorean: false };
  assert.equal(pendingUpdate(applied, defaults), null);
  assert.deepEqual(pendingUpdate(applied, { ...defaults, responseMode: "conversation" }), { responseMode: "conversation" });
  assert.deepEqual(pendingUpdate(applied, { ...defaults, showKorean: true }), { replyKorean: true });
  assert.deepEqual(pendingUpdate(applied, { responseMode: "balanced", showKorean: true }), { responseMode: "balanced", replyKorean: true });
  assert.equal(pendingUpdate({ responseMode: "conversation", replyKorean: true }, { responseMode: "conversation", showKorean: true }), null);
});
