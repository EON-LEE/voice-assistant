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
  private failed = false;
  private epoch = 0;
  constructor() {
    super();
    this.port.onmessage = ({ data }: MessageEvent<{ type: string; buffer?: ArrayBuffer; paused?: boolean; epoch?: number }>) => {
      if (data.type === "buffer" && data.buffer?.byteLength === audioFrameBytes &&
          this.available.length + (this.view ? 1 : 0) < audioBufferCount) this.available.push(data.buffer);
      if (data.type === "pause") {
        this.paused = !!data.paused; this.epoch = data.epoch ?? 0; this.offset = 0; this.converter.reset();
      }
    };
  }
  private readonly emit = (sample: number): void => {
    if (this.failed) return;
    if (!this.view) {
      const buffer = this.available.pop();
      if (!buffer) { this.failed = true; this.port.postMessage({ type: "error", message: "Audio processing cannot keep up (one second of buffered audio). Session stopped without silently dropping speech." }); return; }
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
    if (!this.paused && !this.failed && inputs[0]?.length) this.converter.process(inputs[0], this.emit);
    return true;
  }
}
registerProcessor("meeting-pcm", MeetingAudioProcessor);
