using System.Collections.Concurrent;
using VoiceAssistant.Api;
using Xunit;

namespace VoiceAssistant.Api.Tests;

public sealed class PartialRetrievalTests
{
    private const string Query = "What is our customer delivery plan?";
    private const string Oid = "e59048e0-9a10-433e-8e0e-beb279c0c234";

    [Fact]
    public async Task DebouncesRevisionsAndReusesOnlyExactNormalizedFinal()
    {
        var provider = new Provider();
        await using var cache = new PartialRetrieval(provider, Oid, SessionOptions.Legacy, CancellationToken.None);
        cache.Update(new("turn", 1, "What is our customer", false));
        cache.Update(new("turn", 2, Query, false));
        Assert.Empty(provider.Calls);
        var call = await provider.Next();
        Assert.Equal(ResponseRouting.Normalize(Query), call.Query);
        Assert.Equal(Oid, call.Oid);
        call.Result.SetResult(new("no_matches", []));
        var task = cache.Take("turn", " WHAT  IS OUR CUSTOMER DELIVERY PLAN? ", "knowledge");
        Assert.NotNull(task);
        Assert.Equal("no_matches", (await task!).RequireGrounding().Status);
        Assert.Null(cache.Take("turn", Query + " Not.", "knowledge"));
    }

    [Theory]
    [InlineData("What is our plan for 10?", "What is our plan for 100?")]
    [InlineData("We can deliver the customer plan", "We cannot deliver the customer plan")]
    [InlineData("Our date is 2026-09-23", "Our date is 2026-09-24")]
    public async Task NeverReusesNegationOrNumericCorrections(string partial, string final)
    {
        var provider = new Provider();
        await using var cache = new PartialRetrieval(provider, Oid, SessionOptions.Legacy, CancellationToken.None);
        cache.Update(new("turn", 1, partial, false));
        var call = await provider.Next();
        Assert.Null(cache.Take("turn", final, "knowledge"));
        Assert.True(call.Cancellation.IsCancellationRequested);
    }

    [Fact]
    public async Task FailedMatchedPrefetchIsVisibleWithoutSecondRetrieval()
    {
        var provider = new Provider();
        await using var cache = new PartialRetrieval(provider, Oid, SessionOptions.Legacy, CancellationToken.None);
        cache.Update(new("turn", 1, Query, false));
        var call = await provider.Next();
        call.Result.SetException(new InvalidOperationException("private SDK detail"));
        var result = await cache.Take("turn", Query, "knowledge")!;
        var error = Assert.Throws<ProviderException>(() => result.RequireGrounding());
        Assert.Equal("grounding_unavailable", error.Code);
        Assert.DoesNotContain("private", error.Message);
        Assert.Single(provider.Calls);
        Assert.Null(cache.Take("turn", Query, "knowledge"));
    }

    [Fact]
    public async Task FailureRacingFinalSelectionStillSurfacesOnceThenCanRetry()
    {
        var provider = new Provider();
        await using var cache = new PartialRetrieval(provider, Oid, SessionOptions.Legacy, CancellationToken.None);
        cache.Update(new("turn", 1, Query, false));
        var call = await provider.Next();
        call.Result.SetException(new InvalidOperationException("private failure"));
        // Take may race completion; either order must preserve the initial matched failure.
        var first = cache.Take("turn", Query, "knowledge");
        Assert.NotNull(first);
        var outcome = await first!;
        Assert.Throws<ProviderException>(() => outcome.RequireGrounding());
        Assert.Null(cache.Take("turn", Query, "knowledge"));
    }

    [Fact]
    public async Task SuccessfulExactPrefetchRemainsReusableForManualRequest()
    {
        var provider = new Provider();
        await using var cache = new PartialRetrieval(provider, Oid, SessionOptions.Legacy, CancellationToken.None);
        cache.Update(new("turn", 1, Query, false));
        var call = await provider.Next();
        call.Result.SetResult(new("no_matches", []));
        var first = cache.Take("turn", Query, "knowledge");
        Assert.Equal("no_matches", (await first!).RequireGrounding().Status);
        Assert.Same(first, cache.Take("turn", Query, "knowledge"));
        Assert.Single(provider.Calls);
    }

    [Fact]
    public async Task BoundsStartsToThreePerTurnAndResetsForNewTurn()
    {
        var provider = new Provider();
        await using var cache = new PartialRetrieval(provider, Oid, SessionOptions.Legacy, CancellationToken.None);
        for (var i = 0; i < 3; i++)
        {
            var query = Query + " " + i;
            cache.Update(new("turn", i, query, false));
            var call = await provider.Next();
            call.Result.SetResult(new("disabled", []));
            await cache.Take("turn", query, "knowledge")!;
        }
        cache.Update(new("turn", 4, Query + " fourth", false));
        await Task.Delay(350);
        Assert.Equal(3, provider.Calls.Count);
        cache.Update(new("next-turn", 1, Query, false));
        var next = await provider.Next();
        next.Result.SetResult(new("disabled", []));
        Assert.Equal(4, provider.Calls.Count);
    }

