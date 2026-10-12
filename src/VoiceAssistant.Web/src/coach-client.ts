export type CoachKind = "question" | "reply";
export type Voice = "coach" | "partner";
export type Rate = "normal" | "slow";
export interface Chunk { en: string; ko: string }
export interface Enrichment { korean: string; pronunciation: Chunk[] | null }
export interface Scenario { kind: "sales" | "interview" | "presentation" | "custom"; description: string; difficulty: number }
export interface HistoryEntry { role: "partner" | "user"; text: string }
export interface PracticeSettings { scenario: Scenario; topic: string; useMaterials: boolean; maxTurns: number }
export type Grounding = "disabled" | "grounded" | "no_matches" | "unavailable";
export interface PracticeLine { text: string; grounding: Grounding; sources: { title: string }[] }
export interface PartnerTurn extends PracticeLine { done: boolean; turn: number }
export interface Feedback { correctedEnglish: string; easierEnglish: string; feedbackKo: string; points: { tag: string; ko: string }[]; clarity: number }
export interface PracticeAnswer { question: string; answer: string; correctedEnglish?: string }
export interface PracticeSummary { headlineKo: string; strengthsKo: string[]; improveKo: string[]; phrases: Chunk[] }
export type CoachFetch = (path: string, init: RequestInit) => Promise<Response>;

