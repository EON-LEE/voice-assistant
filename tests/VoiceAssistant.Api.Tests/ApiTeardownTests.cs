using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using VoiceAssistant.Api;
using Xunit;

namespace VoiceAssistant.Api.Tests;

public sealed partial class ApiTests
{
    [Theory]
    [InlineData("speech")]
    [InlineData("retrieval")]
    [InlineData("generation")]
    public async Task AReconnectGetsTheSlotInTimeAndNothingIsLeftUnobservedWhenCleanupHangs(string hang)
    {
        var unobserved = await UnobservedFailuresOfAsync(async () =>
        {
            var logs = new CollectingLogs();
            var provider = new StallingProvider(hang);
            await using var host = await Host.StartAsync(provider: provider, logs: logs);
            try
            {
                using var stalled = await host.ConnectAsync();
                await Send(stalled, WithOptions(new { responseMode = hang == "generation" ? "conversation" : "grounded" }));
                Assert.Equal("session.ready", TypeOf(await Receive(stalled)));
                if (hang == "speech")
                {
                    // A few partials, then nothing: the recognizer stalled, and stopping it will block.
                    foreach (var (revision, text) in new[] { (1, "what is"), (2, "what is the"), (3, "what is the rollout") })
                    {
                        provider.Emit(new("stalled", revision, text, false));
                        Assert.Equal("transcript.partial", TypeOf(await Receive(stalled)));
                    }
                }
                else
                {
                    // The reply's search or model call is stuck and ignores cancellation.
                    provider.Emit(new("stalled", 1, "What is the rollout plan?", true));
                    await provider.Stalled.Task.WaitAsync(TimeSpan.FromSeconds(5));
                }

                // The client reconnects, superseding the stalled session, which must release this user's slot in time.
                var closing = AnswerCloseAsync(stalled);
                var waited = System.Diagnostics.Stopwatch.StartNew();
                using var reconnect = await host.ConnectAsync();
                Assert.InRange(waited.Elapsed, TimeSpan.Zero, MeetingSession.ShutdownBudget + TimeSpan.FromSeconds(1.5));
                await Send(reconnect, Start);
                Assert.Equal("session.ready", TypeOf(await Receive(reconnect)));
                Assert.Contains(await closing, message => TypeOf(message) == "error" &&
                    message.GetProperty("code").GetString() == "session_superseded");
                if (hang == "speech") Assert.True(provider.Stalled.Task.IsCompleted);
                // One content-free line names the phase still running.
                var running = hang == "speech" ? "speech" : "cleanup";
                Assert.Contains(logs.Messages, message => message.Contains("Meeting session shutdown took ", StringComparison.Ordinal) &&
                    message.EndsWith($"; still running in the background: {running}.", StringComparison.Ordinal));

                // Once released, the stalled work fails; that failure is observed, and for the recognizer logged without detail.
                provider.Release();
                await provider.Ended.Task.WaitAsync(TimeSpan.FromSeconds(5));
                if (hang == "speech")
                {
                    await WaitUntilAsync(() => logs.Messages.Any(message => message.Contains("Speech cleanup failed", StringComparison.Ordinal)),
                        TimeSpan.FromSeconds(5));
                    Assert.Contains(logs.Messages, message => message.Contains("Speech cleanup failed", StringComparison.Ordinal));
                }
                await Task.Delay(200);
                await Send(reconnect, """{"type":"session.stop"}""");
                await ReceiveUntilCloseAsync(reconnect);
                Assert.DoesNotContain(logs.Messages, message => message.Contains(StallingProvider.Failure, StringComparison.Ordinal));
            }
            finally
            {
                // Whatever happened above, nothing stays blocked behind this test.
                provider.Release();
            }
        });
        // The watch works (it saw the control), and no failure of the stalled cleanup went unobserved.
        Assert.Contains(UnobservedControl, unobserved);
        Assert.DoesNotContain(StallingProvider.Failure, unobserved);
    }