    [Fact]
    public async Task OneActivePrefetchEvenIfCancelledProviderIgnoresCancellation()
    {
        var provider = new Provider { IgnoreCancellation = true };
        await using var cache = new PartialRetrieval(provider, Oid, SessionOptions.Legacy, CancellationToken.None);
        cache.Update(new("turn", 1, Query, false));
        var first = await provider.Next();
        cache.Update(new("turn", 2, Query + " tomorrow", false));
        await first.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await Task.Delay(350);
        Assert.Single(provider.Calls);
        first.Result.SetResult(new("grounded", []));
        var second = await provider.Next();
        second.Result.SetResult(new("no_matches", []));
        Assert.Equal(2, provider.Calls.Count);
        Assert.Null(cache.Take("turn", Query, "knowledge"));
    }

    [Fact]
    public async Task DisposeCancelsAndReturnsEvenIfRetrievalIgnoresCancellation()
    {
        var provider = new Provider { IgnoreCancellation = true };
        var cache = new PartialRetrieval(provider, Oid, SessionOptions.Legacy, CancellationToken.None);
        cache.Update(new("turn", 1, Query, false));
        var active = await provider.Next();
        await cache.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        await active.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        active.Result.SetResult(new("disabled", []));
    }

    [Fact]
    public async Task NoReuseBetweenTurnsIdentitiesOrExplicitNonKnowledgeRoutes()
    {
        var provider = new Provider();
        await using var first = new PartialRetrieval(provider, Oid, SessionOptions.Legacy, CancellationToken.None);
        await using var second = new PartialRetrieval(provider, "another-identity", SessionOptions.Legacy, CancellationToken.None);
        first.Update(new("turn", 1, Query, false));
        var a = await provider.Next();
        second.Update(new("turn", 1, Query, false));
        var b = await provider.Next();
        Assert.Equal(Oid, a.Oid);
        Assert.Equal("another-identity", b.Oid);
        a.Result.SetResult(new("grounded", [new("authorized-for-first", new("a", "https://example.test/a", null))]));
        b.Result.SetResult(new("no_matches", []));
        Assert.Equal("grounded", (await first.Take("turn", Query, "knowledge")!).RequireGrounding().Status);
        Assert.Equal("no_matches", (await second.Take("turn", Query, "knowledge")!).RequireGrounding().Status);
        Assert.Null(first.Take("different-turn", Query, "knowledge"));
        Assert.Null(second.Take("turn", Query, "transcript"));
    }

    [Theory]
    [InlineData("conversation", "What is our customer delivery plan?")]
    [InlineData("balanced", "Please explain semantic search briefly.")]
    [InlineData("grounded", "short")]
    public async Task SkipsNonKnowledgeAndUnsubstantivePartials(string mode, string query)
    {
        var provider = new Provider();
        await using var cache = new PartialRetrieval(provider, Oid, new() { ResponseMode = mode }, CancellationToken.None);
        cache.Update(new("turn", 1, query, false));
        await Task.Delay(300);
        Assert.Empty(provider.Calls);
    }

    [Fact]
    public async Task ExplicitCancelInvalidatesCachedSuccess()
    {
        var provider = new Provider();
        await using var cache = new PartialRetrieval(provider, Oid, SessionOptions.Legacy, CancellationToken.None);
        cache.Update(new("turn", 1, Query, false));
        var call = await provider.Next();
        call.Result.SetResult(new("grounded", []));
        await cache.Take("turn", Query, "knowledge")!;
        cache.Cancel();
        Assert.Null(cache.Take("turn", Query, "knowledge"));
    }

    [Fact]
    public async Task FollowUpPrefetchUsesSameBoundedActualContextAsFinalAndPreservesOwner()
    {
        var provider = new Provider();
        ConversationTurn[] history = [new("Our Lighthouse rollout uses JMAP.")];
        const string followUp = "Can we deliver it to our customer tomorrow?";
        await using var cache = new PartialRetrieval(provider, Oid, SessionOptions.Legacy, CancellationToken.None);
        cache.Update(new("follow-up", 1, followUp, false), history);
        var call = await provider.Next();
        Assert.Equal(RetrievalQuery.Build(followUp, history), call.Query);
        Assert.Contains("lighthouse", call.Query);
        Assert.Equal(Oid, call.Oid);
        call.Result.SetResult(new("no_matches", []));
        Assert.Equal("no_matches", (await cache.Take("follow-up", followUp, "knowledge", history)!).RequireGrounding().Status);
        Assert.Single(provider.Calls);
        Assert.Null(cache.Take("follow-up", followUp, "knowledge", [new("Our corrected topic is IMAP.")]));
    }

