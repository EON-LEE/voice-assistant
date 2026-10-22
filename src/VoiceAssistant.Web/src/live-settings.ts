import type { CaptureSourceKind } from "./capture.js";
import { defaultOptions, isReplyOn, type ReplyOn, type SessionOptions, type SessionUpdate } from "./options.js";
import { sanitizeNames } from "./address.js";

export type ResponseMode = SessionOptions["responseMode"];
export type DocumentMode = Exclude<ResponseMode, "conversation">;

export interface LiveSettings {
  deviceId: string;
  responseMode: ResponseMode;
  /** The document-search mode that "document search: on" returns to after it was switched off. */
  documentMode: DocumentMode;
  topic: string;
  audioSource: CaptureSourceKind;
  /** Show what the other person said (the question strip and the heard conversation). */
  showCaptions: boolean;
  /** Show Korean meanings. Off means no Korean is requested, pushed or translated at all. */
  showKorean: boolean;
  /** The English answer's text size, in percent of the default size. */
  answerSize: AnswerSize;
  /** Which speech gets an automatic reply ("추천"): everything worth a reply (`all`) or only questions and requests. */
  replyOn: ReplyOn;
  /**
   * "내 이름": the user's own names, sanitized (see `sanitizeNames`). An answer to speech that says one is emphasized.
   * Stored only in this browser: never sent to the server, logged, or part of the session options.
   */
  myName: string;
}

/** The English answer text sizes A− and A+ step through, in percent of the default size (100). */
export const answerSizes = [85, 100, 115, 130, 150] as const;
export type AnswerSize = typeof answerSizes[number];

function isAnswerSize(value: unknown): value is AnswerSize {
  return (answerSizes as readonly unknown[]).includes(value);
}

/** The next smaller (`-1`) or larger (`1`) answer size; the smallest and the largest size stay as they are. */
export function stepAnswerSize(size: AnswerSize, step: -1 | 1): AnswerSize {
  const index = Math.min(answerSizes.length - 1, Math.max(0, answerSizes.indexOf(size) + step));
  return answerSizes[index] ?? 100;
}

/** The audio LiveCoach.exe offers to capture natively for this page (none in a browser). */
export interface HostSources { teams: boolean; system: boolean }
export const noHostSources: HostSources = { teams: false, system: false };

export const settingsKey = "live-coach-settings";

/**
 * The audio source to use here. LiveCoach sources exist only inside LiveCoach.exe (elsewhere a share takes their place);
 * inside it, Teams app audio replaces a stored share and is the default, because a share there never offers the Teams
 * app's own audio. "PC 전체 소리" is used only when it was chosen.
 */
export function resolveAudioSource(stored: unknown, offered: HostSources): CaptureSourceKind {
  switch (stored) {
    case "microphone": return "microphone";
    case "teams": return offered.teams ? "teams" : "display";
    case "host-system": return offered.system ? "host-system" : "display";
    case "display": return offered.teams ? "teams" : "display";
    default: return offered.teams ? "teams" : "microphone";
  }
}

/**
 * Reads the stored settings. Anything missing or invalid falls back to its default, so older stored versions keep working.
 * `offered` is what LiveCoach.exe can capture natively here (see {@link resolveAudioSource}).
 */
export function parseSettings(raw: string | null, offered: HostSources = noHostSources): LiveSettings {
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
    audioSource: resolveAudioSource(value.audioSource, offered),
    showCaptions: value.showCaptions === true,
    showKorean: value.showKorean === true,
    answerSize: isAnswerSize(value.answerSize) ? value.answerSize : 100,
    replyOn: isReplyOn(value.replyOn) ? value.replyOn : "all",
    myName: sanitizeNames(value.myName),
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

/** The settings a connected session runs with, as far as `session.update` can change them. */
export interface AppliedSettings { responseMode: ResponseMode; replyKorean: boolean; replyOn: ReplyOn }

/** What a connected session still has to be told so that it matches the settings; `null` when it already does. */
export function pendingUpdate(applied: AppliedSettings,
  settings: Pick<LiveSettings, "responseMode" | "showKorean" | "replyOn">): SessionUpdate | null {
  const update: SessionUpdate = {
    ...(applied.responseMode !== settings.responseMode ? { responseMode: settings.responseMode } : {}),
    ...(applied.replyKorean !== settings.showKorean ? { replyKorean: settings.showKorean } : {}),
    ...(applied.replyOn !== settings.replyOn ? { replyOn: settings.replyOn } : {}),
  };
  return Object.keys(update).length ? update : null;
}

/**
 * What the quick row says when the server cannot change settings while connected: document search and `replyOn` then wait
 * for the next connection. Korean needs no note, because the page translates by itself.
 */
export function deferredUpdateNote(update: SessionUpdate): string {
  const names = [update.responseMode ? "문서검색" : "", update.replyOn ? "추천" : ""].filter(Boolean);
  return names.length ? `이 서버는 연결 중 ${names.join("·")} 변경을 지원하지 않아요 · 다음 연결부터 적용돼요` : "";
}

/**
 * The live page's `session.start` options: its fixed meeting settings plus the chosen document search, topic, Korean and
 * `replyOn`. The names of "내 이름" are never part of them.
 */
export function liveStartOptions(settings: Pick<LiveSettings, "responseMode" | "topic" | "showKorean" | "replyOn">): SessionOptions {
  // endSilenceMs is required by the options validator but has no effect under semantic segmentation.
  return { ...defaultOptions(), responseMode: settings.responseMode, topic: settings.topic, endSilenceMs: 1100,
    semanticSegmentation: true, replySettleMs: 1000, replyKorean: settings.showKorean, replyOn: settings.replyOn };
}
