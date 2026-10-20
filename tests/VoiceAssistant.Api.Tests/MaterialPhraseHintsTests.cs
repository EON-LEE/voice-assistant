using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Azure;
using Azure.Core.Pipeline;
using Azure.Search.Documents;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VoiceAssistant.Api;
using VoiceAssistant.Api.Knowledge;
using Xunit;

namespace VoiceAssistant.Api.Tests;

public sealed class MaterialPhraseHintsTests
{
    private const string OwnerA = "e59048e0-9a10-433e-8e0e-beb279c0c234";
    private const string OwnerB = "11111111-1111-1111-1111-111111111111";

    [Fact]
    public void TitlesAcronymsCodesAndRepeatedNamesBecomeRankedHints()
    {
        string[] titles = ["Q3_Rollout_Plan.pptx", "Contoso Mail Gateway notes.docx"];
        string[] texts =
        [
            "The Contoso Mail Gateway uses JMAP push. ORN-4 ships in Q3. PROJECT OVERVIEW: the project overview is below.",
            "JMAP and IMAP both matter. ORN-4 replaces ORN-3. Next Steps: ask Northwind Traders. Northwind Traders confirmed JMAP.",
            "We use PowerPoint for the deck. Today Contoso announced nothing."
        ];

        var phrases = MaterialPhrases.Extract(titles, texts).ToList();

        // Titles first, in listing order, without file extensions.
        Assert.Equal(["Q3 Rollout Plan", "Contoso Mail Gateway notes"], phrases.Take(2));
        foreach (var expected in new[] { "JMAP", "IMAP", "ORN-4", "ORN-3", "Q3", "Contoso Mail Gateway", "Northwind Traders", "PowerPoint" })
            Assert.Contains(expected, phrases);
        // Shouted heading words that also appear in lower case are not acronyms; common headings and one-off capitals are dropped.
        foreach (var dropped in new[] { "PROJECT", "OVERVIEW", "PROJECT OVERVIEW", "Next Steps", "Today Contoso", "The Contoso Mail Gateway" })
            Assert.DoesNotContain(dropped, phrases);
        // More mentions rank higher: JMAP (three) before IMAP (one).
        Assert.True(phrases.IndexOf("JMAP") < phrases.IndexOf("IMAP"));
        Assert.Equal(phrases.Count, phrases.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void HintsAreCappedInNumberAndLength()
    {
        var texts = Enumerable.Range(1, 300).Select(index => $"Part AB-{index} is listed.").ToArray();
        string[] titles = ["A title that is far too long to be useful as a speech phrase hint at all.pptx", "Short Title"];

        var phrases = MaterialPhrases.Extract(titles, texts);

        Assert.Equal(MaterialPhrases.MaxPhrases, phrases.Count);
        Assert.All(phrases, phrase => Assert.InRange(phrase.Length, 2, MaterialPhrases.MaxLength));
        Assert.Equal("Short Title", phrases[0]);
    }

    [Fact]
    public void ClientPhrasesComeFirstAndMaterialDuplicatesAreDropped() =>
        Assert.Equal(["JMAP", "Team Sync", "ORN-4"], MaterialPhrases.Merge(["JMAP", "Team Sync"], ["jmap", "ORN-4", "team sync"]));

    [Fact]
    public async Task HintsComeOnlyFromTheCallersMaterialsAreCachedAndRebuiltAfterUploadOrDelete()
    {
        var store = new CountingStore();
        var logger = new ListLogger();
        var hints = new MaterialPhraseHints(store, TimeProvider.System, logger);
        var knowledge = new KnowledgeService(store, TimeProvider.System, hints);
        var rollout = await knowledge.UploadAsync(OwnerA, new("Rollout.txt", "ORN-4 ships with JMAP. JMAP is ready."), CancellationToken.None);
        await knowledge.UploadAsync(OwnerB, new("Other.txt", "ZETA-9 belongs to someone else."), CancellationToken.None);

        var first = await hints.GetAsync(OwnerA);
        Assert.Contains("ORN-4", first);
        Assert.DoesNotContain("ZETA-9", first);
        Assert.Same(first, await hints.GetAsync(OwnerA));
        Assert.Equal(1, store.TextReads);

        await knowledge.UploadAsync(OwnerA, new("Codes.txt", "XR-77 is new."), CancellationToken.None);
        var afterUpload = await hints.GetAsync(OwnerA);
        Assert.Contains("XR-77", afterUpload);
        Assert.Equal(2, store.TextReads);

        await knowledge.DeleteAsync(OwnerA, rollout.Id, CancellationToken.None);
        var afterDelete = await hints.GetAsync(OwnerA);
        Assert.DoesNotContain("ORN-4", afterDelete);
        Assert.Contains("XR-77", afterDelete);
        Assert.Equal(3, store.TextReads);

        // Only counts are logged, never the phrases.
        Assert.NotEmpty(logger.Entries);
        Assert.All(logger.Entries, entry =>
        {
            foreach (var phrase in new[] { "ORN-4", "XR-77", "JMAP", "Rollout" }) Assert.DoesNotContain(phrase, entry.Message);
        });
    }

    [Fact]
    public async Task AFailedBuildYieldsNoHintsLogsOnlyTheExceptionTypeAndIsRetriedNextTime()
    {
        var store = new CountingStore { Failure = new InvalidOperationException("private-cloud-body") };
        var logger = new ListLogger();
        var hints = new MaterialPhraseHints(store, TimeProvider.System, logger);

        Assert.Empty(await hints.GetAsync(OwnerA));
        Assert.Empty(await hints.GetAsync(OwnerA));

        Assert.Equal(2, store.TextReads);
        Assert.All(logger.Entries, entry => Assert.DoesNotContain("private-cloud-body", entry.Message));
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains(nameof(InvalidOperationException)));
    }

    [Fact]
    public async Task AzureStoreReadsMaterialTextThroughTheSameAclFilterAsRetrieval()
    {
        using var handler = new SearchCapture();
        using var http = new HttpClient(handler);
        var options = new SearchClientOptions { Transport = new HttpClientTransport(http) };
        options.Retry.MaxRetries = 0;
        var search = new SearchClient(new Uri("https://example.search.windows.net"), "meeting", new AzureKeyCredential("test-only"), options);
        var store = new AzureKnowledgeStore(search, null, NullLogger<AzureKnowledgeStore>.Instance);

        var texts = await store.ReadTextAsync(OwnerA, 1000, CancellationToken.None);

        Assert.Equal([new MaterialText($"kb-{new string('a', 32)}-0000", "Rollout plan", "ORN-4 ships in Q3.")], texts);
        using var body = JsonDocument.Parse(Assert.Single(handler.Bodies));
        Assert.Equal(AzureMeetingProvider.AclFilter(OwnerA), body.RootElement.GetProperty("filter").GetString());
        Assert.Equal("id,title,content", body.RootElement.GetProperty("select").GetString());
        Assert.Equal(1000, body.RootElement.GetProperty("top").GetInt32());
    }

    [Fact]
    public async Task ADeletedDocumentStopsBiasingSpeechAtOnceEvenWhileSearchStillListsIt()
    {
        var clock = new ManualClock();
        var store = new LaggingStore();
        var hints = new MaterialPhraseHints(store, clock, NullLogger<MaterialPhraseHints>.Instance);
        var knowledge = new KnowledgeService(store, clock, hints);
        var rollout = await knowledge.UploadAsync(OwnerA, new("Rollout.txt", "ORN-4 ships with JMAP."), CancellationToken.None);
        await knowledge.UploadAsync(OwnerA, new("Codes.txt", "XR-77 is new."), CancellationToken.None);
        store.CatchUp(OwnerA);
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Contains("ORN-4", await hints.GetAsync(OwnerA));

        // Search still lists the deleted document and its chunks; the service's record of the delete wins.
        await knowledge.DeleteAsync(OwnerA, rollout.Id, CancellationToken.None);
        var afterDelete = await hints.GetAsync(OwnerA);
        Assert.DoesNotContain("ORN-4", afterDelete);
        Assert.DoesNotContain("Rollout", afterDelete);
        Assert.Contains("XR-77", afterDelete);

        // Built so soon after the change, it is kept only briefly and rebuilt once Search has caught up.
        store.CatchUp(OwnerA);
        clock.Advance(MaterialPhraseHints.ShortLifetime + TimeSpan.FromSeconds(1));
        var rebuilt = await hints.GetAsync(OwnerA);
        Assert.NotSame(afterDelete, rebuilt);
        Assert.DoesNotContain("ORN-4", rebuilt);
        Assert.Contains("XR-77", rebuilt);
        Assert.Equal(3, store.TextReads);
    }

    [Fact]
    public async Task AJustUploadedDeckIsHintedByTitleAtOnceAndByContentOnceSearchCatchesUp()
    {
        var clock = new ManualClock();
        var store = new LaggingStore();
        var hints = new MaterialPhraseHints(store, clock, NullLogger<MaterialPhraseHints>.Instance);
        var knowledge = new KnowledgeService(store, clock, hints);

        await knowledge.UploadAsync(OwnerA, new("Contoso Rollout Deck.pptx", "ORN-4 ships in Q3."), CancellationToken.None);
        var early = await hints.GetAsync(OwnerA);
        // Search does not show the deck yet: its title comes from the service's record of the upload, its text not at all.
        Assert.Contains("Contoso Rollout Deck", early);
        Assert.DoesNotContain("ORN-4", early);

        store.CatchUp(OwnerA);
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Same(early, await hints.GetAsync(OwnerA));
        clock.Advance(MaterialPhraseHints.ShortLifetime - TimeSpan.FromSeconds(9));
        var caughtUp = await hints.GetAsync(OwnerA);
        Assert.Contains("ORN-4", caughtUp);
        Assert.Contains("Contoso Rollout Deck", caughtUp);

        // A build made well after the change keeps the normal lifetime.
        clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Same(caughtUp, await hints.GetAsync(OwnerA));
        Assert.Equal(2, store.TextReads);
    }

    [Fact]
    public async Task HintsStayShortLivedWhileSearchLagsLongerThanUsual()
    {
        var clock = new ManualClock();
        var store = new LaggingStore();
        var hints = new MaterialPhraseHints(store, clock, NullLogger<MaterialPhraseHints>.Instance);
        var knowledge = new KnowledgeService(store, clock, hints);

        await knowledge.UploadAsync(OwnerA, new("Contoso Rollout Deck.pptx", "ORN-4 ships in Q3."), CancellationToken.None);
        clock.Advance(MaterialPhraseHints.IndexLag * 3);
        var lagging = await hints.GetAsync(OwnerA);
        Assert.Contains("Contoso Rollout Deck", lagging);
        Assert.DoesNotContain("ORN-4", lagging);

        // Search did not show the deck yet, so the build is kept only briefly however old the upload is.
        store.CatchUp(OwnerA);
        clock.Advance(MaterialPhraseHints.ShortLifetime + TimeSpan.FromSeconds(1));
        var caughtUp = await hints.GetAsync(OwnerA);
        Assert.Contains("ORN-4", caughtUp);
        clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Same(caughtUp, await hints.GetAsync(OwnerA));
        Assert.Equal(2, store.TextReads);
    }

    [Fact]
    public async Task HintsBuiltSoonAfterAChangeAreShortLivedEvenWhenSearchAlreadyListsIt()
    {
        var clock = new ManualClock();
        var store = new LaggingStore();
        var hints = new MaterialPhraseHints(store, clock, NullLogger<MaterialPhraseHints>.Instance);
        var knowledge = new KnowledgeService(store, clock, hints);

        await knowledge.UploadAsync(OwnerA, new("Contoso Rollout Deck.pptx", "ORN-4 ships in Q3."), CancellationToken.None);
        // Search lists the deck already, but reading its text does not return it yet.
        store.CatchUp(OwnerA, text: false);
        var early = await hints.GetAsync(OwnerA);
        Assert.Contains("Contoso Rollout Deck", early);
        Assert.DoesNotContain("ORN-4", early);

        store.CatchUp(OwnerA);
        clock.Advance(MaterialPhraseHints.ShortLifetime + TimeSpan.FromSeconds(1));
        Assert.Contains("ORN-4", await hints.GetAsync(OwnerA));
        Assert.Equal(2, store.TextReads);
    }

    [Fact]
    public async Task TheRecordOfAnUploadEndsOnceSearchShowsTheDocument()
    {
        var clock = new ManualClock();
        var store = new LaggingStore();
        var hints = new MaterialPhraseHints(store, clock, NullLogger<MaterialPhraseHints>.Instance);
        var knowledge = new KnowledgeService(store, clock, hints);
        var deck = await knowledge.UploadAsync(OwnerA, new("Contoso Rollout Deck.pptx", "ORN-4 ships in Q3."), CancellationToken.None);
        store.CatchUp(OwnerA);
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Contains("Contoso Rollout Deck", await hints.GetAsync(OwnerA));

        // Deleted where this service does not see it (for example through another app instance): the old record of the
        // upload must not bring the deck back.
        await store.DeleteAsync(OwnerA, deck.Id, CancellationToken.None);
        store.CatchUp(OwnerA);
        clock.Advance(MaterialPhraseHints.CacheLifetime + TimeSpan.FromSeconds(1));
        var rebuilt = await hints.GetAsync(OwnerA);
        Assert.DoesNotContain("Contoso Rollout Deck", rebuilt);
        Assert.DoesNotContain("ORN-4", rebuilt);
        Assert.Equal(2, store.TextReads);
    }

    /// <summary>
    /// Like Search after a write: listing and text show the state as of the last <see cref="CatchUp"/>, and the text may lag
    /// behind the listing.
    /// </summary>
    private sealed class LaggingStore : IKnowledgeStore
    {
        private readonly InMemoryKnowledgeStore actual = new();
        private readonly ConcurrentDictionary<string, (IReadOnlyList<KnowledgeDocument> Documents, IReadOnlyList<MaterialText> Texts)> visible = new();
        private int textReads;
        public int TextReads => Volatile.Read(ref textReads);

        public void CatchUp(string owner, bool text = true) => visible[owner] = (actual.ListAsync(owner, CancellationToken.None).Result,
            text ? actual.ReadTextAsync(owner, MaterialPhraseHints.MaxChunks, CancellationToken.None).Result
                : visible.TryGetValue(owner, out var shown) ? shown.Texts : []);

        public Task<IReadOnlyList<KnowledgeDocument>> ListAsync(string owner, CancellationToken cancellation) =>
            Task.FromResult(visible.TryGetValue(owner, out var shown) ? shown.Documents : []);

        public Task<IReadOnlyList<MaterialText>> ReadTextAsync(string owner, int maxChunks, CancellationToken cancellation)
        {
            Interlocked.Increment(ref textReads);
            return Task.FromResult(visible.TryGetValue(owner, out var shown) ? shown.Texts : []);
        }

        public Task UploadAsync(string owner, KnowledgeDocument document, IReadOnlyList<KnowledgeChunk> chunks, CancellationToken cancellation) =>
            actual.UploadAsync(owner, document, chunks, cancellation);

        public Task DeleteAsync(string owner, string documentId, CancellationToken cancellation) =>
            actual.DeleteAsync(owner, documentId, cancellation);
    }

    private sealed class CountingStore : IKnowledgeStore
    {
        private readonly InMemoryKnowledgeStore inner = new();
        private int textReads;
        public int TextReads => Volatile.Read(ref textReads);
        public Exception? Failure { get; init; }
        public Task<IReadOnlyList<KnowledgeDocument>> ListAsync(string owner, CancellationToken cancellation) => inner.ListAsync(owner, cancellation);
        public Task UploadAsync(string owner, KnowledgeDocument document, IReadOnlyList<KnowledgeChunk> chunks, CancellationToken cancellation) =>
            inner.UploadAsync(owner, document, chunks, cancellation);
        public Task DeleteAsync(string owner, string documentId, CancellationToken cancellation) => inner.DeleteAsync(owner, documentId, cancellation);
        public Task<IReadOnlyList<MaterialText>> ReadTextAsync(string owner, int maxChunks, CancellationToken cancellation)
        {
            Interlocked.Increment(ref textReads);
            return Failure is null ? inner.ReadTextAsync(owner, maxChunks, cancellation) : Task.FromException<IReadOnlyList<MaterialText>>(Failure);
        }
    }

    private sealed class SearchCapture : HttpMessageHandler
    {
        public ConcurrentQueue<string> Bodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Enqueue(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""{"value":[{"@search.score":1,"id":"kb-{{new string('a', 32)}}-0000","title":"Rollout plan","content":"ORN-4 ships in Q3."}]}""",
                    Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class ListLogger : ILogger<MaterialPhraseHints>
    {
        public ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Enqueue((logLevel, formatter(state, exception) + (exception is null ? "" : " " + exception)));
    }
}

public sealed partial class ApiTests
{
    private const string FakeOwner = "00000000-0000-0000-0000-000000000001";

    private static async Task<InMemoryKnowledgeStore> MaterialsAsync()
    {
        var store = new InMemoryKnowledgeStore();
        var id = new string('a', 32);
        await store.UploadAsync(FakeOwner, new KnowledgeDocument(id, "Rollout.txt", 1, DateTimeOffset.UtcNow),
            [new KnowledgeChunk($"kb-{id}-0000", "ORN-4 ships with the Contoso Gateway. The Contoso Gateway owners agreed.")], CancellationToken.None);
        return store;
    }

    [Fact]
    public async Task MaterialPhraseHintsReachOnlyTheSpeechPhraseList()
    {
        var provider = new ControlledProvider { AutomaticRetrieval = true };
        await using var host = await Host.StartAsync(provider: provider, knowledge: await MaterialsAsync(),
            configuration: new() { ["Speech:MaterialPhraseHints"] = "true" });
        using var socket = await host.ConnectAsync();
        await Send(socket, WithOptions(new { responseMode = "grounded", phrases = new[] { "Team Sync" } }));
        Assert.Equal("session.ready", TypeOf(await Receive(socket)));

        var phrases = provider.SpeechOptions!.Phrases;
        Assert.Equal("Team Sync", phrases[0]);
        Assert.Contains("ORN-4", phrases);
        Assert.Contains("Contoso Gateway", phrases);
        // Prompts and Korean translation keep the client's phrases only.
        provider.Emit(new("q", 1, "What ships first?", true));
        await ReceiveUntilAsync(socket, Completed);
        Assert.Equal(["Team Sync"], provider.AnswerOptions!.Phrases);
        await Send(socket, """{"type":"session.stop"}""");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task WithoutTheSwitchOrInPracticeTheSpeechPhraseListIsTheClients(bool switchedOn, bool transcribeOnly)
    {
        var provider = new ControlledProvider { AutomaticRetrieval = true };
        await using var host = await Host.StartAsync(provider: provider, knowledge: await MaterialsAsync(),
            configuration: switchedOn ? new() { ["Speech:MaterialPhraseHints"] = "true" } : null);
        using var socket = await host.ConnectAsync();
        await Send(socket, WithOptions(new { responseMode = "grounded", phrases = new[] { "Team Sync" }, transcribeOnly }));
        Assert.Equal("session.ready", TypeOf(await Receive(socket)));

        Assert.Equal(["Team Sync"], provider.SpeechOptions!.Phrases);
        await Send(socket, """{"type":"session.stop"}""");
    }
}
