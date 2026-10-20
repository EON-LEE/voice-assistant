import type { SessionOptions, SessionUpdate } from "./options.js";

export type ResponseMode = SessionOptions["responseMode"];
export type DocumentMode = Exclude<ResponseMode, "conversation">;

export interface LiveSettings {
  deviceId: string;
  responseMode: ResponseMode;
  /** The document-search mode that "document search: on" returns to after it was switched off. */
  documentMode: DocumentMode;
  topic: string;
  audioSource: "microphone" | "display";
  /** Show what the other person said (the question strip and the heard conversation). */
  showCaptions: boolean;
  /** Show Korean meanings. Off means no Korean is requested, pushed or translated at all. */
  showKorean: boolean;
}

export const settingsKey = "live-coach-settings";

/** Reads the stored settings. Anything missing or invalid falls back to its default, so older stored versions keep working. */
export function parseSettings(raw: string | null): LiveSettings {
  let value: Record<string, unknown> = {};
  try {
    const parsed: unknown = JSON.parse(raw ?? "{}");
    if (parsed && typeof parsed === "object" && !Array.isArray(parsed)) value = parsed as Record<string, unknown>;
  } catch { /* corrupt storage: use the defaults */ }
  const responseMode: ResponseMode = value.responseMode === "balanced" || value.responseMode === "conversation" ? value.responseMode : "grounded";
  return {
    deviceId: typeof value.deviceId === "string" ? value.deviceId : "",
    responseMode,
    documentMode: responseMode !== "conversation" ? responseMode : value.documentMode === "balanced" ? "balanced" : "grounded",
    topic: typeof value.topic === "string" ? value.topic.slice(0, 300) : "",
    audioSource: value.audioSource === "display" ? "display" : "microphone",
    showCaptions: value.showCaptions === true,
    showKorean: value.showKorean === true,
  };
}

export function documentSearchOn(settings: Pick<LiveSettings, "responseMode">): boolean {
  return settings.responseMode !== "conversation";
}

export function withResponseMode(settings: LiveSettings, mode: ResponseMode): LiveSettings {
  return { ...settings, responseMode: mode, documentMode: mode === "conversation" ? settings.documentMode : mode };
}

export function withDocumentSearch(settings: LiveSettings, on: boolean): LiveSettings {
  return withResponseMode(settings, on ? settings.documentMode : "conversation");
}

/** What a connected session still has to be told so that it matches the settings; `null` when it already does. */
export function pendingUpdate(applied: { responseMode: ResponseMode; replyKorean: boolean },
  settings: Pick<LiveSettings, "responseMode" | "showKorean">): SessionUpdate | null {
  const update: SessionUpdate = {
    ...(applied.responseMode !== settings.responseMode ? { responseMode: settings.responseMode } : {}),
    ...(applied.replyKorean !== settings.showKorean ? { replyKorean: settings.showKorean } : {}),
  };
  return Object.keys(update).length ? update : null;
}
