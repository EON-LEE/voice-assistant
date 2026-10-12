import { parseEvent, type ServerEvent } from "./protocol.js";
import { demoReply, demoTranscript } from "./demo.js";

export interface Transport {
  connect(onEvent: (event: ServerEvent) => void, onError: (error: Error) => void, signal: AbortSignal): Promise<void>;
  send(message: object): void;
  audio(buffer: ArrayBuffer): void;
  close(): void;
}

export class SocketTransport implements Transport {
  private socket: WebSocket | null = null;
  private closed = false;
  constructor(private readonly endpoint: () => Promise<URL>,
    private readonly factory: (url: string) => WebSocket = url => new WebSocket(url)) {}
  async connect(onEvent: (event: ServerEvent) => void, onError: (error: Error) => void, signal: AbortSignal): Promise<void> {
    const url = await this.endpoint();
    signal.throwIfAborted();
    await new Promise<void>((resolve, reject) => {
      const socket = this.socket = this.factory(url.href);
      let opened = false;
      const timer = setTimeout(() => fail(new Error("Connection timed out. Start again to reconnect.")), 15000);
      const abort = (): void => { fail(new Error("Connection cancelled.")); this.close(); };
      const fail = (error: Error): void => {
        clearTimeout(timer);
        if (!opened) reject(error);
        else if (!this.closed) onError(error);
      };
      signal.addEventListener("abort", abort, { once: true });
      socket.onopen = () => { clearTimeout(timer); opened = true; resolve(); };
      socket.onmessage = event => {
        try { onEvent(parseEvent(event.data)); }
        catch (error) { fail(asError(error)); }
      };
      socket.onerror = () => fail(new Error("WebSocket connection failed. Check sign-in and server availability."));
      socket.onclose = event => {
        signal.removeEventListener("abort", abort);
        fail(new Error(event.code === 1000 ? "The server ended this session. Click Start to continue."
          : "Server disconnected unexpectedly. Audio has stopped; click Start to reconnect."));
      };
    });
  }
  send(message: object): void { this.write(JSON.stringify(message)); }
  audio(buffer: ArrayBuffer): void {
    if (buffer.byteLength !== 640) throw new Error("Audio frames must be 20 ms PCM16 mono.");
    this.write(buffer);
  }
  private write(data: string | ArrayBuffer): void {
    if (this.closed || this.socket?.readyState !== 1) throw new Error("The meeting connection is not open.");
    const size = typeof data === "string" ? new TextEncoder().encode(data).length : data.byteLength;
    if (this.socket.bufferedAmount + size > 32000) throw new Error("Network upload cannot keep up (1 second audio limit). Session stopped.");
    this.socket.send(data);
  }
  close(): void {
    this.closed = true;
    if (this.socket) {
      this.socket.onmessage = this.socket.onerror = this.socket.onclose = null;
      this.socket.close(1000, "Session stopped");
      this.socket = null;
    }
  }
}

export class DemoTransport implements Transport {
  private onEvent: (event: ServerEvent) => void = () => {};
  private timer: ReturnType<typeof setInterval> | undefined;
  private ready = false;
  private response = 0;
  private closed = false;
  private transcript = false;
  async connect(onEvent: (event: ServerEvent) => void, _onError: (error: Error) => void, signal: AbortSignal): Promise<void> {
    signal.throwIfAborted(); this.onEvent = onEvent;
  }
  send(message: { type?: string }): void {
    if (this.closed) throw new Error("Demo has stopped.");
    if (message.type === "session.start") { this.ready = true; this.onEvent({ type: "session.ready" }); }
    if (message.type === "response.cancel") {
      this.cancel(); this.onEvent({ type: "response.cancelled", turnId: "demo-turn", responseId: `demo-${this.response}` });
    }
    if (message.type === "response.request") {
      this.cancel();
      const responseId = `demo-${++this.response}`;
      this.onEvent({ type: "response.started", turnId: "demo-turn", responseId });
      const chunks = demoReply;
      let i = 0;
      this.timer = setInterval(() => {
        if (i < chunks.length) this.onEvent({ type: "response.delta", turnId: "demo-turn", responseId, text: chunks[i++]! });
        else { this.cancel(); this.onEvent({ type: "response.completed", turnId: "demo-turn", responseId, text: chunks.join(""), sources: [] }); }
      }, 150);
    }
  }
  audio(_buffer: ArrayBuffer): void {
    if (this.ready && !this.transcript) {
      this.transcript = true;
      this.onEvent({ type: "transcript.partial", turnId: "demo-turn", revision: 1, text: "Project Lumen needs" });
      this.onEvent({ type: "transcript.final", turnId: "demo-turn", revision: 2, text: demoTranscript });
    }
  }
  private cancel(): void { clearInterval(this.timer); this.timer = undefined; }
  close(): void { this.closed = true; this.cancel(); }
}
export function asError(error: unknown): Error { return error instanceof Error ? error : new Error(String(error)); }
export function isLoopback(hostname: string): boolean { return ["localhost", "127.0.0.1", "[::1]", "::1"].includes(hostname); }
