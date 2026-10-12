namespace VoiceAssistant.Api.Knowledge;

public sealed class KnowledgeService(IKnowledgeStore store, TimeProvider clock)
{
    private readonly object gate = new();
    private readonly Dictionary<string, OwnerState> owners = new(StringComparer.Ordinal);
    private sealed class OwnerState
    {
        internal int Uploads;
        internal SemaphoreSlim Mutation { get; } = new(1, 1);
        // Successful local writes remain counted until Search listing catches up.
        internal Dictionary<string, KnowledgeDocument> Recent { get; } = new(StringComparer.Ordinal);
        internal HashSet<string> Deleted { get; } = new(StringComparer.Ordinal);
    }

    private OwnerState State(string owner)
    {
        owner = KnowledgeIds.Owner(owner);
        lock (gate)
        {
            if (!owners.TryGetValue(owner, out var state)) owners.Add(owner, state = new());
            return state;
        }
    }

    public IDisposable AdmitUpload(string owner)
    {
        var state = State(owner);
        lock (gate)
        {
            if (state.Uploads >= 2) throw KnowledgeException.Busy();
            state.Uploads++;
        }
        return new Admission(() => { lock (gate) state.Uploads--; });
    }

    public async Task<IReadOnlyList<KnowledgeDocument>> ListAsync(string owner, CancellationToken cancellation)
    {
        var state = State(owner);
        await state.Mutation.WaitAsync(cancellation);
        try { return await ListCurrentAsync(owner, state, cancellation); }
        finally { state.Mutation.Release(); }
    }

    private async Task<IReadOnlyList<KnowledgeDocument>> ListCurrentAsync(string owner, OwnerState state, CancellationToken cancellation)
    {
        var listed = (await store.ListAsync(owner, cancellation)).ToDictionary(document => document.Id, StringComparer.Ordinal);
        foreach (var id in state.Deleted.ToArray())
        {
            if (!listed.Remove(id)) state.Deleted.Remove(id);
        }
        foreach (var document in state.Recent.Values.ToArray())
        {
            if (listed.TryGetValue(document.Id, out var visible) && visible.Chunks == document.Chunks) state.Recent.Remove(document.Id);
            listed[document.Id] = document;
        }
        return listed.Values.OrderByDescending(document => document.UpdatedAt).ToArray();
    }

    public async Task<KnowledgeUpload> UploadAsync(string owner, KnowledgeInput input, CancellationToken cancellation)
    {
        var title = KnowledgeRequestReader.Title(input.Title);
        var id = Guid.NewGuid().ToString("N");
        var chunks = KnowledgeChunker.Split(id, title, input.Text);
        var state = State(owner);
        await state.Mutation.WaitAsync(cancellation);
        try
        {
            var current = await ListCurrentAsync(owner, state, cancellation);
            if (current.Count >= KnowledgeLimits.MaxDocuments || current.Sum(document => document.Chunks) + chunks.Count > KnowledgeLimits.MaxChunks)
                throw new KnowledgeException(409, "quota_exceeded", "Your materials quota is full. Delete some documents before uploading more.");
            var document = new KnowledgeDocument(id, title, chunks.Count, clock.GetUtcNow());
            await store.UploadAsync(owner, document, chunks, cancellation);
            state.Recent[id] = document;
            return new(id, title, chunks.Count, input.Text.Length, document.UpdatedAt);
        }
        finally { state.Mutation.Release(); }
    }

    public async Task DeleteAsync(string owner, string id, CancellationToken cancellation)
    {
        if (!KnowledgeIds.ValidDocument(id)) throw KnowledgeException.Invalid();
        var state = State(owner);
        await state.Mutation.WaitAsync(cancellation);
        try
        {
            var known = (await ListCurrentAsync(owner, state, cancellation)).Any(document => document.Id == id);
            await store.DeleteAsync(owner, id, cancellation);
            state.Recent.Remove(id);
            if (known) state.Deleted.Add(id);
        }
        finally { state.Mutation.Release(); }
    }

    private sealed class Admission(Action release) : IDisposable
    {
        private Action? action = release;
        public void Dispose() => Interlocked.Exchange(ref action, null)?.Invoke();
    }
}
