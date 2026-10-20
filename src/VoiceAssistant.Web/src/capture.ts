import { audioBufferCount, audioFrameBytes } from "./audio-limits.js";
import { CodedError } from "./errors.js";

/** Why capture failed or needs attention; the live page turns each kind into its own message and recovery step. */
export type CaptureErrorKind = "cancelled" | "no-audio" | "ended" | "muted" | "silent" | "unsupported" | "permission";
export type CaptureSourceKind = "display" | "microphone";

/** A capture failure with its kind. The English message stays as the full meeting page has always shown it. */
export class CaptureError extends CodedError {
  constructor(readonly kind: CaptureErrorKind, readonly source: CaptureSourceKind, message: string) {
    super(`capture_${kind}`, message);
    this.name = "CaptureError";
  }
}

export interface AudioSource {
  prepare(signal: AbortSignal): Promise<void>;
  start(onAudio: (buffer: ArrayBuffer) => void, onError: (error: Error) => void): void;
  pause(paused: boolean): void;
  stop(): Promise<void>;
}
export interface CaptureDependencies {
  getDisplayMedia(): Promise<MediaStream>;
  context(): AudioContext;
  node(context: AudioContext): AudioWorkletNode;
  workletUrl: string;
  /** Shared-audio level (0..1) at 10 Hz, for a page that shows a meter. */
  onLevel?(level: number): void;
  /** When given, a muted shared track only warns (kind "muted"; an empty message clears it) instead of ending capture. */
  onWarning?(message: string, kind?: CaptureErrorKind): void;
  /** The page stalled for so long that this many 20 ms frames were dropped; capture itself continues. */
  onStall?(frames: number): void;
}
export interface MicrophoneDependencies extends Omit<CaptureDependencies, "getDisplayMedia" | "onLevel" | "onWarning"> {
  getUserMedia(constraints: MediaStreamConstraints): Promise<MediaStream>;
  devices: Pick<MediaDevices, "enumerateDevices" | "addEventListener" | "removeEventListener">;
  deviceId?: string;
  onDevices(devices: MediaDeviceInfo[]): void;
  onWarning(message: string, kind?: CaptureErrorKind): void;
  onLevel(level: number): void;
}

