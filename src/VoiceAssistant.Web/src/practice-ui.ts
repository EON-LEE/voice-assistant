import type { BrowserAuth } from "./auth.js";
import { CoachClient, type Rate, type PartnerTurn, type PracticeSettings } from "./coach-client.js";
import { CoachAudio } from "./coach-audio.js";
import { EnrichmentRequests, renderEnrichment } from "./enrichment.js";
import { MicrophoneAudioSource } from "./capture.js";
import { MeetingSession } from "./session.js";
import { defaultOptions } from "./options.js";
import { SocketTransport } from "./transport.js";
import { PracticeRound, type PracticeView } from "./practice.js";

export class PracticeUI {
  private session: MeetingSession | null = null;
  private generation = 0;
  private configured = false;
  private fake = false;
  private starting = false;
  private scenario: PracticeSettings["scenario"]["kind"] = "sales";
  private hintText = "";
  private feedbackRendered: unknown;
  private summaryRendered: unknown;
  private questionText = "";
  private readonly round: PracticeRound;
  private readonly enrich: EnrichmentRequests;
  private readonly client: CoachClient;
  constructor(private readonly auth: BrowserAuth, private readonly audio: CoachAudio, private readonly workletUrl: string,
    private readonly activity: (active: boolean, status: string) => void) {
    this.client = new CoachClient((path, init) => auth.coachRequest(path, init), () => this.fake);
    this.enrich = new EnrichmentRequests(this.client, (kind, value) => {
      if (kind === "question") renderEnrichment(this.el("practice-question-ko"), null, value);
      else renderEnrichment(this.el("practice-hint-ko"), this.el("practice-pronunciation"), value);
    });
    this.round = new PracticeRound(this.client, view => this.render(view), question => this.partner(question));
    for (const button of document.querySelectorAll<HTMLButtonElement>("[data-scenario]")) {
      button.addEventListener("click", () => {
        this.scenario = button.dataset.scenario as PracticeSettings["scenario"]["kind"];
        for (const item of document.querySelectorAll("[data-scenario]")) item.setAttribute("aria-pressed", String(item === button));
      });
    }
    this.el("practice-consent").addEventListener("change", () => this.render(this.round.view));
    this.el("practice-start").addEventListener("click", () => this.start());
    this.el("practice-stop").addEventListener("click", () => { void this.stop(); });
    this.el("practice-signin").addEventListener("click", () => {
      this.button("practice-signin").disabled = true;
      void auth.signIn().then(() => { this.configureStatus(); }).catch(() => this.showError("Sign-in did not finish. Click Sign in to retry."))
        .finally(() => this.render(this.round.view));
    });
    this.el("practice-answer").addEventListener("click", () => {
      if (!this.session?.isReady) return;
      this.audio.stop(); this.round.answer(); this.session.pause(false);
    });
    this.el("practice-done").addEventListener("click", () => {
      if (!this.round.canFinish) return;
      this.audio.stop(); this.session?.pause(true); void this.round.done();
    });
    this.el("practice-skip").addEventListener("click", () => {
      this.audio.stop(); this.session?.pause(true); void this.round.done(true);
    });
    this.el("practice-next").addEventListener("click", () => { this.audio.stop(); this.session?.pause(true); this.enrich.reset(); void this.round.next(); });
    this.el("practice-retry").addEventListener("click", () => {
      if (!this.session?.isReady) { this.showError("The microphone session ended. Stop and start a new round."); return; }
      void this.round.retryLast();
    });
    this.el("practice-hint").addEventListener("click", () => { if (this.check("practice-smart").checked) void this.round.hint(); });
    this.el("partner-listen").addEventListener("click", () => {
      if (this.round.view.question) void this.listen(this.round.view.question.text, "partner", "partner");
    });
    this.el("hint-listen").addEventListener("click", () => { if (this.hintText) void this.listen(this.hintText, "coach", "hint"); });
  }
  get active(): boolean { return this.starting || this.round.active; }
  async configure(): Promise<void> {
    const generation = ++this.generation;
    try {
      const config = await this.auth.initialize();
      if (generation !== this.generation) return;
      this.configured = true; this.fake = config.mode === "Fake"; this.configureStatus();
    } catch { this.configured = false; this.showError("Practice service is unavailable. Return to Meeting, then select Practice to retry."); }
    this.render(this.round.view);
  }
  private configureStatus(): void {
    this.el("practice-auth-status").textContent = this.fake
      ? "LOCAL FAKE practice — deterministic test partner, not Azure AI."
      : this.auth.signedIn ? "Signed in · microphone answers are sent for transcription and coaching."
      : "Sign in before starting practice. Nothing is recorded before you click Start.";
  }
  private start(): void {
    if (this.active || !this.configured || !this.auth.signedIn || !this.check("practice-consent").checked) return;
    let settings: PracticeSettings;
    try {
      settings = { scenario: { kind: this.scenario, description: this.input("practice-description").value,
        difficulty: Number(this.input("practice-difficulty").value) }, topic: this.input("practice-topic").value,
        useMaterials: this.check("practice-materials").checked, maxTurns: Number(this.input("practice-count").value) };
      this.round.begin(settings);
    } catch { this.showError("Check scenario, custom description, difficulty, and number of questions (1–12)."); return; }
    this.starting = true; this.showError(""); this.enrich.reset(); this.audio.stop();
    if (!navigator.mediaDevices?.getUserMedia) {
      this.starting = false; this.round.stop(); this.showError("Microphone access requires HTTPS and a supported browser."); return;
    }
    const generation = ++this.generation;
    const source = new MicrophoneAudioSource({
      getUserMedia: constraints => {
        if (!navigator.mediaDevices?.getUserMedia) throw new Error("Microphone access needs HTTPS and a supported browser.");
        return navigator.mediaDevices.getUserMedia(constraints);
      },
      devices: navigator.mediaDevices,
      context: () => new AudioContext(),
      node: context => new AudioWorkletNode(context, "meeting-pcm", { numberOfInputs: 1, numberOfOutputs: 1, outputChannelCount: [1] }),
      workletUrl: this.workletUrl, onDevices: () => {},
      onWarning: message => { this.el("practice-warning").textContent = message; this.el("practice-warning").hidden = !message; },
      onLevel: () => {},
    });
    this.session = new MeetingSession(source, new SocketTransport(() => this.auth.endpoint(false)), event => {
      if (generation !== this.generation) return;
      if (event.type === "session.ready") {
        this.starting = false; this.session?.pause(true); void this.round.next();
      } else if (event.type === "transcript.partial" || event.type === "transcript.final") {
        this.round.transcript(event.turnId!, event.text!, event.type === "transcript.final");
      }
    }, status => {
      if (generation !== this.generation) return;
      if (status.startsWith("Stopped")) {
        this.session = null; this.starting = false; this.audio.stop(); this.enrich.reset();
        const completed = this.round.view.phase === "summary";
        if (!completed) { this.round.stop(); this.showError(status.includes("time limit") ? status : "Microphone session ended. Start a new round to continue."); }
        this.activity(false, status); this.render(this.round.view);
      }
    }, error => { if (generation === this.generation) this.showError(error.message); },
    { ...defaultOptions(), responseMode: "conversation", transcribeOnly: true });
    // Start synchronously under the user's gesture; no TTS is allowed to open a microphone.
    void this.session.start(); this.activity(true, "Practice starting"); this.render(this.round.view);
  }
  async stop(): Promise<void> {
    this.generation++; this.starting = false; const session = this.session; this.session = null;
    this.audio.stop(); this.enrich.reset(); this.round.stop(); this.check("practice-consent").checked = false;
    await session?.stop(); this.activity(false, "Practice stopped"); this.render(this.round.view);
  }
  private partner(question: PartnerTurn): void {
    this.session?.pause(true); this.enrich.reset();
    void this.enrich.load("question", question.text);
    // Autoplay only follows explicit Start/Next in an active practice round. A blocked play has a visible button fallback.
    void this.listen(question.text, "partner", "partner");
  }
  private async listen(text: string, voice: "coach" | "partner", key: string): Promise<void> {
    this.session?.pause(true);
    if (this.round.view.phase === "answering") {
      // Listening is explicit; freeze answer input while synthesized audio could feed the microphone.
      this.round.pauseAnswer();
    }
    await this.audio.play(text, voice, this.input("practice-rate").value as Rate, key);
  }
  private render(view: PracticeView): void {
    const running = this.active;
    this.el("practice-view").classList.toggle("running", running);
    this.el<HTMLFieldSetElement>("practice-settings").disabled = running;
    this.button("practice-start").disabled = running || !this.configured || !this.auth.signedIn || !this.check("practice-consent").checked;
    this.button("practice-stop").disabled = !running;
    this.button("practice-signin").disabled = running || !this.configured || this.fake;
    this.el("practice-status").textContent = ({ idle: "Not started", connecting: "Opening microphone…", busy: "Coach is thinking…",
      question: "Listen, then answer when ready", answering: "Listening to your answer · 말해 보세요", feedback: "Review your answer · 피드백",
      summary: "Round complete · 연습 완료", error: "Request failed · 다시 시도" })[view.phase];
    if (view.error) this.showError(view.error); else if (view.phase !== "idle") this.showError("");
    this.el("practice-retry").hidden = view.phase !== "error";
    this.el("practice-conversation").hidden = !view.question || view.phase === "summary";
    if (this.questionText !== (view.question?.text ?? "")) {
      this.questionText = view.question?.text ?? ""; this.el("practice-question").textContent = this.questionText;
      const sources = this.el("practice-sources"); sources.replaceChildren();
      if (view.question) {
        const item = document.createElement("li"); item.textContent = `Question ${view.question.turn} · References: ${view.question.grounding}`; sources.append(item);
        for (const source of view.question.sources) { const item = document.createElement("li"); item.textContent = source.title; sources.append(item); }
      }
    }
    this.button("practice-answer").disabled = view.phase !== "question" || !this.session?.isReady;
    this.button("practice-done").disabled = !this.round.canFinish;
    this.button("practice-skip").disabled = !["question", "answering"].includes(view.phase);
    this.button("practice-hint").disabled = !["question", "answering"].includes(view.phase) || !this.check("practice-smart").checked;
    this.button("partner-listen").disabled = !view.question || view.phase === "busy";
    this.el("practice-answer-text").textContent = view.answer ? `Your answer: ${view.answer}` : "Your final spoken answer will appear here.";
    this.el("practice-partial").textContent = view.partial ? `Partial: ${view.partial}` : "";
    if (this.hintText !== (view.hint?.text ?? "")) {
      this.hintText = view.hint?.text ?? ""; this.el("practice-hint-text").textContent = this.hintText;
      this.el("practice-hint-box").hidden = !view.hint;
      this.el("practice-hint-sources").replaceChildren();
      if (view.hint) {
        this.text(this.el("practice-hint-sources"), "li", `Hint references: ${view.hint.grounding}`);
        for (const source of view.hint.sources) this.text(this.el("practice-hint-sources"), "li", source.title);
      }
      if (view.hint) void this.enrich.load("reply", view.hint.text); else this.enrich.clear("reply");
    }
    this.el("practice-feedback").hidden = !view.feedback;
    this.el("practice-next").hidden = view.phase !== "feedback";
    if (this.feedbackRendered !== view.feedback) {
      this.feedbackRendered = view.feedback;
      const host = this.el("practice-feedback"); host.replaceChildren();
      if (view.feedback) {
        this.text(host, "h3", "Make it your own · 더 자연스럽게");
        this.text(host, "p", view.feedback.feedbackKo, "ko");
        for (const [label, value, key] of [["Corrected English", view.feedback.correctedEnglish, "corrected"],
          ["Easier English", view.feedback.easierEnglish, "easier"]]) {
          this.text(host, "h4", label!); this.text(host, "p", value!); this.listenButton(host, value!, key!);
        }
        this.text(host, "p", `Clarity: ${view.feedback.clarity}/5 — clarity of the words, not a grade of you.`);
        for (const point of view.feedback.points) this.text(host, "p", `${point.tag}: ${point.ko}`, "ko");
        this.text(host, "p", "Based on recognized text only. This does not assess your pronunciation or accent.");
      }
    }
    this.el("practice-summary").hidden = !view.summary;
    if (this.summaryRendered !== view.summary) {
      this.summaryRendered = view.summary; const host = this.el("practice-summary"); host.replaceChildren();
      if (view.summary) {
        this.text(host, "h3", view.summary.headlineKo, "ko");
        this.text(host, "h4", "Strengths · 잘한 점");
        for (const value of view.summary.strengthsKo) this.text(host, "p", value, "ko");
        this.text(host, "h4", "Next time · 다음 연습");
        for (const value of view.summary.improveKo) this.text(host, "p", value, "ko");
        this.text(host, "h4", "Reusable phrases · 다시 쓸 표현");
        view.summary.phrases.forEach((phrase, i) => {
          this.text(host, "p", phrase.en); this.text(host, "p", phrase.ko, "ko"); this.listenButton(host, phrase.en, `phrase-${i}`);
        });
        const active = this.session; this.session = null; void active?.stop(); this.activity(false, "Practice complete");
      }
    }
    if (running && view.phase !== "summary") this.activity(true, this.el("practice-status").textContent ?? "Practice");
  }
  private listenButton(host: HTMLElement, text: string, key: string): void {
    const button = document.createElement("button"); button.type = "button"; button.className = "listen-pill";
    button.textContent = "Click to listen · 듣기"; button.dataset.listen = key;
    button.addEventListener("click", () => { void this.listen(text, "coach", key); }); host.append(button);
  }
  private text(host: HTMLElement, tag: string, value: string, lang?: string): void {
    const element = document.createElement(tag); element.textContent = value; if (lang) element.lang = lang; host.append(element);
  }
  private showError(message: string): void { this.el("practice-error").textContent = message; this.el("practice-error").hidden = !message; }
  private el<T extends HTMLElement = HTMLElement>(id: string): T { return document.getElementById(id)! as T; }
  private button(id: string): HTMLButtonElement { return this.el<HTMLButtonElement>(id); }
  private input(id: string): HTMLInputElement | HTMLSelectElement | HTMLTextAreaElement { return this.el(id); }
  private check(id: string): HTMLInputElement { return this.el<HTMLInputElement>(id); }
}
