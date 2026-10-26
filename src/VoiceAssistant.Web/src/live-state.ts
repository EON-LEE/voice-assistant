import type { Grounding, ServerEvent, Source } from "./protocol.js";

export interface LiveTurn { id: string; revision: number; text: string; final: boolean }
export interface LiveReply {
  id: string; turnId: string; text: string; complete: boolean; sources: Source[];
  /** What the reply answers: its gathered finals (`segments` of `response.started`), joined with single spaces. */
  question?: string;
  grounding?: Grounding; suggestions?: string[]; respondNow?: boolean;
  /** The primary line is final (`primaryDone`), so it can be read before the reply completes. */
  primaryDone?: boolean;
  /** The reply stopped after its primary line was shown: the line stays, without alternative or grounding. */
  interrupted?: boolean;
}
export function answers(reply: LiveReply | null): string[] {
  if (!reply) return [];
  return reply.suggestions ?? (reply.text.trim() ? [reply.text] : []);
}
/**
 * A reply line split after its first sentence, the direct answer the reply starts with, so the screen can set it apart.
 * The two parts join back to exactly the line. A sentence ends at ".", "!" or "?" before a space and a capital letter or
 * digit, so "U.S. customers" or "0.0446%" do not split; a line without such an end is all lead.
 */
export function leadSentence(text: string): [lead: string, rest: string] {
  const end = /[.!?]+["'’”)\]]*(?=\s+["'‘“(]?[A-Z0-9])/u.exec(text);
  if (!end) return [text, ""];
  const at = end.index + end[0].length;
  return [text.slice(0, at), text.slice(at)];
}

/**
 * The labels above the two suggestion cards. `previous`: the answer on screen is for an earlier question, because a newer
 * reply is being prepared or held back (see {@link LiveState.showsEarlierAnswer}).
 */
export function suggestionLabels(previous: boolean): { primary: string; alternative: string } {
  return { primary: previous ? "이전 질문용 답변" : "추천 1", alternative: "추천 2 · 다른 방향" };
}

export const meaningTexts = {
  waiting: "영어 추천 답변의 한국어 뜻이 함께 표시됩니다.",
  preparing: "한국어 번역 준비 중…",
  interrupted: "답변이 중단되어 한국어 뜻이 없습니다 · 우클릭 → 한국어 번역 재시도",
} as const;
/**
 * The Korean meaning under the primary line: the translation when there is one, else a line the server already pushed
 * for a reply that has not completed. An interrupted reply never completes, so it gets a final label instead of "preparing".
 */
export function primaryMeaning(display: LiveReply | null, translated: string | undefined, pushed: string | null | undefined): string {
  if (!display) return meaningTexts.waiting;
  if (translated !== undefined) return translated;
  if (!display.complete && typeof pushed === "string") return pushed;
  return display.interrupted ? meaningTexts.interrupted : meaningTexts.preparing;
}
/** A reply line the user can ask to translate: a completed reply, or a primary line that stayed after an interruption. */
export function translatable(reply: LiveReply | null): reply is LiveReply {
  return !!reply && (reply.complete || !!reply.interrupted);
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
  /** A `response_filtered` error arrived: the `response.cancelled` that follows withdraws its reply, wherever it is shown. */
  private withdrawing = false;

  /** The newest readable reply: a finished primary line or a completed reply. */
  get shown(): LiveReply | null { return this.shownReply; }
  get display(): LiveReply | null { return this.heldReply ?? this.shownReply; }
  get readingHeld(): boolean { return this.heldReply !== null; }
  get hasNewerReply(): boolean { return !!this.heldReply && !!this.shownReply && this.heldReply.id !== this.shownReply.id; }
  get latest(): LiveTurn | undefined { return this.turns.at(-1); }
  /** A newer reply is being produced. With gathered replies, not every final gets its own reply. */
  get generating(): boolean { return !!this.current && !this.current.complete; }
  /**
   * The answer on screen is for an earlier question: a newer reply arrived while it was held, or, while `connected`, a newer
   * one is being prepared.
   */
  showsEarlierAnswer(connected: boolean): boolean {
    const display = this.display;
    return !!display && (this.hasNewerReply || connected && this.generating && this.current?.id !== display.id);
  }

  resetSession(): void {
    this.turns = []; this.current = null; this.lastCompleted = null; this.error = "";
    this.seen.clear(); this.order = []; this.latestTurn = undefined;
    this.suppressedTurn = undefined; this.suppressAll = false;
    this.heldReply = null; this.shownReply = null; this.withdrawing = false;
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
  /** The connection producing the current reply is gone: a shown primary line stays (interrupted), a draft is dropped. */
  endReply(): void {
    this.withdrawing = false;
    if (this.current && !this.current.complete) this.retire();
  }
  /**
   * A new live session starts. Starting fresh clears everything. Continuing (다시 공유 / 다시 시작) keeps the conversation,
   * but never an old pause or a reply the previous session was still producing, either of which would hide new replies.
   */
  restart(keepConversation: boolean): void {
    if (!keepConversation) { this.resetSession(); return; }
    this.suppressAll = false;
    this.suppressedTurn = undefined;
    this.error = "";
    this.endReply();
  }

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
  /** The content filter stopped this reply: none of its text stays, neither shown, held nor as an interrupted line. */
  private withdraw(id: string): void {
    if (this.current?.id === id) this.current = null;
    if (this.shownReply?.id === id) this.shownReply = null;
    if (this.heldReply?.id === id) this.heldReply = null;
  }
  /**
   * What a reply answers. The server gathers the finals of one settle window into one reply but names only the latest turn;
   * `segments` says how many finals it answers, so they are joined, ending with the target. Without `segments` (an older
   * server), or when those finals are no longer all known, it is the target turn alone.
   */
  private answered(target: LiveTurn, segments: number | undefined): string {
    if (!segments || segments < 2 || !target.final) return target.text;
    const finals = this.turns.slice(0, this.turns.indexOf(target) + 1).filter(turn => turn.final);
    if (finals.length < segments) return target.text;
    return finals.slice(-segments).map(turn => turn.text.trim()).join(" ");
  }

  apply(e: ServerEvent): void {
    switch (e.type) {
      case "error":
        this.error = `${e.code}: ${e.message}${e.retryable ? " (You can retry.)" : ""}`;
        // It concerns the reply being produced, which never completed; its response.cancelled follows.
        if (e.code === "response_filtered") {
          this.withdrawing = true;
          if (this.current && !this.current.complete) this.withdraw(this.current.id);
        }
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
        // The withdrawn reply's response.cancelled always comes before a new reply; without it nothing is left to withdraw.
        this.withdrawing = false;
        if (this.suppressAll || this.suppressedTurn === e.turnId || this.seen.has(e.responseId!)) return;
        const target = this.turns.find(turn => turn.id === e.turnId);
        if (!target) return;
        this.seen.add(e.responseId!); this.order.push(e.responseId!);
        if (this.order.length > 256) this.seen.delete(this.order.shift()!);
        this.error = "";
        this.retire();
        this.current = { id: e.responseId!, turnId: e.turnId!, question: this.answered(target, e.segments), text: "", complete: false,
          sources: [] };
        return;
      case "response.delta":
      case "response.completed":
      case "response.cancelled": {
        // Matched by id, even when this page had already stopped following the reply (a pause kept its line, interrupted).
        if (e.type === "response.cancelled" && this.withdrawing) { this.withdrawing = false; this.withdraw(e.responseId!); return; }
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
