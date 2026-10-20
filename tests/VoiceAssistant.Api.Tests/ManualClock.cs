namespace VoiceAssistant.Api.Tests;

/// <summary>
/// A TimeProvider whose timestamps and timers move only when a test calls <see cref="Advance"/>, so timing logic is
/// exercised deterministically instead of with real sleeps. Due timer callbacks run inline on the advancing thread.
/// </summary>
internal sealed class ManualClock : TimeProvider
{
    private static readonly DateTimeOffset Origin = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly object gate = new();
    private readonly List<ManualTimer> timers = [];
    private TaskCompletionSource created = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long now;
    private int timersCreated;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() { lock (gate) return now; }
    public override DateTimeOffset GetUtcNow() { lock (gate) return Origin.AddTicks(now); }

    public int TimersCreated { get { lock (gate) return timersCreated; } }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        TaskCompletionSource signal;
        lock (gate)
        {
            timer.Arm(dueTime, period);
            timers.Add(timer);
            timersCreated++;
            signal = created;
            created = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        signal.TrySetResult();
        return timer;
    }

    /// <summary>Waits, bounded by real time, until at least <paramref name="count"/> timers were created on this clock.</summary>
    public async Task WaitForTimersAsync(int count, TimeSpan? timeout = null)
    {
        using var cancel = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(5));
        while (true)
        {
            Task next;
            lock (gate)
            {
                if (timersCreated >= count) return;
                next = created.Task;
            }
            await next.WaitAsync(cancel.Token);
        }
    }

    public void Advance(TimeSpan by)
    {
        if (by < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(by));
        var due = new List<ManualTimer>();
        lock (gate)
        {
            now += by.Ticks;
            foreach (var timer in timers.Where(timer => timer.DueAt is { } at && at <= now).OrderBy(timer => timer.DueAt))
            {
                due.Add(timer);
                timer.Fired();
            }
            timers.RemoveAll(timer => timer.DueAt is null);
        }
        foreach (var timer in due) timer.Invoke();
    }

    private sealed class ManualTimer(ManualClock owner, TimerCallback callback, object? state) : ITimer
    {
        private long period;
        internal long? DueAt { get; private set; }

        // Caller holds the owner's gate.
        internal void Arm(TimeSpan dueTime, TimeSpan period)
        {
            DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : owner.now + Math.Max(0, dueTime.Ticks);
            this.period = period == Timeout.InfiniteTimeSpan || period <= TimeSpan.Zero ? 0 : period.Ticks;
        }

        // Caller holds the owner's gate. A periodic timer re-arms; a one-shot timer disarms.
        internal void Fired() => DueAt = period > 0 ? DueAt + period : null;

        internal void Invoke() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner.gate)
            {
                Arm(dueTime, period);
                if (DueAt is not null && !owner.timers.Contains(this)) owner.timers.Add(this);
            }
            return true;
        }

        public void Dispose()
        {
            lock (owner.gate) DueAt = null;
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
