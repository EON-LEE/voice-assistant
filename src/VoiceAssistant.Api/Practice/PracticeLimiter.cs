namespace VoiceAssistant.Api.Practice;

public sealed class PracticeLimiter(TimeProvider clock)
{
    private readonly object gate = new();
    private readonly Dictionary<(string Owner, bool Speak), Bucket> buckets = new();
    private int admissions;
    private sealed class Bucket
    {
        internal int Active;
        internal Queue<long> Started { get; } = new();
    }
    public IDisposable Enter(string owner, bool speak)
    {
        lock (gate)
        {
            var now = clock.GetTimestamp();
            if (++admissions % 64 == 0)
                foreach (var pair in buckets.ToArray())
                {
                    Prune(pair.Value, now);
                    if (pair.Value.Active == 0 && pair.Value.Started.Count == 0) buckets.Remove(pair.Key);
                }
            if (!buckets.TryGetValue((owner, speak), out var bucket)) buckets.Add((owner, speak), bucket = new());
            Prune(bucket, now);
            if (bucket.Active >= (speak ? 3 : 8) || bucket.Started.Count >= (speak ? 30 : 60)) throw PracticeException.Busy();
            bucket.Started.Enqueue(now);
            bucket.Active++;
            return new Lease(() => { lock (gate) bucket.Active--; });
        }
    }
    private void Prune(Bucket bucket, long now)
    {
        while (bucket.Started.TryPeek(out var start) && clock.GetElapsedTime(start, now) >= TimeSpan.FromMinutes(1)) bucket.Started.Dequeue();
    }
    private sealed class Lease(Action release) : IDisposable
    {
        private Action? release = release;
        public void Dispose() => Interlocked.Exchange(ref release, null)?.Invoke();
    }
}
