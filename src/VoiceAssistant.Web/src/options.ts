export interface SessionOptions {
  responseMode: "balanced" | "grounded" | "conversation";
  profile: { name: string; role: string; project: string };
  profileConfirmed: boolean;
  topic: string;
  phrases: string[];
  endSilenceMs: number;
}

export function defaultOptions(): SessionOptions {
  return { responseMode: "balanced", profile: { name: "", role: "", project: "" },
    profileConfirmed: false, topic: "", phrases: [], endSilenceMs: 500 };
}

/** Validate and copy at Start so later form edits cannot mutate the active session. */
export function validateOptions(input: SessionOptions): SessionOptions {
  if (!["balanced", "grounded", "conversation"].includes(input.responseMode))
    throw new Error("Choose Balanced, Always knowledge, or Conversation only.");
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
  return { responseMode: input.responseMode, profile, profileConfirmed: input.profileConfirmed,
    topic: field(input.topic, 300, "Meeting topic"), phrases, endSilenceMs: input.endSilenceMs };
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
