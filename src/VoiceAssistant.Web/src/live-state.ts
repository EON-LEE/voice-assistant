import type { Grounding, ServerEvent, Source } from "./protocol.js";

export interface LiveTurn { id: string; revision: number; text: string; final: boolean }
export interface LiveReply {
  id: string; turnId: string; text: string; complete: boolean; sources: Source[];
  grounding?: Grounding; suggestions?: string[]; respondNow?: boolean;
}
export function answers(reply: LiveReply | null): string[] {
  if (!reply) return [];
  return reply.suggestions ?? (reply.text.trim() ? [reply.text] : []);
}

const maxText = 32768;
function bounded(text: string): string {
  if (text.length > maxText) throw new Error("Transcript or reply exceeds the 32 KiB text limit.");
  return text;
}

/**
 * Same ownership rules as the desktop overlay: ongoing speech never discards a started reply,
 * the last completed suggestions stay visible while the next reply is prepared, and stale ids are ignored.
 */
export class LiveState {
  turns: LiveTurn[] = [];
  current: LiveReply | null = null;
  lastCompleted: LiveReply | null = null;
  error = "";
  private seen = new Set<string>();
  private order: string[] = [];
  private latestTurn: string | undefined;
  private suppressedTurn: string | undefined;
  private suppressAll = false;

  get display(): LiveReply | null { return this.current?.text ? this.current : this.lastCompleted; }
  get latest(): LiveTurn | undefined { return this.turns.at(-1); }
  /** A new final utterance is waiting for (or receiving) a reply that is not yet the displayed one. */
  get generating(): boolean {
    const latest = this.latest;
    return !!this.current && !this.current.complete || !!latest?.final && this.display?.turnId !== latest.id;
  }

  resetSession(): void {
    this.turns = []; this.current = null; this.lastCompleted = null; this.error = "";
    this.seen.clear(); this.order = []; this.latestTurn = undefined;
    this.suppressedTurn = undefined; this.suppressAll = false;
  }
  pause(value: boolean): void {
    this.suppressAll = value;
    if (value) this.cancelCurrent(); else this.suppressedTurn = undefined;
  }
  cancelCurrent(): void {
    this.suppressedTurn = this.current?.turnId ?? this.latestTurn;
    this.current = null;
  }
  beginRequest(): void { this.suppressedTurn = undefined; this.error = ""; }
  clearError(): void { this.error = ""; }

  apply(e: ServerEvent): void {
    switch (e.type) {
      case "error":
        this.error = `${e.code}: ${e.message}${e.retryable ? " (You can retry.)" : ""}`;
        return;
      case "transcript.partial":
      case "transcript.final": {
        const index = this.turns.findIndex(turn => turn.id === e.turnId);
        const old = this.turns[index];
        if (old && (e.revision! < old.revision || old.final || e.revision === old.revision && e.type === "transcript.partial")) return;
        const turn: LiveTurn = { id: e.turnId!, revision: e.revision!, text: bounded(e.text!), final: e.type === "transcript.final" };
        if (old) this.turns[index] = turn;
        else {
          this.turns.push(turn); this.latestTurn = turn.id;
          if (this.turns.length > 64) this.turns.shift();
        }
        return;
      }
      case "response.started":
        if (this.suppressAll || this.suppressedTurn === e.turnId || this.seen.has(e.responseId!)) return;
        if (!this.turns.some(turn => turn.id === e.turnId)) return;
        this.seen.add(e.responseId!); this.order.push(e.responseId!);
        if (this.order.length > 256) this.seen.delete(this.order.shift()!);
        this.error = "";
        this.current = { id: e.responseId!, turnId: e.turnId!, text: "", complete: false, sources: [] };
        return;
      case "response.delta":
      case "response.completed":
      case "response.cancelled": {
        const current = this.current;
        if (!current || current.complete || this.suppressAll || this.suppressedTurn === e.turnId ||
          e.responseId !== current.id || e.turnId !== current.turnId) return;
        if (e.type === "response.cancelled") { this.current = null; return; }
        if (e.type === "response.delta") { this.current = { ...current, text: bounded(current.text + e.text) }; return; }
        this.current = { ...current, text: bounded(e.text!), complete: true, sources: structuredClone(e.sources ?? []),
          ...(e.grounding ? { grounding: e.grounding } : {}), ...(e.suggestions ? { suggestions: [...e.suggestions] } : {}), ...(e.respondNow ? { respondNow: true } : {}) };
        this.lastCompleted = this.current;
        return;
      }
    }
  }
}

/** Network/service interruptions are retried automatically; sign-in, permission and protocol failures are not. */
export function isTransientFailure(message: string): boolean {
  if (/superseded|expired|permission|not allowed|denied|microphone|HTTPS|invalid_|time limit|unsupported|forbidden|\(40[13]\)|sign in before/i.test(message))
    return false;
  return /disconnect|WebSocket|timed out|connection|network|ready within|cannot keep up|ended this session|session_ended|stream_ended|Failed to fetch|\(5\d\d\)|\(429\)|busy|unavailable/i.test(message);
}

/** Splits long recognized speech at word boundaries into the enrichment API's 600-character limit. */
export function translationChunks(text: string): string[] {
  let remaining = text.trim();
  if (!remaining) return [];
  const chunks: string[] = [];
  while (remaining.length > 600) {
    let boundary = remaining.lastIndexOf(" ", 600);
    if (boundary <= 0) boundary = 600;
    chunks.push(remaining.slice(0, boundary));
    remaining = remaining.slice(boundary).trimStart();
  }
  if (remaining) chunks.push(remaining);
  return chunks;
}
