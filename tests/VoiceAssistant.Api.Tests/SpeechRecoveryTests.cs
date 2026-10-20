using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using Azure.Core;
using Microsoft.CognitiveServices.Speech;
using VoiceAssistant.Api;
using Xunit;

namespace VoiceAssistant.Api.Tests;

public sealed class SpeechTokenRenewalTests
{
    [Fact]
    public async Task FailedRenewalWhileTheTokenIsValidIsRetriedOnTheNextTickAndRecovers()
    {
        var clock = new ManualClock();
        var credential = new ScriptedCredential(clock, fail: [true, false]);
        var delays = new List<TimeSpan>();
        var applied = new List<string>();
        var errors = new List<ProviderException>();
        using var cancellation = new CancellationTokenSource();

        await SpeechAuthorization.RefreshAsync(credential, "/resource", token =>
        {
            applied.Add(token);
            cancellation.Cancel();
        }, errors.Add, cancellation.Token, Advance(clock, delays), expiresOn: clock.GetUtcNow().AddMinutes(20), clock: clock);

        Assert.Empty(errors);
        Assert.Equal(["aad#/resource#test-token-2"], applied);
        Assert.Equal([TimeSpan.FromMinutes(5), SpeechAuthorization.RetryInterval, TimeSpan.FromMinutes(5)], delays);
    }

    [Fact]
    public async Task RenewalThatKeepsFailingEndsTheSessionOnlyWhenTheTokenExpires()
    {
        var clock = new ManualClock();
        var expiresOn = clock.GetUtcNow().AddMinutes(6);
        var credential = new ScriptedCredential(clock, fail: [true]);
        var delays = new List<TimeSpan>();
        var errors = new List<(ProviderException Error, DateTimeOffset At)>();

        await SpeechAuthorization.RefreshAsync(credential, "/resource", _ => Assert.Fail("No token was renewed."),
            error => errors.Add((error, clock.GetUtcNow())), CancellationToken.None, Advance(clock, delays), expiresOn, clock);

        // Renewal fails at 5:00 and 5:30 while the token is still valid; at 6:00 it has expired, so the session ends.
        Assert.Equal([TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30)], delays);
        Assert.Equal(3, credential.Calls);
        var (error, at) = Assert.Single(errors);
        Assert.Equal("speech_authentication_failed", error.Code);
        Assert.Equal(expiresOn, at);
        Assert.DoesNotContain("secret", error.Message);
    }

    private static Func<TimeSpan, CancellationToken, Task> Advance(ManualClock clock, List<TimeSpan> delays) => (duration, token) =>
    {
        delays.Add(duration);
        token.ThrowIfCancellationRequested();
        clock.Advance(duration);
        return Task.CompletedTask;
    };

    /// <summary>Fails or succeeds per call as scripted (the last entry repeats); tokens are valid for an hour.</summary>
    private sealed class ScriptedCredential(TimeProvider clock, bool[] fail) : TokenCredential
    {
        public int Calls { get; private set; }

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Calls++;
            if (fail[Math.Min(Calls, fail.Length) - 1]) throw new InvalidOperationException("secret credential failure");
            return ValueTask.FromResult(new AccessToken($"test-token-{Calls}", clock.GetUtcNow().AddHours(1)));
        }
    }
}

public sealed partial class ApiTests
{
    private const string BeforeFailure = "We moved the fictional rollout to Friday.";