    [Theory]
    [InlineData("start")]
    [InlineData("late start")]
    public async Task AReconnectGetsTheSlotInTimeWhenTheRecognizerStartOfTheSessionItSupersedesHangs(string hang)
    {
        var unobserved = await UnobservedFailuresOfAsync(async () =>
        {
            var logs = new CollectingLogs();
            var provider = new StallingProvider(hang);
            await using var host = await Host.StartAsync(provider: provider, logs: logs);
            try
            {
                // The recognizer start stalls: cancelled, it blocks in its own cleanup, or it ignores cancellation altogether.
                using var stalled = await host.ConnectAsync();
                await Send(stalled, Start);
                await provider.Stalled.Task.WaitAsync(TimeSpan.FromSeconds(5));

                var closing = AnswerCloseAsync(stalled);
                var waited = System.Diagnostics.Stopwatch.StartNew();
                using var reconnect = await host.ConnectAsync();
                Assert.InRange(waited.Elapsed, TimeSpan.Zero, MeetingSession.ShutdownBudget + TimeSpan.FromSeconds(1.5));
                await Send(reconnect, Start);
                Assert.Equal("session.ready", TypeOf(await Receive(reconnect)));
                var ended = await closing;
                Assert.Contains(ended, message => TypeOf(message) == "error");
                Assert.DoesNotContain(ended, message => TypeOf(message) == "session.ready");
                Assert.Contains(logs.Messages, message => message.Contains("Meeting session shutdown took ", StringComparison.Ordinal) &&
                    message.EndsWith("; still running in the background: speech.", StringComparison.Ordinal));

                // Released, the cancelled start fails, observed; a recognizer that starts after all is disposed at once.
                provider.Release();
                await provider.Ended.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await Send(reconnect, """{"type":"session.stop"}""");
                await ReceiveUntilCloseAsync(reconnect);
                await WaitUntilAsync(() => provider.Disposed == provider.Streams, TimeSpan.FromSeconds(5));
                Assert.Equal(hang == "late start" ? 2 : 1, provider.Streams);
                Assert.Equal(provider.Streams, provider.Disposed);
                Assert.DoesNotContain(logs.Messages, message => message.Contains(StallingProvider.Failure, StringComparison.Ordinal));
            }
            finally
            {
                provider.Release();
            }
        });
        Assert.Contains(UnobservedControl, unobserved);
        Assert.DoesNotContain(StallingProvider.Failure, unobserved);
    }

