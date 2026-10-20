import { parseEvent, type ServerEvent } from "./protocol.js";
import { demoReply, demoTranscript } from "./demo.js";
import { CodedError } from "./errors.js";
import { audioFrameBytes } from "./audio-limits.js";

/** The server accepts binary messages of at most 32 KiB; 51 whole 20 ms frames fit in one. */
export const maxAudioMessageBytes = Math.floor(32768 / audioFrameBytes) * audioFrameBytes;
/** About three seconds of 16 kHz PCM16 may wait in the browser's socket buffer before the upload counts as stuck. */
export const socketBufferedLimit = 96000;

export interface Transport {
  connect(onEvent: (event: ServerEvent) => void, onError: (error: Error) => void, signal: AbortSignal): Promise<void>;
  send(message: object): void;
  audio(buffer: ArrayBuffer): void;
  close(): void;
  /** Bytes queued in the browser but not yet sent; transports without a socket report nothing. */
  readonly bufferedAmount?: number;
}

export class SocketTransport implements Transport {
  private socket: WebSocket | null = null;
  private closed = false;
  constructor(private readonly endpoint: () => Promise<URL>,
    private readonly factory: (url: string) => WebSocket = url => new WebSocket(url)) {}
  get bufferedAmount(): number { return this.socket?.bufferedAmount ?? 0; }
  async connect(onEvent: (event: ServerEvent) => void, onError: (error: Error) => void, signal: AbortSignal): Promise<void> {
    const url = await this.endpoint();
    signal.throwIfAborted();
    await new Promise<void>((resolve, reject) => {
      const socket = this.socket = this.factory(url.href);
      let opened = false;
      const timer = setTimeout(() => fail(new CodedError("connect_timeout", "Connection timed out. Start again to reconnect.")), 15000);
      const abort = (): void => { fail(new CodedError("cancelled", "Connection cancelled.")); this.close(); };
      const fail = (error: Error): void => {
        clearTimeout(timer);
        if (!opened) reject(error);
        else if (!this.closed) onError(error);
      };
      signal.addEventListener("abort", abort, { once: true });
      socket.onopen = () => { clearTimeout(timer); opened = true; resolve(); };
      socket.onmessage = event => {
        let parsed: ServerEvent;
        try { parsed = parseEvent(event.data); }
        catch (error) { fail(new CodedError("protocol", asError(error).message)); return; }
        try { onEvent(parsed); }
        catch (error) { fail(asError(error)); }
      };
      socket.onerror = () => fail(new CodedError("socket_error", "WebSocket connection failed. Check sign-in and server availability."));
      socket.onclose = event => {
        signal.removeEventListener("abort", abort);
        fail(event.code === 1000 ? new CodedError("server_closed", "The server ended this session. Click Start to continue.")
          : new CodedError("socket_closed", "Server disconnected unexpectedly. Audio has stopped; click Start to reconnect."));
      };
    });
  }
  send(message: object): void { this.write(JSON.stringify(message)); }
  /** One or more whole 20 ms PCM16 mono frames, at most 32 KiB per message. */
  audio(buffer: ArrayBuffer): void {
    if (!buffer.byteLength || buffer.byteLength % audioFrameBytes) throw new CodedError("bad_audio", "Audio frames must be 20 ms PCM16 mono.");
    if (buffer.byteLength > maxAudioMessageBytes) throw new CodedError("bad_audio", "An audio message must not exceed 32 KiB.");
    this.write(buffer);
  }
  private write(data: string | ArrayBuffer): void {
    if (this.closed || this.socket?.readyState !== 1) throw new CodedError("socket_not_open", "The meeting connection is not open.");
    const size = typeof data === "string" ? new TextEncoder().encode(data).length : data.byteLength;
    if (this.socket.bufferedAmount + size > socketBufferedLimit)
      throw new CodedError("upload_backpressure", "Network upload cannot keep up (3 second audio limit). Session stopped.");
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
