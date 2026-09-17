export interface Source { title: string; url: string; updatedAt?: string | null }
export type Grounding = "disabled" | "grounded" | "unavailable" | "no_matches";
export interface ServerEvent {
  type: string; turnId?: string; revision?: number; text?: string; responseId?: string;
  sources?: Source[]; code?: string; message?: string; retryable?: boolean;
  grounding?: Grounding;
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
  if (type === "session.ready") return { type };
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
  if (type === "response.delta") return { type, turnId, responseId, text: text("text") };
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
    return { type, turnId, responseId, text: text("text"), sources, grounding: e.grounding as Grounding | undefined };
  }
  throw new Error(`Unsupported server event: ${type}`);
}