    [Fact]
    public async Task ANormalShutdownStaysQuickAndQuietAndStopsTheRecognizerBeforeTheNextSessionStarts()
    {
        var logs = new CollectingLogs();
        var provider = new StallingProvider("none");
        await using var host = await Host.StartAsync(provider: provider, logs: logs);
        using var first = await host.ConnectAsync();
        await Send(first, Start);
        Assert.Equal("session.ready", TypeOf(await Receive(first)));
        provider.Emit(new("question", 1, "What is the rollout plan?", true));
        await ReceiveUntilAsync(first, Completed);

        var closing = AnswerCloseAsync(first);
        var waited = System.Diagnostics.Stopwatch.StartNew();
        using var second = await host.ConnectAsync();
        // It never waits out the shutdown budget, which only cleanup that hangs reaches.
        Assert.InRange(waited.Elapsed, TimeSpan.Zero, MeetingSession.ShutdownBudget);
        await Send(second, Start);
        Assert.Equal("session.ready", TypeOf(await Receive(second)));
        await closing;
        await Send(second, """{"type":"session.stop"}""");
        await ReceiveUntilCloseAsync(second);

        // The first recognizer was gone before the second started, and nothing failed or was left running in the background.
        Assert.Equal(2, provider.Streams);
        Assert.False(provider.Overlapped);
        await WaitUntilAsync(() => provider.Disposed == 2, TimeSpan.FromSeconds(3));
        Assert.Equal(2, provider.Disposed);
        Assert.DoesNotContain(logs.Messages, message => message.Contains("cleanup failed", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Meeting shutdown step", StringComparison.Ordinal) ||
            (message.Contains("still running", StringComparison.Ordinal) && !message.EndsWith("in the background: none.", StringComparison.Ordinal)));
    }

    private const string UnobservedControl = "Synthetic control: a failure nobody observes.";

    /// <summary>
    /// Runs a test, then collects the failures of tasks nobody observed, which the finalizer reports once they are collected.
    /// A control failure dropped at the end proves that the watch can see one.
    /// </summary>
    private static async Task<IReadOnlyCollection<string>> UnobservedFailuresOfAsync(Func<Task> test)
    {
        var unobserved = new ConcurrentQueue<string>();
        void Watch(object? sender, UnobservedTaskExceptionEventArgs args)
        {
            foreach (var exception in args.Exception.Flatten().InnerExceptions) unobserved.Enqueue(exception.Message);
        }
        TaskScheduler.UnobservedTaskException += Watch;
        try
        {
            await test();
            DropUnobservedFailure();
            for (var attempt = 0; attempt < 3; attempt++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= Watch;
        }
        return unobserved;
    }

    /// <summary>Reads until the server closes, then answers the close as a browser does at once (ClientWebSocket does not).</summary>
    private static async Task<List<System.Text.Json.JsonElement>> AnswerCloseAsync(System.Net.WebSockets.ClientWebSocket socket)
    {
        var messages = await ReceiveUntilCloseAsync(socket);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await socket.CloseOutputAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "", timeout.Token);
        return messages;
    }

    /// <summary>A faulted task nobody observes, which the finalizer reports: proves the watch in a test can see one.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void DropUnobservedFailure() => _ = Task.FromException(new InvalidOperationException(UnobservedControl));

    /// <summary>
    /// A provider whose first session's recognizer or cleanup can hang: its start stalls (cancelled, it blocks in its own cleanup
    /// as a stalled native recognizer can; or it ignores cancellation and still returns a recognizer later), stopping its
    /// recognizer blocks the calling thread, its search ignores cancellation and never returns, or its model stream ignores
    /// cancellation. Released by the test, each then fails (or starts late), so a failure nobody observed would be reported as an
    /// unobserved task exception.
    /// </summary>
    private sealed class StallingProvider(string hang) : IMeetingProvider
    {
        public const string Failure = "Synthetic failure of a stalled cleanup.";
        private readonly ManualResetEventSlim release = new();
        private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int starts;
        private int streams;
        private int disposed;
        public int Streams => Volatile.Read(ref streams);
        public int Disposed => Volatile.Read(ref disposed);
        /// <summary>A recognizer was started while an earlier one had not been disposed yet.</summary>
        public volatile bool Overlapped;
        public Action<Transcript> Emit { get; private set; } = _ => throw new InvalidOperationException("Speech is not started.");
        public TaskCompletionSource Stalled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Ended { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release()
        {
            release.Set();
            released.TrySetResult();
        }

        public async Task<ISpeechStream> StartSpeechAsync(Action<Transcript> transcript, Action<ProviderException> error,
            CancellationToken cancellation)
        {
            Emit = transcript;
            var first = Interlocked.Increment(ref starts) == 1;
            if (first && hang == "start")
            {
                Stalled.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, cancellation); }
                catch (OperationCanceledException)
                {
                    // Like AzureMeetingProvider, a cancelled start disposes the recognizer it set up, and stopping a stalled one blocks.
                    await Task.Run(() => release.Wait(), CancellationToken.None);
                    Ended.TrySetResult();
                    throw new InvalidOperationException(Failure);
                }
            }
            if (first && hang == "late start")
            {
                Stalled.TrySetResult();
                await released.Task;
            }
            if (Streams > Disposed) Overlapped = true;
            Interlocked.Increment(ref streams);
            if (first && hang == "late start") Ended.TrySetResult();
            return new Stream(this, first && hang == "speech");
        }

        public Task<Grounding> RetrieveAsync(string query, string objectId, CancellationToken cancellation)
        {
            if (hang != "retrieval") return Task.FromResult(new Grounding("no_matches", []));
            Stalled.TrySetResult();
            return released.Task.ContinueWith<Grounding>(_ =>
            {
                Ended.TrySetResult();
                throw new InvalidOperationException(Failure);
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }

        public IAsyncEnumerable<string> AnswerAsync(IReadOnlyList<ConversationTurn> conversation, Grounding grounding, CancellationToken cancellation) =>
            throw new InvalidOperationException("Replies use AnswerWithSuggestionsAsync.");

        public async IAsyncEnumerable<ReplyUpdate> AnswerWithSuggestionsAsync(IReadOnlyList<ConversationTurn> conversation, Grounding grounding,
            SessionOptions options, string responseRoute, [EnumeratorCancellation] CancellationToken cancellation)
        {
            if (hang == "generation")
            {
                Stalled.TrySetResult();
                await released.Task;
                Ended.TrySetResult();
                throw new InvalidOperationException(Failure);
            }
            await Task.Yield();
            yield return new("A short synthetic reply.");
        }

        private sealed class Stream(StallingProvider owner, bool stall) : ISpeechStream
        {
            public void Write(byte[] audio) { }
            public Task CompleteInputAsync(CancellationToken cancellation) => Task.CompletedTask;

            public ValueTask DisposeAsync()
            {
                if (stall)
                {
                    owner.Stalled.TrySetResult();
                    // A stalled native recognizer can block the thread that stops it.
                    owner.release.Wait();
                    owner.Ended.TrySetResult();
                    throw new InvalidOperationException(Failure);
                }
                Interlocked.Increment(ref owner.disposed);
                return ValueTask.CompletedTask;
            }
        }
    }
}
