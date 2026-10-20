import type { HostMessageEvent, HostWebView } from "../../src/VoiceAssistant.Web/src/host-audio.js";

export type Posted = { type: string; source?: string; id?: string; seq?: number };

/** window.chrome.webview as LiveCoach.exe provides it: JSON text both ways. */
export class FakeWebView implements HostWebView {
  readonly posted: Posted[] = [];
  private readonly listeners = new Set<(event: HostMessageEvent) => void>();
  postMessage(message: string): void { this.posted.push(JSON.parse(message) as Posted); }
  addEventListener(_type: "message", listener: (event: HostMessageEvent) => void): void { this.listeners.add(listener); }
  removeEventListener(_type: "message", listener: (event: HostMessageEvent) => void): void { this.listeners.delete(listener); }
  get listening(): number { return this.listeners.size; }
  sent(type: string): Posted[] { return this.posted.filter(message => message.type === type); }
  /** The id of the latest capture the page asked for. */
  get lastStart(): Posted | undefined { return this.sent("host-audio.start").at(-1); }
  host(message: object): void { for (const listener of [...this.listeners]) listener({ data: JSON.stringify(message) }); }
  status(id: string, state: string, detail = "", source = "teams", code?: string): void {
    this.host({ type: "host-audio.status", id, source, state, detail, ...(code ? { code } : {}) });
  }
  /** One frame message: a 640-byte frame per marker, every byte set to that marker. */
  frames(seq: number, ...markers: number[]): void {
    this.host({ type: "host-audio.frame", seq, pcm: Buffer.concat(markers.map(marker => Buffer.alloc(640, marker))).toString("base64") });
  }
  /** One frame whose 16-bit samples all equal `sample`. */
  samples(seq: number, sample: number): void {
    const frame = Buffer.alloc(640);
    for (let i = 0; i < 320; i++) frame.writeInt16LE(sample, i * 2);
    this.host({ type: "host-audio.frame", seq, pcm: frame.toString("base64") });
  }
}
