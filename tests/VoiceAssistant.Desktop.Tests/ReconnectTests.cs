using System.Net.WebSockets;
using System.Reflection;
using System.Threading.Channels;
using System.Windows.Controls;
using System.Windows.Threading;
using VoiceAssistant.Desktop.Audio;
using VoiceAssistant.Desktop.Protocol;

namespace VoiceAssistant.Desktop.Tests;

public sealed class ReconnectTests
{
    [Theory]
    [InlineData("websocket", true)]
    [InlineData("server-closed", true)]
    [InlineData("busy", true)]
    [InlineData("timeout", true)]
    [InlineData("invalid_start", false)]
    [InlineData("superseded", false)]
    [InlineData("unauthorized", false)]
    [InlineData("forbidden-api", false)]
    [InlineData("protocol", false)]
    [InlineData("signin", false)]
    public void OnlyNetworkAndServiceInterruptionsAreRetried(string kind, bool transient)
    {
        Exception failure = kind switch
        {
            "websocket" => new WebSocketException("The remote party closed the WebSocket connection without completing the close handshake."),
            "server-closed" => new IOException("Server disconnected (NormalClosure): Session ended"),
            "busy" => new ApiRequestException("busy"),
            "timeout" => new TaskCanceledException(),
            "invalid_start" => new IOException("invalid_start: First message must be protocol v1"),
            "superseded" => new IOException("session_superseded: Live started in another window or device."),
            "unauthorized" => new IOException("unauthorized: sign in"),
            "forbidden-api" => new ApiRequestException("forbidden"),
            "protocol" => new InvalidDataException("Unsupported server event"),
            _ => new InvalidOperationException("Microsoft requires authentication again.")
        };
        var method = typeof(MainWindow).GetMethod("IsTransientDisconnect", BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert.Equal(transient, (bool)method.Invoke(null, [failure])!);
    }

    [Fact]
    public async Task DroppedConnectionReconnectsAutomaticallyAndKeepsConversationAndSuggestions()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(async () =>
            {
                var settings = new ClientSettings { Mode = ConnectionMode.Production };
                using var api = new AuthenticatedApiClient(settings, new NoNetworkIdentity(), new OfflineHandler());
                var window = new MainWindow(settings, new NativeIdentity(settings), api);
                try
                {
                    var first = new ScriptedTransport();
                    first.Events.Writer.TryWrite(new("session.ready"));
                    first.Events.Writer.TryWrite(new("transcript.final", "t1", 1, "What is the plan?"));
                    first.Events.Writer.TryWrite(new("response.started", "t1", ResponseId: "r1"));
                    first.Events.Writer.TryWrite(new("response.completed", "t1", Text: "Review first.", ResponseId: "r1",
                        Sources: [], Suggestions: ["Review first.", "Could we check the owner?"]));
                    first.FailAfterEvents = new WebSocketException("The remote party closed the WebSocket connection without completing the close handshake.");
                    var second = new ScriptedTransport();
                    second.Events.Writer.TryWrite(new("session.ready"));
                    second.Events.Writer.TryWrite(new("transcript.final", "t2", 1, "And the date?"));
                    var transports = new Queue<ScriptedTransport>([second]);
                    var type = typeof(MainWindow);
                    type.GetProperty("TransportFactory", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .SetValue(window, (Func<IMeetingTransport>)(() => transports.Dequeue()));
                    type.GetProperty("AudioSourceFactory", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .SetValue(window, (Func<IAudioSource>)(() => new SilentSource()));
                    var lifetime = new CancellationTokenSource();
                    type.GetField("client", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, new MeetingClient(first));
                    type.GetField("lifetime", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, lifetime);
                    var run = (Task)type.GetMethod("RunMeetingAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .Invoke(window, [settings, null, lifetime.Token])!;
                    var state = (ReplyState)type.GetField("state", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                    var deadline = DateTime.UtcNow.AddSeconds(8);
                    while (state.Turns.Count < 2 && DateTime.UtcNow < deadline) await Task.Delay(25);
                    Assert.True(first.Disposed);
                    Assert.Equal(["t1", "t2"], state.Turns.Select(turn => turn.TurnId));
                    Assert.Equal("r1", state.Display!.ResponseId);
                    Assert.Equal(2, state.Display.Answers.Count);
                    Assert.False(run.IsCompleted);
                    lifetime.Cancel();
                    await run.WaitAsync(TimeSpan.FromSeconds(5));
                    Assert.True(second.Disposed);
                    Assert.True(((Button)window.FindName("StartButton")).IsEnabled);
                    done.TrySetResult();
                }
                catch (Exception ex) { done.TrySetException(ex); }
                finally
                {
                    await window.CloseForOwnerAsync();
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
    }

    private sealed class OfflineHandler : System.Net.Http.HttpMessageHandler
    {
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable));
    }

    private sealed class NoNetworkIdentity : IAccessTokenProvider
    {
        public Task<string> AcquireTokenAsync(bool interactive, CancellationToken cancellationToken = default) =>
            Task.FromResult("test-only");
    }

    private sealed class SilentSource : IAudioSource
    {
        public event Action<byte[]>? Data { add { } remove { } }
        public event Action<Exception>? Failed { add { } remove { } }
        public void Start() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ScriptedTransport : IMeetingTransport
    {
        public Channel<ServerEvent> Events { get; } = Channel.CreateUnbounded<ServerEvent>();
        public Exception? FailAfterEvents { get; set; }
        public bool Disposed { get; private set; }
        public Task ConnectAsync(ClientSettings settings, CancellationToken token) => Task.CompletedTask;
        public Task SendTextAsync(string json, CancellationToken token) => Task.CompletedTask;
        public Task SendAudioAsync(byte[] pcm, CancellationToken token) => Task.CompletedTask;
        public async Task<ServerEvent> ReceiveAsync(CancellationToken token)
        {
            if (Events.Reader.TryRead(out var next)) return next;
            if (FailAfterEvents is not null) throw FailAfterEvents;
            return await Events.Reader.ReadAsync(token);
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
