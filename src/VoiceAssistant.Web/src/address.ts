/**
 * The name emphasis of the live page. The "내 이름" setting holds up to three of the user's own names, and a reply to speech
 * that says one of them is labelled as the user's turn. The names stay in this browser: they are never sent to the server,
 * logged, or part of session options or telemetry.
 */

/** How "내 이름" is bounded: the whole stored value, the number of names and the length of each name. */
export const nameLimits = { length: 80, names: 3, min: 2, max: 40 } as const;

/**
 * The names in a typed or stored "내 이름" value, in order. They are comma-separated, with whitespace collapsed; each has
 * 2–40 characters and at least one letter. Repeats are dropped, and at most three names fit in 80 characters together.
 * Anything else is left out.
 */
export function parseNames(value: unknown): string[] {
  if (typeof value !== "string") return [];
  const names: string[] = [];
  for (const part of value.split(",")) {
    const name = part.replace(/[\s\p{Cc}]+/gu, " ").trim();
    if (name.length < nameLimits.min || name.length > nameLimits.max || !/\p{L}/u.test(name)) continue;
    if (names.some(kept => kept.toLowerCase() === name.toLowerCase())) continue;
    if ([...names, name].join(", ").length > nameLimits.length) break;
    names.push(name);
    if (names.length === nameLimits.names) break;
  }
  return names;
}

/** The stored form of "내 이름": its names joined with ", ", or "" when there are none. */
export function sanitizeNames(value: unknown): string {
  return parseNames(value).join(", ");
}

/** A word of a name as a literal pattern; a straight or a curly apostrophe matches either. */
function literal(word: string): string {
  return word.replace(/[\\^$.*+?()[\]{}|]/g, "\\$&").replace(/['\u2019]/g, "['\u2019]");
}

let compiled: { key: string; pattern: RegExp | null } | undefined;
/** One pattern for all the names, compiled again only when they change. */
function namePattern(names: readonly string[]): RegExp | null {
  const key = names.join("\n");
  if (compiled?.key === key) return compiled.pattern;
  const phrases = names.map(name => name.normalize("NFC").split(/\s+/u).filter(Boolean).map(literal).join("\\s+")).filter(Boolean);
  // A whole word: no letter, digit or combining mark right before or after it, so "Eon" is not found in "Leon" or "Eonix".
  const pattern = phrases.length
    ? new RegExp(`(?<![\\p{L}\\p{N}\\p{M}])(?:${phrases.join("|")})(?![\\p{L}\\p{N}\\p{M}])`, "iu") : null;
  compiled = { key, pattern };
  return pattern;
}

/**
 * Whether the text says one of the names as a whole word, case-insensitive and Unicode-aware: "Eon" is found in
 * "Eon, what do you think?", "Thanks Eon." and "Eon's team", but not in "Leon", "neon" or "Eonix". A name of several words
 * matches as a phrase with any whitespace between its words.
 */
export function mentionsName(text: string | undefined, names: readonly string[]): boolean {
  if (!text || !names.length) return false;
  return namePattern(names)?.test(text.normalize("NFC")) ?? false;
}

export const primaryLabels = {
  previous: "이전 질문용 답변",
  addressed: "이름이 불렸어요 · 지금 답할 차례",
  suggested: "추천 영어 답변",
} as const;

/** The label above the primary answer, and whether its card gets the `my-turn` emphasis. */
export interface PrimaryLabel { text: string; myTurn: boolean }

/**
 * The primary answer's label. An answer to an earlier question keeps its "previous" label. Otherwise only an answer to speech
 * that says one of the user's names is emphasized; the server's `respondNow` guess decides nothing here.
 */
export function primaryLabel(previous: boolean, answered: string | undefined, names: readonly string[]): PrimaryLabel {
  if (previous) return { text: primaryLabels.previous, myTurn: false };
  return mentionsName(answered, names)
    ? { text: primaryLabels.addressed, myTurn: true }
    : { text: primaryLabels.suggested, myTurn: false };
}
