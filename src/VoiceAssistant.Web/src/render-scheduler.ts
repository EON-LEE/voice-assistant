export interface RenderClock {
  set(callback: () => void, delay: number): unknown;
  clear(handle: unknown): void;
}
export interface RenderWindow {
  setTimeout(callback: () => void, delay: number): number;
  clearTimeout(id: number): void;
  requestAnimationFrame?(callback: FrameRequestCallback): number;
  cancelAnimationFrame?(id: number): void;
}
interface WindowTask { window: RenderWindow; timer: number; fallback?: number; frame?: number; cancelled: boolean }
/** Timers/rAF are registered with the visible owner window, never the hidden opener when floating. */
export class OwnerWindowClock implements RenderClock {
  constructor(private readonly owner: () => RenderWindow) {}
  set(callback: () => void, delay: number): WindowTask {
    const win = this.owner();
    const task: WindowTask = { window: win, timer: 0, cancelled: false };
    const finish = (): void => {
      if (task.cancelled) return; this.clear(task); callback();
    };
    task.timer = win.setTimeout(() => {
      if (task.cancelled) return;
      task.fallback = win.setTimeout(finish, 16);
      if (win.requestAnimationFrame) task.frame = win.requestAnimationFrame(finish);
    }, delay);
    return task;
  }
  clear(handle: unknown): void {
    const task = handle as WindowTask; task.cancelled = true; task.window.clearTimeout(task.timer);
    if (task.fallback !== undefined) task.window.clearTimeout(task.fallback);
    if (task.frame !== undefined) task.window.cancelAnimationFrame?.(task.frame);
  }
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
