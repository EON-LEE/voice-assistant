import { PcmConverter } from "./pcm.js";
import { audioBufferCount, audioFrameBytes } from "./audio-limits.js";
declare const sampleRate: number;
declare class AudioWorkletProcessor { readonly port: MessagePort; }
declare function registerProcessor(name: string, processor: typeof AudioWorkletProcessor): void;

class MeetingAudioProcessor extends AudioWorkletProcessor {
  private readonly converter = new PcmConverter(sampleRate);
  private available: ArrayBuffer[] = [];
  private view: DataView | null = null;
  private offset = 0;
  private paused = true;
  private epoch = 0;
  /** Samples dropped while the page held every buffer; reported once a buffer comes back. */
  private dropped = 0;
  constructor() {
    super();
    this.port.onmessage = ({ data }: MessageEvent<{ type: string; buffer?: ArrayBuffer; paused?: boolean; epoch?: number }>) => {
      if (data.type === "buffer" && data.buffer?.byteLength === audioFrameBytes &&
          this.available.length + (this.view ? 1 : 0) < audioBufferCount) {
        this.available.push(data.buffer);
        this.reportStall();
      }
      if (data.type === "pause") {
        this.paused = !!data.paused; this.epoch = data.epoch ?? 0; this.offset = 0; this.converter.reset();
        this.reportStall();
      }
    };
  }
  /** A stall is never silent: the page learns how many 20 ms frames were lost, then capture simply continues. */
  private reportStall(): void {
    if (!this.dropped) return;
    this.port.postMessage({ type: "stall", frames: Math.ceil(this.dropped / (audioFrameBytes / 2)) });
    this.dropped = 0;
  }
  private readonly emit = (sample: number): void => {
    if (!this.view) {
      const buffer = this.available.pop();
      if (!buffer) { this.dropped++; return; }
      this.view = new DataView(buffer);
    }
    this.view.setInt16(this.offset, sample, true); this.offset += 2;
    if (this.offset === audioFrameBytes) {
      const buffer = this.view.buffer;
      this.port.postMessage({ type: "audio", buffer, epoch: this.epoch }, [buffer]);
      this.view = null; this.offset = 0;
    }
  };
  process(inputs: Float32Array[][]): boolean {
    if (!this.paused && inputs[0]?.length) this.converter.process(inputs[0], this.emit);
    return true;
  }
}
registerProcessor("meeting-pcm", MeetingAudioProcessor);
