import { CoachClient, CoachError, validateSettings, type PracticeSettings, type HistoryEntry,
  type PartnerTurn, type Feedback, type PracticeAnswer, type PracticeSummary, type PracticeLine } from "./coach-client.js";

export type PracticePhase = "idle" | "connecting" | "question" | "answering" | "feedback" | "summary" | "busy" | "error";
export interface PracticeView {
  phase: PracticePhase; question: PartnerTurn | null; answer: string; partial: string;
  feedback: Feedback | null; summary: PracticeSummary | null; hint: PracticeLine | null; error: string;
}
export class PracticeRound {
  view: PracticeView = { phase: "idle", question: null, answer: "", partial: "", feedback: null, summary: null, hint: null, error: "" };
  private settings: PracticeSettings | undefined;
  private history: HistoryEntry[] = [];
  private turns: PracticeAnswer[] = [];
  private request: AbortController | undefined;
  private hintRequest: AbortController | undefined;
  private finalIds = new Set<string>();
  private generation = 0;
  private retry: (() => Promise<void>) | undefined;
  private retryAt = 0;
  private hintRetryAt = 0;
  private answerBlocked = false;
  constructor(private readonly client: Pick<CoachClient, "turn" | "suggest" | "feedback" | "summary">,
    private readonly changed: (view: PracticeView) => void, private readonly questionReady: (turn: PartnerTurn) => void) {}
  get active(): boolean { return this.view.phase !== "idle"; }
  get canFinish(): boolean { return this.view.phase === "answering" && !!this.view.answer && !this.view.partial && !this.answerBlocked; }
  begin(settings: PracticeSettings): void {
    this.stop(); this.settings = validateSettings(settings); this.view.phase = "connecting"; this.emit();
  }
  async next(): Promise<void> {
    if (!this.settings || !["connecting", "feedback", "error"].includes(this.view.phase)) return;
    await this.perform(async signal => {
      const turn = await this.client.turn(this.settings!, this.history.map(e => ({ ...e })), signal);
      if (signal.aborted) return;
      this.hintRequest?.abort();
      this.view.question = turn; this.view.hint = null; this.view.feedback = null; this.view.answer = ""; this.view.partial = "";
      this.finalIds.clear();
      this.answerBlocked = false;
      if (turn.done) { await this.finish(signal); return; }
      this.history.push({ role: "partner", text: turn.text });
      this.view.phase = "question"; this.emit(); this.questionReady(turn);
    }, () => this.next());
  }
  answer(): void {
    if (this.view.phase !== "question") return;
    this.view.phase = "answering"; this.view.partial = ""; this.emit();
  }
  pauseAnswer(): void {
    if (this.view.phase === "answering") { this.view.phase = "question"; this.view.partial = ""; this.emit(); }
  }
  transcript(turnId: string, text: string, final: boolean): void {
    if (this.view.phase !== "answering") return;
    if (final) {
      if (this.finalIds.has(turnId)) return;
      const joined = [this.view.answer, text].filter(Boolean).join(" ");
      if (joined.length > 800 || /[\u0000-\u001f\u007f-\u009f]/u.test(joined)) {
        this.answerBlocked = true;
        this.view.error = "This answer is too long or contains unsupported characters. Skip this answer or restart with a shorter reply.";
        this.emit(); return;
      }
      this.finalIds.add(turnId); this.view.answer = joined; this.view.partial = "";
    } else this.view.partial = text.slice(0, 800);
    this.emit();
  }
  async done(skip = false): Promise<void> {
    if (!this.settings || !this.view.question || !["question", "answering", "error"].includes(this.view.phase)) return;
    const question = this.view.question.text, answer = skip ? "" : this.view.answer;
    if (this.answerBlocked && !skip) return;
    this.hintRequest?.abort();
    await this.perform(async signal => {
      const feedback = await this.client.feedback(this.settings!.scenario, question, answer, signal);
      if (signal.aborted) return;
      this.history.push({ role: "user", text: answer });
      this.turns.push({ question, answer, correctedEnglish: feedback.correctedEnglish });
      this.view.answer = answer; this.view.partial = ""; this.view.feedback = feedback; this.view.phase = "feedback"; this.emit();
    }, () => this.done(skip));
  }
  async hint(): Promise<void> {
    if (!this.settings || !this.view.question || !["question", "answering"].includes(this.view.phase)) return;
    if (Date.now() < this.hintRetryAt) {
      this.view.error = `Wait ${Math.ceil((this.hintRetryAt - Date.now()) / 1000)} seconds before requesting another hint.`; this.emit(); return;
    }
    this.hintRequest?.abort(); const request = this.hintRequest = new AbortController(), generation = this.generation;
    try {
      const hint = await this.client.suggest(this.settings, this.view.question.text, request.signal);
      if (!request.signal.aborted && generation === this.generation) { this.view.hint = hint; this.view.error = ""; this.emit(); }
    } catch (error) {
      if (!request.signal.aborted && generation === this.generation) {
        this.hintRetryAt = error instanceof CoachError ? Date.now() + error.retryAfter * 1000 : 0;
        this.view.error = error instanceof CoachError ? error.message : "Hint unavailable. Request it again when ready."; this.emit();
      }
    }
  }
  async retryLast(): Promise<void> {
    if (Date.now() < this.retryAt) { this.view.error = `Please wait ${Math.ceil((this.retryAt - Date.now()) / 1000)} seconds before retrying.`; this.emit(); return; }
    await this.retry?.();
  }
  stop(): void {
    this.generation++; this.request?.abort(); this.hintRequest?.abort(); this.retry = undefined; this.retryAt = this.hintRetryAt = 0;
    this.history = []; this.turns = []; this.settings = undefined; this.finalIds.clear(); this.answerBlocked = false;
    this.view = { phase: "idle", question: null, answer: "", partial: "", feedback: null, summary: null, hint: null, error: "" };
    this.emit();
  }
  private async finish(signal: AbortSignal): Promise<void> {
    try {
      const summary = await this.client.summary(this.settings!, this.turns.map(turn => ({ ...turn })), signal);
      if (signal.aborted) return;
      this.view.summary = summary; this.view.phase = "summary"; this.emit();
    } catch (error) {
      this.retry = () => this.perform(s => this.finish(s), () => this.retryLast());
      throw error;
    }
  }
  private async perform(operation: (signal: AbortSignal) => Promise<void>, retry: () => Promise<void>): Promise<void> {
    this.request?.abort(); const request = this.request = new AbortController(), generation = this.generation;
    this.view.phase = "busy"; this.view.error = ""; this.retry = retry; this.emit();
    try { await operation(request.signal); }
    catch (error) {
      if (request.signal.aborted || generation !== this.generation) return;
      this.view.phase = "error"; this.view.error = error instanceof CoachError ? error.message : "Practice request failed. Retry or stop.";
      this.retryAt = error instanceof CoachError ? Date.now() + error.retryAfter * 1000 : 0; this.emit();
    }
  }
  private emit(): void { this.changed(this.view); }
}
