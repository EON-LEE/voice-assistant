using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using OpenAI.Embeddings;

namespace VoiceAssistant.Api.Knowledge;

public sealed class AzureKnowledgeStore(SearchClient? search, EmbeddingClient? embeddings, ILogger<AzureKnowledgeStore> logger) : IKnowledgeStore
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string Owner, string Id), string[]> recentKeys = new();
    private sealed record OwnedChunk(string Key, KnowledgeDocument Document);

    public async Task<IReadOnlyList<KnowledgeDocument>> ListAsync(string owner, CancellationToken cancellation)
    {
        var chunks = await ReadOwnedAsync(owner, cancellation);
        return chunks.GroupBy(chunk => chunk.Document.Id).Select(group => group.First().Document with
        {
            Chunks = group.Count(), UpdatedAt = group.Max(chunk => chunk.Document.UpdatedAt)
        }).OrderByDescending(document => document.UpdatedAt).ToArray();
    }

    private async Task<List<OwnedChunk>> ReadOwnedAsync(string owner, CancellationToken cancellation)
    {
        if (search is null) throw KnowledgeException.Unavailable();
        var options = new SearchOptions { Filter = AzureMeetingProvider.AclFilter(KnowledgeIds.Owner(owner)), Size = 1000 };
        foreach (var field in new[] { "id", "title", "url", "updatedAt" }) options.Select.Add(field);
        var results = await search.SearchAsync<SearchDocument>("*", options, cancellation);
        var chunks = new List<OwnedChunk>();
        await foreach (var result in results.Value.GetResultsAsync().WithCancellation(cancellation))
        {
            var value = result.Document;
            if (!value.TryGetValue("id", out var keyValue) || keyValue is not string key) throw KnowledgeException.Unavailable();
            var id = KnowledgeIds.DocumentOf(key);
            if (id is null) continue;
            if (!value.TryGetValue("title", out var titleValue) || titleValue is not string title ||
                !value.TryGetValue("url", out var url) || !Equals(url, $"https://my-materials.invalid/{id}") ||
                !value.TryGetValue("updatedAt", out var time) || !TryDate(time, out var updatedAt))
                throw KnowledgeException.Unavailable();
            chunks.Add(new(key, new(id, title, 0, updatedAt)));
        }
        return chunks;
    }

    private static bool TryDate(object? value, out DateTimeOffset date)
    {
        if (value is DateTimeOffset offset) { date = offset; return true; }
        if (value is DateTime time) { date = new DateTimeOffset(time.ToUniversalTime()); return true; }
        return DateTimeOffset.TryParse(value as string, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out date);
    }

    public async Task UploadAsync(string owner, KnowledgeDocument document, IReadOnlyList<KnowledgeChunk> chunks, CancellationToken cancellation)
    {
        owner = KnowledgeIds.Owner(owner);
        if (search is null || embeddings is null) throw KnowledgeException.Unavailable();
        if (!KnowledgeIds.ValidDocument(document.Id) || chunks.Count == 0 ||
            chunks.Any(chunk => KnowledgeIds.DocumentOf(chunk.Id) != document.Id))
            throw KnowledgeException.Invalid();
        var values = new List<SearchDocument>(chunks.Count);
        // Prepare all embeddings before indexing anything, so embedding failure leaves no partial document.
        foreach (var batch in chunks.Chunk(16))
        {
            var response = await embeddings.GenerateEmbeddingsAsync(batch.Select(chunk => chunk.Content),
                new EmbeddingGenerationOptions { Dimensions = 1536 }, cancellation);
            if (response.Value.Count != batch.Length) throw KnowledgeException.Unavailable();
            for (var i = 0; i < batch.Length; i++)
            {
                var vector = response.Value[i].ToFloats().ToArray();
                if (vector.Length != 1536 || vector.Any(value => !float.IsFinite(value))) throw KnowledgeException.Unavailable();
                values.Add(new()
                {
                    ["id"] = batch[i].Id, ["title"] = document.Title, ["content"] = batch[i].Content,
                    ["url"] = $"https://my-materials.invalid/{document.Id}", ["updatedAt"] = document.UpdatedAt,
                    ["allowedPrincipalIds"] = new[] { owner }, ["contentVector"] = vector
                });
            }
        }
        try
        {
            foreach (var batch in values.Chunk(16))
            {
                var response = await search.IndexDocumentsAsync(IndexDocumentsBatch.Upload(batch),
                    new IndexDocumentsOptions { ThrowOnAnyError = false }, cancellation);
                CheckResults(response.Value, batch.Select(value => (string)value["id"]));
            }
            recentKeys[(owner, document.Id)] = chunks.Select(chunk => chunk.Id).ToArray();
        }
        catch (Exception)
        {
            // Delete every generated key, including a batch whose response was lost/partially accepted.
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try { await DeleteKeysAsync(chunks.Select(chunk => chunk.Id).ToArray(), cleanup.Token, rollback: true); }
            catch (Exception)
            {
                logger.LogError("Materials rollback could not be confirmed. No document content or identifiers logged.");
                throw KnowledgeException.Unavailable();
            }
            throw KnowledgeException.Unavailable();
        }
    }

    public async Task DeleteAsync(string owner, string documentId, CancellationToken cancellation)
    {
        if (!KnowledgeIds.ValidDocument(documentId)) throw KnowledgeException.Invalid();
        var chunks = await ReadOwnedAsync(owner, cancellation);
        var keys = chunks.Where(chunk => chunk.Document.Id == documentId).Select(chunk => chunk.Key);
        if (recentKeys.TryGetValue((KnowledgeIds.Owner(owner), documentId), out var recent)) keys = keys.Concat(recent);
        await DeleteKeysAsync(keys.Distinct(StringComparer.Ordinal).ToArray(), cancellation);
        recentKeys.TryRemove((KnowledgeIds.Owner(owner), documentId), out _);
    }

    private async Task DeleteKeysAsync(string[] keys, CancellationToken cancellation, bool rollback = false)
    {
        var failed = false;
        foreach (var batch in keys.Chunk(100))
        {
            var deleted = false;
            for (var attempt = 0; attempt < (rollback ? 2 : 1) && !deleted; attempt++)
            {
                try
                {
                    var response = await search!.IndexDocumentsAsync(IndexDocumentsBatch.Delete("id", batch),
                        new IndexDocumentsOptions { ThrowOnAnyError = false }, cancellation);
                    CheckResults(response.Value, batch);
                    deleted = true;
                }
                catch (Exception) when (rollback && !cancellation.IsCancellationRequested)
                {
                    // Continue to every generated batch even if another batch cannot be confirmed deleted.
                }
            }
            failed |= !deleted;
        }
        if (failed) throw KnowledgeException.Unavailable();
    }

    private static void CheckResults(IndexDocumentsResult response, IEnumerable<string> expectedKeys)
    {
        var keys = expectedKeys.ToHashSet(StringComparer.Ordinal);
        if (response.Results.Count != keys.Count || response.Results.Any(result => !result.Succeeded || !keys.Remove(result.Key)) || keys.Count != 0)
            throw KnowledgeException.Unavailable();
    }
}