    [Fact]
    public async Task RetryableSpeechCancellationRestartsTheRecognizerAndKeepsTheConversation()
    {
        using var capture = new MetricCapture();
        var clock = new ManualClock();
        var provider = new RestartingSpeechProvider();
        await using var host = await Host.StartAsync(provider: provider, clock: clock);
        using var socket = await host.ConnectAsync();
        await StartConversationAsync(socket, provider);

        await RestartAsync(provider, clock, capture, restart: 1, CancellationErrorCode.ConnectionFailure);

        Assert.True(provider.Stream(0).Disposed);
        // Audio now reaches the new recognizer, and its speech is answered with the earlier turn still in the history.
        await socket.SendAsync(new byte[640], WebSocketMessageType.Binary, true, CancellationToken.None);
        await WaitUntilAsync(() => provider.Stream(1).Writes > 0, TimeSpan.FromSeconds(5));
        Assert.True(provider.Stream(1).Writes > 0);
        provider.Stream(1).Emit(new("after", 1, "Can we still make Friday?", true));
        var messages = await ReceiveUntilAsync(socket, Completed);
        Assert.DoesNotContain(messages, message => TypeOf(message) == "error");
        Assert.Equal("after", messages.Single(message => TypeOf(message) == "response.completed").GetProperty("turnId").GetString());
        Assert.Contains(provider.LastHistory!, turn => turn.Text == BeforeFailure);
        Assert.Contains(capture.Named("voiceassistant.speech.restarts"), measurement =>
            measurement.Tags.Contains(new("code", "connection_failure")) && measurement.Tags.Contains(new("outcome", "restarted")));
        await Send(socket, """{"type":"session.stop"}""");
    }

    [Fact]
    public async Task ALateErrorOfAReplacedRecognizerIsIgnored()
    {
        using var capture = new MetricCapture();
        var clock = new ManualClock();
        var provider = new RestartingSpeechProvider();
        await using var host = await Host.StartAsync(provider: provider, clock: clock);
        using var socket = await host.ConnectAsync();
        await StartConversationAsync(socket, provider);
        await RestartAsync(provider, clock, capture, restart: 1, CancellationErrorCode.ServiceTimeout);

        provider.Stream(0).Fail(CancellationErrorCode.ConnectionFailure);
        provider.Stream(1).Emit(new("after", 1, "Can we still make Friday?", true));

        var messages = await ReceiveUntilAsync(socket, Completed);
        Assert.DoesNotContain(messages, message => TypeOf(message) == "error");
        Assert.Equal(2, provider.StreamCount);
        await Send(socket, """{"type":"session.stop"}""");
    }

    [Fact]
    public async Task AFailureReportedWhileTheNewRecognizerStartsLeadsToAnotherRestart()
    {
        using var capture = new MetricCapture();
        var clock = new ManualClock();
        var provider = new RestartingSpeechProvider { FailDuringStart = CancellationErrorCode.ServiceUnavailable };
        await using var host = await Host.StartAsync(provider: provider, clock: clock);
        using var socket = await host.ConnectAsync();
        await StartConversationAsync(socket, provider);

        var timers = clock.TimersCreated;
        provider.Stream(0).Fail(CancellationErrorCode.ConnectionFailure);
        await clock.WaitForTimersAsync(timers + 1);
        clock.Advance(MeetingSession.SpeechRestartDelay(1));
        // The second recognizer reported a failure before the session took it over, so it is replaced after the next backoff.
        await clock.WaitForTimersAsync(timers + 2);
        clock.Advance(MeetingSession.SpeechRestartDelay(2));
        await WaitUntilAsync(() => capture.Named("voiceassistant.speech.restarts")
            .Count(measurement => measurement.Tags.Contains(new("outcome", "restarted"))) >= 2, TimeSpan.FromSeconds(5));

        Assert.Equal(3, provider.StreamCount);
        Assert.True(provider.Stream(1).Disposed);
        Assert.Contains(capture.Named("voiceassistant.speech.restarts"), measurement => measurement.Tags.Contains(new("code", "service_unavailable")));
        provider.Stream(2).Emit(new("after", 1, "Can we still make Friday?", true));
        var messages = await ReceiveUntilAsync(socket, Completed);
        Assert.DoesNotContain(messages, message => TypeOf(message) == "error");
        await Send(socket, """{"type":"session.stop"}""");
    }

