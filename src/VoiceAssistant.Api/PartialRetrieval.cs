using System.Threading.Channels;

namespace VoiceAssistant.Api;

internal sealed record RetrievalOutcome(Grounding? Grounding, ProviderException? Failure)
{
    internal Grounding RequireGrounding() => Grounding ?? throw Failure ??
        new ProviderException("grounding_unavailable", "Grounding is unavailable. Please retry.");
}

// One session/identity, one worker and one candidate slot. Never shared between callers.
internal sealed class PartialRetrieval : IAsyncDisposable
{
    private readonly IMeetingProvider provider;
    private readonly string objectId;
    private readonly SessionOptions options;
    private readonly object gate = new();
    private readonly Channel<bool> changed = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });
    private readonly CancellationTokenSource lifetime;
    private readonly Task worker;
    private Candidate? candidate;
    private string? turn;
    private int starts;
    private bool closed;

    private sealed class Candidate(string turnId, string text, string query, CancellationToken lifetime)
    {
        internal string TurnId { get; } = turnId;
        internal string Text { get; } = text;
        internal string Query { get; } = query;
        internal CancellationTokenSource Cancellation { get; } = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        internal TaskCompletionSource<RetrievalOutcome> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Started { get; set; }
        internal bool Processing { get; set; }
        internal bool Finished { get; set; }
        internal bool Cancelled { get; set; }
        internal bool Selected { get; set; }
        internal void Cancel()
        {
            Cancelled = true;
            if (!Finished) Cancellation.Cancel();
            if (!Processing && !Finished)
            {
                Finished = true;
                Cancellation.Dispose();
                Result.TrySetResult(new(null, new("grounding_unavailable", "Prefetched grounding was cancelled.")));
            }
        }
    }

    internal PartialRetrieval(IMeetingProvider provider, string objectId, SessionOptions options, CancellationToken cancellation)
    {
        this.provider = provider;
        this.objectId = objectId;
        this.options = options;
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        worker = Task.Run(WorkAsync);
    }

    internal void Update(Transcript partial, IReadOnlyList<ConversationTurn>? history = null)
    {
        var text = ResponseRouting.Normalize(partial.Text);
        var query = RetrievalQuery.Build(partial.Text, history);
        lock (gate)
        {
            if (closed) return;
            if (turn != partial.TurnId)
            {
                candidate?.Cancel();
                turn = partial.TurnId;
                starts = 0;
            }
            if (candidate is { Cancelled: false } same && same.TurnId == partial.TurnId && same.Text == text && same.Query == query) return;
            candidate?.Cancel();
            candidate = null;
            if (starts >= 3 || !ResponseRouting.Substantive(text) || ResponseRouting.Select(partial.Text, options, history) != "knowledge") return;
            candidate = new(partial.TurnId, text, query, lifetime.Token);
            changed.Writer.TryWrite(true);
        }
    }

    internal Task<RetrievalOutcome>? Take(string turnId, string finalText, string route, IReadOnlyList<ConversationTurn>? history = null)
    {
        lock (gate)
        {
            if (!closed && route == "knowledge" && candidate is { Started: true, Cancelled: false } match &&
                match.TurnId == turnId && match.Text == ResponseRouting.Normalize(finalText) &&
                match.Query == RetrievalQuery.Build(finalText, history))
            {
                if (match.Selected && match.Result.Task.IsCompletedSuccessfully && match.Result.Task.Result.Failure is not null)
                {
                    match.Cancel();
                    return null;
                }
                match.Selected = true;
                return match.Result.Task;
            }
            candidate?.Cancel();
            return null;
        }
    }

    internal void Cancel()
    {
        lock (gate) candidate?.Cancel();
    }

    private async Task WorkAsync()
    {
        try
        {
            await foreach (var _ in changed.Reader.ReadAllAsync(lifetime.Token))
            {
                Candidate? current;
                lock (gate)
                {
                    current = candidate;
                    if (current is null || current.Cancelled || current.Finished) continue;
                    current.Processing = true;
                }
                try
                {
                    await Task.Delay(250, current.Cancellation.Token);
                    lock (gate)
                    {
                        if (current != candidate || current.Cancelled || starts >= 3) continue;
                        starts++;
                        current.Started = true;
                        current.Cancellation.CancelAfter(TimeSpan.FromSeconds(10));
                    }
                    // Keep a noncooperative cancelled provider occupying this single worker; don't spawn replacements.
                    var operation = RetrieveAsync(current.Query, current.Cancellation.Token);
                    var outcome = await operation.WaitAsync(lifetime.Token);
                    current.Result.TrySetResult(outcome);
                }
                catch (OperationCanceledException)
                {
                    current.Result.TrySetResult(new(null, new("grounding_unavailable", "Prefetched grounding was cancelled or timed out.")));
                }
                finally
                {
                    lock (gate)
                    {
                        current.Finished = true;
                        current.Cancellation.Dispose();
                    }
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }

    private async Task<RetrievalOutcome> RetrieveAsync(string query, CancellationToken cancellation)
    {
        try
        {
            var grounding = await MeetingMetrics.MeasureRetrievalAsync(provider, query, objectId, cancellation);
            cancellation.ThrowIfCancellationRequested();
            return new(grounding, null);
        }
        catch (Exception)
        {
            return new(null, new("grounding_unavailable", "Grounding is unavailable. Please retry."));
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (gate)
        {
            closed = true;
            candidate?.Cancel();
        }
        lifetime.Cancel();
        changed.Writer.TryComplete();
        await worker;
        lifetime.Dispose();
    }
}
