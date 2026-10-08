// Local-only verification of live.html against the Fake API (synthetic Chromium microphone, no real device).
import { chromium } from "playwright";
const out = process.argv[2];
const browser = await chromium.launch({ channel: "msedge", args: ["--use-fake-ui-for-media-stream", "--use-fake-device-for-media-stream"] });
const page = await browser.newPage({ viewport: { width: 760, height: 900 } });
const logs = [];
page.on("console", m => logs.push(`${m.type()}: ${m.text()}`));
page.on("pageerror", e => logs.push(`pageerror: ${e.message}`));
await page.goto("http://127.0.0.1:5173/live.html");
await page.waitForTimeout(1500);
await page.screenshot({ path: `${out}\\web-live-idle.png` });
await page.locator("#live-panel").click({ button: "right", position: { x: 200, y: 300 } });
await page.screenshot({ path: `${out}\\web-live-menu.png` });
await page.locator('[data-action="start"]').click();
await page.waitForFunction(() => document.querySelector("#live-panel").dataset.capture === "on", null, { timeout: 15000 });
const status = await page.locator("#capture-status").textContent();
await page.waitForFunction(() => !document.querySelector("#answer-text").textContent.includes("표시됩니다"), null, { timeout: 20000 });
await page.waitForTimeout(1500);
const result = await page.evaluate(() => ({
  capture: document.querySelector("#live-panel").dataset.capture,
  lines: [...document.querySelectorAll("#conversation .live-line")].map(l => [...l.querySelectorAll("p")].map(p => p.textContent)),
  answer: document.querySelector("#answer-text").textContent,
  answerKo: document.querySelector("#answer-korean").textContent,
  altVisible: !document.querySelector("#alternative-card").hidden,
  alt: document.querySelector("#alternative-text").textContent,
  altKo: document.querySelector("#alternative-korean").textContent,
  label: document.querySelector("#primary-label").textContent,
  continuity: document.querySelector("#continuity").textContent,
  error: document.querySelector("#session-error").textContent,
  panel: (r => ({ w: r.width, h: r.height }))(document.querySelector("#live-panel").getBoundingClientRect()),
}));
await page.screenshot({ path: `${out}\\web-live-answer.png` });
console.log(JSON.stringify({ status, ...result }, null, 1));
await page.locator("#live-panel").click({ button: "right", position: { x: 200, y: 300 } });
await page.locator('[data-action="stop"]').click();
await page.waitForTimeout(800);
console.log("after stop:", await page.locator("#capture-status").textContent(), "| answer kept:", await page.locator("#answer-text").textContent());
console.log(logs.filter(l => !l.startsWith("debug")).slice(-10).join("\n"));
await browser.close();