    [Fact]
    public async Task SpeechRestartsAreLimitedPerSessionAndThenTheSessionEnds()
    {
        using var capture = new MetricCapture();
        var clock = new ManualClock();
        var provider = new RestartingSpeechProvider();
        await using var host = await Host.StartAsync(provider: provider, clock: clock);
        using var socket = await host.ConnectAsync();
        await StartConversationAsync(socket, provider);

        for (var restart = 1; restart <= MeetingSession.MaxSpeechRestarts; restart++)
            await RestartAsync(provider, clock, capture, restart, CancellationErrorCode.ServiceTimeout);
        provider.Stream(MeetingSession.MaxSpeechRestarts).Fail(CancellationErrorCode.ServiceTimeout);

        var messages = await ReceiveUntilCloseAsync(socket);
        Assert.Equal("speech_unavailable", Assert.Single(messages, message => TypeOf(message) == "error").GetProperty("code").GetString());
        Assert.Equal(MeetingSession.MaxSpeechRestarts + 1, provider.StreamCount);
        await WaitUntilAsync(() => provider.AllDisposed, TimeSpan.FromSeconds(5));
        Assert.True(provider.AllDisposed);
    }

    [Fact]
    public async Task FailedRestartAttemptsCountTowardTheLimitAndThenTheSessionEnds()
    {
        using var capture = new MetricCapture();
        var clock = new ManualClock();
        var provider = new RestartingSpeechProvider { ThrowOnRestarts = int.MaxValue };
        await using var host = await Host.StartAsync(provider: provider, clock: clock);
        using var socket = await host.ConnectAsync();
        await StartConversationAsync(socket, provider);

        var timers = clock.TimersCreated;
        provider.Stream(0).Fail(CancellationErrorCode.ConnectionFailure);
        for (var restart = 1; restart <= MeetingSession.MaxSpeechRestarts; restart++)
        {
            await clock.WaitForTimersAsync(timers + restart);
            clock.Advance(MeetingSession.SpeechRestartDelay(restart));
            await WaitUntilAsync(() => provider.StartCalls == restart + 1, TimeSpan.FromSeconds(5));
        }

        var messages = await ReceiveUntilCloseAsync(socket);
        Assert.Equal("speech_unavailable", Assert.Single(messages, message => TypeOf(message) == "error").GetProperty("code").GetString());
        Assert.Equal(MeetingSession.MaxSpeechRestarts + 1, provider.StartCalls);
        Assert.Equal(MeetingSession.MaxSpeechRestarts, capture.Named("voiceassistant.speech.restarts")
            .Count(measurement => measurement.Tags.Contains(new("outcome", "failed"))));
    }

    [Fact]
    public async Task NonRetryableSpeechCancellationStillEndsTheSessionAtOnce()
    {
        var provider = new RestartingSpeechProvider();
        await using var host = await Host.StartAsync(provider: provider);
        using var socket = await host.ConnectAsync();
        await StartConversationAsync(socket, provider);

        provider.Stream(0).Fail(CancellationErrorCode.AuthenticationFailure);

        var messages = await ReceiveUntilCloseAsync(socket);
        Assert.Equal("speech_unavailable", Assert.Single(messages, message => TypeOf(message) == "error").GetProperty("code").GetString());
        Assert.Equal(1, provider.StartCalls);
    }

    [Theory]
    [InlineData(1, 500)]
    [InlineData(2, 1000)]
    [InlineData(3, 2000)]
    public void SpeechRestartBackoffDoublesFromHalfASecond(int restart, int milliseconds) =>
        Assert.Equal(TimeSpan.FromMilliseconds(milliseconds), MeetingSession.SpeechRestartDelay(restart));

    /// <summary>Starts a conversation-mode session and answers one statement, so the history holds a turn from the first recognizer.</summary>
    private static async Task StartConversationAsync(ClientWebSocket socket, RestartingSpeechProvider provider)
    {
        await Send(socket, WithOptions(new { responseMode = "conversation" }));
        Assert.Equal("session.ready", TypeOf(await Receive(socket)));
        provider.Stream(0).Emit(new("before", 1, BeforeFailure, true));
        await ReceiveUntilAsync(socket, Completed);
    }

