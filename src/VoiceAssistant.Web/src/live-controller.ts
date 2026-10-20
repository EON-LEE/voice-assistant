import type { AudioSource, CaptureErrorKind, CaptureSourceKind } from "./capture.js";
import { audioFrameBytes } from "./audio-limits.js";
import { CodedError } from "./errors.js";
import { classifyError, classifyServerError, type Classified } from "./live-errors.js";
import { createStartMessage, validateUpdate, type SessionOptions, type SessionUpdate } from "./options.js";
import type { ServerEvent } from "./protocol.js";
import { maxAudioMessageBytes, socketBufferedLimit, type Transport } from "./transport.js";

export interface LiveClock {
  now(): number;
  setTimeout(callback: () => void, ms: number): unknown;
  clearTimeout(handle: unknown): void;
}
export interface LiveNetwork {
  online(): boolean;
  /** Calls back when the browser reports the network is back; returns the unsubscribe function. */
  onOnline(listener: () => void): () => void;
}
/** What the audio source reports besides PCM frames. */
export interface SourceHooks {
  level(level: number): void;
  warning(kind: CaptureErrorKind | null): void;
  stall(frames: number): void;
}
export interface LiveControllerDeps {
  /** Called only from `start()`, which runs inside the user's click. */
  createSource(kind: CaptureSourceKind, hooks: SourceHooks): AudioSource;
  createTransport(): Transport;
  /** Read at every connection, so each new session starts with the latest settings. */
  options(): SessionOptions;
  /** Every server event except `error`, which the controller turns into a notice, reconnect, rollover or stop. */
  onEvent(event: ServerEvent): void;
  /** The server connection ended or was replaced; replies in flight end with it. */
  onDisconnect(reason: Classified): void;
  /** Something the page shows has changed. */
  onChange(): void;
  clock?: LiveClock;
  network?: LiveNetwork;
}

export const liveLimits = {
  maxReconnects: 20,
  /** A connection that stayed up this long starts the reconnect count again. */
  stableMs: 30000,
  handshakeMs: 15000,
  /** Audio kept while the connection is restored: the newest four seconds of 16 kHz PCM16. */
  backlogBytes: 4 * 32000,
  /** Client-side token bucket, a margin under the server's 128 KB burst and 64 KB/s refill. */
  burstBytes: 120000,
  refillBytesPerSecond: 60000,
  /** Socket room kept free so that commands never wait behind audio. */
  commandHeadroom: 4096,
  /** One silent frame this often keeps the server's 90 s receive-idle timer from firing while nothing else is sent. */
  keepaliveMs: 20000,
  silenceMs: 15000,
  silenceLevel: 0.002,
  offlineCheckMs: 5000,
  pumpMs: 20,
} as const;

export type LivePhase = "idle" | "starting" | "connecting" | "live" | "reconnecting" | "stopped";

export const browserClock: LiveClock = {
  now: () => performance.now(),
  setTimeout: (callback, ms) => setTimeout(callback, ms),
  clearTimeout: handle => clearTimeout(handle as ReturnType<typeof setTimeout>),
};
export const browserNetwork: LiveNetwork = {
  online: () => typeof navigator === "undefined" || navigator.onLine !== false,
  onOnline: listener => { addEventListener("online", listener); return () => removeEventListener("online", listener); },
};

/** Warns after continuous near-silence while capture runs; any sound, a pause or a mute resets it. */
export class SilenceWatch {
  silent = false;
  private quietSince: number | undefined;
  constructor(private readonly afterMs: number, private readonly floor: number) {}
  /** Feeds one level reading (0..1); returns true when `silent` changed. */
  update(level: number, now: number, listening: boolean): boolean {
    const before = this.silent;
    if (!listening || level > this.floor) { this.quietSince = undefined; this.silent = false; }
    else { this.quietSince ??= now; this.silent = now - this.quietSince >= this.afterMs; }
    return before !== this.silent;
  }
  reset(): void { this.quietSince = undefined; this.silent = false; }
}

function joinFrames(frames: ArrayBuffer[]): ArrayBuffer {
  const bytes = new Uint8Array(frames.reduce((total, frame) => total + frame.byteLength, 0));
  let offset = 0;
  for (const frame of frames) { bytes.set(new Uint8Array(frame), offset); offset += frame.byteLength; }
  return bytes.buffer;
}

