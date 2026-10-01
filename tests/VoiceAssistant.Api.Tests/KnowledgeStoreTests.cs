using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using Azure;
using Azure.AI.OpenAI;
using Azure.Core.Pipeline;
using Azure.Search.Documents;
using Microsoft.Extensions.Logging.Abstractions;
using VoiceAssistant.Api.Knowledge;
using Xunit;

namespace VoiceAssistant.Api.Tests;

public sealed class KnowledgeStoreTests
{
    private const string OwnerA = "e59048e0-9a10-433e-8e0e-beb279c0c234";
    private const string OwnerB = "11111111-1111-1111-1111-111111111111";
    private const string DocumentId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void IdParsingNeverAdmitsOperatorPrefixesSuffixesOrTrailingNewlines()
    {
        Assert.True(KnowledgeIds.ValidDocument(DocumentId));
        Assert.False(KnowledgeIds.ValidDocument(DocumentId + "\n"));
        Assert.False(KnowledgeIds.ValidDocument(DocumentId.ToUpperInvariant()));
        Assert.Null(KnowledgeIds.DocumentOf($"kb-{DocumentId}-0000\n"));
        Assert.Null(KnowledgeIds.DocumentOf($"kb-{DocumentId}-0000-extra"));
        Assert.Null(KnowledgeIds.DocumentOf($"operator-{DocumentId}-0000"));
        Assert.Equal(DocumentId, KnowledgeIds.DocumentOf($"kb-{DocumentId}-0000"));
    }

    [Fact]
    public async Task AzureUploadUsesBoundedEmbeddingBatchesAndExistingSchemaAcl()
    {
        using var handler = new Transport();
        using var http = new HttpClient(handler);
        var store = Store(http);
        var chunks = Chunks(35);
        await store.UploadAsync(OwnerA, Document(35), chunks, CancellationToken.None);
        Assert.Equal(new[] { 16, 16, 3 }, handler.EmbeddingBatchSizes);
        Assert.Equal(3, handler.UploadCalls);
        Assert.Equal(35, handler.Indexed.Count);
        foreach (var value in handler.Indexed)
        {
            Assert.Equal(8, value.EnumerateObject().Count());
            Assert.Equal(OwnerA, value.GetProperty("allowedPrincipalIds")[0].GetString());
            Assert.Equal(1, value.GetProperty("allowedPrincipalIds").GetArrayLength());
            Assert.Equal(1536, value.GetProperty("contentVector").GetArrayLength());
            Assert.Equal($"https://my-materials.invalid/{DocumentId}", value.GetProperty("url").GetString());
            Assert.StartsWith("kb-" + DocumentId + "-", value.GetProperty("id").GetString());
            Assert.Equal("original.txt", value.GetProperty("title").GetString());
        }
    }

    [Fact]
    public async Task EmbeddingFailurePrecedesEveryIndexWrite()
    {
        using var handler = new Transport { FailEmbeddingBatch = 2 };
        using var http = new HttpClient(handler);
        await Assert.ThrowsAnyAsync<Exception>(() => Store(http).UploadAsync(OwnerA, Document(20), Chunks(20), CancellationToken.None));
        Assert.Equal(0, handler.UploadCalls);
        Assert.Empty(handler.DeletedKeys);
    }

    [Theory]
    [InlineData(1535)]
    [InlineData(0)]
    public async Task InvalidEmbeddingShapeNeverIndexes(int dimensions)
    {
        using var handler = new Transport { VectorDimensions = dimensions };
        using var http = new HttpClient(handler);
        await Assert.ThrowsAnyAsync<Exception>(() => Store(http).UploadAsync(OwnerA, Document(1), Chunks(1), CancellationToken.None));
        Assert.Empty(handler.Indexed);
    }

    [Fact]
    public async Task PartialIndexFailureRollsBackEveryGeneratedKeyIncludingFailedBatch()
    {
        using var handler = new Transport { PartialUploadBatch = 2 };
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<KnowledgeException>(() => Store(http).UploadAsync(OwnerA, Document(35), Chunks(35), CancellationToken.None));
        Assert.Equal(503, error.Status);
        Assert.Equal(2, handler.UploadCalls);
        Assert.Equal(Chunks(35).Select(chunk => chunk.Id).Order(), handler.DeletedKeys.Order());
        Assert.DoesNotContain("private-cloud-body", error.Message);
    }

