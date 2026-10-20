import { audioFrameBytes } from "./audio-limits.js";
import type { AudioSource } from "./capture.js";

/**
 * Native meeting audio from the LiveCoach Windows host (LiveCoach.exe, WebView2).
 *
 * Page to host (JSON text through `window.chrome.webview.postMessage`):
 * `host-audio.start {source, id}`, `host-audio.stop {id}` and `host-audio.ack {seq, id}`.
 * Host to page: `host-audio.status {id, source, state, detail}` and `host-audio.frame {seq, pcm}`, where `pcm` is
 * base64 of 1 to 5 consecutive 20 ms frames (640 bytes each, 16 kHz mono PCM16LE) and `seq` numbers the first of them.
 * A capture starts at seq 0; a gap means the host dropped frames because the page did not keep up. The host keeps at
 * most 50 frames unacknowledged, so the page acknowledges every few frames. The host keeps the stream continuous
 * (silence while nothing is captured), like the browser worklet.
 */
export type HostAudioKind = "teams" | "system";
export type HostAudioState = "capturing" | "teams-not-running" | "unsupported-os" | "error" | "stopped";
export interface HostAudioStatus {
  readonly state: HostAudioState;
  readonly detail: string;
  readonly source: HostAudioKind;
}

/** The part of `window.chrome.webview` (WebView2) this module uses. */
export interface HostWebView {
  postMessage(message: string): void;
  addEventListener(type: "message", listener: (event: HostMessageEvent) => void): void;
  removeEventListener(type: "message", listener: (event: HostMessageEvent) => void): void;
}
export interface HostMessageEvent { readonly data: unknown }

export const hostAudioVersion = 1;
export const maxFramesPerMessage = 5;
/** Frames between acknowledgements; well inside the host's 50-frame window. */
export const ackEveryFrames = 5;
const states: readonly HostAudioState[] = ["capturing", "teams-not-running", "unsupported-os", "error", "stopped"];
const kinds: readonly HostAudioKind[] = ["teams", "system"];

function record(value: unknown): Record<string, unknown> | undefined {
  return value !== null && typeof value === "object" ? value as Record<string, unknown> : undefined;
}

/** The WebView2 bridge when this page runs inside a WebView2 host, otherwise undefined. */
export function hostWebView(scope: unknown = globalThis): HostWebView | undefined {
  const webview = record(record(record(scope)?.["chrome"])?.["webview"]);
  if (typeof webview?.["postMessage"] !== "function" || typeof webview["addEventListener"] !== "function" ||
      typeof webview["removeEventListener"] !== "function") return undefined;
  return webview as unknown as HostWebView;
}

/**
 * Whether LiveCoach.exe hosts this page and offers native audio (optionally a given source). The host announces it
 * as `window.__liveShell.hostAudio = { version: 1, sources: [...] }` on its trusted origin only; "teams" is missing
 * on Windows builds without process loopback.
 */
export function isHostAudioAvailable(source?: HostAudioKind, scope: unknown = globalThis): boolean {
  if (!hostWebView(scope)) return false;
  const hostAudio = record(record(record(scope)?.["__liveShell"])?.["hostAudio"]);
  const sources: unknown = hostAudio?.["sources"];
  if (hostAudio?.["version"] !== hostAudioVersion || !Array.isArray(sources)) return false;
  const offered: readonly unknown[] = sources;
  return source ? offered.includes(source) : offered.some(value => kinds.includes(value as HostAudioKind));
}

/** What a host status means for the meeting: fatal errors end it, other messages are warnings ("" clears them). */
export function describeHostStatus(status: HostAudioStatus): { fatal: boolean; message: string } {
  const label = status.source === "teams" ? "Teams audio" : "System audio";
  switch (status.state) {
    case "capturing": return { fatal: false, message: "" };
    case "teams-not-running":
      return { fatal: false, message: "Microsoft Teams is not running. Live Coach starts listening as soon as Teams opens." };
    case "error": return { fatal: false, message: `${label}: ${status.detail}` };
    case "unsupported-os":
      return { fatal: true, message: `Capturing only Teams is unsupported on this Windows version. ${status.detail}`.trim() };
    case "stopped": return { fatal: true, message: "LiveCoach stopped capturing audio. Start again." };
  }
}