type TimerName = "handshake" | "retry" | "pump" | "keepalive";

/**
 * One live session: a single audio source, prepared once from the user's click, feeding a sequence of server
 * connections. A dropped connection is restored without touching the source, so a screen share never needs
 * the picker again; the newest four seconds of audio wait in a backlog and are flushed within the server's rate limits.
 */
export class LiveController {
  phase: LivePhase = "idle";
  /** The capture kind of the current (or last) live session. */
  source: CaptureSourceKind = "microphone";
  attempt = 0;
  offline = false;
  /** Reconnecting because the server's session time limit was reached. */
  rollingOver = false;
  paused = false;
  /** Why the session stopped, or that it waits for a sign-in; null while running normally. */
  failure: Classified | null = null;
  /** The latest non-fatal server notice; cleared when the next reply starts. */
  notice: Classified | null = null;
  /** "muted" or "silent" while capture runs. */
  warning: CaptureErrorKind | null = null;
  readonly stats = { reconnects: 0, rollovers: 0, keepalives: 0, uploadStalls: 0, workletStalls: 0, droppedFrames: 0 };

  private readonly clock: LiveClock;
  private readonly network: LiveNetwork;
  private audio: AudioSource | undefined;
  private capturing = false;
  private lifetime: AbortController | undefined;
  private transport: Transport | undefined;
  private connection: AbortController | undefined;
  private generation = 0;
  private ready = false;
  private readyAt: number | undefined;
  private closingCode: string | undefined;
  private waitingForSignIn = false;
  private readonly timers: Partial<Record<TimerName, unknown>> = {};
  private stopWatchingNetwork: (() => void) | undefined;
  private backlog: ArrayBuffer[] = [];
  private backlogBytes = 0;
  private tokens = 0;
  private refilledAt = 0;
  private lastSentAt = 0;
  private flushing = false;
  private stalled = false;
  private muted = false;
  private readonly silence = new SilenceWatch(liveLimits.silenceMs, liveLimits.silenceLevel);

  constructor(private readonly deps: LiveControllerDeps) {
    this.clock = deps.clock ?? browserClock;
    this.network = deps.network ?? browserNetwork;
  }

  get active(): boolean {
    return this.phase === "starting" || this.phase === "connecting" || this.phase === "live" || this.phase === "reconnecting";
  }
  get isReady(): boolean { return this.ready; }
  /** The picker or permission prompt is done and audio is being captured. */
  get isCapturing(): boolean { return this.capturing; }
  /** The connection waits for an interactive sign-in; `resume()` continues with the same audio. */
  get needsSignIn(): boolean { return this.waitingForSignIn; }

  /** Call directly from the user's click: the share picker (or microphone prompt) opens before anything is awaited. */
  start(kind: CaptureSourceKind): void {
    if (this.active) return;
    this.source = kind;
    this.failure = this.notice = this.warning = null;
    this.attempt = 0;
    this.readyAt = undefined;
    this.offline = this.rollingOver = this.paused = this.waitingForSignIn = this.muted = false;
    this.clearBacklog(); this.silence.reset();
    const lifetime = this.lifetime = new AbortController();
    this.setPhase("starting");
    let audio: AudioSource, prepared: Promise<void>;
    try {
      audio = this.audio = this.deps.createSource(kind, this.hooks);
      prepared = audio.prepare(lifetime.signal);
    } catch (error) { this.halt(this.startFailure(error)); return; }
    prepared.then(() => {
      if (lifetime.signal.aborted || this.audio !== audio) return;
      audio.start(this.onAudio, this.onSourceError);
      this.capturing = true;
      if (this.paused) audio.pause(true);
      this.connect();
    }).catch(error => {
      if (lifetime.signal.aborted || this.audio !== audio) return;
      this.halt(this.startFailure(error));
    });
  }

  /** Ends the live session at the user's request. */
  async stop(): Promise<void> {
    if (this.phase === "idle") return;
    this.failure = this.notice = null;
    this.rollingOver = this.offline = false;
    this.attempt = 0;
    const stopped = this.shutdown();
    this.setPhase("idle");
    await stopped;
  }

