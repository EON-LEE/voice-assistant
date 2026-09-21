import { audioBufferCount, audioFrameBytes } from "./audio-limits.js";

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
}

export class DisplayAudioSource implements AudioSource {
  private stream: MediaStream | undefined;
  private context: AudioContext | undefined;
  private node: AudioWorkletNode | undefined;
  private input: MediaStreamAudioSourceNode | undefined;
  private stopped = false;
  private paused = true;
  private epoch = 0;
  private onError: (error: Error) => void = () => {};
  constructor(private readonly deps: CaptureDependencies) {}
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
      if (this.stopped || signal.aborted) { for (const track of stream.getTracks()) track.stop(); throw new Error("Sharing cancelled."); }
      if (stream.getAudioTracks().length === 0) throw new Error("No shared audio was provided. Choose the Teams web tab and enable Share tab audio. Window/Teams desktop audio may not be supported.");
      for (const track of stream.getTracks()) {
        track.onended = () => { void this.stop(); this.onError(new Error("Sharing ended or the source disconnected. All capture has stopped.")); };
        if (track.kind === "audio") track.onmute = () => { void this.stop(); this.onError(new Error("Shared audio became unavailable. Share the source again.")); };
      }
      const resumeError = await resumed;
      if (resumeError) throw resumeError;
      signal.throwIfAborted();
      if (this.context.state !== "running") throw new Error("Browser audio could not start. Stop and use Share again.");
      await this.context.audioWorklet.addModule(this.deps.workletUrl);
      signal.throwIfAborted();
      if (this.stopped) throw new Error("Sharing stopped during setup.");
      this.node = this.deps.node(this.context);
      this.node.onprocessorerror = () => { void this.stop(); this.onError(new Error("Audio processing failed. Start a new session.")); };
      for (let i = 0; i < audioBufferCount; i++) {
        const buffer = new ArrayBuffer(audioFrameBytes);
        this.node.port.postMessage({ type: "buffer", buffer }, [buffer]);
      }
      this.input = this.context.createMediaStreamSource(new MediaStream(stream.getAudioTracks()));
      this.input.connect(this.node);
      // The processor has silent output, so it runs without replaying the meeting.
      this.node.connect(this.context.destination);
    } catch (error) {
      // Even if context creation fails, dispose tracks granted by the pending picker.
      void capture.then(stream => { for (const track of stream.getTracks()) track.stop(); }, () => {});
      await this.stop();
      throw error;
    } finally { signal.removeEventListener("abort", aborted); }
  }
  start(onAudio: (buffer: ArrayBuffer) => void, onError: (error: Error) => void): void {
    this.onError = onError;
    if (!this.node || this.stopped || this.stream?.getAudioTracks().some(t => t.readyState === "ended"))
      throw new Error("Shared audio is no longer available.");
    this.node.port.onmessage = (event: MessageEvent<{ type: string; buffer: ArrayBuffer; epoch: number; message: string }>) => {
      const data = event.data;
      if (data.type === "error") { onError(new Error(data.message)); return; }
      if (data.type !== "audio" || !(data.buffer instanceof ArrayBuffer)) return;
      try { if (!this.paused && !this.stopped && data.epoch === this.epoch) onAudio(data.buffer); }
      catch (error) { onError(error instanceof Error ? error : new Error(String(error))); }
      finally { if (!this.stopped) this.node?.port.postMessage({ type: "buffer", buffer: data.buffer }, [data.buffer]); }
    };
    this.pause(false);
  }
  pause(value: boolean): void { this.paused = value; this.epoch++; this.node?.port.postMessage({ type: "pause", paused: value, epoch: this.epoch }); }
  async stop(): Promise<void> {
    if (this.stopped) return;
    this.stopped = true; this.paused = true;
    for (const track of this.stream?.getTracks() ?? []) { track.onended = track.onmute = null; track.stop(); }
    this.input?.disconnect();
    if (this.node) { this.node.port.onmessage = null; this.node.port.close(); this.node.disconnect(); }
    if (this.context && this.context.state !== "closed") await this.context.close();
  }
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