/** Base64 PCM of a frame message as separate 640-byte frames; throws for anything else. */
export function decodeFrames(pcm: string): ArrayBuffer[] {
  let binary: string;
  try { binary = atob(pcm); } catch { throw new Error("LiveCoach sent invalid audio data. Start again."); }
  const count = binary.length / audioFrameBytes;
  if (!Number.isInteger(count) || count < 1 || count > maxFramesPerMessage) throw new Error("LiveCoach sent invalid audio data. Start again.");
  const frames: ArrayBuffer[] = [];
  for (let frame = 0; frame < count; frame++) {
    const bytes = new Uint8Array(audioFrameBytes);
    for (let i = 0; i < audioFrameBytes; i++) bytes[i] = binary.charCodeAt(frame * audioFrameBytes + i);
    frames.push(bytes.buffer);
  }
  return frames;
}

type HostMessage =
  | { readonly kind: "status"; readonly id: string | undefined; readonly status: HostAudioStatus }
  | { readonly kind: "frame"; readonly seq: number; readonly pcm: string }
  | { readonly kind: "invalid" };

function parseHostMessage(data: unknown): HostMessage | undefined {
  let value: unknown = data;
  if (typeof data === "string") {
    if (!data.includes("host-audio.")) return undefined;
    try { value = JSON.parse(data); } catch { return undefined; }
  }
  const message = record(value);
  if (message?.["type"] === "host-audio.status") {
    const { state, source, detail, id } = message;
    if (!states.includes(state as HostAudioState) || !kinds.includes(source as HostAudioKind)) return undefined;
    return { kind: "status", id: typeof id === "string" ? id : undefined,
      status: { state: state as HostAudioState, source: source as HostAudioKind, detail: typeof detail === "string" ? detail : "" } };
  }
  if (message?.["type"] === "host-audio.frame") {
    const { seq, pcm } = message;
    return typeof seq === "number" && Number.isSafeInteger(seq) && seq >= 0 && typeof pcm === "string"
      ? { kind: "frame", seq, pcm } : { kind: "invalid" };
  }
  return undefined;
}

export interface HostAudioDependencies {
  webview: HostWebView;
  source: HostAudioKind;
  /** Conditions that do not end the meeting (Teams not running, capture problems); "" clears the warning. */
  onWarning(message: string): void;
  /** Every status of this capture, for example to show the host's own wording. */
  onStatus?(status: HostAudioStatus): void;
  /** Frames the host dropped: missing in this gap and in total. */
  onGap?(missing: number, total: number): void;
  /** How long `prepare` waits for LiveCoach to answer; 5 seconds by default. */
  answerTimeoutMs?: number;
  /** The capture id the host echoes; generated when omitted. */
  id?: string;
}

const pageId = Math.random().toString(36).slice(2, 10) || "page";
let captures = 0;

/**
 * An {@link AudioSource} whose audio LiveCoach.exe captures natively: only the Teams app ("teams"), or the default
 * output device when the user explicitly chooses "system". Same contract as the browser sources: `prepare` asks for
 * capture, `start` begins delivering 640-byte frames after `session.ready`, `pause` drops audio locally and `stop`
 * releases the host capture. Needs no user gesture and no browser permission.
 */
export class HostAudioSource implements AudioSource {
  private readonly id: string;
  private stopped = false;
  private paused = true;
  private requested = false;
  private confirmed = false;
  private failure: Error | undefined;
  private expected = 0;
  private unacknowledged = 0;
  private missing = 0;
  private onAudio: ((buffer: ArrayBuffer) => void) | undefined;
  private onError: (error: Error) => void = () => {};
  private answer: { resolve(): void; reject(error: Error): void } | undefined;
  private timer: ReturnType<typeof setTimeout> | undefined;
  private readonly listener = (event: HostMessageEvent): void => this.receive(event.data);

  constructor(private readonly deps: HostAudioDependencies) {
    this.id = deps.id ?? `${pageId}-${++captures}`;
  }

