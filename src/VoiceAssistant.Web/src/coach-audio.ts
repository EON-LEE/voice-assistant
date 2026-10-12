import { CoachError, type CoachClient, type Rate, type Voice } from "./coach-client.js";

export interface AudioClip {
  src: string; currentTime: number;
  onended: ((this: GlobalEventHandlers, event: Event) => unknown) | null;
  onerror: OnErrorEventHandler;
  play(): Promise<void>; pause(): void; removeAttribute(name: string): void; load(): void;
}
export interface ClipState { state: "idle" | "loading" | "playing" | "error"; key: string; message: string }
export class CoachAudio {
  private request: AbortController | undefined;
  private url: string | undefined;
  private generation = 0;
  private retryAt = 0;
  private ended: (() => void) | undefined;
  private state: ClipState = { state: "idle", key: "", message: "" };
  constructor(private readonly client: Pick<CoachClient, "speak">, private readonly player: AudioClip,
    private readonly changed: (state: ClipState) => void,
    private readonly urls = { create: (blob: Blob) => URL.createObjectURL(blob), revoke: (url: string) => URL.revokeObjectURL(url) }) {}
  get active(): boolean { return this.state.state === "loading" || this.state.state === "playing"; }
  async play(text: string, voice: Voice, rate: Rate, key: string, onEnded?: () => void): Promise<boolean> {
    this.stop(); const generation = this.generation;
    if (Date.now() < this.retryAt) {
      this.set({ state: "error", key, message: `Please wait ${Math.ceil((this.retryAt - Date.now()) / 1000)} seconds before retrying audio.` }); return false;
    }
    this.request = new AbortController(); this.ended = onEnded;
    this.set({ state: "loading", key, message: "Preparing audio…" });
    try {
      const blob = await this.client.speak(text, voice, rate, this.request.signal);
      if (generation !== this.generation) return false;
      this.url = this.urls.create(blob); this.player.src = this.url;
      this.player.onended = () => {
        if (generation !== this.generation) return;
        const ended = this.ended; this.stop(); ended?.();
      };
      this.player.onerror = () => {
        if (generation !== this.generation) return;
        this.stop(); this.set({ state: "error", key, message: "Audio could not play. Click to listen again." });
      };
      await this.player.play();
      if (generation !== this.generation) return false;
      this.set({ state: "playing", key, message: "Playing…" }); return true;
    } catch (error) {
      if (generation !== this.generation) return false;
      this.stop();
      if (error instanceof CoachError) this.retryAt = Date.now() + error.retryAfter * 1000;
      this.set({ state: "error", key, message: error instanceof CoachError ? error.message : error instanceof Error && error.name !== "NotAllowedError"
        ? "Audio is unavailable. Click to listen to retry." : "Playback needs a click. Use the listen button." });
      return false;
    }
  }
  stop(): void {
    this.generation++; this.request?.abort(); this.request = undefined; this.ended = undefined;
    this.player.onended = this.player.onerror = null; this.player.pause(); this.player.removeAttribute("src"); this.player.load();
    if (this.url) this.urls.revoke(this.url); this.url = undefined;
    this.set({ state: "idle", key: "", message: "" });
  }
  private set(state: ClipState): void { this.state = state; this.changed(state); }
}