    [Fact]
    public async Task ContextChangeInvalidatesOtherwiseIdenticalPartial()
    {
        var provider = new Provider();
        await using var cache = new PartialRetrieval(provider, Oid, SessionOptions.Legacy, CancellationToken.None);
        cache.Update(new("turn", 1, Query, false), [new("Lighthouse uses JMAP.")]);
        var first = await provider.Next();
        cache.Update(new("turn", 2, Query, false), [new("Lighthouse uses IMAP instead.")]);
        Assert.True(first.Cancellation.IsCancellationRequested);
        var second = await provider.Next();
        Assert.Contains("imap instead", second.Query);
        second.Result.SetResult(new("no_matches", []));
    }

    [Fact]
    public async Task FinalTranscriptRetrievesImmediatelyAndIsReusedDespitePunctuationAndCase()
    {
        var provider = new Provider();
        await using var cache = new PartialRetrieval(provider, Oid, SessionOptions.Legacy, CancellationToken.None);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        cache.Update(new("turn", 1, Query, true));
        var call = await provider.Next();
        Assert.True(clock.ElapsedMilliseconds < 200, $"A final must not wait for the 250 ms partial debounce ({clock.ElapsedMilliseconds} ms).");
        call.Result.SetResult(new("no_matches", []));
        var task = cache.Take("turn", "what is our   customer delivery plan", "knowledge");
        Assert.NotNull(task);
        Assert.Equal("no_matches", (await task!).RequireGrounding().Status);
        Assert.Single(provider.Calls);
    }

    [Fact]
    public async Task FinalRetrievalIsNeitherCountedNorBlockedByThePartialStartCap()
    {
        var provider = new Provider();
        await using var cache = new PartialRetrieval(provider, Oid, SessionOptions.Legacy, CancellationToken.None);
        for (var i = 0; i < 3; i++)
        {
            var query = Query + " " + i;
            cache.Update(new("turn", i, query, false));
            (await provider.Next()).Result.SetResult(new("disabled", []));
            await cache.Take("turn", query, "knowledge")!;
        }
        cache.Update(new("turn", 4, Query + " fourth", false));
        await Task.Delay(350);
        Assert.Equal(3, provider.Calls.Count);

        const string final = "What is our customer delivery plan this quarter?";
        cache.Update(new("turn", 5, final, true));
        var call = await provider.Next();
        call.Result.SetResult(new("no_matches", []));
        Assert.Equal("no_matches", (await cache.Take("turn", final, "knowledge")!).RequireGrounding().Status);
        Assert.Equal(4, provider.Calls.Count);
    }

    [Fact]
    public async Task FinalPromotesIdenticalPartialThatIsStillWaitingOutItsDebounce()
    {
        var provider = new Provider();
        await using var cache = new PartialRetrieval(provider, Oid, SessionOptions.Legacy, CancellationToken.None);
        cache.Update(new("turn", 1, Query, false));
        cache.Update(new("turn", 2, Query, true));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var call = await provider.Next();
        Assert.True(clock.ElapsedMilliseconds < 200, $"The final must start the retrieval without the remaining debounce ({clock.ElapsedMilliseconds} ms).");
        call.Result.SetResult(new("no_matches", []));
        Assert.NotNull(cache.Take("turn", Query, "knowledge"));
        await Task.Delay(350);
        Assert.Single(provider.Calls);
    }

    [Fact]
    public async Task FinalKeepsIdenticalPartialRetrievalAlreadyInFlightInsteadOfSearchingTwice()
    {
        var provider = new Provider();
        await using var cache = new PartialRetrieval(provider, Oid, SessionOptions.Legacy, CancellationToken.None);
        cache.Update(new("turn", 1, "What is our customer delivery plan", false));
        var call = await provider.Next();
        cache.Update(new("turn", 2, Query, true));
        Assert.False(call.Cancellation.IsCancellationRequested);
        call.Result.SetResult(new("no_matches", []));
        Assert.Equal("no_matches", (await cache.Take("turn", Query, "knowledge")!).RequireGrounding().Status);
        await Task.Delay(100);
        Assert.Single(provider.Calls);
    }

    [Theory]
    [InlineData("What is our plan for 10?", "What is our plan for 100?")]
    [InlineData("We can deliver the customer plan.", "We cannot deliver the customer plan.")]
    [InlineData("Our date is 2026-09-23.", "Our date is 2026-09-24.")]
    [InlineData("Is the budget 1.5 million?", "Is the budget 15 million?")]
    [InlineData("We can't ship the customer plan.", "We can ship the customer plan.")]
    public async Task NeverReusesFinalRetrievalForDifferentWords(string first, string second)
    {
        var provider = new Provider();
        await using var cache = new PartialRetrieval(provider, Oid, SessionOptions.Legacy, CancellationToken.None);
        cache.Update(new("turn", 1, first, true));
        var call = await provider.Next();
        Assert.Null(cache.Take("turn", second, "knowledge"));
        Assert.True(call.Cancellation.IsCancellationRequested);
    }

