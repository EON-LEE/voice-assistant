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