  /** After an interactive sign-in, reconnects with the same audio source. */
  resume(): void {
    if (!this.active || !this.waitingForSignIn) return;
    this.waitingForSignIn = false;
    this.failure = null;
    this.attempt = 0;
    this.setPhase("connecting");
    this.connect();
  }

  /** Pausing drops audio locally (nothing captured meanwhile is ever sent) and cancels the reply being generated. */
  pause(value: boolean): void {
    if (this.paused === value) return;
    this.paused = value;
    this.audio?.pause(value);
    if (value) {
      this.clearBacklog();
      this.silence.reset();
      if (this.warning === "silent") this.warning = null;
      this.command({ type: "response.cancel" });
    }
    this.deps.onChange();
  }

  request(): void { if (!this.paused) this.command({ type: "response.request" }); }

  /** Changes document search or Korean for replies that start from now on; only after `session.ready` said `sessionUpdate`. */
  update(settings: SessionUpdate): void {
    const options = validateUpdate(settings);
    this.command({ type: "session.update", options });
  }

  private command(message: object): void {
    const transport = this.transport;
    if (!this.ready || !transport) return;
    try { transport.send(message); } catch (error) { this.lost(this.generation, error); }
  }

  private readonly hooks: SourceHooks = {
    level: level => {
      if (this.silence.update(level, this.clock.now(), this.capturing && !this.paused && !this.muted)) this.updateWarning();
    },
    warning: kind => {
      this.muted = kind === "muted";
      if (this.muted) this.silence.reset();
      this.updateWarning();
    },
    stall: frames => { this.stats.workletStalls++; this.stats.droppedFrames += frames; },
  };
  private updateWarning(): void {
    this.warning = this.muted ? "muted" : this.silence.silent ? "silent" : null;
    this.deps.onChange();
  }

  private readonly onAudio = (frame: ArrayBuffer): void => {
    if (!this.capturing || this.paused) return;
    // The worklet reuses its buffer once this call returns.
    this.backlog.push(frame.slice(0));
    this.backlogBytes += frame.byteLength;
    if (this.ready) this.pump(); else this.trimBacklog();
  };

  private readonly onSourceError = (error: Error): void => {
    if (!this.active) return;
    const reason = classifyError(error);
    this.halt(reason.disposition === "fatal" ? reason : this.captureFailed());
  };

  private startFailure(error: unknown): Classified {
    const reason = classifyError(error);
    return reason.disposition === "fatal" ? reason : this.captureFailed();
  }
  private captureFailed(): Classified {
    return { code: "capture_failed", disposition: "fatal", action: this.source === "display" ? "reshare" : "restart" };
  }

  private connect(): void {
    this.clearTimer("retry");
    this.stopWatchingNetwork?.(); this.stopWatchingNetwork = undefined;
    if (!this.active) return;
    const generation = ++this.generation;
    let message: object;
    try { message = createStartMessage(this.deps.options()); }
    catch { this.halt({ code: "invalid_options", disposition: "fatal", action: "restart" }); return; }
    const transport = this.transport = this.deps.createTransport();
    const connection = this.connection = new AbortController();
    this.ready = false;
    this.closingCode = undefined;
    if (this.phase !== "reconnecting") this.setPhase("connecting");
    transport.connect(event => this.receive(generation, event), error => this.lost(generation, error), connection.signal).then(() => {
      if (generation !== this.generation) return;
      this.timers.handshake = this.clock.setTimeout(() => this.lost(generation,
        new CodedError("handshake_timeout", "Server did not become ready within 15 seconds.")), liveLimits.handshakeMs);
      transport.send(message);
    }).catch(error => this.lost(generation, error));
  }

  private receive(generation: number, event: ServerEvent): void {
    if (generation !== this.generation) return;
    if (event.type === "error") { this.serverError(generation, event); return; }
    if (!this.ready) {
      if (event.type !== "session.ready") {
        this.lost(generation, new CodedError("protocol", "Expected session.ready before any transcript or audio."));
        return;
      }
      this.clearTimer("handshake");
      this.ready = true;
      this.readyAt = this.refilledAt = this.lastSentAt = this.clock.now();
      this.tokens = liveLimits.burstBytes;
      this.offline = this.rollingOver = false;
      this.flushing = this.backlog.length > 0;
      this.stalled = false;
      this.setPhase("live");
      this.deps.onEvent(event);
      if (generation !== this.generation) return;
      this.pump();
      this.scheduleKeepalive();
      return;
    }
    if (event.type === "session.ready") {
      this.lost(generation, new CodedError("protocol", "Unexpected duplicate session.ready."));
      return;
    }
    if (event.type === "response.started" && this.notice) { this.notice = null; this.deps.onChange(); }
    this.deps.onEvent(event);
  }

