import type { AudioSource } from "./capture.js";

export const demoTranscript = "Project Lumen needs a brief update by Friday.";
export const demoReply = ["Let me confirm ", "the remaining details ", "before Friday's update."];
type DemoPlayer = Pick<HTMLAudioElement, "src" | "play" | "pause" | "currentTime" | "ended" |
  "load" | "addEventListener" | "removeEventListener" | "removeAttribute">;

/** Plays only the bundled example, never microphone/system audio or model output. */
export class DemoAudioSource implements AudioSource {
  private stopped = false;
  private paused = false;
  private delivered = false;
  private onAudio: ((buffer: ArrayBuffer) => void) | undefined;
  private onError: ((error: Error) => void) | undefined;
  private signal: AbortSignal | undefined;
  private readonly ended = (): void => {
    if (this.stopped || this.paused || this.delivered || !this.onAudio) return;
    this.delivered = true;
    try { this.onAudio(new ArrayBuffer(640)); }
    catch (error) { this.onError?.(error instanceof Error ? error : new Error(String(error))); }
  };
  private readonly failed = (): void => {
    this.onError?.(new Error("The demo audio could not be loaded. Stop and retry the demo."));
  };
  private readonly aborted = (): void => { void this.stop(); };
  constructor(private readonly player: DemoPlayer, private readonly url: string) {}
  async prepare(signal: AbortSignal): Promise<void> {
    signal.throwIfAborted();
    this.signal = signal;
    signal.addEventListener("abort", this.aborted, { once: true });
    this.player.src = this.url;
    this.player.currentTime = 0;
    this.player.addEventListener("ended", this.ended);
    this.player.addEventListener("error", this.failed);
    try {
      // Called from Start's click before awaiting anything, preserving playback permission.
      await this.player.play();
      signal.throwIfAborted();
      if (this.stopped) throw new Error("Demo stopped.");
    } catch {
      await this.stop();
      throw new Error("Demo audio could not play. Check browser sound permissions and click Start demo again.");
    }
  }
  start(onAudio: (buffer: ArrayBuffer) => void, onError: (error: Error) => void): void {
    if (this.stopped) throw new Error("Demo stopped.");
    this.onAudio = onAudio;
    this.onError = onError;
    if (this.player.ended) this.ended();
  }
  pause(paused: boolean): void {
    if (this.stopped) return;
    this.paused = paused;
    if (paused) this.player.pause();
    else if (this.player.ended) this.ended();
    else if (!this.delivered) void this.player.play().catch(() =>
      this.onError?.(new Error("Demo audio could not resume. Stop and start the demo again.")));
  }
  async stop(): Promise<void> {
    if (this.stopped) return;
    this.stopped = true;
    this.signal?.removeEventListener("abort", this.aborted);
    this.player.removeEventListener("ended", this.ended);
    this.player.removeEventListener("error", this.failed);
    this.player.pause();
    this.player.removeAttribute("src");
    this.player.load();
    this.onAudio = undefined;
    this.onError = undefined;
  }
}
