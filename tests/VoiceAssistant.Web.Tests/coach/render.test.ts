import { it, expect } from "vitest";
import { createRequire } from "node:module";
import { renderEnrichment } from "../../../src/VoiceAssistant.Web/src/enrichment.js";
it("renders stacked aligned English/Hangul chunks as inert text and clears stale rows", () => {
  const { JSDOM } = createRequire(new URL("../../../src/VoiceAssistant.Web/package.json", import.meta.url))("jsdom");
  const dom = new JSDOM("<!doctype html><html><body></body></html>");
  const previous = globalThis.document;
  globalThis.document = dom.window.document;
  try {
  const korean = document.createElement("p"), pronunciation = document.createElement("div");
  renderEnrichment(korean, pronunciation, { korean: "계획을 확인해요.", pronunciation: [{ en: "<img onerror=bad>", ko: "플랜" }, { en: "now.", ko: "나우." }] });
  expect(pronunciation.querySelectorAll(".pronunciation-chunk")).toHaveLength(2);
  expect(pronunciation.querySelectorAll("img")).toHaveLength(0);
  expect(pronunciation.querySelector(".pronunciation-en")?.textContent).toBe("<img onerror=bad>");
  expect(pronunciation.querySelector(".pronunciation-ko")?.getAttribute("lang")).toBe("ko");
  renderEnrichment(korean, pronunciation, null);
  expect(korean.hidden).toBe(true); expect(pronunciation.hidden).toBe(true); expect(pronunciation.childElementCount).toBe(0);
  } finally { globalThis.document = previous; dom.window.close(); }
});