  private serverError(generation: number, event: ServerEvent): void {
    const reason = classifyServerError(event.code ?? "", event.retryable === true);
    switch (reason.disposition) {
      case "rollover": this.rollover(generation); return;
      case "fatal": this.halt(reason); return;
      // The server closes the socket next; that close reconnects with this reason.
      case "transient": this.closingCode = reason.code; return;
      case "notice": this.notice = reason; this.deps.onChange(); return;
    }
  }

  /** The server's session time limit: open the next session at once, with the same audio and no message. */
  private rollover(generation: number): void {
    if (generation !== this.generation) return;
    this.stats.rollovers++;
    this.dropConnection();
    this.deps.onDisconnect(classifyServerError("session_time_limit", false));
    if (!this.active) return;
    this.rollingOver = true;
    this.setPhase("reconnecting");
    this.connect();
  }

  private lost(generation: number, error: unknown): void {
    if (generation !== this.generation || !this.active) return;
    let reason = this.closingCode ? classifyServerError(this.closingCode, true) : classifyError(error);
    if (reason.disposition === "notice") reason = { ...reason, disposition: "transient" };
    this.dropConnection();
    this.trimBacklog();
    this.deps.onDisconnect(reason);
    if (!this.active) return;
    if (reason.disposition === "rollover") { this.rollingOver = true; this.setPhase("reconnecting"); this.connect(); return; }
    if (reason.action === "signin") {
      // Keep capturing (the newest four seconds are kept) until the user signs in; `resume()` continues.
      this.failure = reason;
      this.waitingForSignIn = true;
      this.setPhase("reconnecting");
      return;
    }
    if (reason.disposition === "fatal") { this.halt(reason); return; }
    this.retry();
  }

  private retry(): void {
    if (!this.network.online()) {
      // Time offline does not count as reconnect attempts.
      this.offline = true;
      this.setPhase("reconnecting");
      this.watchNetwork();
      return;
    }
    this.offline = false;
    this.attempt = this.readyAt !== undefined && this.clock.now() - this.readyAt > liveLimits.stableMs ? 1 : this.attempt + 1;
    this.readyAt = undefined;
    if (this.attempt > liveLimits.maxReconnects) {
      this.halt({ code: "reconnect_exhausted", disposition: "fatal", action: this.source === "display" ? "reshare" : "restart" });
      return;
    }
    this.stats.reconnects++;
    this.setPhase("reconnecting");
    this.timers.retry = this.clock.setTimeout(() => this.connect(), Math.min(3000, 300 * 2 ** Math.min(this.attempt - 1, 4)));
  }

  private watchNetwork(): void {
    this.stopWatchingNetwork?.();
    const resume = (): void => {
      if (!this.active || !this.offline) return;
      if (!this.network.online()) { this.timers.retry = this.clock.setTimeout(resume, liveLimits.offlineCheckMs); return; }
      this.offline = false;
      this.connect();
    };
    this.stopWatchingNetwork = this.network.onOnline(resume);
    // Some systems never fire `online`; look again now and then.
    this.timers.retry = this.clock.setTimeout(resume, liveLimits.offlineCheckMs);
  }

