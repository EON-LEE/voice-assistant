import { describe, it, expect, vi } from "vitest";
import { CoachClient, CoachError, validateEnrichment, validateFeedback, validateSettings, validateSummary, validateTurn } from "../../../src/VoiceAssistant.Web/src/coach-client.js";
import { defaultOptions, createStartMessage } from "../../../src/VoiceAssistant.Web/src/options.js";
const signal = () => new AbortController().signal;
const settings = { scenario: { kind: "sales" as const, description: "", difficulty: 2 }, topic: "Original Lumen", useMaterials: true, maxTurns: 1 };
const feedback = { correctedEnglish: "We can check the plan.", easierEnglish: "We can check it.", feedbackKo: "잘 말했어요. 짧게 나누면 더 좋아요.",
  points: [{ tag: "clarity", ko: "핵심을 먼저 말해 보세요." }], clarity: 4 };
const summary = { headlineKo: "한 가지 질문을 연습했어요.", strengthsKo: ["명확하게 답했어요."], improveKo: ["짧게 말해 보세요."],
  phrases: [{ en: "Let me check the plan.", ko: "계획을 확인하겠습니다." }] };
const replyEnrichment = { korean: "계획을 확인하겠습니다.", pronunciation: [{ en: "Let me check", ko: "렛 미 체크" }, { en: "the plan.", ko: "더 플랜." }] };