export class CoachError extends Error {
  constructor(readonly code: string, readonly retryAfter = 0) {
    super(({ busy: `The coach is busy. Retry${retryAfter ? ` after ${retryAfter} seconds` : " shortly"}.`,
      unauthorized: "Sign in again to use the coach.", forbidden: "Coach access is not permitted for this account.",
      invalid_request: "Check the practice settings or shorten your text.", too_large: "This request is too long. Shorten the practice answer or history.",
      provider_timeout: "The coach took too long. Retry when ready.", invalid_response: "The coach returned an invalid response. Please retry.",
      provider_unavailable: "The coach is unavailable. Please retry." } as Record<string, string>)[code] ?? "The coach request failed. Please retry.");
  }
}
function record(value: unknown, keys: string[]): Record<string, unknown> {
  if (!value || typeof value !== "object" || Array.isArray(value)) throw new CoachError("invalid_response");
  const object = value as Record<string, unknown>;
  if (Object.keys(object).some(key => !keys.includes(key)) || keys.some(key => !(key in object))) throw new CoachError("invalid_response");
  return object;
}
function text(value: unknown, min: number, max: number, allowNewline = false): string {
  if (typeof value !== "string" || value.length > max || value.trim().length < min ||
    (allowNewline ? /[\u0000-\u0009\u000b-\u001f\u007f-\u009f]/u : /[\u0000-\u001f\u007f-\u009f]/u).test(value))
    throw new CoachError("invalid_response");
  return value;
}
function bool(value: unknown): boolean { if (typeof value !== "boolean") throw new CoachError("invalid_response"); return value; }
function integer(value: unknown, min: number, max: number): number {
  if (!Number.isInteger(value) || (value as number) < min || (value as number) > max) throw new CoachError("invalid_response");
  return value as number;
}
function choice<T extends string>(value: unknown, values: readonly T[]): T {
  if (typeof value !== "string" || !values.includes(value as T)) throw new CoachError("invalid_response"); return value as T;
}
function array(value: unknown, max: number): unknown[] { if (!Array.isArray(value) || value.length > max) throw new CoachError("invalid_response"); return value; }
export function normalizeWords(value: string): string { return value.trim().replace(/\s+/g, " "); }
function english(value: unknown, maxWords: number, question = false): string {
  const s = text(value, 1, 800);
  if (s !== normalizeWords(s) || !/[A-Za-z]/.test(s) || /[^A-Za-z0-9 .,?!':()\-]/u.test(s) ||
    /you could say|http/i.test(s) || s.startsWith("-") || s.includes(" - ") || /^\d/.test(s) && s.includes(". ") ||
    normalizeWords(s).split(" ").length > maxWords || sentenceCount(s) > 2 ||
    question && !s.endsWith("?")) throw new CoachError("invalid_response");
  return s;
}
function sentenceCount(s: string): number {
  const value = s.replace(/(\d)\.(?=\d)/g, "$1");
  const endings = value.match(/[.!?]+/g)?.length ?? 0;
  return endings + (/[.!?]\s*$/.test(value) ? 0 : 1);
}
function korean(value: unknown, max: number, fake: boolean): string {
  const s = text(value, 1, max);
  if (fake && s.startsWith("[fake-ko] ")) return s;
  const hangul = s.match(/[\uac00-\ud7a3]/gu)?.length ?? 0;
  if (!hangul || hangul < (s.match(/[A-Za-z]/g)?.length ?? 0) || /[<>*`#"]|\p{So}|\p{Cs}/u.test(s))
    throw new CoachError("invalid_response");
  return s;
}
export function validateEnrichment(value: unknown, kind: CoachKind, input: string, fake = false): Enrichment {
  const object = record(value, ["korean", "pronunciation"]);
  const ko = korean(object.korean, 400, fake);
  if (kind === "question") {
    if (object.pronunciation !== null) throw new CoachError("invalid_response");
    return { korean: ko, pronunciation: null };
  }
  const chunks = array(object.pronunciation, 40);
  if (!chunks.length) throw new CoachError("invalid_response");
  const pronunciation = chunks.map(value => {
    const chunk = record(value, ["en", "ko"]);
    const en = text(chunk.en, 1, 600), ko = text(chunk.ko, 1, 80);
    if (en !== normalizeWords(en) || en.split(" ").length > 4 || !/^[\uac00-\ud7a3\d ,.?!'-]+$/u.test(ko) ||
      !/[\uac00-\ud7a3]/u.test(ko) || /\d/.test(ko) && !/\d/.test(en)) throw new CoachError("invalid_response");
    return { en, ko };
  });
  if (pronunciation.map(chunk => normalizeWords(chunk.en)).join(" ") !== normalizeWords(input)) throw new CoachError("invalid_response");
  return { korean: ko, pronunciation };
}
export function validateSettings(input: PracticeSettings): PracticeSettings {
  try {
    const scenario = record(input.scenario, ["kind", "description", "difficulty"]);
    const kind = choice(scenario.kind, ["sales", "interview", "presentation", "custom"] as const);
    return { scenario: { kind, description: text(scenario.description, kind === "custom" ? 1 : 0, 400),
      difficulty: integer(scenario.difficulty, 1, 3) }, topic: text(input.topic, 0, 300),
      useMaterials: bool(input.useMaterials), maxTurns: integer(input.maxTurns, 1, 12) };
  } catch { throw new CoachError("invalid_request"); }
}
function sources(value: unknown): { title: string }[] {
  const result = array(value, 5).map(value => ({ title: text(record(value, ["title"]).title, 1, 200) }));
  if (new Set(result.map(source => source.title)).size !== result.length) throw new CoachError("invalid_response");
  return result;
}
function line(object: Record<string, unknown>, maxWords: number, question = false): PracticeLine {
  return { text: english(object.text, maxWords, question),
    grounding: choice(object.grounding, ["disabled", "grounded", "no_matches", "unavailable"] as const), sources: sources(object.sources) };
}
export function validateTurn(value: unknown, asked: number, maxTurns: number): PartnerTurn {
  const object = record(value, ["text", "done", "turn", "grounding", "sources"]);
  const done = bool(object.done), turn = integer(object.turn, 1, 13);
  if (turn !== asked + 1 || done !== (asked >= maxTurns)) throw new CoachError("invalid_response");
  return { ...line(object, 30, !done), done, turn };
}
export function validateFeedback(value: unknown, fake = false): Feedback {
  const o = record(value, ["correctedEnglish", "easierEnglish", "feedbackKo", "points", "clarity"]);
  const feedbackKo = korean(o.feedbackKo, 400, fake);
  if (sentenceCount(feedbackKo) > 2) throw new CoachError("invalid_response");
  return { correctedEnglish: english(o.correctedEnglish, 40), easierEnglish: english(o.easierEnglish, 40), feedbackKo,
    points: array(o.points, 3).map(value => {
      const p = record(value, ["tag", "ko"]);
      return { tag: choice(p.tag, ["grammar", "vocabulary", "clarity", "length", "tone"] as const), ko: korean(p.ko, 120, fake) };
    }), clarity: integer(o.clarity, 1, 5) };
}
export function validateSummary(value: unknown, fake = false): PracticeSummary {
  const o = record(value, ["headlineKo", "strengthsKo", "improveKo", "phrases"]);
  return { headlineKo: korean(o.headlineKo, 400, fake), strengthsKo: array(o.strengthsKo, 3).map(s => korean(s, 120, fake)),
    improveKo: array(o.improveKo, 3).map(s => korean(s, 120, fake)),
    phrases: array(o.phrases, 8).map(value => { const p = record(value, ["en", "ko"]); return { en: english(p.en, 40), ko: korean(p.ko, 120, fake) }; }) };
}
export class CoachClient {
  constructor(private readonly request: CoachFetch, private readonly fake: () => boolean = () => false) {}
  private async post(path: string, input: object, signal: AbortSignal, audio = false): Promise<Response> {
    const body = JSON.stringify(input);
    if (new TextEncoder().encode(body).byteLength > 16384) throw new CoachError("too_large");
    const combined = AbortSignal.any([signal, AbortSignal.timeout(audio ? 20000 : 25000)]);
    const response = await this.request(path, { method: "POST", body, headers: { "Content-Type": "application/json; charset=utf-8" }, signal: combined });
    if (!response.ok) {
      let code = response.status === 401 ? "unauthorized" : response.status === 403 ? "forbidden" : response.status === 429 ? "busy" : "provider_unavailable";
      if (![401, 403, 429].includes(response.status)) {
        try { const data: unknown = await response.json(); if (data && typeof data === "object" && "error" in data && typeof data.error === "string") code = data.error; }
        catch { /* Never display raw server bodies. */ }
      }
      const after = response.headers.get("Retry-After");
      const seconds = after ? (/^\d+$/.test(after) ? Number(after) : Math.ceil((Date.parse(after) - Date.now()) / 1000)) : 0;
      throw new CoachError(code, Number.isFinite(seconds) ? Math.max(0, Math.min(3600, seconds)) : 0);
    }
    if (response.status !== 200) throw new CoachError("invalid_response");
    return response;
  }
  private async json(path: string, input: object, signal: AbortSignal): Promise<unknown> {
    const response = await this.post(path, input, signal);
    if (!response.headers.get("Content-Type")?.includes("application/json")) throw new CoachError("invalid_response");
    const body = await boundedBody(response, 65536);
    try { return JSON.parse(new TextDecoder().decode(body)); } catch { throw new CoachError("invalid_response"); }
  }
  async enrich(kind: CoachKind, input: string, signal: AbortSignal): Promise<Enrichment> {
    text(input, 1, 600, true);
    return validateEnrichment(await this.json("/api/assist/enrich", { kind, text: input }, signal), kind, input, this.fake());
  }
  /** Korean meaning only (no pronunciation work). Product names may stay in Latin letters. */
  async translate(kind: CoachKind, input: string, signal: AbortSignal): Promise<string> {
    text(input, 1, 600, true);
    const object = record(await this.json("/api/assist/enrich", { kind, text: input, translationOnly: true }, signal),
      ["korean", "pronunciation"]);
    const ko = text(object.korean, 1, 400);
    if (!(this.fake() && ko.startsWith("[fake-ko] ")) && !/[\uac00-\ud7a3]/u.test(ko)) throw new CoachError("invalid_response");
    return ko;
  }
  async speak(input: string, voice: Voice, rate: Rate, signal: AbortSignal): Promise<Blob> {
    text(input, 1, 400, true); choice(voice, ["coach", "partner"]); choice(rate, ["normal", "slow"]);
    const response = await this.post("/api/assist/speak", { text: input, voice, rate }, signal, true);
    const type = response.headers.get("Content-Type")?.split(";")[0]?.trim();
    if (type !== "audio/mpeg" && !(this.fake() && type === "audio/wav")) throw new CoachError("invalid_response");
    const bytes = await boundedBody(response, 4 * 1024 * 1024);
    if (!bytes.byteLength) throw new CoachError("invalid_response");
    return new Blob([bytes], { type });
  }
  async turn(settings: PracticeSettings, history: HistoryEntry[], signal: AbortSignal): Promise<PartnerTurn> {
    const validated = validateSettings(settings);
    if (history.length > 24 || history.length % 2 !== 0) throw new CoachError("invalid_request");
    history.forEach((entry, i) => {
      if (entry.role !== (i % 2 ? "user" : "partner")) throw new CoachError("invalid_request");
      text(entry.text, entry.role === "partner" ? 1 : 0, 800);
    });
    return validateTurn(await this.json("/api/practice/turn", { ...validated, history }, signal), history.length / 2, settings.maxTurns);
  }
  async suggest(settings: PracticeSettings, question: string, signal: AbortSignal): Promise<PracticeLine> {
    const { scenario, topic, useMaterials } = validateSettings(settings); text(question, 1, 800);
    return line(record(await this.json("/api/practice/suggest", { scenario, topic, useMaterials, question }, signal), ["text", "grounding", "sources"]), 25);
  }
  async feedback(scenario: Scenario, question: string, answer: string, signal: AbortSignal): Promise<Feedback> {
    validateSettings({ scenario, topic: "", useMaterials: false, maxTurns: 1 }); text(question, 1, 800); text(answer, 0, 800);
    return validateFeedback(await this.json("/api/practice/feedback", { scenario, question, answer }, signal), this.fake());
  }
  async summary(settings: PracticeSettings, turns: PracticeAnswer[], signal: AbortSignal): Promise<PracticeSummary> {
    const { scenario, topic } = validateSettings(settings);
    if (!turns.length || turns.length > 12) throw new CoachError("invalid_request");
    for (const turn of turns) { text(turn.question, 0, 800); text(turn.answer, 0, 800); if (turn.correctedEnglish !== undefined) text(turn.correctedEnglish, 0, 800); }
    return validateSummary(await this.json("/api/practice/summary", { scenario, topic, turns }, signal), this.fake());
  }
}
async function boundedBody(response: Response, limit: number): Promise<ArrayBuffer> {
  if (!response.body) throw new CoachError("invalid_response");
  const reader = response.body.getReader(); const chunks: Uint8Array[] = []; let size = 0;
  try {
    while (true) {
      const { value, done } = await reader.read(); if (done) break;
      size += value.byteLength; if (size > limit) throw new CoachError("invalid_response"); chunks.push(value);
    }
  } finally { await reader.cancel(); reader.releaseLock(); }
  const bytes = new Uint8Array(size); let offset = 0;
  for (const chunk of chunks) { bytes.set(chunk, offset); offset += chunk.byteLength; }
  return bytes.buffer;
}