    [Fact]
    public async Task LostIndexResponseAlsoRollsBackAndFailedRollbackNeverReportsSuccess()
    {
        using var handler = new Transport { FailUpload = true, FailDelete = true };
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<KnowledgeException>(() => Store(http).UploadAsync(OwnerA, Document(1), Chunks(1), CancellationToken.None));
        Assert.Equal("knowledge_unavailable", error.Code);
        Assert.NotEmpty(handler.DeletedKeys);
    }

    [Fact]
    public async Task RequestCancellationAfterWriteUsesIndependentRollbackToken()
    {
        using var cancellation = new CancellationTokenSource();
        using var handler = new Transport { CancelAfterUpload = cancellation.Cancel };
        using var http = new HttpClient(handler);
        var failure = await Assert.ThrowsAsync<KnowledgeException>(() => Store(http).UploadAsync(OwnerA, Document(17), Chunks(17), cancellation.Token));
        Assert.Equal(503, failure.Status);
        Assert.Equal(17, handler.DeletedKeys.Count);
        Assert.False(handler.DeleteTokenCancelled);
    }

    [Fact]
    public async Task ListingAndDeletionPageWithAclAndNeverSelectVectorsOrOperatorIds()
    {
        using var handler = new Transport { Listing = true };
        using var http = new HttpClient(handler);
        var store = Store(http);
        var listed = await store.ListAsync(OwnerA, CancellationToken.None);
        Assert.Single(listed);
        Assert.Equal(DocumentId, listed[0].Id);
        Assert.Equal(2, listed[0].Chunks);
        Assert.Equal(2, handler.SearchBodies.Count);
        foreach (var body in handler.SearchBodies)
        {
            Assert.Equal(AzureMeetingProvider.AclFilter(OwnerA), body.GetProperty("filter").GetString());
            Assert.Equal("id,title,url,updatedAt", body.GetProperty("select").GetString());
            Assert.DoesNotContain("contentVector", body.ToString());
        }
        await store.DeleteAsync(OwnerB, DocumentId, CancellationToken.None);
        Assert.Empty(handler.DeletedKeys);
        await store.DeleteAsync(OwnerA, new string('b', 32), CancellationToken.None);
        Assert.Empty(handler.DeletedKeys);
        await store.DeleteAsync(OwnerA, DocumentId, CancellationToken.None);
        Assert.Equal(2, handler.DeletedKeys.Distinct().Count());
        Assert.DoesNotContain("operator-doc", handler.DeletedKeys);
        Assert.DoesNotContain("kb-" + DocumentId + "-0000-extra", handler.DeletedKeys);
        Assert.Equal(400, (await Assert.ThrowsAsync<KnowledgeException>(() => store.DeleteAsync(OwnerA, "operator-doc", CancellationToken.None))).Status);
    }

    [Fact]
    public async Task ImmediateDeleteCanRemoveLocallyWrittenChunksBeforeSearchRefreshWithoutCrossUserAccess()
    {
        using var handler = new Transport();
        using var http = new HttpClient(handler);
        var store = Store(http);
        await store.UploadAsync(OwnerA, Document(2), Chunks(2), CancellationToken.None);
        await store.DeleteAsync(OwnerB, DocumentId, CancellationToken.None);
        Assert.Empty(handler.DeletedKeys);
        await store.DeleteAsync(OwnerA, DocumentId, CancellationToken.None);
        Assert.Equal(2, handler.DeletedKeys.Count);
    }

    [Fact]
    public async Task FakeStoreSeparatesOwnersAndDeleteIsIdempotent()
    {
        var store = new InMemoryKnowledgeStore();
        await store.UploadAsync(OwnerA, Document(1), Chunks(1), CancellationToken.None);
        Assert.Empty(await store.ListAsync(OwnerB, CancellationToken.None));
        await store.DeleteAsync(OwnerB, DocumentId, CancellationToken.None);
        Assert.Single(await store.ListAsync(OwnerA, CancellationToken.None));
        await store.DeleteAsync(OwnerA, DocumentId, CancellationToken.None);
        await store.DeleteAsync(OwnerA, DocumentId, CancellationToken.None);
        Assert.Empty(await store.ListAsync(OwnerA, CancellationToken.None));
    }