  /** Sends the backlog as fast as the server's token bucket and the socket buffer allow, in messages of at most 32 KiB. */
  private pump(): void {
    this.clearTimer("pump");
    const transport = this.transport;
    if (!this.ready || !transport) return;
    const now = this.refill();
    while (this.backlog.length) {
      const room = socketBufferedLimit - liveLimits.commandHeadroom - (transport.bufferedAmount ?? 0);
      const frames = Math.min(this.backlog.length, Math.floor(Math.min(this.tokens, room, maxAudioMessageBytes) / audioFrameBytes));
      if (frames <= 0) break;
      const batch = this.backlog.slice(0, frames);
      const chunk = frames === 1 ? batch[0]! : joinFrames(batch);
      try { transport.audio(chunk); }
      catch (error) { this.lost(this.generation, error); return; }
      this.backlog.splice(0, frames);
      this.backlogBytes -= chunk.byteLength;
      this.tokens -= chunk.byteLength;
      this.lastSentAt = now;
    }
    if (!this.backlog.length) { this.flushing = this.stalled = false; return; }
    if (this.backlogBytes > liveLimits.backlogBytes) {
      this.lost(this.generation, new CodedError("upload_backpressure", "Network upload cannot keep up (4 second audio limit)."));
      return;
    }
    if (!this.flushing && !this.stalled) { this.stalled = true; this.stats.uploadStalls++; }
    this.timers.pump = this.clock.setTimeout(() => this.pump(), liveLimits.pumpMs);
  }

  private refill(): number {
    const now = this.clock.now();
    this.tokens = Math.min(liveLimits.burstBytes, this.tokens + (now - this.refilledAt) * liveLimits.refillBytesPerSecond / 1000);
    this.refilledAt = now;
    return now;
  }

  private scheduleKeepalive(): void {
    this.clearTimer("keepalive");
    if (!this.ready) return;
    const wait = Math.max(0, this.lastSentAt + liveLimits.keepaliveMs - this.clock.now());
    this.timers.keepalive = this.clock.setTimeout(() => this.keepalive(), wait);
  }
  /** "Send silence when capture is active but quiet": one all-zero 20 ms frame when nothing was sent for 20 s (paused, muted). */
  private keepalive(): void {
    this.timers.keepalive = undefined;
    const transport = this.transport;
    if (!this.ready || !transport) return;
    const now = this.refill();
    if (now - this.lastSentAt >= liveLimits.keepaliveMs && !this.backlog.length) {
      try { transport.audio(new ArrayBuffer(audioFrameBytes)); }
      catch (error) { this.lost(this.generation, error); return; }
      this.tokens -= audioFrameBytes;
      this.lastSentAt = now;
      this.stats.keepalives++;
    }
    this.scheduleKeepalive();
  }

  private trimBacklog(): void {
    while (this.backlogBytes > liveLimits.backlogBytes) {
      const dropped = this.backlog.shift()!;
      this.backlogBytes -= dropped.byteLength;
      this.stats.droppedFrames += dropped.byteLength / audioFrameBytes;
    }
  }
  private clearBacklog(): void {
    this.backlog = [];
    this.backlogBytes = 0;
    this.flushing = this.stalled = false;
  }

  /** Stops and keeps the reason; the page offers `reason.action` (re-share, restart, sign in) when there is one. */
  private halt(reason: Classified): void {
    if (!this.active) return;
    this.failure = reason;
    void this.shutdown();
    this.setPhase("stopped");
  }

  private shutdown(): Promise<void> {
    this.lifetime?.abort();
    this.lifetime = undefined;
    this.clearTimer("retry");
    this.stopWatchingNetwork?.(); this.stopWatchingNetwork = undefined;
    if (this.ready) {
      try { this.transport?.send({ type: "session.stop" }); } catch { /* the connection is already gone */ }
    }
    this.dropConnection();
    const audio = this.audio;
    this.audio = undefined;
    this.capturing = this.muted = this.waitingForSignIn = false;
    this.clearBacklog();
    this.silence.reset();
    this.warning = null;
    return audio ? audio.stop().catch(() => undefined) : Promise.resolve();
  }

  private dropConnection(): void {
    this.generation++;
    this.clearTimer("handshake"); this.clearTimer("pump"); this.clearTimer("keepalive");
    const transport = this.transport;
    this.transport = undefined;
    this.connection?.abort();
    this.connection = undefined;
    this.ready = false;
    this.closingCode = undefined;
    this.flushing = this.stalled = false;
    try { transport?.close(); } catch { /* already closed */ }
  }

  private clearTimer(name: TimerName): void {
    const handle = this.timers[name];
    if (handle === undefined) return;
    this.clock.clearTimeout(handle);
    this.timers[name] = undefined;
  }
  private setPhase(phase: LivePhase): void {
    this.phase = phase;
    this.deps.onChange();
  }
}