class WorkletAudioSource implements AudioSource {
  private stream: MediaStream | undefined;
  private context: AudioContext | undefined;
  private node: AudioWorkletNode | undefined;
  private input: MediaStreamAudioSourceNode | undefined;
  private stopped = false;
  private paused = true;
  private epoch = 0;
  private onError: (error: Error) => void = () => {};
  private failure: Error | undefined;
  private meter: ReturnType<typeof setInterval> | undefined;
  private analyser: AnalyserNode | undefined;
  private muted = false;
  private activeDevice = "";
  private readonly kind: CaptureSourceKind;
  constructor(private readonly deps: CaptureDependencies, private readonly mic?: MicrophoneDependencies) {
    this.kind = mic ? "microphone" : "display";
  }
  private get warn(): ((message: string, kind?: CaptureErrorKind) => void) | undefined { return this.mic?.onWarning ?? this.deps.onWarning; }
  private get level(): ((level: number) => void) | undefined { return this.mic?.onLevel ?? this.deps.onLevel; }
  private error(kind: CaptureErrorKind, message: string): CaptureError { return new CaptureError(kind, this.kind, message); }
  private fail(error: Error): void {
    if (this.stopped) return;
    this.failure = error;
    void this.stop().catch(() => this.onError(new Error("Audio cleanup failed; close this page to release the device.")));
    this.onError(error);
  }
  private readonly devicesChanged = (): void => {
    void this.refreshDevices().catch(() => this.fail(this.error("ended", "Could not check microphone availability. Start again.")));
  };
  private async refreshDevices(): Promise<void> {
    if (!this.mic) return;
    const devices = (await this.mic.devices.enumerateDevices()).filter(device => device.kind === "audioinput");
    if (this.stopped) return;
    this.mic.onDevices(devices);
    if (this.activeDevice && !devices.some(device => device.deviceId === this.activeDevice))
      this.fail(this.error("ended", "The selected microphone was disconnected. Choose an available microphone and Start again."));
  }
  /** Invoke directly from a click; both browser permission and resume start before awaiting. */
  async prepare(signal: AbortSignal): Promise<void> {
    signal.throwIfAborted();
    const capture = this.deps.getDisplayMedia();
    const aborted = (): void => { void this.stop(); };
    signal.addEventListener("abort", aborted, { once: true });
    try {
      this.context = this.deps.context();
      const resumed = this.context.resume().then(() => null, error => error as unknown);
      const stream = await capture;
      this.stream = stream;
      if (this.stopped || signal.aborted) { for (const track of stream.getTracks()) track.stop(); throw this.error("cancelled", "Sharing cancelled."); }
      if (stream.getAudioTracks().length === 0) throw this.error("no-audio", this.mic ? "No microphone audio track was provided." : "No shared audio was provided. Choose the Teams web tab and enable Share tab audio. Window/Teams desktop audio may not be supported.");
      for (const track of stream.getTracks()) {
        track.onended = () => this.fail(this.error("ended", this.mic ? "Microphone ended or disconnected. Start again to continue." : "Sharing ended or the source disconnected. All capture has stopped."));
        if (track.kind === "audio") {
          track.onmute = () => {
            const warn = this.warn;
            if (!warn) { this.fail(this.error("muted", "Shared audio became unavailable. Share the source again.")); return; }
            this.muted = true; this.updatePause();
            warn(this.mic ? "Microphone is muted by the system/browser. Unmute it to resume room audio."
              : "Shared audio is muted. Capture resumes when the source unmutes.", "muted");
          };
          track.onunmute = () => {
            const warn = this.warn;
            if (!warn || this.stopped) return;
            this.muted = false; this.updatePause(); warn("");
          };
        }
      }
      if (this.mic) {
        const track = stream.getAudioTracks()[0]!;
        this.activeDevice = track.getSettings().deviceId ?? this.mic.deviceId ?? "";
        this.muted = track.muted;
        if (this.muted) this.mic.onWarning("Microphone is muted by the system/browser. Unmute it to resume room audio.", "muted");
        this.mic.devices.addEventListener("devicechange", this.devicesChanged);
        await this.refreshDevices();
        if (this.failure) throw this.failure;
      }
      const resumeError = await resumed;
      if (resumeError) throw resumeError;
      signal.throwIfAborted();
      if (this.context.state !== "running") throw this.error("cancelled", "Browser audio could not start. Stop and use Share again.");
      await this.context.audioWorklet.addModule(this.deps.workletUrl);
      signal.throwIfAborted();
      if (this.stopped) throw this.error("cancelled", "Sharing stopped during setup.");
      this.node = this.deps.node(this.context);
      this.node.onprocessorerror = () => this.fail(this.error("ended", "Audio processing failed. Start a new session."));
      for (let i = 0; i < audioBufferCount; i++) {
        const buffer = new ArrayBuffer(audioFrameBytes);
        this.node.port.postMessage({ type: "buffer", buffer }, [buffer]);
      }
      this.input = this.context.createMediaStreamSource(new MediaStream(stream.getAudioTracks()));
      this.input.connect(this.node);
      // The processor has silent output, so it runs without replaying the meeting.
      this.node.connect(this.context.destination);
      const onLevel = this.level;
      if (onLevel) {
        const analyser = this.analyser = this.context.createAnalyser();
        analyser.fftSize = 256;
        const samples = new Float32Array(256);
        this.input.connect(analyser);
        this.meter = setInterval(() => {
          analyser.getFloatTimeDomainData(samples);
          let peak = 0;
          for (const sample of samples) peak = Math.max(peak, Math.abs(sample));
          onLevel(this.muted || this.paused ? 0 : Math.min(1, peak));
        }, 100);
      }
    } catch (error) {
      // Even if context creation fails, dispose tracks granted by the pending picker.
      void capture.then(stream => { for (const track of stream.getTracks()) track.stop(); }, () => {});
      await this.stop();
      throw this.mic ? microphoneError(error) : displayError(error);
    } finally { signal.removeEventListener("abort", aborted); }
  }
  start(onAudio: (buffer: ArrayBuffer) => void, onError: (error: Error) => void): void {
    this.onError = onError;
    if (this.failure) throw this.failure;
    if (!this.node || this.stopped || this.stream?.getAudioTracks().some(t => t.readyState === "ended"))
      throw this.error("ended", "Shared audio is no longer available.");
    this.node.port.onmessage = (event: MessageEvent<{ type: string; buffer: ArrayBuffer; epoch: number; message: string; frames: number }>) => {
      const data = event.data;
      if (data.type === "error") { onError(new Error(data.message)); return; }
      if (data.type === "stall") { if (!this.stopped && Number.isSafeInteger(data.frames)) this.deps.onStall?.(data.frames); return; }
      if (data.type !== "audio" || !(data.buffer instanceof ArrayBuffer)) return;
      try { if (!this.paused && !this.muted && !this.stopped && data.epoch === this.epoch) onAudio(data.buffer); }
      catch (error) { onError(error instanceof Error ? error : new Error(String(error))); }
      finally { if (!this.stopped) this.node?.port.postMessage({ type: "buffer", buffer: data.buffer }, [data.buffer]); }
    };
    this.pause(false);
  }
  pause(value: boolean): void { this.paused = value; this.updatePause(); }
  private updatePause(): void { this.epoch++; this.node?.port.postMessage({ type: "pause", paused: this.paused || this.muted, epoch: this.epoch }); }
  async stop(): Promise<void> {
    if (this.stopped) return;
    this.stopped = true; this.paused = true;
    clearInterval(this.meter);
    this.mic?.devices.removeEventListener("devicechange", this.devicesChanged);
    this.level?.(0); this.warn?.("");
    for (const track of this.stream?.getTracks() ?? []) { track.onended = track.onmute = track.onunmute = null; track.stop(); }
    this.input?.disconnect();
    this.analyser?.disconnect();
    if (this.node) { this.node.port.onmessage = null; this.node.port.close(); this.node.disconnect(); }
    if (this.context && this.context.state !== "closed") await this.context.close();
  }
}