    /// <summary>
    /// Fails the current recognizer, lets the restart backoff pass on the manual clock, and waits until the session has the
    /// new recognizer.
    /// </summary>
    private static async Task RestartAsync(RestartingSpeechProvider provider, ManualClock clock, MetricCapture capture, int restart,
        CancellationErrorCode failWith)
    {
        var timers = clock.TimersCreated;
        provider.Stream(restart - 1).Fail(failWith);
        await clock.WaitForTimersAsync(timers + 1);
        clock.Advance(MeetingSession.SpeechRestartDelay(restart));
        await WaitUntilAsync(() => capture.Named("voiceassistant.speech.restarts").Count(measurement =>
            measurement.Tags.Contains(new("outcome", "restarted"))) >= restart, TimeSpan.FromSeconds(5));
        Assert.Equal(restart + 1, provider.StreamCount);
    }
}

/// <summary>A speech provider whose recognizers a test can fail, as the Azure one does on a cancellation.</summary>
internal sealed class RestartingSpeechProvider : IMeetingProvider
{
    private readonly object gate = new();
    private readonly List<SpeechStub> streams = [];
    private int startCalls;
    /// <summary>How many restarts (starts after the first) throw instead of returning a recognizer.</summary>
    public int ThrowOnRestarts { get; set; }
    /// <summary>A cancellation the next restarted recognizer reports while it starts, before the session took it over.</summary>
    public CancellationErrorCode? FailDuringStart { get; set; }
    public ConversationTurn[]? LastHistory { get; private set; }
    public int StartCalls => Volatile.Read(ref startCalls);
    public int StreamCount { get { lock (gate) return streams.Count; } }
    public bool AllDisposed { get { lock (gate) return streams.All(stream => stream.Disposed); } }
    public SpeechStub Stream(int index) { lock (gate) return streams[index]; }

    public Task<ISpeechStream> StartSpeechAsync(Action<Transcript> transcript, Action<ProviderException> error, CancellationToken cancellation) =>
        throw new InvalidOperationException("Session must supply typed options.");

    public Task<ISpeechStream> StartSpeechAsync(SessionOptions options, Action<Transcript> transcript, Action<ProviderException> error,
        CancellationToken cancellation)
    {
        var call = Interlocked.Increment(ref startCalls);
        if (call > 1 && ThrowOnRestarts-- > 0) throw new ProviderException("speech_unavailable", "Synthetic start failure.");
        var stream = new SpeechStub(transcript, error);
        lock (gate) streams.Add(stream);
        if (call > 1 && FailDuringStart is { } code)
        {
            FailDuringStart = null;
            stream.Fail(code);
        }
        return Task.FromResult<ISpeechStream>(stream);
    }

    public Task<Grounding> RetrieveAsync(string query, string objectId, CancellationToken cancellation) =>
        Task.FromResult(new Grounding("no_matches", []));

    public async IAsyncEnumerable<string> AnswerAsync(IReadOnlyList<ConversationTurn> conversation, Grounding grounding,
        [EnumeratorCancellation] CancellationToken cancellation)
    {
        LastHistory = conversation.ToArray();
        await Task.Yield();
        yield return "A short synthetic reply.";
    }
}

internal sealed class SpeechStub(Action<Transcript> transcript, Action<ProviderException> error) : ISpeechStream
{
    private int writes;
    public int Writes => Volatile.Read(ref writes);
    public volatile bool Disposed;

    public void Emit(Transcript value) => transcript(value);

    /// <summary>Reports a recognizer cancellation exactly as the Azure provider does.</summary>
    public void Fail(CancellationErrorCode code) => error(new ProviderException("speech_unavailable",
        "Speech recognition ended unexpectedly. Reconnect to continue.") { SpeechCancellation = new(CancellationReason.Error, code) });

    public void Write(byte[] audio) => Interlocked.Increment(ref writes);

    public Task CompleteInputAsync(CancellationToken cancellation) =>
        throw new InvalidOperationException("Live session must not signal finite EOF.");

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}
