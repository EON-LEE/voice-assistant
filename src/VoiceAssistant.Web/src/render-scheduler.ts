export interface RenderClock {
  set(callback: () => void, delay: number): unknown;
  clear(handle: unknown): void;
}

/** One pending render regardless of event volume; controls and final events flush immediately. */
export class RenderScheduler {
  private pending: unknown = undefined;
  constructor(private readonly render: () => void, private readonly clock: RenderClock = {
    set: (callback, delay) => setTimeout(callback, delay),
    clear: handle => clearTimeout(handle as ReturnType<typeof setTimeout>),
  }) {}
  schedule(): void {
    if (this.pending !== undefined) return;
    this.pending = this.clock.set(() => { this.pending = undefined; this.render(); }, 50);
  }
  flush(): void { this.cancel(); this.render(); }
  cancel(): void {
    if (this.pending === undefined) return;
    this.clock.clear(this.pending);
    this.pending = undefined;
  }
}
