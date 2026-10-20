export interface Source { title: string; url: string; updatedAt?: string | null }
export type Grounding = "disabled" | "grounded" | "unavailable" | "no_matches";
export type ResponseRoute = "transcript" | "profile" | "knowledge";
export interface ServerEvent {
  type: string; turnId?: string; revision?: number; text?: string; responseId?: string;
  sources?: Source[]; code?: string; message?: string; retryable?: boolean;
  grounding?: Grounding;
  responseRoute?: ResponseRoute;
  retrievalPrefetched?: boolean;
  /** One or two English replies; the first always equals `text`. Older servers omit it. */
  suggestions?: string[];
  /** The latest speech invites the listener to respond now (emphasis only). */
  respondNow?: boolean;
  /** `session.ready`: the server pushes a Korean meaning (`response.korean`) for every reply line. `session.updated`: the effective value. */
  replyKorean?: boolean;
  /** `session.ready`: this server accepts `session.update` (document search and Korean can change while running). */
  sessionUpdate?: boolean;
  /** `session.updated`: the effective document-search mode. */
  responseMode?: "balanced" | "grounded" | "conversation";
  /** `response.delta`: the primary line (first suggestion) is complete with this delta; sent at most once per response. */
  primaryDone?: boolean;
  /** `response.korean`: the reply line (0 = primary, 1 = alternative) and its Korean meaning, or `failed` when it has none. */
  index?: number; korean?: string; failed?: boolean;
}
export const startMessage = {
  type: "session.start", protocolVersion: 1,
  audio: { encoding: "pcm_s16le", sampleRate: 16000, channels: 1 },
} as const;

export function parseEvent(data: unknown): ServerEvent {
  if (typeof data !== "string" || new TextEncoder().encode(data).length > 65536)
    throw new Error("Server message must be text of at most 64 KiB.");
  const event: unknown = JSON.parse(data);
  if (!event || typeof event !== "object") throw new Error("Invalid server event.");
  const e = event as Record<string, unknown>;
  const text = (key: string): string => {
    const value = e[key];
    if (typeof value !== "string" || value.length > 32768) throw new Error(`Invalid ${key} in server event.`);
    return value;
  };
  const type = text("type");
  if (type === "session.ready") {
    if (e.replyKorean !== undefined && typeof e.replyKorean !== "boolean") throw new Error("Invalid replyKorean flag.");
    if (e.sessionUpdate !== undefined && typeof e.sessionUpdate !== "boolean") throw new Error("Invalid sessionUpdate flag.");
    return { type, ...(e.replyKorean === true ? { replyKorean: true } : {}), ...(e.sessionUpdate === true ? { sessionUpdate: true } : {}) };
  }
  if (type === "session.updated") {
    if (typeof e.replyKorean !== "boolean") throw new Error("Invalid replyKorean flag.");
    const responseMode = text("responseMode");
    if (responseMode !== "balanced" && responseMode !== "grounded" && responseMode !== "conversation")
      throw new Error("Invalid responseMode in server event.");
    return { type, responseMode, replyKorean: e.replyKorean };
  }
  if (type === "error") {
    if (typeof e.retryable !== "boolean") throw new Error("Invalid retryable error flag.");
    return { type, code: text("code"), message: text("message"), retryable: e.retryable };
  }
  const turnId = text("turnId");
  if (type === "transcript.partial" || type === "transcript.final") {
    if (!Number.isSafeInteger(e.revision) || (e.revision as number) < 0) throw new Error("Invalid transcript revision.");
    return { type, turnId, revision: e.revision as number, text: text("text") };
  }
  const responseId = text("responseId");
  if (type === "response.started" || type === "response.cancelled") return { type, turnId, responseId };
  if (type === "response.delta") {
    const delta = text("text");
    if (e.primaryDone !== undefined && typeof e.primaryDone !== "boolean") throw new Error("Invalid primaryDone flag.");
    // An empty delta exists only to say that the primary line is already complete.
    if (!delta && e.primaryDone !== true) throw new Error("A response delta must carry text unless it marks the primary line done.");
    return { type, turnId, responseId, text: delta, ...(e.primaryDone === true ? { primaryDone: true } : {}) };
  }
  if (type === "response.korean") {
    if (e.index !== 0 && e.index !== 1) throw new Error("Invalid reply line index.");
    if (e.failed !== undefined) {
      if (e.failed !== true || e.korean !== undefined) throw new Error("Invalid translation failure.");
      return { type, turnId, responseId, index: e.index, failed: true };
    }
    const korean = text("korean");
    if (!korean.trim() || korean.length > 400) throw new Error("Invalid korean in server event.");
    return { type, turnId, responseId, index: e.index, korean };
  }
  if (type === "response.completed") {
    if (!Array.isArray(e.sources) || e.sources.length > 20) throw new Error("Invalid response sources.");
    const sources: Source[] = e.sources.map((s: unknown) => {
      if (!s || typeof s !== "object") throw new Error("Invalid source.");
      const source = s as Record<string, unknown>;
      if (typeof source.title !== "string" || typeof source.url !== "string" ||
        (source.updatedAt != null && typeof source.updatedAt !== "string")) throw new Error("Invalid source fields.");
      return { title: source.title, url: source.url, updatedAt: source.updatedAt as string | null | undefined };
    });
    if (e.grounding !== undefined && !["disabled", "grounded", "unavailable", "no_matches"].includes(e.grounding as string))
      throw new Error("Invalid grounding status.");
    if (e.responseRoute !== undefined && !["transcript", "profile", "knowledge"].includes(e.responseRoute as string))
      throw new Error("Invalid response route.");
    if (e.respondNow !== undefined && typeof e.respondNow !== "boolean") throw new Error("Invalid respondNow flag.");
    if (e.retrievalPrefetched !== undefined && typeof e.retrievalPrefetched !== "boolean")
      throw new Error("Invalid retrieval prefetch flag.");
    let suggestions: string[] | undefined;
    if (e.suggestions !== undefined) {
      if (!Array.isArray(e.suggestions) || e.suggestions.length < 1 || e.suggestions.length > 2 ||
        e.suggestions.some(value => typeof value !== "string" || !value.trim() || value.length > 8000))
        throw new Error("Invalid response suggestions.");
      suggestions = e.suggestions as string[];
      if (suggestions[0] !== e.text) throw new Error("The primary suggestion must match response text.");
    }
    return { type, turnId, responseId, text: text("text"), sources, grounding: e.grounding as Grounding | undefined,
      responseRoute: e.responseRoute as ResponseRoute | undefined, retrievalPrefetched: e.retrievalPrefetched as boolean | undefined,
      ...(suggestions ? { suggestions } : {}), ...(e.respondNow === undefined ? {} : { respondNow: e.respondNow as boolean }) };
  }
  throw new Error(`Unsupported server event: ${type}`);
}
