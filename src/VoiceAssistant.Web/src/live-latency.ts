/** Reading latency for one reply, in milliseconds from the final transcript it answers. Debug use only; nothing is sent. */
export interface LatencyRecord {
  turnId: string;
  responseId: string;
  /** Final transcript → the reply's first readable line on screen (the primary line, or the completed reply). */
  finalToShownMs?: number;
  /** Final transcript → `response.completed` received. */
  finalToCompletedMs?: number;
  /** The first readable line came from `primaryDone`, before completion. */
  early?: boolean;
}

const maxRecords = 50;

export class LatencyMarks {
  readonly records: LatencyRecord[] = [];
  private readonly finals = new Map<string, number>();
  private readonly shown = new Set<string>();
  constructor(private readonly now: () => number, private readonly mark?: (name: string) => void) {}

  final(turnId: string): void {
    this.finals.set(turnId, this.now());
    if (this.finals.size > maxRecords) this.finals.delete(this.finals.keys().next().value!);
    this.mark?.(`live:final:${turnId}`);
  }
  /** Call when a reply first becomes readable on screen. */
  shownReply(responseId: string, turnId: string, complete: boolean): void {
    if (this.shown.has(responseId)) return;
    this.shown.add(responseId);
    if (this.shown.size > maxRecords) this.shown.delete(this.shown.values().next().value!);
    const record = this.record(responseId, turnId), final = this.finals.get(turnId);
    if (final !== undefined) record.finalToShownMs = Math.round(this.now() - final);
    record.early = !complete;
    this.mark?.(`live:shown:${responseId}`);
  }
  completed(responseId: string, turnId: string): void {
    const record = this.record(responseId, turnId), final = this.finals.get(turnId);
    if (final !== undefined && record.finalToCompletedMs === undefined) record.finalToCompletedMs = Math.round(this.now() - final);
    this.mark?.(`live:completed:${responseId}`);
  }
  private record(responseId: string, turnId: string): LatencyRecord {
    let record = this.records.find(item => item.responseId === responseId);
    if (!record) {
      record = { turnId, responseId };
      this.records.push(record);
      if (this.records.length > maxRecords) this.records.shift();
    }
    return record;
  }
}

/** Debug output is opt-in: `?debug=1` in the address, or localStorage `live-coach-debug` = "1". */
export function debugEnabled(search: string, storage: Pick<Storage, "getItem"> | undefined): boolean {
  try { return new URLSearchParams(search).get("debug") === "1" || storage?.getItem("live-coach-debug") === "1"; }
  catch { return false; }
}
