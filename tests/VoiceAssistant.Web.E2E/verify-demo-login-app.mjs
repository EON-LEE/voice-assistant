// Local verification: log in through the demo dialog inside the LiveCoach app (started with --fake-mic) and start live.
import { chromium } from "playwright";
const browser = await chromium.connectOverCDP("http://127.0.0.1:9333");
const page = browser.contexts()[0].pages().find(p => p.url().includes("live.html"));
await page.evaluate(() => localStorage.removeItem("live-coach-demo-session"));
await page.reload();
await page.waitForTimeout(1500);
await page.locator("#live-panel").click({ button: "right", position: { x: 200, y: 300 } });
await page.locator('[data-action="start"]').click();
const dialog = page.locator("dialog[aria-labelledby=demo-login-title]");
await dialog.waitFor({ timeout: 10000 });
await dialog.locator('input[type=text]').fill("test");
await dialog.locator('input[type=password]').fill("wrong");
await dialog.locator('button[value=login]').click();
await page.locator("dialog[aria-labelledby=demo-login-title] [role=alert]").filter({ hasText: "올바르지" }).waitFor({ timeout: 10000 });
console.log("wrong password message shown");
await dialog.locator('input[type=password]').fill("test");
if (await dialog.locator('input[type=text]').inputValue() !== "test") throw new Error("username was not kept after a wrong password");
await dialog.locator('button[value=login]').click();
await page.waitForTimeout(12000);
console.log("after login:", JSON.stringify(await page.evaluate(() => ({
  capture: document.querySelector("#live-panel").dataset.capture,
  status: document.querySelector("#capture-status").textContent,
  error: document.querySelector("#session-error").textContent,
  dialogOpen: !!document.querySelector("dialog[open]"),
  stored: !!localStorage.getItem("live-coach-demo-session"),
}))));
await page.waitForFunction(() => document.querySelector("#live-panel").dataset.capture === "on", null, { timeout: 30000 });
console.log("status:", await page.locator("#capture-status").textContent());
console.log("stored session:", await page.evaluate(() => !!localStorage.getItem("live-coach-demo-session")));
await page.waitForTimeout(8000);
console.log("error line:", JSON.stringify(await page.locator("#session-error").textContent()));
await page.locator("#live-panel").click({ button: "right", position: { x: 200, y: 300 } });
await page.locator('[data-action="stop"]').click();
await page.waitForTimeout(1000);
console.log("after stop:", await page.locator("#capture-status").textContent());
await browser.close();
