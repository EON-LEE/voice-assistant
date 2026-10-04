import { afterEach, describe, it, expect, vi } from "vitest";
import { CoachAudio, type AudioClip } from "../../../src/VoiceAssistant.Web/src/coach-audio.js";
import { EnrichmentRequests } from "../../../src/VoiceAssistant.Web/src/enrichment.js";
import { PracticeRound } from "../../../src/VoiceAssistant.Web/src/practice.js";
import { CoachError } from "../../../src/VoiceAssistant.Web/src/coach-client.js";
import { MeetingSession } from "../../../src/VoiceAssistant.Web/src/session.js";
import { defaultOptions } from "../../../src/VoiceAssistant.Web/src/options.js";
afterEach(() => vi.useRealTimers());
const deferred = <T>() => { let resolve!: (value: T) => void; const promise = new Promise<T>(r => { resolve = r; }); return { promise, resolve }; };
describe("optional enrichment never delays English and ignores obsolete results", () => {
  it("aborts stale question/reply and guards when fetch ignores cancellation", async () => {
    const first = deferred<any>(), second = deferred<any>(); const changed = vi.fn();
    const enrich = vi.fn().mockReturnValueOnce(first.promise).mockReturnValueOnce(second.promise);
    const requests = new EnrichmentRequests({ enrich }, changed);
    const a = requests.load("question", "Old?"); requests.reset();
    const b = requests.load("question", "New?");
    first.resolve({ korean: "이전", pronunciation: null }); await a;
    expect(changed).not.toHaveBeenCalledWith("question", { korean: "이전", pronunciation: null });
    second.resolve({ korean: "새 질문", pronunciation: null }); await b;
    expect(changed).toHaveBeenCalledWith("question", { korean: "새 질문", pronunciation: null });
    expect(enrich.mock.calls[0]![2].aborted).toBe(true);
  });
  it("silently omits failure and oversized assistance instead of English success fallback", async () => {
    const changed = vi.fn(), enrich = vi.fn().mockRejectedValue(new Error("offline"));
    const requests = new EnrichmentRequests({ enrich }, changed);
    await requests.load("reply", "Hello."); await requests.load("reply", "x".repeat(601));
    expect(enrich).toHaveBeenCalledTimes(1);
    expect(changed.mock.calls.every(call => call[1] === null)).toBe(true);
  });
});
describe("one audio clip with deterministic Blob disposal", () => {
  function fixture(speak = vi.fn(async () => new Blob(["audio"]))) {
    const player: AudioClip = { src: "", currentTime: 0, onended: null, onerror: null,
      play: vi.fn(async () => {}), pause: vi.fn(), removeAttribute: vi.fn(), load: vi.fn() };
    const create = vi.fn().mockReturnValueOnce("blob:1").mockReturnValue("blob:2"), revoke = vi.fn(), changed = vi.fn();
    const audio = new CoachAudio({ speak }, player, changed, { create, revoke });
    return { player, audio, create, revoke, changed, speak };
  }
  it("plays only on request, revokes previous URL, keeps one clip and cleans on ended/stop", async () => {
    const f = fixture(); expect(f.player.play).not.toHaveBeenCalled();
    const ended = vi.fn(); await f.audio.play("One.", "coach", "normal", "one", ended);
    await f.audio.play("Two.", "partner", "slow", "two", ended);
    expect(f.revoke).toHaveBeenCalledWith("blob:1"); expect(f.player.src).toBe("blob:2");
    f.player.onended?.call({} as GlobalEventHandlers, new Event("ended"));
    expect(f.revoke).toHaveBeenCalledWith("blob:2"); expect(ended).toHaveBeenCalledTimes(1); expect(f.audio.active).toBe(false);
  });
  it("stop before delayed fetch prevents auto-play and URL creation", async () => {
    const result = deferred<Blob>(), f = fixture(vi.fn(() => result.promise));
    const task = f.audio.play("One.", "coach", "normal", "one"); f.audio.stop();
    result.resolve(new Blob(["late"])); await task;
    expect(f.player.play).not.toHaveBeenCalled(); expect(f.create).not.toHaveBeenCalled();
  });
  it("blocked autoplay presents explicit click fallback and releases URL", async () => {
    const f = fixture(); vi.mocked(f.player.play).mockRejectedValue(new DOMException("blocked", "NotAllowedError"));
    expect(await f.audio.play("One.", "partner", "normal", "one")).toBe(false);
    expect(f.changed).toHaveBeenLastCalledWith(expect.objectContaining({ state: "error", message: expect.stringContaining("click") }));
    expect(f.revoke).toHaveBeenCalledWith("blob:1");
  });
});
describe("stateless practice rounds", () => {
  const settings = { scenario: { kind: "sales" as const, description: "", difficulty: 2 }, topic: "", useMaterials: true, maxTurns: 1 };
  const question = { text: "What is the goal?", turn: 1, done: false, grounding: "disabled" as const, sources: [] };
  const feedback = { correctedEnglish: "We will check.", easierEnglish: "We can check.", feedbackKo: "잘했어요.", points: [], clarity: 4 };
  const summary = { headlineKo: "잘했어요.", strengthsKo: [], improveKo: [], phrases: [] };
  function fixture() {
    const client = { turn: vi.fn().mockResolvedValueOnce(question).mockResolvedValue({ ...question, text: "Thanks.", turn: 2, done: true }),
      suggest: vi.fn().mockResolvedValue({ text: "Let me check.", grounding: "disabled", sources: [] }),
      feedback: vi.fn().mockResolvedValue(feedback), summary: vi.fn().mockResolvedValue(summary) };
    const ready = vi.fn(), changed = vi.fn(); return { client, ready, changed, round: new PracticeRound(client, changed, ready) };
  }
  it("joins only unique final answers, pauses phases, sends bounded history and summarizes", async () => {
    const f = fixture(); f.round.begin(settings); await f.round.next();
    expect(f.round.view.phase).toBe("question"); f.round.transcript("ignored", "Partner playback", true);
    expect(f.round.view.answer).toBe("");
    f.round.answer(); f.round.transcript("a", "We", false); expect(f.round.view.answer).toBe("");
    f.round.transcript("a", "We will", true); f.round.transcript("a", "duplicate", true); f.round.transcript("b", "check.", true);
    expect(f.round.view.answer).toBe("We will check."); await f.round.done();
    expect(f.round.view.phase).toBe("feedback"); await f.round.next();
    expect(f.client.turn.mock.calls[1]![1]).toEqual([{ role: "partner", text: question.text }, { role: "user", text: "We will check." }]);
    expect(f.client.summary).toHaveBeenCalledWith(settings, [{ question: question.text, answer: "We will check.", correctedEnglish: feedback.correctedEnglish }], expect.any(AbortSignal));
    expect(f.round.view.summary).toEqual(summary); f.round.stop(); expect(f.round.view.answer).toBe(""); expect(f.round.view.summary).toBeNull();
  });
  it("skip sends empty answer and retry429 preserves history without duplicate question", async () => {
    vi.useFakeTimers(); const f = fixture(); f.client.feedback.mockRejectedValueOnce(new CoachError("busy", 5));
    f.round.begin(settings); await f.round.next(); await f.round.done(true);
    expect(f.round.view.phase).toBe("error"); await f.round.retryLast(); expect(f.client.feedback).toHaveBeenCalledTimes(1);
    vi.advanceTimersByTime(5000); await f.round.retryLast();
    expect(f.client.feedback).toHaveBeenCalledTimes(2); expect(f.client.feedback.mock.calls[1]![2]).toBe("");
    await f.round.next(); expect(f.client.turn.mock.calls[1]![1]).toHaveLength(2);
  });
  it("stop ignores delayed server question and no stale autoplay callback", async () => {
    const f = fixture(), pending = deferred<any>(); f.client.turn.mockReset().mockReturnValue(pending.promise);
    f.round.begin(settings); const task = f.round.next(); f.round.stop(); pending.resolve(question); await task;
    expect(f.round.view.phase).toBe("idle"); expect(f.ready).not.toHaveBeenCalled();
  });
  it("failed optional hint does not block submitting a valid final spoken answer", async () => {
    const f = fixture(); f.client.suggest.mockRejectedValue(new CoachError("busy", 2));
    f.round.begin(settings); await f.round.next(); f.round.answer(); f.round.transcript("a", "We can check.", true);
    await f.round.hint(); expect(f.round.view.error).toContain("busy"); expect(f.round.canFinish).toBe(true);
    await f.round.done(); expect(f.round.view.phase).toBe("feedback");
  });
  it("overflow answer refuses submission instead of silently clipping speech", async () => {
    const f = fixture(); f.round.begin(settings); await f.round.next(); f.round.answer();
    f.round.transcript("a", "x".repeat(801), true);
    expect(f.round.canFinish).toBe(false); await f.round.done(); expect(f.client.feedback).not.toHaveBeenCalled();
    await f.round.done(true); expect(f.client.feedback.mock.calls[0]![2]).toBe("");
  });
  it("summary retry does not ask or append another partner question", async () => {
    const f = fixture(); f.client.summary.mockRejectedValueOnce(new CoachError("provider_timeout"));
    f.round.begin(settings); await f.round.next(); await f.round.done(true); await f.round.next();
    expect(f.round.view.phase).toBe("error"); await f.round.retryLast();
    expect(f.round.view.phase).toBe("summary"); expect(f.client.turn).toHaveBeenCalledTimes(2);
    expect(f.client.summary).toHaveBeenCalledTimes(2);
  });
  it("keeps transcribe_only error nonfatal on recognition-only sockets", async () => {
    let receive: (event: any) => void = () => {}; const stop = vi.fn(async () => {}), error = vi.fn(), audio = vi.fn();
    const session = new MeetingSession({ prepare: async () => {}, start(emit) { emit(new ArrayBuffer(640)); }, pause() {}, stop },
      { connect: async fn => { receive = fn; }, audio, send() {}, close() {} }, () => {}, () => {}, error,
      { ...defaultOptions(), transcribeOnly: true });
    await session.start(); receive({ type: "session.ready" }); receive({ type: "error", code: "transcribe_only", retryable: false, message: "Recognition only" });
    expect(audio).not.toHaveBeenCalled(); expect(session.isReady).toBe(true); expect(stop).not.toHaveBeenCalled(); expect(error).not.toHaveBeenCalled(); await session.stop();
  });
});