  /** Asks LiveCoach to capture and waits for its first answer. Teams not running is a warning, not a failure. */
  async prepare(signal: AbortSignal): Promise<void> {
    signal.throwIfAborted();
    if (this.stopped) throw new Error("Host audio capture was stopped.");
    const aborted = (): void => { this.settle(new Error("Audio capture cancelled.")); void this.stop(); };
    signal.addEventListener("abort", aborted, { once: true });
    try {
      const answered = new Promise<void>((resolve, reject) => { this.answer = { resolve, reject }; });
      // Observed below; this only keeps an early failure from also surfacing as an unhandled rejection.
      answered.catch(() => {});
      this.deps.webview.addEventListener("message", this.listener);
      this.requested = true;
      this.send({ type: "host-audio.start", source: this.deps.source, id: this.id });
      this.timer = setTimeout(() => this.settle(new Error(
        "LiveCoach did not answer the audio request. Update LiveCoach or choose another audio source.")), this.deps.answerTimeoutMs ?? 5000);
      await answered;
      signal.throwIfAborted();
    } catch (error) {
      await this.stop();
      throw error instanceof Error ? error : new Error(String(error));
    } finally {
      clearTimeout(this.timer);
      signal.removeEventListener("abort", aborted);
    }
  }

  start(onAudio: (buffer: ArrayBuffer) => void, onError: (error: Error) => void): void {
    this.onError = onError;
    if (this.failure) throw this.failure;
    if (this.stopped || !this.confirmed) throw new Error("Host audio is no longer available.");
    this.onAudio = onAudio;
    this.pause(false);
  }

  pause(paused: boolean): void { this.paused = paused; }

  async stop(): Promise<void> {
    if (this.stopped) return;
    this.stopped = true;
    this.paused = true;
    this.onAudio = undefined;
    this.deps.webview.removeEventListener("message", this.listener);
    this.settle(new Error("Host audio capture was stopped."));
    if (this.requested) {
      try { this.send({ type: "host-audio.stop", id: this.id }); } catch { /* the host is gone, so nothing captures for this page */ }
    }
    this.deps.onWarning("");
  }

  private receive(data: unknown): void {
    if (this.stopped) return;
    const message = parseHostMessage(data);
    if (!message) return;
    if (message.kind === "status") {
      // Statuses of another or an earlier capture are not ours.
      if (message.id !== this.id) return;
      if (!this.confirmed) { this.confirmed = true; this.expected = 0; }
      this.deps.onStatus?.(message.status);
      const effect = describeHostStatus(message.status);
      if (effect.fatal) {
        if (this.answer) this.settle(new Error(effect.message));
        else this.fail(new Error(effect.message));
        return;
      }
      this.deps.onWarning(effect.message);
      this.settle();
      return;
    }
    // Frames before our first status belong to an earlier capture.
    if (!this.confirmed) return;
    let frames: ArrayBuffer[];
    try {
      if (message.kind === "invalid") throw new Error("LiveCoach sent invalid audio data. Start again.");
      frames = decodeFrames(message.pcm);
    } catch (error) {
      this.fail(error instanceof Error ? error : new Error(String(error)));
      return;
    }
    if (message.seq < this.expected) return;
    if (message.seq > this.expected) {
      const gap = message.seq - this.expected;
      this.missing += gap;
      this.deps.onGap?.(gap, this.missing);
    }
    this.expected = message.seq + frames.length;
    this.acknowledge(this.expected - 1, frames.length);
    for (const frame of frames) {
      if (this.paused || this.stopped || !this.onAudio) return;
      try { this.onAudio(frame); }
      catch (error) { this.onError(error instanceof Error ? error : new Error(String(error))); return; }
    }
  }

  private acknowledge(seq: number, frames: number): void {
    this.unacknowledged += frames;
    if (this.unacknowledged < ackEveryFrames) return;
    this.unacknowledged = 0;
    try { this.send({ type: "host-audio.ack", seq, id: this.id }); }
    catch (error) { this.fail(error instanceof Error ? error : new Error(String(error))); }
  }

  private send(message: object): void { this.deps.webview.postMessage(JSON.stringify(message)); }

  private settle(error?: Error): void {
    const answer = this.answer;
    if (!answer) return;
    this.answer = undefined;
    clearTimeout(this.timer);
    if (error) answer.reject(error);
    else answer.resolve();
  }

  private fail(error: Error): void {
    if (this.stopped) return;
    this.failure = error;
    void this.stop();
    this.onError(error);
  }
}