    [Fact]
    public async Task ServiceEnforcesDocumentAndChunkQuotasAndTracksUnrefreshedSuccess()
    {
        foreach (var counts in new[] { (Documents: 300, Chunks: 300), (Documents: 1, Chunks: 5000) })
        {
            var store = new QuotaStore(counts.Documents, counts.Chunks);
            var service = new KnowledgeService(store, TimeProvider.System);
            var error = await Assert.ThrowsAsync<KnowledgeException>(() =>
                service.UploadAsync(OwnerA, new("original.txt", "Original note."), CancellationToken.None));
            Assert.Equal(409, error.Status);
            Assert.Equal(0, store.Uploads);
        }
        var lagging = new QuotaStore(299, 299);
        var tracked = new KnowledgeService(lagging, TimeProvider.System);
        await tracked.UploadAsync(OwnerA, new("original.txt", "Original note."), CancellationToken.None);
        Assert.Equal(300, (await tracked.ListAsync(OwnerA, CancellationToken.None)).Count);
        Assert.Equal(409, (await Assert.ThrowsAsync<KnowledgeException>(() =>
            tracked.UploadAsync(OwnerA, new("another.txt", "Another note."), CancellationToken.None))).Status);
    }

    [Fact]
    public void AdmissionIsTwoPerUserIndependentAndReleasesExactlyOnce()
    {
        var service = new KnowledgeService(new InMemoryKnowledgeStore(), TimeProvider.System);
        using var first = service.AdmitUpload(OwnerA);
        using var second = service.AdmitUpload(OwnerA);
        Assert.Equal(429, Assert.Throws<KnowledgeException>(() => service.AdmitUpload(OwnerA)).Status);
        using var other = service.AdmitUpload(OwnerB);
        first.Dispose();
        first.Dispose();
        using var replacement = service.AdmitUpload(OwnerA);
        Assert.Equal(429, Assert.Throws<KnowledgeException>(() => service.AdmitUpload(OwnerA)).Status);
    }

    private static KnowledgeDocument Document(int chunks) => new(DocumentId, "original.txt", chunks, DateTimeOffset.Parse("2026-10-01T00:00:00Z"));
    private static KnowledgeChunk[] Chunks(int count) => Enumerable.Range(0, count).Select(i => new KnowledgeChunk($"kb-{DocumentId}-{i:D4}", "original.txt\nOriginal chunk " + i)).ToArray();
    private static AzureKnowledgeStore Store(HttpClient http)
    {
        var openAI = new AzureOpenAIClient(new Uri("https://example.openai.azure.com"), new ApiKeyCredential("test-only"),
            new AzureOpenAIClientOptions { Transport = new HttpClientPipelineTransport(http) });
        var options = new SearchClientOptions { Transport = new HttpClientTransport(http) };
        options.Retry.MaxRetries = 0;
        var search = new SearchClient(new Uri("https://example.search.windows.net"), "meeting", new AzureKeyCredential("test-only"), options);
        return new(search, openAI.GetEmbeddingClient("embedding"), NullLogger<AzureKnowledgeStore>.Instance);
    }

    private sealed class QuotaStore(int documents, int chunks) : IKnowledgeStore
    {
        public int Uploads;
        public Task<IReadOnlyList<KnowledgeDocument>> ListAsync(string owner, CancellationToken cancellation) =>
            Task.FromResult<IReadOnlyList<KnowledgeDocument>>(Enumerable.Range(0, documents)
                .Select(i => new KnowledgeDocument(i.ToString("x32"), "Original", i == 0 ? chunks - documents + 1 : 1, DateTimeOffset.UtcNow)).ToArray());
        public Task UploadAsync(string owner, KnowledgeDocument document, IReadOnlyList<KnowledgeChunk> chunks, CancellationToken cancellation)
        { Uploads++; return Task.CompletedTask; }
        public Task DeleteAsync(string owner, string documentId, CancellationToken cancellation) => Task.CompletedTask;
    }

