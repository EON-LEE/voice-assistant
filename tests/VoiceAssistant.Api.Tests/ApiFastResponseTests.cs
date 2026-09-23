using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using VoiceAssistant.Api;
using Xunit;

namespace VoiceAssistant.Api.Tests;

public sealed partial class ApiTests
{
    [Theory]
    [InlineData("balanced", "What is JMAP?", "transcript", 0)]
    [InlineData("conversation", "What is our customer deadline tomorrow?", "transcript", 0)]
    [InlineData("balanced", "Introduce yourself and your project", "profile", 0)]
    [InlineData("balanced", "What is our customer deadline?", "knowledge", 1)]
    [InlineData("grounded", "What is JMAP?", "knowledge", 1)]
    public async Task TypedOptionsReachSpeechAndModelAndCompletionShowsRouting(string mode, string query, string route, int retrievals)
    {
        var provider = new ControlledProvider { AutomaticRetrieval = true };
        await using var host = await Host.StartAsync(provider: provider);
        using var socket = await host.ConnectAsync();
        await Send(socket, WithOptions(new
        {
            responseMode = mode, profile = new { name = "Mina", role = "Software engineer", project = "Evaluating email client interoperability" },
            profileConfirmed = true, topic = "Email", phrases = new[] { "JMAP", "IMAP", "CONDSTORE", "QRESYNC" }, endSilenceMs = 500
        }));
        await Receive(socket);
        Assert.Equal(500, provider.SpeechOptions!.EndSilenceMs);
        Assert.Equal(4, provider.SpeechOptions.Phrases.Count);
        provider.Emit(new("turn", 1, query, true));
        var completion = await Until(socket, "response.completed");
        Assert.Equal(route, completion.GetProperty("responseRoute").GetString());
        Assert.False(completion.GetProperty("retrievalPrefetched").GetBoolean());
        Assert.Equal(0, completion.GetProperty("sources").GetArrayLength());
        Assert.Equal(retrievals, provider.Retrievals);
        Assert.Equal(provider.SpeechOptions, provider.AnswerOptions);
        Assert.Equal(route, provider.Route);
        await Send(socket, """{"type":"session.stop"}""");
    }

