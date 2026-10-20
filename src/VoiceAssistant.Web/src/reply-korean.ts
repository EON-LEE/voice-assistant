import type { ServerEvent } from "./protocol.js";

/** How long a completed reply waits for the server's Korean line before the client translates it itself. */
export const pushedKoreanWaitMs = 4000;

type Line = string | null;
interface Waiter { responseId: string; index: number; done: () => void }

const maxResponses = 32;

/**
 * Korean meanings the server pushes for reply lines (`response.korean`). A line can arrive before or after its reply
 * completes. A line the server could not translate, or one that fails the client's check, is stored as `null` so the
 * client translates that line itself.
 */
export class PushedKorean {
  private readonly lines = new Map<string, [Line | undefined, Line | undefined]>();
  private waiters: Waiter[] = [];

  constructor(private readonly accepts: (korean: string) => boolean) {}

  record(e: ServerEvent): void {
    if (e.type !== "response.korean" || !e.responseId || (e.index !== 0 && e.index !== 1)) return;
    let entry = this.lines.get(e.responseId);
    if (!entry) {
      entry = [undefined, undefined];
      this.lines.set(e.responseId, entry);
      if (this.lines.size > maxResponses) this.lines.delete(this.lines.keys().next().value!);
    }
    if (entry[e.index] !== undefined) return;
    entry[e.index] = e.korean !== undefined && this.accepts(e.korean) ? e.korean : null;
    for (const waiter of this.waiters.filter(item => item.responseId === e.responseId && item.index === e.index)) waiter.done();
  }

  /** The recorded meaning; `null` when the line has none, `undefined` when nothing arrived yet. */
  peek(responseId: string, index: number): Line | undefined {
    return this.lines.get(responseId)?.[index];
  }

  /** Resolves with the meaning once it arrives, or null on a failed line, a timeout, an abort or `expire()`. */
  async wait(responseId: string, index: number, timeoutMs: number, signal: AbortSignal): Promise<string | null> {
    const known = this.peek(responseId, index);
    if (known !== undefined) return known;
    if (signal.aborted || timeoutMs <= 0) return null;
    await new Promise<void>(resolve => {
      const waiter: Waiter = { responseId, index, done: () => {
        clearTimeout(timer);
        signal.removeEventListener("abort", waiter.done);
        this.waiters = this.waiters.filter(item => item !== waiter);
        resolve();
      } };
      const timer = setTimeout(waiter.done, timeoutMs);
      signal.addEventListener("abort", waiter.done, { once: true });
      this.waiters.push(waiter);
    });
    return this.peek(responseId, index) ?? null;
  }

  /** The connection ended, so nothing more will arrive: waiting lines stop waiting. */
  expire(): void {
    for (const waiter of [...this.waiters]) waiter.done();
  }

  /** Forgets every response that is not in `keep`. */
  prune(keep: ReadonlySet<string | undefined>): void {
    for (const id of [...this.lines.keys()]) if (!keep.has(id)) this.lines.delete(id);
  }

  clear(): void {
    this.expire();
    this.lines.clear();
  }
}