    private sealed class Transport : HttpMessageHandler
    {
        public int FailEmbeddingBatch { get; init; }
        public int PartialUploadBatch { get; init; }
        public int VectorDimensions { get; init; } = 1536;
        public bool FailUpload { get; init; }
        public bool FailDelete { get; init; }
        public bool Listing { get; init; }
        public Action? CancelAfterUpload { get; init; }
        public bool DeleteTokenCancelled { get; private set; }
        public List<int> EmbeddingBatchSizes { get; } = [];
        public List<JsonElement> Indexed { get; } = [];
        public List<JsonElement> SearchBodies { get; } = [];
        public List<string> DeletedKeys { get; } = [];
        public int UploadCalls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            if (request.RequestUri!.Host.Contains("openai"))
            {
                var inputs = root.GetProperty("input").EnumerateArray().ToArray();
                Assert.InRange(inputs.Length, 1, 16);
                Assert.Equal(1536, root.GetProperty("dimensions").GetInt32());
                EmbeddingBatchSizes.Add(inputs.Length);
                if (EmbeddingBatchSizes.Count == FailEmbeddingBatch) return Response("""{"error":{"code":"invalid_request_error","message":"private-cloud-body"}}""", HttpStatusCode.BadRequest);
                return Response(JsonSerializer.Serialize(new
                {
                    @object = "list", model = "embedding", data = inputs.Select((_, i) => new
                    {
                        @object = "embedding", index = i, embedding = Convert.ToBase64String(new byte[VectorDimensions * 4])
                    }), usage = new { prompt_tokens = 1, total_tokens = 1 }
                }));
            }
            if (root.TryGetProperty("search", out _))
            {
                SearchBodies.Add(root.Clone());
                var owned = root.GetProperty("filter").GetString()!.Contains(OwnerA);
                if (!Listing || !owned) return Response("""{"value":[]}""");
                if (root.TryGetProperty("skip", out _))
                    return Response(JsonSerializer.Serialize(new { value = new[] { Row("kb-" + DocumentId + "-0001") } }));
                return Response(JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["value"] = new[] { Row("kb-" + DocumentId + "-0000"), Row("operator-doc"), Row("kb-" + DocumentId + "-0000-extra") },
                    ["@odata.nextLink"] = "https://example.search.windows.net/indexes/meeting/docs/search?api-version=2024-07-01",
                    ["@search.nextPageParameters"] = new { search = "*", filter = AzureMeetingProvider.AclFilter(OwnerA), select = "id,title,url,updatedAt", skip = 1000, top = 1000 }
                }));
            }
            var values = root.GetProperty("value").EnumerateArray().ToArray();
            var delete = values[0].GetProperty("@search.action").GetString() == "delete";
            if (delete)
            {
                DeleteTokenCancelled |= cancellationToken.IsCancellationRequested;
                DeletedKeys.AddRange(values.Select(value => value.GetProperty("id").GetString()!));
            }
            else
            {
                UploadCalls++;
                Indexed.AddRange(values.Select(value => value.Clone()));
                CancelAfterUpload?.Invoke();
            }
            if ((delete && FailDelete) || (!delete && FailUpload))
                return Response("""{"error":{"code":"unavailable","message":"private-cloud-body"}}""", HttpStatusCode.ServiceUnavailable);
            return Response(JsonSerializer.Serialize(new
            {
                value = values.Select((value, index) => new
                {
                    key = value.GetProperty("id").GetString(),
                    status = delete || UploadCalls != PartialUploadBatch || index != 0,
                    statusCode = !delete && UploadCalls == PartialUploadBatch && index == 0 ? 400 : 200,
                    errorMessage = !delete && UploadCalls == PartialUploadBatch && index == 0 ? "private-cloud-body" : null
                })
            }));
        }
        private static object Row(string id) => new Dictionary<string, object>
        {
            ["@search.score"] = 1, ["id"] = id, ["title"] = "Original document",
            ["url"] = $"https://my-materials.invalid/{DocumentId}", ["updatedAt"] = "2026-10-01T00:00:00Z"
        };
        private static HttpResponseMessage Response(string body, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
