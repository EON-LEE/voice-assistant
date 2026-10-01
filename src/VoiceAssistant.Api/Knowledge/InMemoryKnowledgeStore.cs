namespace VoiceAssistant.Api.Knowledge;

public sealed class InMemoryKnowledgeStore : IKnowledgeStore
{
    private readonly object gate = new();
    private readonly Dictionary<(string Owner, string Id), (KnowledgeDocument Document, KnowledgeChunk[] Chunks)> documents = new();

    public Task<IReadOnlyList<KnowledgeDocument>> ListAsync(string owner, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        owner = KnowledgeIds.Owner(owner);
        lock (gate)
            return Task.FromResult<IReadOnlyList<KnowledgeDocument>>(documents.Where(item => item.Key.Owner == owner)
                .Select(item => item.Value.Document).OrderByDescending(document => document.UpdatedAt).ToArray());
    }

    public Task UploadAsync(string owner, KnowledgeDocument document, IReadOnlyList<KnowledgeChunk> chunks, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        owner = KnowledgeIds.Owner(owner);
        lock (gate) documents.Add((owner, document.Id), (document, chunks.ToArray()));
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string owner, string documentId, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        owner = KnowledgeIds.Owner(owner);
        if (!KnowledgeIds.ValidDocument(documentId)) throw KnowledgeException.Invalid();
        lock (gate) documents.Remove((owner, documentId));
        return Task.CompletedTask;
    }
}
