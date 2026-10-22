import { test } from "node:test";
import assert from "node:assert/strict";
import { mentionsName, nameLimits, parseNames, primaryLabel, primaryLabels, sanitizeNames } from "../../src/VoiceAssistant.Web/src/address.js";

test("Name: a name is found as a whole word, whatever its case, and not inside another word", () => {
  for (const text of ["Eon, what do you think?", "Thanks Eon.", "Eon's team owns the rollout.", "Eon’s team owns it.",
    "So, EON, can you take this one?", "What about you, eon?", "(Eon) and Mina", "Over to you—Eon."])
    assert.equal(mentionsName(text, ["Eon"]), true, text);
  for (const text of ["Leon, what do you think?", "The neon sign is broken.", "Eonix shipped the update.", "Eon2 is the code name.",
    "We reviewed the eons of logs.", "", "What do you think?"])
    assert.equal(mentionsName(text, ["Eon"]), false, text);
  assert.equal(mentionsName(undefined, ["Eon"]), false);
  assert.equal(mentionsName("Eon, what do you think?", []), false, "without names nothing is addressed to the user");
});

test("Name: several words match as a phrase with any whitespace between them", () => {
  const names = ["Eon Lee"];
  for (const text of ["Eon Lee, can you answer that?", "Thanks, eon   lee.", "Is that right, Eon\tLee?", "Eon\nLee, go ahead."])
    assert.equal(mentionsName(text, names), true, text);
  for (const text of ["EonLee, can you answer?", "Eon, can you answer?", "Eon Leeds is a city.", "Leon Lee, go ahead."])
    assert.equal(mentionsName(text, names), false, text);
});

test("Name: matching is Unicode-aware for letters, case and composed accents", () => {
  assert.equal(mentionsName("Thanks, José.", ["Jose\u0301"]), true, "a decomposed accent matches its composed form");
  assert.equal(mentionsName("Thanks, Jose\u0301.", ["Jose"]), false, "an accent makes it another name");
  assert.equal(mentionsName("ÉLODIE, your view?", ["Élodie"]), true);
  assert.equal(mentionsName("Ask 이온, please.", ["이온"]), true);
  assert.equal(mentionsName("Did O’Brien agree?", ["O'Brien"]), true, "a straight apostrophe in a name also matches a curly one");
  assert.equal(mentionsName("Ask Dr. (Kim) now.", ["(Kim)"]), true, "characters that mean something in a pattern are literal");
  assert.equal(mentionsName("Ask Kim now.", ["K.m"]), false);
  for (const odd of ["a{2}", "x]y", "a\\b", "c-d", "e/f", "^g$", "h|i", "(j", "k)", "l[m", "n*?", "o+p"])
    assert.equal(mentionsName(`Over to ${odd}, please.`, [odd]), true, odd);
});

test("Name: any of up to three names counts, and the list can change", () => {
  assert.equal(mentionsName("Mina, could you check?", ["Eon", "Mina"]), true);
  assert.equal(mentionsName("Eon, could you check?", ["Eon", "Mina"]), true);
  assert.equal(mentionsName("Mina, could you check?", ["Eon"]), false, "a changed list is used at once");
});

test("Name: the setting keeps at most three names of 2 to 40 characters, at most 80 characters in all", () => {
  assert.deepEqual(nameLimits, { length: 80, names: 3, min: 2, max: 40 });
  assert.deepEqual(parseNames("  Eon ,  Eon   Lee  "), ["Eon", "Eon Lee"]);
  assert.equal(sanitizeNames("  Eon ,Eon   Lee,  "), "Eon, Eon Lee");
  assert.deepEqual(parseNames("E, Eon, , 7, --, Mina"), ["Eon", "Mina"], "too short, empty and letterless names are left out");
  assert.deepEqual(parseNames("Eon, eon, EON, Mina"), ["Eon", "Mina"], "a repeated name counts once");
  assert.deepEqual(parseNames("Ann, Bob, Cid, Dan"), ["Ann", "Bob", "Cid"]);
  assert.deepEqual(parseNames(`${"a".repeat(41)}, Eon`), ["Eon"], "a name over 40 characters is left out");
  assert.deepEqual(parseNames("a".repeat(40)), ["a".repeat(40)]);
  const long = [`${"a".repeat(38)}`, `${"b".repeat(38)}`, "Eon"];
  assert.deepEqual(parseNames(long.join(",")), long.slice(0, 2), "the third name would pass 80 characters");
  assert.ok(sanitizeNames(long.join(",")).length <= 80);
  assert.equal(sanitizeNames("Eon\u0000\nLee"), "Eon Lee", "control characters and line breaks become one space");
  for (const value of [undefined, null, 7, true, ["Eon"], { name: "Eon" }]) assert.equal(sanitizeNames(value), "", JSON.stringify(value));
  assert.equal(sanitizeNames(sanitizeNames(" Eon ,Mina ")), "Eon, Mina", "sanitizing a kept value changes nothing");
});

test("Label: only an answer to speech that says the user's name is emphasized, never the server's respondNow guess", () => {
  const names = ["Eon"];
  assert.deepEqual(primaryLabel(false, "Eon, what do you think?", names), { text: "이름이 불렸어요 · 지금 답할 차례", myTurn: true });
  assert.deepEqual(primaryLabel(false, "What do you think?", names), { text: "추천 영어 답변", myTurn: false });
  assert.deepEqual(primaryLabel(false, "Eon, what do you think?", []), { text: "추천 영어 답변", myTurn: false },
    "without a name nothing is emphasized");
  assert.deepEqual(primaryLabel(false, undefined, names), { text: "추천 영어 답변", myTurn: false });
  assert.deepEqual(primaryLabel(true, "Eon, what do you think?", names), { text: "이전 질문용 답변", myTurn: false },
    "an answer to an earlier question keeps its label and is never emphasized");
  assert.deepEqual(primaryLabels, { previous: "이전 질문용 답변", addressed: "이름이 불렸어요 · 지금 답할 차례", suggested: "추천 영어 답변" });
});
