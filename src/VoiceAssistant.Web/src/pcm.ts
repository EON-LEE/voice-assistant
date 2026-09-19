/** Fixed-memory streaming downmix, low-pass FIR and fractional-phase resampler. */
export class PcmConverter {
  private readonly taps = 127;
  private readonly kernel = new Float64Array(this.taps);
  private readonly history = new Float64Array(this.taps);
  private position = 0;
  private phase = 0;
  private previous = 0;
  constructor(private readonly rate: number) {
    if (!Number.isInteger(rate) || rate < 16000 || rate > 192000) throw new Error("Unsupported audio sample rate.");
    const cutoff = Math.min(7200 / rate, .45);
    let sum = 0;
    for (let i = 0; i < this.taps; i++) {
      const n = i - (this.taps - 1) / 2;
      const sinc = n === 0 ? 2 * cutoff : Math.sin(2 * Math.PI * cutoff * n) / (Math.PI * n);
      this.kernel[i] = sinc * (.42 - .5 * Math.cos(2 * Math.PI * i / (this.taps - 1)) + .08 * Math.cos(4 * Math.PI * i / (this.taps - 1)));
      sum += this.kernel[i]!;
    }
    for (let i = 0; i < this.taps; i++) this.kernel[i] = this.kernel[i]! / sum;
  }
  reset(): void { this.history.fill(0); this.position = this.phase = this.previous = 0; }
  process(channels: readonly Float32Array[], emit: (sample: number) => void): void {
    if (!channels.length) return;
    if (channels.length > 32) throw new Error("Unsupported audio input: at most 32 channels are allowed.");
    const length = channels[0]!.length;
    for (const channel of channels) if (channel.length !== length) throw new Error("Mismatched audio channels.");
    for (let frame = 0; frame < length; frame++) {
      let mono = 0;
      for (const channel of channels) {
        const sample = channel[frame]!;
        mono += Number.isFinite(sample) ? Math.max(-1, Math.min(1, sample)) : 0;
      }
      this.history[this.position] = mono / channels.length;
      let filtered = 0;
      for (let i = 0; i < this.taps; i++) filtered += this.kernel[i]! * this.history[(this.position - i + this.taps) % this.taps]!;
      this.position = (this.position + 1) % this.taps;
      this.phase += 16000;
      if (this.phase >= this.rate) {
        this.phase -= this.rate;
        const value = this.previous + (1 - this.phase / 16000) * (filtered - this.previous);
        emit(Math.max(-32768, Math.min(32767, Math.round(value * 32768))));
      }
      this.previous = filtered;
    }
  }
}
