// Local-only: forcibly drops the meeting WebSocket and checks automatic reconnect keeps the screen state.
import { chromium } from "playwright";
const browser = await chromium.launch({ channel: "msedge", args: ["--use-fake-ui-for-media-stream", "--use-fake-device-for-media-stream"] });
const page = await browser.newPage({ viewport: { width: 760, height: 900 } });
const servers = [];
await page.routeWebSocket(/\/api\/meeting/, ws => { servers.push(ws.connectToServer()); });
await page.goto("http://127.0.0.1:5173/live.html");
await page.waitForTimeout(1000);
await page.locator("#live-panel").click({ button: "right", position: { x: 200, y: 300 } });
await page.locator('[data-action="start"]').click();
await page.waitForFunction(() => !document.querySelector("#answer-text").textContent.includes("표시됩니다"), null, { timeout: 20000 });
const before = await page.locator("#answer-text").textContent();
servers.at(-1).close({ code: 1011, reason: "simulated network drop" });
await page.waitForFunction(() => document.querySelector("#live-panel").dataset.capture === "reconnecting", null, { timeout: 5000 });
const during = { status: await page.locator("#capture-status").textContent(), error: await page.locator("#session-error").textContent(),
  answer: await page.locator("#answer-text").textContent() };
await page.waitForFunction(() => document.querySelector("#live-panel").dataset.capture === "on", null, { timeout: 15000 });
await page.waitForTimeout(500);
const after = { status: await page.locator("#capture-status").textContent(), error: await page.locator("#session-error").textContent(),
  sockets: servers.length, lines: await page.locator("#conversation .live-line").count() };
console.log(JSON.stringify({ before, during, after }, null, 1));
await browser.close();
