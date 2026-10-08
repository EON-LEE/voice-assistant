// Local-only: drives the page hosted inside the LiveCoach WebView2 app (started with --fake-mic) over CDP.
import { chromium } from "playwright";
const browser = await chromium.connectOverCDP("http://127.0.0.1:9333");
const page = browser.contexts()[0].pages().find(p => p.url().includes("live.html"));
const state = () => page.evaluate(() => ({
  capture: document.querySelector("#live-panel").dataset.capture,
  status: document.querySelector("#capture-status").textContent,
  panelBg: getComputedStyle(document.querySelector("#live-panel")).backgroundColor,
  bodyBg: getComputedStyle(document.body).backgroundColor,
  menu: [...document.querySelectorAll("#live-menu button")].filter(b => getComputedStyle(b).display !== "none").map(b => b.textContent),
  lines: [...document.querySelectorAll("#conversation .live-line")].map(l => [...l.querySelectorAll("p")].map(p => p.textContent).join(" / ")),
  answer: document.querySelector("#answer-text").textContent,
  alt: document.querySelector("#alternative-card").hidden ? null : document.querySelector("#alternative-text").textContent,
  error: document.querySelector("#session-error").textContent,
}));
console.log("before:", JSON.stringify(await state()));
await page.locator("#live-panel").click({ button: "right", position: { x: 200, y: 300 } });
await page.locator('[data-action="start"]').click();
await page.waitForFunction(() => !document.querySelector("#answer-text").textContent.includes("표시됩니다"), null, { timeout: 25000 });
await page.waitForTimeout(1500);
console.log("live:", JSON.stringify(await state(), null, 1));
await page.locator("#live-panel").click({ button: "right", position: { x: 200, y: 300 } });
await page.locator('[data-shell="opacity"]').click();
await page.waitForTimeout(400);
console.log("after opacity:", (await state()).panelBg);
await browser.close();
