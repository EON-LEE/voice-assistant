using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using VoiceAssistant.Api;
using Xunit;

namespace VoiceAssistant.Api.Tests;

/// <summary>Collects every log line the app writes, for assertions about what is (not) logged.</summary>
internal sealed class CollectingLogs : ILoggerProvider
{
    public ConcurrentQueue<string> Messages { get; } = new();
    public ILogger CreateLogger(string categoryName) => new Logger(Messages);
    public void Dispose() { }

    private sealed class Logger(ConcurrentQueue<string> messages) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            messages.Enqueue($"{logLevel}: {formatter(state, exception)}");
    }
}

public sealed partial class ApiTests
{
    [Fact]
    public async Task AClassifierCancelledWithItsReplyIsNotLoggedAsAFailure()
    {
        using var capture = new MetricCapture();
        var logs = new CollectingLogs();
        var provider = new ClassifierProvider { HangUntilCancelled = true };
        await using var host = await Host.StartAsync(provider: provider, logs: logs);
        using var socket = await host.ConnectAsync();
        await Send(socket, WithOptions(new { responseMode = "conversation" }));
        Assert.Equal("session.ready", TypeOf(await Receive(socket)));

        provider.Emit(new("q", 1, "What do you think?", true));
        // The answer is complete, so the reply now only waits for the classifier: cancel it right there.
        await ReceiveUntilAsync(socket, found => found.Any(message => TypeOf(message) == "response.delta" && message.TryGetProperty("primaryDone", out _)));
        await Send(socket, """{"type":"response.cancel"}""");
        await Until(socket, "response.cancelled");
        await WaitUntilAsync(() => capture.Named("voiceassistant.reply.classifier_wait").Any(), TimeSpan.FromSeconds(5));

        // The metric keeps its outcome as before; the log no longer calls a cancellation a classifier failure.
        Assert.Contains(capture.Named("voiceassistant.reply.classifier_wait"), measurement => measurement.Tags.Contains(new("outcome", "failed")));
        Assert.DoesNotContain(logs.Messages, message => message.Contains("Turn classifier failed", StringComparison.Ordinal));
        await Send(socket, """{"type":"session.stop"}""");
    }

    [Fact]
    public async Task AGenuineClassifierFailureIsStillLogged()
    {
        var logs = new CollectingLogs();
        var provider = new ClassifierProvider { Failure = new InvalidOperationException("Synthetic classifier failure.") };
        await using var host = await Host.StartAsync(provider: provider, logs: logs);
        using var socket = await host.ConnectAsync();
        await Send(socket, WithOptions(new { responseMode = "conversation" }));
        Assert.Equal("session.ready", TypeOf(await Receive(socket)));

        provider.Emit(new("q", 1, "What do you think?", true));
        await ReceiveUntilAsync(socket, Completed);

        Assert.Contains(logs.Messages, message =>
            message.Contains("Turn classifier failed: InvalidOperationException, HTTP status none", StringComparison.Ordinal));
        await Send(socket, """{"type":"session.stop"}""");
    }

    /// <summary>Answers at once; its turn classifier either waits until the reply is cancelled or fails.</summary>
    private sealed class ClassifierProvider : IMeetingProvider
    {
        public bool HangUntilCancelled { get; init; }
        public Exception? Failure { get; init; }
        public Action<Transcript> Emit { get; private set; } = _ => throw new InvalidOperationException("Speech is not started.");

        public Task<ISpeechStream> StartSpeechAsync(Action<Transcript> transcript, Action<ProviderException> error, CancellationToken cancellation)
        {
            Emit = transcript;
            return Task.FromResult<ISpeechStream>(new SpeechStub(transcript, error));
        }

        public Task<Grounding> RetrieveAsync(string query, string objectId, CancellationToken cancellation) =>
            Task.FromResult(new Grounding("disabled", []));

        public async IAsyncEnumerable<string> AnswerAsync(IReadOnlyList<ConversationTurn> conversation, Grounding grounding,
            [EnumeratorCancellation] CancellationToken cancellation)
        {
            await Task.Yield();
            yield return "A short synthetic reply.";
        }

        public Task<bool> ShouldRespondAsync(IReadOnlyList<ConversationTurn> conversation, CancellationToken cancellation) =>
            Failure is not null ? Task.FromException<bool>(Failure) : HangAsync(cancellation);

        private async Task<bool> HangAsync(CancellationToken cancellation)
        {
            await Task.Delay(HangUntilCancelled ? Timeout.InfiniteTimeSpan : TimeSpan.Zero, cancellation);
            return false;
        }
    }
}