    [Fact]
    public async Task LegacyHandshakeKeepsGroundedAndSevenHundredMilliseconds()
    {
        var provider = new ControlledProvider { AutomaticRetrieval = true };
        await using var host = await Host.StartAsync(provider: provider);
        using var socket = await host.ConnectAsync();
        await Send(socket, Start);
        await Receive(socket);
        Assert.Equal("grounded", provider.SpeechOptions!.ResponseMode);
        Assert.Equal(700, provider.SpeechOptions.EndSilenceMs);
        provider.Emit(new("turn", 1, "Hello", true));
        Assert.Equal("knowledge", (await Until(socket, "response.completed")).GetProperty("responseRoute").GetString());
        Assert.Equal(1, provider.Retrievals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StablePartialIsReusedAndFailedSearchRemainsVisible(bool fail)
    {
        const string query = "What is our customer delivery plan?";
        var provider = new ControlledProvider();
        await using var host = await Host.StartAsync(provider: provider);
        using var socket = await host.ConnectAsync();
        await Send(socket, WithOptions(new { responseMode = "balanced" }));
        await Receive(socket);
        provider.Emit(new("turn", 1, query, false));
        await provider.RetrievalStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        provider.Emit(new("turn", 2, query, true));
        await Until(socket, "response.started");
        if (fail) provider.Release.SetException(new InvalidOperationException("private provider detail"));
        else provider.Release.SetResult(new("no_matches", []));
        if (fail) Assert.Equal("grounding_unavailable", (await Until(socket, "error")).GetProperty("code").GetString());
        var completion = await Until(socket, "response.completed");
        Assert.True(completion.GetProperty("retrievalPrefetched").GetBoolean());
        Assert.Equal("knowledge", completion.GetProperty("responseRoute").GetString());
        Assert.Equal(fail ? "unavailable" : "no_matches", completion.GetProperty("grounding").GetString());
        Assert.Equal(1, provider.Retrievals);
        Assert.Equal(fail ? 0 : 1, provider.Answers);
    }

    [Fact]
    public async Task ManualRetryAfterMatchedPrefetchFailurePerformsFreshRecoveredRetrieval()
    {
        const string query = "What is our customer delivery plan?";
        var provider = new ControlledProvider();
        await using var host = await Host.StartAsync(provider: provider);
        using var socket = await host.ConnectAsync();
        await Send(socket, WithOptions(new { responseMode = "balanced" }));
        await Receive(socket);
        provider.Emit(new("turn", 1, query, false));
        await provider.RetrievalStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        provider.Emit(new("turn", 2, query, true));
        await Until(socket, "response.started");
        provider.Release.SetException(new InvalidOperationException("private failure"));
        Assert.Equal("grounding_unavailable", (await Until(socket, "error")).GetProperty("code").GetString());
        var failed = await Until(socket, "response.completed");
        Assert.Equal("unavailable", failed.GetProperty("grounding").GetString());
        Assert.True(failed.GetProperty("retrievalPrefetched").GetBoolean());
        Assert.Equal(1, provider.Retrievals);
        Assert.Equal(0, provider.Answers);

        provider.AutomaticRetrieval = true;
        await Send(socket, """{"type":"response.request"}""");
        var recovered = await Until(socket, "response.completed");
        Assert.Equal("no_matches", recovered.GetProperty("grounding").GetString());
        Assert.False(recovered.GetProperty("retrievalPrefetched").GetBoolean());
        Assert.Equal("knowledge", recovered.GetProperty("responseRoute").GetString());
        Assert.NotEqual(failed.GetProperty("responseId").GetString(), recovered.GetProperty("responseId").GetString());
        Assert.Equal(2, provider.Retrievals);
        Assert.Equal(1, provider.Answers);
    }

    [Fact]
    public async Task PendingPrefetchDoesNotBlockAudioCancelOrStopAndNeverLeaksObsoleteResponse()
    {
        const string query = "What is our customer delivery plan?";
        var provider = new ControlledProvider { IgnoreCancellation = true };
        await using var host = await Host.StartAsync(provider: provider);
        using var socket = await host.ConnectAsync();
        await Send(socket, Start);
        await Receive(socket);
        provider.Emit(new("turn", 1, query, false));
        await provider.RetrievalStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        provider.Emit(new("turn", 2, query, true));
        await Until(socket, "response.started");
        await socket.SendAsync(new byte[640], WebSocketMessageType.Binary, true, CancellationToken.None);
        await provider.AudioWritten.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Send(socket, """{"type":"response.cancel"}""");
        Assert.Equal("response.cancelled", (await Receive(socket)).GetProperty("type").GetString());
        await Send(socket, """{"type":"session.stop"}""");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        Assert.Equal(WebSocketMessageType.Close, (await socket.ReceiveAsync(new byte[4096], timeout.Token)).MessageType);
        provider.Release.SetResult(new("grounded", [new("obsolete", new("secret", "https://example.test", null))]));
        Assert.Equal(0, provider.Answers);
        Assert.True(provider.Disposed);
    }

    [Fact]
    public async Task SynchronouslySlowResponseProviderDoesNotBlockActorCommands()
    {
        using var release = new ManualResetEventSlim();
        var provider = new ControlledProvider { BlockRetrieval = release };
        await using var host = await Host.StartAsync(provider: provider);
        using var socket = await host.ConnectAsync();
        await Send(socket, Start);
        await Receive(socket);
        provider.Emit(new("turn", 1, "What is our delivery plan?", true));
        await Until(socket, "response.started");
        await provider.RetrievalStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        try
        {
            await Send(socket, """{"type":"response.cancel"}""");
            Assert.Equal("response.cancelled", (await Receive(socket)).GetProperty("type").GetString());
            await socket.SendAsync(new byte[640], WebSocketMessageType.Binary, true, CancellationToken.None);
            await provider.AudioWritten.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            release.Set();
            provider.Release.TrySetResult(new("disabled", []));
        }
        await Send(socket, """{"type":"session.stop"}""");
    }

    [Fact]
    public async Task UnicodeOptionsWithinCharacterLimitsFitBoundedHandshake()
    {
        var provider = new ControlledProvider();
        await using var host = await Host.StartAsync(provider: provider);
        using var socket = await host.ConnectAsync();
        var start = WithOptions(new
        {
            phrases = Enumerable.Repeat(new string('\uAC00', 64), 32).ToArray(), topic = new string('\uAC00', 300),
            profile = new { name = new string('\uAC00', 100), role = new string('\uAC00', 160), project = new string('\uAC00', 300) },
            profileConfirmed = true
        });
        await Send(socket, start);
        Assert.Equal("session.ready", (await Receive(socket)).GetProperty("type").GetString());
        Assert.Equal(32, provider.SpeechOptions!.Phrases.Count);
    }

    [Fact]
    public async Task StartupAndCommandByteLimitsAreSeparateAndBounded()
    {
        await using (var oversizedHost = await Host.StartAsync())
        using (var oversized = await oversizedHost.ConnectAsync())
        {
            await Send(oversized, new string(' ', 32769));
            Assert.Equal("message_limit", (await Receive(oversized)).GetProperty("code").GetString());
        }
        await using var host = await Host.StartAsync();
        using var socket = await host.ConnectAsync();
        await Send(socket, Start);
        await Receive(socket);
        await Send(socket, new string(' ', 4097));
        Assert.Equal("message_limit", (await Receive(socket)).GetProperty("code").GetString());
    }

    private static string WithOptions(object options) => JsonSerializer.Serialize(new
    {
        type = "session.start", protocolVersion = 1,
        audio = new { encoding = "pcm_s16le", sampleRate = 16000, channels = 1 }, options
    });

    private sealed class ControlledProvider : IMeetingProvider
    {
        public bool AutomaticRetrieval { get; set; }
        public bool IgnoreCancellation { get; init; }
        public ManualResetEventSlim? BlockRetrieval { get; init; }
        public SessionOptions? SpeechOptions { get; private set; }
        public SessionOptions? AnswerOptions { get; private set; }
        public string? Route { get; private set; }
        public Action<Transcript> Emit { get; private set; } = _ => throw new InvalidOperationException();
        public int Retrievals;
        public int Answers;
        public bool Disposed { get; private set; }
        public TaskCompletionSource RetrievalStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AudioWritten { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<Grounding> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ISpeechStream> StartSpeechAsync(Action<Transcript> transcript, Action<ProviderException> error, CancellationToken cancellation) =>
            throw new InvalidOperationException("Session must supply typed options.");
        public Task<ISpeechStream> StartSpeechAsync(SessionOptions options, Action<Transcript> transcript, Action<ProviderException> error, CancellationToken cancellation)
        {
            SpeechOptions = options;
            Emit = transcript;
            return Task.FromResult<ISpeechStream>(new Stream(() => AudioWritten.TrySetResult(), () => Disposed = true));
        }
        public Task<Grounding> RetrieveAsync(string query, string objectId, CancellationToken cancellation)
        {
            Interlocked.Increment(ref Retrievals);
            RetrievalStarted.TrySetResult();
            BlockRetrieval?.Wait();
            if (AutomaticRetrieval) return Task.FromResult(new Grounding("no_matches", []));
            return IgnoreCancellation ? Release.Task : Release.Task.WaitAsync(cancellation);
        }
        public IAsyncEnumerable<string> AnswerAsync(IReadOnlyList<ConversationTurn> conversation, Grounding grounding, CancellationToken cancellation) =>
            throw new InvalidOperationException("Session must supply typed options.");
        public async IAsyncEnumerable<string> AnswerAsync(IReadOnlyList<ConversationTurn> conversation, Grounding grounding,
            SessionOptions options, string route, [EnumeratorCancellation] CancellationToken cancellation)
        {
            Answers++;
            AnswerOptions = options;
            Route = route;
            await Task.Yield();
            cancellation.ThrowIfCancellationRequested();
            yield return "Here is a concise response grounded only in the supplied context.";
        }
        private sealed class Stream(Action write, Action dispose) : ISpeechStream
        {
            public void Write(byte[] audio) => write();
            public Task CompleteInputAsync(CancellationToken cancellation) => throw new InvalidOperationException("Live session must not signal finite EOF.");
            public ValueTask DisposeAsync() { dispose(); return ValueTask.CompletedTask; }
        }
    }
}
