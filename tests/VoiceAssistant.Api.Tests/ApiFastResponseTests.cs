using System.Net.WebSockets;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using VoiceAssistant.Api;
using Xunit;

namespace VoiceAssistant.Api.Tests;

public sealed partial class ApiTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FollowUpRetrievalAndModelUseActualSpeechNotGeneratedSuggestions(bool prefetch)
    {
        var provider = new ControlledProvider { AutomaticRetrieval = true, Alternative = "Let's check the next step." };
        await using var host = await Host.StartAsync(provider: provider);
        using var socket = await host.ConnectAsync();
        await Send(socket, Start);
        await Receive(socket);
        provider.Emit(new("topic", 1, "Our Lighthouse rollout uses JMAP.", true));
        var first = await Until(socket, "response.completed");
        Assert.Equal(first.GetProperty("text").GetString(), first.GetProperty("suggestions")[0].GetString());
        Assert.Equal(2, first.GetProperty("suggestions").GetArrayLength());
        await provider.RetrievalQueries.Reader.ReadAsync();

        const string followUp = "Can we deliver it to our customer tomorrow?";
        if (prefetch)
        {
            provider.Emit(new("follow-up", 1, followUp, false));
            var speculative = await provider.RetrievalQueries.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Contains("lighthouse", speculative);
        }
        provider.Emit(new("follow-up", 2, followUp, true));
        var final = await Until(socket, "response.completed");
        Assert.Equal(prefetch, final.GetProperty("retrievalPrefetched").GetBoolean());
        Assert.Equal(2, provider.Retrievals);
        Assert.Equal(2, provider.Answers);
        Assert.Equal(["Our Lighthouse rollout uses JMAP.", followUp], provider.LastHistory!.Select(turn => turn.Text));
        Assert.DoesNotContain(provider.LastHistory!, turn => turn.Text.Contains("Let's check"));
        if (!prefetch)
        {
            var query = await provider.RetrievalQueries.Reader.ReadAsync();
            Assert.Contains("lighthouse", query);
            Assert.Contains("jmap", query);
            Assert.Contains("deliver it", query);
            Assert.DoesNotContain("concise response", query);
            Assert.DoesNotContain("next step", query);
        }
        await Send(socket, """{"type":"session.stop"}""");
    }

    [Fact]
    public async Task LongMeetingKeepsBoundedEarlierRecognizedTopicWithoutInventingSummary()
    {
        var provider = new ControlledProvider { AutomaticRetrieval = true, Alternative = "Let's check the next step." };
        await using var host = await Host.StartAsync(provider: provider);
        using var socket = await host.ConnectAsync();
        await Send(socket, Start);
        await Receive(socket);
        provider.Emit(new("opening", 1, "Our Lighthouse migration uses JMAP.", true));
        await Until(socket, "response.completed");
        await provider.RetrievalQueries.Reader.ReadAsync();
        for (var index = 0; index < 20; index++)
        {
            provider.Emit(new($"turn-{index}", 1, $"Actual recognized progress update {index}.", true));
            await Until(socket, "response.completed");
            await provider.RetrievalQueries.Reader.ReadAsync();
        }
        provider.Emit(new("follow-up", 1, "Can we deliver it tomorrow?", true));
        await Until(socket, "response.completed");
        var query = await provider.RetrievalQueries.Reader.ReadAsync();
        Assert.Contains("lighthouse", query);
        Assert.Contains("jmap", query);
        Assert.StartsWith("Our Lighthouse migration uses JMAP.", provider.LastHistory![0].Text);
        Assert.True(provider.LastHistory.Length <= 16);
        Assert.True(provider.LastHistory.Sum(turn => turn.Text.Length) <= 25200);
        Assert.DoesNotContain(provider.LastHistory!, turn => turn.Text.Contains("Let's check"));
        await Send(socket, """{"type":"session.stop"}""");
    }

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
        if (route == "profile")
        {
            Assert.Single(completion.GetProperty("suggestions").EnumerateArray());
            Assert.Null(provider.AnswerOptions);
            Assert.Equal(0, provider.Answers);
            Assert.Equal("My name is Mina; my role is Software engineer. My current project is Evaluating email client interoperability.",
                completion.GetProperty("text").GetString());
        }
        else
        {
            Assert.Equal(provider.SpeechOptions, provider.AnswerOptions);
            Assert.Equal(route, provider.Route);
        }
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
        Assert.Single(completion.GetProperty("suggestions").EnumerateArray());
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
    public async Task ContinuousSpeechCompletesPendingReplyThenAnswersLatestFinal()
    {
        var provider = new ControlledProvider { Alternative = "Could we review the plan together?" };
        await using var host = await Host.StartAsync(provider: provider);
        using var socket = await host.ConnectAsync();
        await Send(socket, Start);
        await Receive(socket);
        provider.Emit(new("one", 1, "What is our delivery plan?", true));
        var started = await Until(socket, "response.started");
        await provider.RetrievalStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        provider.Emit(new("two", 1, "And", false));
        await Until(socket, "transcript.partial");
        provider.Emit(new("two", 2, "And who owns launch?", true));
        await Until(socket, "transcript.final");
        provider.Emit(new("three", 1, "What should we do next?", true));
        await Until(socket, "transcript.final");
        provider.AutomaticRetrieval = true;
        provider.Release.SetResult(new("no_matches", []));
        var first = await Until(socket, "response.completed");
        Assert.Equal(started.GetProperty("responseId").GetString(), first.GetProperty("responseId").GetString());
        Assert.Equal("one", first.GetProperty("turnId").GetString());
        Assert.Equal(2, first.GetProperty("suggestions").GetArrayLength());
        var next = await Until(socket, "response.completed");
        Assert.Equal("three", next.GetProperty("turnId").GetString());
        Assert.Equal(2, provider.Answers);
        Assert.Equal(3, provider.LastHistory!.Length);
    }

    [Fact]
    public async Task SettledRepliesSkipBackchannelsAndAnswerGatheredSpeechOnce()
    {
        var provider = new ControlledProvider { AutomaticRetrieval = true, Alternative = "What is the price?" };
        await using var host = await Host.StartAsync(provider: provider);
        using var socket = await host.ConnectAsync();
        await Send(socket, WithOptions(new { responseMode = "grounded", replySettleMs = 400 }));
        await Receive(socket);
        provider.Emit(new("a", 1, "Yeah.", true));
        provider.Emit(new("b", 1, "OK, good.", true));
        await Until(socket, "transcript.final");
        await Until(socket, "transcript.final");
        await Task.Delay(900);
        Assert.Equal(0, provider.Answers);
        provider.Emit(new("c", 1, "So we are going to sell this remote control.", true));
        provider.Emit(new("d", 1, "What price should we pick?", true));
        var completed = await Until(socket, "response.completed");
        Assert.Equal("d", completed.GetProperty("turnId").GetString());
        Assert.True(completed.GetProperty("respondNow").GetBoolean());
        await Task.Delay(900);
        Assert.Equal(1, provider.Answers);
        Assert.Contains(provider.LastHistory!, turn => turn.Text.Contains("remote control"));
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

    [Fact]
    public async Task GroupIntroductionIsFactOnlyWithoutSearchModelOrModelTimingMeasurements()
    {
        var measurements = new System.Collections.Concurrent.ConcurrentBag<string>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, owner) =>
            {
                if (instrument.Meter.Name == MeetingMetrics.MeterName) owner.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((instrument, _, _, _) => measurements.Add(instrument.Name));
        listener.Start();
        var provider = new ControlledProvider();
        await using var host = await Host.StartAsync(provider: provider);
        using var socket = await host.ConnectAsync();
        await Send(socket, WithOptions(new
        {
            responseMode = "balanced", profileConfirmed = true,
            profile = new { name = "Mina", role = "Software engineer", project = "Evaluating email client interoperability" }
        }));
        await Receive(socket);
        provider.Emit(new("turn", 1,
            "And I would like everybody to maybe shortly introduce himself and the project and first touchpoint with the technology.", true));
        var result = await Until(socket, "response.completed");
        Assert.Equal("My name is Mina; my role is Software engineer. My current project is Evaluating email client interoperability.", result.GetProperty("text").GetString());
        Assert.Equal("profile", result.GetProperty("responseRoute").GetString());
        Assert.Equal("disabled", result.GetProperty("grounding").GetString());
        Assert.False(result.GetProperty("retrievalPrefetched").GetBoolean());
        Assert.Empty(result.GetProperty("sources").EnumerateArray());
        Assert.Equal(0, provider.Answers);
        Assert.Equal(0, provider.Retrievals);
        await Send(socket, """{"type":"session.stop"}""");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        Assert.Equal(WebSocketMessageType.Close, (await socket.ReceiveAsync(new byte[4096], timeout.Token)).MessageType);
        Assert.Empty(measurements);
    }

    private sealed class ControlledProvider : IMeetingProvider
    {
        public string? Alternative { get; init; }
        public ConversationTurn[]? LastHistory { get; private set; }
        public System.Threading.Channels.Channel<string> RetrievalQueries { get; } =
            System.Threading.Channels.Channel.CreateUnbounded<string>();
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
            RetrievalQueries.Writer.TryWrite(query);
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
            LastHistory = conversation.ToArray();
            AnswerOptions = options;
            Route = route;
            await Task.Yield();
            cancellation.ThrowIfCancellationRequested();
            yield return "Here is a concise response grounded only in the supplied context.";
        }
        public async IAsyncEnumerable<ReplyUpdate> AnswerWithSuggestionsAsync(IReadOnlyList<ConversationTurn> conversation, Grounding grounding,
            SessionOptions options, string route, [EnumeratorCancellation] CancellationToken cancellation)
        {
            await foreach (var text in AnswerAsync(conversation, grounding, options, route, cancellation))
                yield return new(text);
            if (Alternative is not null) yield return new("", Alternative);
        }
        private sealed class Stream(Action write, Action dispose) : ISpeechStream
        {
            public void Write(byte[] audio) => write();
            public Task CompleteInputAsync(CancellationToken cancellation) => throw new InvalidOperationException("Live session must not signal finite EOF.");
            public ValueTask DisposeAsync() { dispose(); return ValueTask.CompletedTask; }
        }
    }
}
