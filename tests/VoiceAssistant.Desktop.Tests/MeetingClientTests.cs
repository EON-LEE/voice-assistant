using System.Collections.Concurrent;
using System.Threading.Channels;
using VoiceAssistant.Desktop.Audio;
using VoiceAssistant.Desktop.Protocol;

namespace VoiceAssistant.Desktop.Tests;

public sealed class MeetingClientTests
{
    [Fact]
    public async Task OpensAudioOnlyAfterReadyAndDisposesOnStop()
    {
        var transport = new ControlledTransport();
        var source = new TestAudioSource();
        var client = new MeetingClient(transport);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        int created = 0;
        Task run = client.RunAsync(new(), () => { created++; return source; }, _ => { }, _ => { }, timeout.Token);
        await transport.Started.Task.WaitAsync(timeout.Token);
        Assert.Equal(0, created);
        Assert.False(source.Started);
        transport.Events.Writer.TryWrite(new("session.ready"));
        await source.StartSignal.Task.WaitAsync(timeout.Token);
        Assert.Equal(1, created);
        source.Emit(new byte[640]);
        await transport.AudioSent.Task.WaitAsync(timeout.Token);
        await client.StopAsync();
        await run;
        Assert.True(source.Disposed);
        Assert.True(transport.Disposed);
        Assert.Contains("""{"type":"session.stop"}""", transport.Commands);
    }

    [Fact]
    public async Task RejectsHandshakeAndNeverCreatesAudio()
    {
        var transport = new ControlledTransport();
        transport.Events.Writer.TryWrite(new("transcript.partial", "t", 1, "bad ordering"));
        var client = new MeetingClient(transport);
        await Assert.ThrowsAsync<InvalidDataException>(() => client.RunAsync(new(),
            () => throw new InvalidOperationException("Must not open a device"), _ => { }, _ => { }, CancellationToken.None));
        Assert.True(transport.Disposed);
    }

    [Fact]
    public async Task OverflowStopsRatherThanAccumulatingOrSilentlyDropping()
    {
        var transport = new ControlledTransport();
        transport.Events.Writer.TryWrite(new("session.ready"));
        var source = new TestAudioSource { OnStartBytes = new byte[640 * 51] };
        var client = new MeetingClient(transport);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var error = await Assert.ThrowsAsync<IOException>(() =>
            client.RunAsync(new(), () => source, _ => { }, _ => { }, timeout.Token));
        Assert.Contains("queue limit", error.Message);
        Assert.True(source.Disposed);
        Assert.True(transport.Disposed);
    }

    [Fact]
    public async Task PauseDropsAudioCancelsReplyAndResumeSendsNewFrames()
    {
        var transport = new ControlledTransport();
        transport.Events.Writer.TryWrite(new("session.ready"));
        var source = new TestAudioSource();
        var client = new MeetingClient(transport);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task run = client.RunAsync(new(), () => source, _ => { }, _ => { }, timeout.Token);
        await source.StartSignal.Task.WaitAsync(timeout.Token);
        await client.SetPausedAsync(true, timeout.Token);
        source.Emit(new byte[640 * 100]);
        Assert.Empty(transport.Audio);
        Assert.Contains("""{"type":"response.cancel"}""", transport.Commands);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.RequestResponseAsync(timeout.Token));
        await client.SetPausedAsync(false, timeout.Token);
        source.Emit(new byte[640]);
        await transport.AudioSent.Task.WaitAsync(timeout.Token);
        Assert.Single(transport.Audio);
        await client.StopAsync();
        await run;
    }

    [Fact]
    public async Task DeviceFailurePropagatesAndReleasesResources()
    {
        var transport = new ControlledTransport();
        transport.Events.Writer.TryWrite(new("session.ready"));
        var source = new TestAudioSource();
        var client = new MeetingClient(transport);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task run = client.RunAsync(new(), () => source, _ => { }, _ => { }, timeout.Token);
        await source.StartSignal.Task.WaitAsync(timeout.Token);
        source.Fail(new IOException("Endpoint disconnected"));
        var error = await Assert.ThrowsAsync<IOException>(() => run);
        Assert.Equal("Endpoint disconnected", error.Message);
        Assert.True(source.Disposed);
    }

    [Fact]
    public async Task ExplicitDemoAutoRepliesToFinalTranscriptAndAllowsRetryWithoutDevicesOrNetwork()
    {
        var client = new MeetingClient(new DemoMeetingTransport());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var completed = new TaskCompletionSource<ServerEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var retried = new TaskCompletionSource<ServerEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task run = client.RunAsync(new(), () => new SyntheticAudioSource(), e =>
        {
            if (e.Type != "response.completed") return;
            if (e.ResponseId == "demo-1") completed.TrySetResult(e);
            if (e.ResponseId == "demo-2") retried.TrySetResult(e);
        }, _ => { }, timeout.Token);
        Assert.Equal("Yes, I can share an update by Friday.", (await completed.Task.WaitAsync(timeout.Token)).Text);
        await client.RequestResponseAsync(timeout.Token);
        Assert.Equal("Yes, I can share an update by Friday.", (await retried.Task.WaitAsync(timeout.Token)).Text);
        await client.StopAsync();
        await run;
    }

    private sealed class TestAudioSource : IAudioSource
    {
        public event Action<byte[]>? Data;
        public event Action<Exception>? Failed;
        public bool Started { get; private set; }
        public bool Disposed { get; private set; }
        public byte[]? OnStartBytes { get; init; }
        public TaskCompletionSource StartSignal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Start()
        {
            Started = true;
            if (OnStartBytes is not null) Emit(OnStartBytes);
            StartSignal.TrySetResult();
        }
        public void Emit(byte[] data) => Data?.Invoke(data);
        public void Fail(Exception error) => Failed?.Invoke(error);
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    private sealed class ControlledTransport : IMeetingTransport
    {
        public Channel<ServerEvent> Events { get; } = Channel.CreateUnbounded<ServerEvent>();
        public ConcurrentQueue<string> Commands { get; } = new();
        public ConcurrentQueue<byte[]> Audio { get; } = new();
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AudioSent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }
        public Task ConnectAsync(ClientSettings settings, CancellationToken token) => Task.CompletedTask;
        public Task SendTextAsync(string json, CancellationToken token)
        {
            Commands.Enqueue(json);
            if (json == MeetingClient.StartMessage) Started.TrySetResult();
            return Task.CompletedTask;
        }
        public Task SendAudioAsync(byte[] pcm, CancellationToken token)
        {
            Audio.Enqueue(pcm);
            AudioSent.TrySetResult();
            return Task.CompletedTask;
        }
        public async Task<ServerEvent> ReceiveAsync(CancellationToken token) => await Events.Reader.ReadAsync(token);
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