export class DisplayAudioSource extends WorkletAudioSource {
  constructor(deps: CaptureDependencies) { super(deps); }
}
export class MicrophoneAudioSource extends WorkletAudioSource {
  constructor(deps: MicrophoneDependencies) {
    super({ ...deps, getDisplayMedia: () => deps.getUserMedia({
      audio: { channelCount: 1, echoCancellation: true, noiseSuppression: true, autoGainControl: true,
        ...(deps.deviceId ? { deviceId: { exact: deps.deviceId } } : {}) }, video: false,
    }) }, deps);
  }
}
/** Maps a share-picker rejection to its kind; the browser's own message is kept. Other failures pass through unchanged. */
export function displayError(error: unknown): Error {
  if (!(error instanceof Error)) return new Error(String(error));
  if (error instanceof CaptureError) return error;
  const kinds: Record<string, CaptureErrorKind> = {
    NotAllowedError: /system/i.test(error.message) ? "permission" : "cancelled",
    AbortError: "cancelled", InvalidStateError: "cancelled", NotFoundError: "cancelled", NotReadableError: "cancelled",
    SecurityError: "permission", NotSupportedError: "unsupported",
  };
  const kind = kinds[error.name];
  return kind ? new CaptureError(kind, "display", error.message || "Screen sharing did not start.") : error;
}
export function microphoneError(error: unknown): Error {
  if (error instanceof CaptureError) return error;
  const name = error instanceof Error ? error.name : "";
  const known: Record<string, [CaptureErrorKind, string]> = {
    NotAllowedError: ["permission", "Microphone permission denied. Allow microphone access for this site, then Start again."],
    NotFoundError: ["no-audio", "No microphone was found. Connect a microphone, then Start again."],
    NotReadableError: ["no-audio", "Microphone is busy or unavailable. Close other apps using it and try again."],
    OverconstrainedError: ["no-audio", "The selected microphone is unavailable. Select another device and Start again."],
    SecurityError: ["permission", "Microphone access is blocked by browser or organization policy."],
  };
  const match = known[name];
  if (match) return new CaptureError(match[0], "microphone", match[1]);
  return error instanceof Error ? error : new Error("Microphone capture failed.");
}
export class SyntheticSource implements AudioSource {
  private timer: ReturnType<typeof setInterval> | undefined;
  private paused = false;
  private readonly frame = new ArrayBuffer(640);
  private readonly silence = new ArrayBuffer(640);
  private sent = 0;
  constructor() {
    const view = new DataView(this.frame);
    for (let i = 0; i < 320; i++) view.setInt16(i * 2, Math.round(1200 * Math.sin(2 * Math.PI * 440 * i / 16000)), true);
  }
  async prepare(signal: AbortSignal): Promise<void> { signal.throwIfAborted(); }
  start(onAudio: (buffer: ArrayBuffer) => void, onError: (error: Error) => void): void {
    this.timer = setInterval(() => {
      try { if (!this.paused) onAudio(this.sent++ < 20 ? this.frame : this.silence); }
      catch (error) { onError(error instanceof Error ? error : new Error(String(error))); }
    }, 20);
  }
  pause(value: boolean): void { this.paused = value; }
  async stop(): Promise<void> { clearInterval(this.timer); }
}
