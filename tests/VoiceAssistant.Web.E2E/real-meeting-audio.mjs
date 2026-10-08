// Real meeting-audio test: LiveCoach app (--fake-mic --audio-file <wav>) against the deployed service.
// Records every second what the overlay shows and writes a JSON report plus a summary.
import { chromium } from "playwright";
import { writeFileSync } from "node:fs";
const [, , outDir = ".", seconds = "260"] = process.argv;
const browser = await chromium.connectOverCDP("http://127.0.0.1:9333");
const page = browser.contexts()[0].pages().find(p => p.url().includes("live.html"));
page.on("dialog", d => d.dismiss());
const snapshot = () => page.evaluate(() => ({
  capture: document.querySelector("#live-panel").dataset.capture,
  status: document.querySelector("#capture-status").textContent,
  error: document.querySelector("#session-error").textContent,
  lines: [...document.querySelectorAll("#conversation .live-line")].map(l => {
    const [en, ko] = [...l.querySelectorAll("p")].map(p => p.textContent); return { en, ko };
  }),
  answer: document.querySelector("#answer-text").textContent,
  answerKo: document.querySelector("#answer-korean").textContent,
  alt: document.querySelector("#alternative-card").hidden ? "" : document.querySelector("#alternative-text").textContent,
  altKo: document.querySelector("#alternative-card").hidden ? "" : document.querySelector("#alternative-korean").textContent,
  label: document.querySelector("#primary-label").textContent,
  grounding: document.querySelector("#grounding-badge").textContent,
}));
await page.locator("#live-panel").click({ button: "right", position: { x: 200, y: 300 } });
await page.locator('[data-action="start"]').click();
const loginDialog = page.locator("dialog[aria-labelledby=demo-login-title]");
if (await loginDialog.isVisible({ timeout: 3000 }).catch(() => false)) {
  await loginDialog.locator("input[type=text]").fill(process.env.DEMO_USER ?? "test");
  await loginDialog.locator("input[type=password]").fill(process.env.DEMO_PASSWORD ?? "test");
  await loginDialog.locator("button[value=login]").click();
}
await page.waitForFunction(() => document.querySelector("#live-panel").dataset.capture === "on", null, { timeout: 30000 });
const started = Date.now();
const timeline = [], answers = [], finals = new Map();
let previous = null, reconnects = 0;
while (Date.now() - started < Number(seconds) * 1000) {
  const s = await snapshot();
  const t = Math.round((Date.now() - started) / 100) / 10;
  if (s.capture === "reconnecting" && previous?.capture !== "reconnecting") reconnects++;
  for (const line of s.lines) if (!line.ko.startsWith("듣는 중") && !finals.has(line.en)) finals.set(line.en, { t, ko: line.ko });
  for (const line of s.lines) { const f = finals.get(line.en); if (f && (f.ko.includes("번역 중") || !f.ko) && !line.ko.includes("번역 중")) { f.ko = line.ko; f.koAt = t; } }
  if (!previous || s.answer !== previous.answer || s.alt !== previous.alt)
    if (!s.answer.includes("표시됩니다")) answers.push({ t, answer: s.answer, alt: s.alt, label: s.label, grounding: s.grounding });
  if (s.error && s.error !== previous?.error) timeline.push({ t, error: s.error });
  previous = s;
  await page.waitForTimeout(1000);
}
await page.screenshot({ path: `${outDir}/real-meeting-final.png` });
const final = await snapshot();
await page.locator("#live-panel").click({ button: "right", position: { x: 200, y: 300 } });
await page.locator('[data-action="stop"]').click();
const finalList = [...finals.entries()].map(([en, v]) => ({ en, ...v }));
const words = finalList.map(f => f.en.split(/\s+/).filter(Boolean).length);
const report = {
  source: "AMI Meeting Corpus ES2002a, Array1-01 far-field room microphone, 04:00-08:00 (CC BY 4.0, University of Edinburgh)",
  seconds: Number(seconds), reconnects,
  recognizedSegments: finalList.length,
  averageWordsPerSegment: words.length ? Math.round(words.reduce((a, b) => a + b, 0) / words.length * 10) / 10 : 0,
  shortSegments: words.filter(w => w <= 3).length,
  koreanFailures: finalList.filter(f => /실패|오류|제한|초과/.test(f.ko)).length,
  koreanPending: finalList.filter(f => f.ko.includes("번역 중")).length,
  suggestionChanges: answers.length,
  suggestionsWithAlternative: answers.filter(a => a.alt).length,
  errors: timeline, finals: finalList, answers, finalScreen: final,
};
writeFileSync(`${outDir}/real-meeting-report.json`, JSON.stringify(report, null, 1));
const { finals: _f, answers: _a, finalScreen: _s, ...summary } = report;
console.log(JSON.stringify(summary, null, 1));
console.log("--- first segments ---");
for (const f of finalList.slice(0, 12)) console.log(`${f.t}s | ${f.en} | ${f.ko}`);
console.log("--- suggestions ---");
for (const a of answers.slice(0, 12)) console.log(`${a.t}s | ${a.answer} || ${a.alt}`);
await browser.close();
