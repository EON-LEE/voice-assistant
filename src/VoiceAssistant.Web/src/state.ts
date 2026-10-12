import type { ServerEvent, Source, Grounding, ResponseRoute } from "./protocol.js";

export interface Turn { id: string; revision: number; text: string; final: boolean }
export interface Reply { id: string; turnId: string; text: string; complete: boolean; sources: Source[]; grounding?: Grounding; responseRoute?: ResponseRoute; retrievalPrefetched?: boolean }

export class ReplyState {
  turns: Turn[] = [];
  current: Reply | null = null;
  pinned: Reply | null = null;
  error = "";
  private seen = new Set<string>();
  private suppressed: string | undefined;
  private paused = false;
  get latest(): string | undefined { return this.turns.at(-1)?.id; }
  reset(): void { this.turns = []; this.current = null; this.seen.clear(); this.suppressed = undefined; this.paused = false; this.error = ""; }
  pin(): void { if (this.current) this.pinned = structuredClone(this.current); }
  cancel(): void { this.suppressed = this.latest; this.current = null; }
  request(): void { this.suppressed = undefined; this.error = ""; }
  pause(value: boolean, preserveCompleted = false): void {
    this.paused = value;
    if (value) {
      this.suppressed = this.latest;
      if (!preserveCompleted || !this.current?.complete) this.current = null;
    } else this.suppressed = undefined;
  }
  apply(e: ServerEvent): void {
    if (e.type === "error") { this.error = `${e.code}: ${e.message}${e.retryable ? " You can retry." : ""}`; return; }
    if (e.type === "transcript.partial" || e.type === "transcript.final") {
      const old = this.turns.find(t => t.id === e.turnId);
      if (old && (old.final || e.revision! < old.revision || e.revision === old.revision && e.type === "transcript.partial")) return;
      const turn = { id: e.turnId!, revision: e.revision!, text: bounded(e.text!), final: e.type === "transcript.final" };
      if (old) Object.assign(old, turn);
      else { this.turns.push(turn); this.current = null; if (this.turns.length > 64) this.turns.shift(); }
      return;
    }
    if (e.turnId !== this.latest || this.paused || e.turnId === this.suppressed) return;
    if (e.type === "response.started") {
      if (this.seen.has(e.responseId!)) return;
      this.seen.add(e.responseId!);
      if (this.seen.size > 256) this.seen.delete(this.seen.values().next().value!);
      this.current = { id: e.responseId!, turnId: e.turnId!, text: "", complete: false, sources: [] };
    } else if (this.current && this.current.id === e.responseId && !this.current.complete) {
      if (e.type === "response.delta") this.current = { ...this.current, text: bounded(this.current.text + e.text) };
      if (e.type === "response.completed") this.current = { ...this.current, text: bounded(e.text!), complete: true,
        sources: structuredClone(e.sources ?? []), grounding: e.grounding,
        responseRoute: e.responseRoute, retrievalPrefetched: e.retrievalPrefetched };
      if (e.type === "response.cancelled") this.current = null;
    }
  }
}
function bounded(text: string): string {
  if (text.length > 32768) throw new Error("Transcript/reply exceeded the 32 KiB text limit.");
  return text;
}
