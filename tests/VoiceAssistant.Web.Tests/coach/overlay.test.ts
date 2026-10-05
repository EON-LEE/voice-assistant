import { afterEach, describe, expect, it, vi } from "vitest";
import { createRequire } from "node:module";
import { readFileSync } from "node:fs";
import { opacityKey, readOpacity, saveOpacity, validOpacity, Overlay } from "../../../src/VoiceAssistant.Web/src/overlay.js";
import { OwnerWindowClock, RenderScheduler, type RenderWindow } from "../../../src/VoiceAssistant.Web/src/render-scheduler.js";
afterEach(() => vi.useRealTimers());
describe("only a bounded opacity UI preference is persisted", () => {
  it.each([undefined, null, "", "  ", "no", 34, 101, Infinity])("invalid value %s uses80", input => expect(validOpacity(input)).toBe(80));
  it("accepts endpoints rounds values and writes only a numeric preference", () => {
    expect(validOpacity("35")).toBe(35); expect(validOpacity(100)).toBe(100); expect(validOpacity(65.4)).toBe(65);
    const setItem = vi.fn(); expect(saveOpacity({ setItem }, 50)).toBe(true); expect(setItem).toHaveBeenCalledExactlyOnceWith(opacityKey, "50");
    expect(readOpacity({ getItem: () => "37" })).toBe(37);
    expect(readOpacity({ getItem: () => { throw new Error("blocked"); } })).toBe(80);
    expect(saveOpacity({ setItem: () => { throw new Error("blocked"); } }, 50)).toBe(false);
  });
});
describe("visible owner window render clock", () => {
  function clock(raf: boolean) {
    const frames = new Map<number, FrameRequestCallback>(); let id = 0;
    const win: RenderWindow = {
      setTimeout: (fn, delay) => setTimeout(fn, delay) as unknown as number,
      clearTimeout: handle => clearTimeout(handle),
      ...(raf ? { requestAnimationFrame: (fn: FrameRequestCallback) => { frames.set(++id, fn); return id; },
        cancelAnimationFrame: (handle: number) => { frames.delete(handle); } } : {}),
    };
    return { win, frames };
  }
  it("coalesces requests and falls back when rAF stalls in a hidden window", () => {
    vi.useFakeTimers(); const window = clock(true), paint = vi.fn();
    const scheduler = new RenderScheduler(paint, new OwnerWindowClock(() => window.win));
    for (let i = 0; i < 1000; i++) scheduler.schedule();
    expect(vi.getTimerCount()).toBe(1);
    vi.advanceTimersByTime(50); expect(paint).not.toHaveBeenCalled(); expect(window.frames.size).toBe(1);
    vi.advanceTimersByTime(16); expect(paint).toHaveBeenCalledTimes(1); expect(window.frames.size).toBe(0);
    expect(vi.getTimerCount()).toBe(0);
  });
  it("rAF wins only once and flush/move cancels the previous window handles", () => {
    vi.useFakeTimers(); const a = clock(true), b = clock(true); let active = a.win; const paint = vi.fn();
    const scheduler = new RenderScheduler(paint, new OwnerWindowClock(() => active));
    scheduler.schedule(); vi.advanceTimersByTime(50); const old = [...a.frames.values()][0]!;
    active = b.win; scheduler.flush(); expect(paint).toHaveBeenCalledTimes(1);
    old(0); expect(paint).toHaveBeenCalledTimes(1);
    scheduler.schedule(); vi.advanceTimersByTime(50); [...b.frames.values()][0]!(0);
    vi.advanceTimersByTime(16); expect(paint).toHaveBeenCalledTimes(2); expect(a.frames.size).toBe(0);
  });
  it("works without rAF and clean cancellation prevents late renders", () => {
    vi.useFakeTimers(); const c = clock(false), paint = vi.fn();
    const scheduler = new RenderScheduler(paint, new OwnerWindowClock(() => c.win));
    scheduler.schedule(); scheduler.cancel(); vi.advanceTimersByTime(100); expect(paint).not.toHaveBeenCalled();
    scheduler.schedule(); vi.advanceTimersByTime(66); expect(paint).toHaveBeenCalledTimes(1);
  });
});
it("moves original nodes into settings with default closed state and restores sheet focus", () => {
  const { JSDOM } = createRequire(new URL("../../../src/VoiceAssistant.Web/package.json", import.meta.url))("jsdom");
  const html = readFileSync(new URL("../../../src/VoiceAssistant.Web/index.html", import.meta.url), "utf8");
  const dom = new JSDOM(html, { url: "https://fixture.invalid/" });
  const card = dom.window.document.querySelector(".coach-shell") as HTMLElement;
  const mode = dom.window.document.getElementById("mode");
  const overlay = new Overlay(card);
  expect(overlay.get("mode")).toBe(mode);
  expect(overlay.get("settings-sheet").hidden).toBe(true);
  expect(overlay.get<HTMLButtonElement>("float-toggle").disabled).toBe(true);
  overlay.get("settings-toggle").focus(); overlay.open("settings");
  expect(overlay.get("settings-sheet").hidden).toBe(false);
  expect(dom.window.document.activeElement?.id).toBe("settings-close");
  expect(overlay.get("overlay-content").inert).toBe(true);
  overlay.close(); expect(dom.window.document.activeElement?.id).toBe("settings-toggle");
  expect(overlay.get("overlay-content").inert).toBe(false);
  expect(card.style.getPropertyValue("--overlay-alpha")).toBe("0.8");
  expect(dom.window.localStorage.length).toBe(0); dom.window.close();
});