    [Theory]
    [InlineData("Yeah.")]
    [InlineData("OK, good.")]
    [InlineData("Thanks.")]
    [InlineData("Uh huh.")]
    public async Task BackchannelFinalsAreNotRetrieved(string text)
    {
        var provider = new Provider();
        await using var cache = new PartialRetrieval(provider, Oid, SessionOptions.Legacy, CancellationToken.None);
        cache.Update(new("turn", 1, text, true));
        await Task.Delay(150);
        Assert.Empty(provider.Calls);
        Assert.Null(cache.Take("turn", text, "knowledge"));
    }

    [Fact]
    public async Task UnstartedFinalRetrievalBehindABusyWorkerIsNeverHandedToTheResponse()
    {
        var provider = new Provider { IgnoreCancellation = true };
        await using var cache = new PartialRetrieval(provider, Oid, SessionOptions.Legacy, CancellationToken.None);
        cache.Update(new("turn", 1, Query + " first", false));
        var busy = await provider.Next();
        cache.Update(new("turn", 2, Query, true));
        await Task.Delay(50);
        Assert.Null(cache.Take("turn", Query, "knowledge"));
        busy.Result.SetResult(new("grounded", []));
        await Task.Delay(100);
        Assert.Single(provider.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RetrievalAReplyAlreadyWaitsForSurvivesLaterSpeech(bool nextTurn)
    {
        var provider = new Provider();
        await using var cache = new PartialRetrieval(provider, Oid, SessionOptions.Legacy, CancellationToken.None);
        cache.Update(new("turn", 1, Query, false));
        var call = await provider.Next();
        var selected = cache.Take("turn", Query, "knowledge");
        Assert.NotNull(selected);

        // Whatever the speaker says next must not turn the answer being prepared into a grounding error.
        const string later = "What is our customer return policy for damaged goods?";
        var laterTurn = nextTurn ? "next" : "turn";
        cache.Update(new(laterTurn, nextTurn ? 1 : 2, later, false));
        Assert.False(call.Cancellation.IsCancellationRequested);

        call.Result.SetResult(new("grounded", []));
        Assert.Equal("grounded", (await selected!).RequireGrounding().Status);
        // The later speech is still prefetched, once the single worker is free again.
        var following = await provider.Next();
        Assert.Equal(ResponseRouting.Normalize(later), following.Query);
        following.Result.SetResult(new("no_matches", []));
        Assert.Equal("no_matches", (await cache.Take(laterTurn, later, "knowledge")!).RequireGrounding().Status);
    }

    [Fact]
    public async Task SpeechOfTheNextTurnStillCancelsRetrievalNobodyWaitsFor()
    {
        var provider = new Provider();
        await using var cache = new PartialRetrieval(provider, Oid, SessionOptions.Legacy, CancellationToken.None);
        cache.Update(new("turn", 1, Query, false));
        var call = await provider.Next();
        cache.Update(new("next", 1, "What is our customer return policy for damaged goods?", false));
        Assert.True(call.Cancellation.IsCancellationRequested);
        Assert.Null(cache.Take("turn", Query, "knowledge"));
    }

    private sealed record Call(string Query, string Oid, CancellationToken Cancellation)
    {
        internal TaskCompletionSource<Grounding> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class Provider : IMeetingProvider
    {
        internal bool IgnoreCancellation { get; init; }
        internal ConcurrentQueue<Call> Calls { get; } = new();
        private readonly System.Threading.Channels.Channel<Call> arrivals = System.Threading.Channels.Channel.CreateUnbounded<Call>();
        internal async Task<Call> Next() => await arrivals.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        public async Task<Grounding> RetrieveAsync(string query, string objectId, CancellationToken cancellation)
        {
            var call = new Call(query, objectId, cancellation);
            Calls.Enqueue(call);
            arrivals.Writer.TryWrite(call);
            using var registration = cancellation.Register(() => call.Cancelled.TrySetResult());
            return IgnoreCancellation ? await call.Result.Task : await call.Result.Task.WaitAsync(cancellation);
        }
        public Task<ISpeechStream> StartSpeechAsync(Action<Transcript> transcript, Action<ProviderException> error, CancellationToken cancellation) => throw new NotSupportedException();
        public IAsyncEnumerable<string> AnswerAsync(IReadOnlyList<ConversationTurn> conversation, Grounding grounding, CancellationToken cancellation) => throw new NotSupportedException();
    }
}