describe("strict enrichment response", () => {
  it("validates exact ordered chunks, Hangul and nullable question pronunciation", () => {
    expect(validateEnrichment(replyEnrichment, "reply", "Let  me check the plan.")).toEqual(replyEnrichment);
    expect(validateEnrichment({ korean: "무슨 계획인가요?", pronunciation: null }, "question", "What plan?").pronunciation).toBeNull();
  });
  it.each([
    { ...replyEnrichment, extra: true },
    { ...replyEnrichment, korean: "English echo" },
    { ...replyEnrichment, pronunciation: [] },
    { ...replyEnrichment, pronunciation: [{ en: "the plan. Let me check", ko: "계획" }] },
    { ...replyEnrichment, pronunciation: [{ en: "Let me check the plan.", ko: "계획" }] },
    { ...replyEnrichment, pronunciation: [{ en: "Let me check", ko: "let me" }, { en: "the plan.", ko: "더 플랜" }] },
    { ...replyEnrichment, pronunciation: [{ en: "Let me check", ko: "123" }, { en: "the plan.", ko: "더 플랜" }] },
  ])("rejects malformed aligned reply %#", value => expect(() => validateEnrichment(value, "reply", "Let me check the plan.")).toThrow(CoachError));
  it("allows Fake marker only when explicitly local Fake is selected", () => {
    const value = { korean: "[fake-ko] What plan?", pronunciation: null };
    expect(() => validateEnrichment(value, "question", "What plan?")).toThrow();
    expect(validateEnrichment(value, "question", "What plan?", true).korean).toContain("[fake-ko]");
    expect(() => validateEnrichment({ korean: "계획", pronunciation: [] }, "question", "What plan?")).toThrow();
  });
});
describe("practice schema boundaries", () => {
  it("checks scenario custom text difficulty counts and booleans", () => {
    expect(validateSettings(settings)).toEqual(settings);
    expect(() => validateSettings({ ...settings, scenario: { ...settings.scenario, kind: "custom", description: " " } })).toThrow();
    for (const difficulty of [0, 4, 1.5]) expect(() => validateSettings({ ...settings, scenario: { ...settings.scenario, difficulty } })).toThrow();
    for (const maxTurns of [0, 13, 2.5]) expect(() => validateSettings({ ...settings, maxTurns })).toThrow();
  });
  it("validates closing/count agreement and strict titles/no private URL fields", () => {
    expect(validateTurn({ text: "What is the goal?", done: false, turn: 1, grounding: "disabled", sources: [] }, 0, 1).turn).toBe(1);
    expect(validateTurn({ text: "Thanks for practicing.", done: true, turn: 2, grounding: "disabled", sources: [] }, 1, 1).done).toBe(true);
    for (const extra of [{ done: true }, { turn: 2 }, { text: "No question." }, { sources: [{ title: "A", url: "private" }] },
      { sources: [{ title: "A" }, { title: "A" }] }]) expect(() => validateTurn({
        text: "What is the goal?", done: false, turn: 1, grounding: "disabled", sources: [], ...extra,
      }, 0, 1)).toThrow();
  });
  it("enforces feedback language clarity tags and English length", () => {
    expect(validateFeedback(feedback)).toEqual(feedback);
    expect(() => validateFeedback({ ...feedback, clarity: 6 })).toThrow();
    expect(() => validateFeedback({ ...feedback, points: [{ tag: "accent", ko: "안녕" }] })).toThrow();
    expect(() => validateFeedback({ ...feedback, correctedEnglish: "word ".repeat(41) })).toThrow();
    expect(() => validateFeedback({ ...feedback, easierEnglish: "<script>bad</script>" })).toThrow();
    expect(() => validateFeedback({ ...feedback, feedbackKo: "Great job!" })).toThrow();
    expect(() => validateFeedback({ ...feedback, extra: "no" })).toThrow();
  });
  it("validates summary item limits and Korean/English pairs", () => {
    expect(validateSummary(summary)).toEqual(summary);
    expect(() => validateSummary({ ...summary, strengthsKo: Array(4).fill("잘했어요") })).toThrow();
    expect(() => validateSummary({ ...summary, phrases: Array(9).fill(summary.phrases[0]) })).toThrow();
    expect(() => validateSummary({ ...summary, phrases: [{ en: "Hello.", ko: "English only" }] })).toThrow();
  });
});
describe("bounded API clients", () => {
  it("sends exact additive bodies and never sends unknown owner fields", async () => {
    const request = vi.fn(async (path: string) => Response.json(path.endsWith("enrich") ? replyEnrichment : feedback));
    const client = new CoachClient(request);
    await client.enrich("reply", "Let me check the plan.", signal());
    const body = JSON.parse(request.mock.calls[0]![1]!.body as string);
    expect(body).toEqual({ kind: "reply", text: "Let me check the plan." });
    await client.feedback(settings.scenario, "What is the goal?", "", signal());
    expect(JSON.parse(request.mock.calls[1]![1]!.body as string).answer).toBe("");
  });
  it("rejects invalid history and oversized16KiB before making a request", async () => {
    const request = vi.fn(); const client = new CoachClient(request);
    await expect(client.turn(settings, [{ role: "user", text: "wrong" }, { role: "partner", text: "wrong" }], signal())).rejects.toThrow();
    await expect(client.turn(settings, Array.from({ length: 24 }, (_, i) => ({ role: i % 2 ? "user" as const : "partner" as const, text: "가".repeat(800) })), signal())).rejects.toMatchObject({ code: "too_large" });
    expect(request).not.toHaveBeenCalled();
  });
  it("maps429 Retry-After and avoids private provider error text", async () => {
    const client = new CoachClient(async () => Response.json({ error: "busy", message: "secret provider internals" }, { status: 429, headers: { "Retry-After": "7" } }));
    await expect(client.enrich("question", "What?", signal())).rejects.toMatchObject({ code: "busy", retryAfter: 7 });
    try { await client.enrich("question", "What?", signal()); } catch (error) { expect((error as Error).message).not.toContain("secret"); }
  });
  it("does not treat failed/malformed JSON responses as fake success", async () => {
    await expect(new CoachClient(async () => new Response("<bad>", { status: 502 })).enrich("question", "What?", signal())).rejects.toThrow();
    await expect(new CoachClient(async () => Response.json({ korean: "안녕" })).enrich("question", "What?", signal())).rejects.toThrow();
  });
  it("accepts MIME-consistent MP3 and explicitFake WAV, rejects unbounded/wrong audio", async () => {
    const audio = (type: string) => async () => new Response(new Uint8Array([1, 2, 3]), { headers: { "Content-Type": type } });
    expect((await new CoachClient(audio("audio/mpeg")).speak("Hello.", "coach", "slow", signal())).type).toBe("audio/mpeg");
    expect((await new CoachClient(audio("audio/wav"), () => true).speak("Hello.", "partner", "normal", signal())).type).toBe("audio/wav");
    await expect(new CoachClient(audio("audio/wav")).speak("Hello.", "coach", "slow", signal())).rejects.toThrow();
    await expect(new CoachClient(audio("text/html")).speak("Hello.", "coach", "slow", signal())).rejects.toThrow();
    await expect(new CoachClient(async () => new Response(new Uint8Array(4 * 1024 * 1024 + 1), { headers: { "Content-Type": "audio/mpeg" } })).speak("Hello.", "coach", "slow", signal())).rejects.toThrow();
  });
  it("includes transcribeOnly only when explicitly set and validates its type", () => {
    expect(createStartMessage(defaultOptions())).not.toHaveProperty("options.transcribeOnly");
    expect(createStartMessage({ ...defaultOptions(), transcribeOnly: true })).toHaveProperty("options.transcribeOnly", true);
    expect(() => createStartMessage({ ...defaultOptions(), transcribeOnly: null as unknown as boolean })).toThrow();
  });
});
