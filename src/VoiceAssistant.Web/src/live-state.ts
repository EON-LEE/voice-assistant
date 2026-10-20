import type { Grounding, ServerEvent, Source } from "./protocol.js";

export interface LiveTurn { id: string; revision: number; text: string; final: boolean }
export interface LiveReply {
  id: string; turnId: string; text: string; complete: boolean; sources: Source[];
  question?: string; grounding?: Grounding; suggestions?: string[]; respondNow?: boolean;
  /** The primary line is final (`primaryDone`), so it can be read before the reply completes. */
  primaryDone?: boolean;
  /** The reply stopped after its primary line was shown: the line stays, without alternative or grounding. */
  interrupted?: boolean;
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
 * A reply becomes readable once its primary line is final (`primaryDone`) or it completes; drafts never replace
 * the answer on screen. An explicit reading hold preserves its question and reply while capture continues, and
 * the held reply still gains its alternative and grounding when it completes.
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
  private heldReply: LiveReply | null = null;
  private shownReply: LiveReply | null = null;

  /** The newest readable reply: a finished primary line or a completed reply. */
  get shown(): LiveReply | null { return this.shownReply; }
  get display(): LiveReply | null { return this.heldReply ?? this.shownReply; }
  get readingHeld(): boolean { return this.heldReply !== null; }
  get hasNewerReply(): boolean { return !!this.heldReply && !!this.shownReply && this.heldReply.id !== this.shownReply.id; }
  get latest(): LiveTurn | undefined { return this.turns.at(-1); }
  /** A newer reply is being produced. With gathered replies, not every final gets its own reply. */
  get generating(): boolean { return !!this.current && !this.current.complete; }

  resetSession(): void {
    this.turns = []; this.current = null; this.lastCompleted = null; this.error = "";
    this.seen.clear(); this.order = []; this.latestTurn = undefined;
    this.suppressedTurn = undefined; this.suppressAll = false;
    this.heldReply = null; this.shownReply = null;
  }
  holdDisplay(): void {
    const display = this.display;
    if (display && (display.complete || display.primaryDone)) this.heldReply = display;
  }
  releaseDisplay(): void { this.heldReply = null; }
  pause(value: boolean): void {
    this.suppressAll = value;
    if (value) this.cancelCurrent(); else this.suppressedTurn = undefined;
  }
  cancelCurrent(): void {
    this.suppressedTurn = this.current?.turnId ?? this.latestTurn;
    this.retire();
  }
  beginRequest(): void { this.suppressedTurn = undefined; this.error = ""; }
  clearError(): void { this.error = ""; }

  /** A reply that ends after its primary line was shown keeps that line readable, marked as interrupted. */
  private retire(): void {
    const current = this.current;
    if (current && !current.complete && current.primaryDone) this.show({ ...current, interrupted: true });
    this.current = null;
  }
  private show(reply: LiveReply): void {
    this.shownReply = reply;
    // A held reply keeps its text on screen and only gains what arrives later (alternative, grounding, sources).
    if (this.heldReply?.id === reply.id) this.heldReply = reply;
  }

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
        const target = this.turns.find(turn => turn.id === e.turnId);
        if (!target) return;
        this.seen.add(e.responseId!); this.order.push(e.responseId!);
        if (this.order.length > 256) this.seen.delete(this.order.shift()!);
        this.error = "";
        this.retire();
        this.current = { id: e.responseId!, turnId: e.turnId!, question: target.text, text: "", complete: false, sources: [] };
        return;
      case "response.delta":
      case "response.completed":
      case "response.cancelled": {
        const current = this.current;
        if (!current || current.complete || this.suppressAll || this.suppressedTurn === e.turnId ||
          e.responseId !== current.id || e.turnId !== current.turnId) return;
        if (e.type === "response.cancelled") { this.retire(); return; }
        if (e.type === "response.delta") {
          const primaryDone = current.primaryDone || e.primaryDone === true;
          this.current = { ...current, text: bounded(current.text + (e.text ?? "")), ...(primaryDone ? { primaryDone: true } : {}) };
          if (primaryDone) this.show(this.current);
          return;
        }
        // The completed text is authoritative: it replaces a shown primary line that differs from it.
        this.current = { ...current, text: bounded(e.text!), complete: true, sources: structuredClone(e.sources ?? []),
          ...(e.grounding ? { grounding: e.grounding } : {}), ...(e.suggestions ? { suggestions: [...e.suggestions] } : {}), ...(e.respondNow ? { respondNow: true } : {}) };
        this.lastCompleted = this.current;
        this.show(this.current);
        return;
      }
    }
  }
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
