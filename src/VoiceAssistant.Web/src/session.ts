import type { AudioSource } from "./capture.js";
import type { ServerEvent } from "./protocol.js";
import { asError, type Transport } from "./transport.js";
import { createStartMessage, type SessionOptions } from "./options.js";

export class MeetingSession {
  private readonly lifetime = new AbortController();
  private ready = false;
  private stopped = false;
  private paused = false;
  private handshake: ReturnType<typeof setTimeout> | undefined;
  private stopTask: Promise<void> | undefined;
  private stopStatus = "Stopped · no capture";
  constructor(private readonly source: AudioSource, private readonly transport: Transport,
    private readonly event: (event: ServerEvent) => void,
    private readonly status: (text: string) => void, private readonly error: (error: Error) => void,
    private readonly options?: SessionOptions) {}
  get isReady(): boolean { return this.ready && !this.stopped; }
  async start(): Promise<void> {
    this.status("Preparing session…");
    try {
      const message = createStartMessage(this.options);
      // prepare is invoked synchronously from Start to preserve browser user activation.
      await this.source.prepare(this.lifetime.signal);
      if (this.stopped) return;
      this.status("Connecting…");
      await this.transport.connect(e => this.receive(e), error => this.fail(error), this.lifetime.signal);
      if (this.stopped) return;
      this.handshake = setTimeout(() => this.fail(new Error("Server did not become ready within 15 seconds.")), 15000);
      this.transport.send(message);
    } catch (error) { if (!this.stopped) this.fail(asError(error)); }
  }
  private receive(e: ServerEvent): void {
    if (this.stopped) return;
    try {
      if (!this.ready) {
        if (e.type === "error") throw new Error(`${e.code}: ${e.message}`);
        if (e.type !== "session.ready") throw new Error("Expected session.ready before any transcript or audio.");
        clearTimeout(this.handshake);
        this.ready = true;
        this.source.start(buffer => {
          if (this.isReady && !this.paused) this.transport.audio(buffer);
        }, error => this.fail(error));
        this.status("Connected · audio streaming");
      } else if (e.type === "session.ready") throw new Error("Unexpected duplicate session.ready.");
      this.event(e);
      if (e.type === "error" && e.code === "session_time_limit")
        this.stopStatus = "Stopped · Session time limit reached – click Start to continue";
      if (e.type === "error" && !e.retryable) this.fail(new Error(`${e.code}: ${e.message}`));
    } catch (error) { this.fail(asError(error)); }
  }
  pause(value: boolean): void {
    if (!this.isReady) return;
    this.paused = value; this.source.pause(value);
    if (value) this.command("response.cancel");
    this.status(value ? "Paused · dropping audio locally" : "Connected · audio streaming");
  }
  request(): void { if (!this.paused) this.command("response.request"); }
  cancel(): void { this.command("response.cancel"); }
  private command(type: string): void {
    if (!this.isReady) return;
    try { this.transport.send({ type }); } catch (error) { this.fail(asError(error)); }
  }
  private fail(error: Error): void { if (!this.stopped) { this.error(error); void this.stop(); } }
  stop(): Promise<void> {
    if (this.stopTask) return this.stopTask;
    this.stopped = true;
    clearTimeout(this.handshake);
    this.source.pause(true);
    if (this.ready) {
      try { this.transport.send({ type: "session.stop" }); }
      catch (error) { this.error(new Error(`Stopped locally; server stop failed: ${asError(error).message}`)); }
    }
    this.ready = false;
    this.transport.close();
    this.lifetime.abort();
    this.stopTask = this.source.stop().catch(error => this.error(asError(error)))
      .finally(() => this.status(this.stopStatus));
    return this.stopTask;
  }
}
