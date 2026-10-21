export interface SessionOptions {
  responseMode: "balanced" | "grounded" | "conversation";
  profile: { name: string; role: string; project: string };
  profileConfirmed: boolean;
  topic: string;
  phrases: string[];
  endSilenceMs: number;
  transcribeOnly?: boolean;
  /** Azure sentence-oriented caption segmentation for continuous meeting speech. */
  semanticSegmentation?: boolean;
  /** Gather speech until this pause (ms) and suggest once per meaningful unit; 0 = every final utterance. */
  replySettleMs?: number;
  /** Ask the server to push a Korean meaning for each reply line (`response.korean`) instead of a separate request per line. */
  replyKorean?: boolean;
  /**
   * Continue the conversation of this user's previous server session. The live page sets it only when it reconnects or rolls
   * over a live session whose earlier connection was ready, never on a Start; the server then answers with `resumedTurns`.
   */
  resumeConversation?: boolean;
}

/** What `session.update` can change while a session runs; replies that start later use it. */
export interface SessionUpdate {
  responseMode?: SessionOptions["responseMode"];
  replyKorean?: boolean;
}

export function defaultOptions(): SessionOptions {
  return { responseMode: "balanced", profile: { name: "", role: "", project: "" },
    profileConfirmed: false, topic: "", phrases: [], endSilenceMs: 500 };
}

/** Validate and copy at Start so later form edits cannot mutate the active session. */
export function validateOptions(input: SessionOptions): SessionOptions {
  if (!["balanced", "grounded", "conversation"].includes(input.responseMode))
    throw new Error("Choose Document search: On when needed, On every reply, or Off.");
  const field = (value: string, limit: number, name: string): string => {
    if (typeof value !== "string") throw new Error(`${name} must be text.`);
    const trimmed = value.trim();
    if (trimmed.length > limit) throw new Error(`${name} must be at most ${limit} characters.`);
    if (/[\u0000-\u001f\u007f-\u009f]/u.test(trimmed))
      throw new Error(`${name} must not contain control characters or line breaks.`);
    return trimmed;
  };
  if (!input.profile || typeof input.profileConfirmed !== "boolean") throw new Error("Invalid profile confirmation.");
  const profile = {
    name: field(input.profile.name, 100, "Name"), role: field(input.profile.role, 160, "Role"),
    project: field(input.profile.project, 300, "Project"),
  };
  if (Object.values(profile).some(Boolean) && !input.profileConfirmed)
    throw new Error("Confirm that the profile details are accurate, or clear them before starting.");
  if (!Array.isArray(input.phrases) || input.phrases.length > 40) throw new Error("Use at most 40 terms, one per line.");
  const phrases = input.phrases.map(value => field(value, 64, "Each term"));
  if (phrases.some(value => !value)) throw new Error("Terms must not be empty.");
  if (phrases.reduce((sum, value) => sum + value.length, 0) > 2048) throw new Error("Terms must total at most 2048 characters.");
  if (!Number.isInteger(input.endSilenceMs) || input.endSilenceMs < 350 || input.endSilenceMs > 1500)
    throw new Error("End-of-turn silence must be an integer between 350 and 1500 ms.");
  if (input.transcribeOnly !== undefined && typeof input.transcribeOnly !== "boolean")
    throw new Error("transcribeOnly must be a boolean.");
  if (input.semanticSegmentation !== undefined && typeof input.semanticSegmentation !== "boolean")
    throw new Error("semanticSegmentation must be a boolean.");
  if (input.replySettleMs !== undefined && (!Number.isInteger(input.replySettleMs) ||
      input.replySettleMs !== 0 && (input.replySettleMs < 300 || input.replySettleMs > 5000)))
    throw new Error("replySettleMs must be 0 or an integer between 300 and 5000.");
  if (input.replyKorean !== undefined && typeof input.replyKorean !== "boolean")
    throw new Error("replyKorean must be a boolean.");
  if (input.resumeConversation !== undefined && typeof input.resumeConversation !== "boolean")
    throw new Error("resumeConversation must be a boolean.");
  return { responseMode: input.responseMode, profile, profileConfirmed: input.profileConfirmed,
    topic: field(input.topic, 300, "Meeting topic"), phrases, endSilenceMs: input.endSilenceMs,
    ...(input.transcribeOnly === undefined ? {} : { transcribeOnly: input.transcribeOnly }),
    ...(input.semanticSegmentation === undefined ? {} : { semanticSegmentation: input.semanticSegmentation }),
    ...(input.replySettleMs === undefined ? {} : { replySettleMs: input.replySettleMs }),
    ...(input.replyKorean === undefined ? {} : { replyKorean: input.replyKorean }),
    ...(input.resumeConversation === undefined ? {} : { resumeConversation: input.resumeConversation }) };
}

/** Validate and copy a `session.update` payload: only the given, known settings, at least one. */
export function validateUpdate(input: SessionUpdate): SessionUpdate {
  if (!input || typeof input !== "object" || Array.isArray(input)) throw new Error("A settings update must be an object.");
  if (Object.keys(input).some(key => key !== "responseMode" && key !== "replyKorean"))
    throw new Error("A settings update changes responseMode or replyKorean only.");
  if (input.responseMode !== undefined && !["balanced", "grounded", "conversation"].includes(input.responseMode))
    throw new Error("Choose Document search: On when needed, On every reply, or Off.");
  if (input.replyKorean !== undefined && typeof input.replyKorean !== "boolean")
    throw new Error("replyKorean must be a boolean.");
  const update: SessionUpdate = { ...(input.responseMode === undefined ? {} : { responseMode: input.responseMode }),
    ...(input.replyKorean === undefined ? {} : { replyKorean: input.replyKorean }) };
  if (!Object.keys(update).length) throw new Error("A settings update must change at least one setting.");
  return update;
}

export function parsePhrases(text: string): string[] {
  return text.split(/\r?\n/).map(value => value.trim()).filter(Boolean);
}

export function createStartMessage(options?: SessionOptions): typeof startMessage | (typeof startMessage & { options: SessionOptions }) {
  const message = options === undefined ? startMessage : { ...startMessage, options: validateOptions(options) };
  if (new TextEncoder().encode(JSON.stringify(message)).byteLength > 32768)
    throw new Error("Meeting start settings exceed the 32 KiB UTF-8 limit. Shorten the context before starting.");
  return message;
}
import { startMessage } from "./protocol.js";
