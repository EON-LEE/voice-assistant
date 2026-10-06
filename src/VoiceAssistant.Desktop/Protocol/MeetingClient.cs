using System.Threading.Channels;
using VoiceAssistant.Desktop.Audio;

namespace VoiceAssistant.Desktop.Protocol;

public sealed class MeetingClient
{
    public const string StartMessage = """{"type":"session.start","protocolVersion":1,"audio":{"encoding":"pcm_s16le","sampleRate":16000,"channels":1}}""";
    private readonly IMeetingTransport transport;
    private readonly object audioGate = new();
    private readonly CancellationTokenSource stop = new();
    private readonly SemaphoreSlim audioSendGate = new(1, 1);
    private readonly Channel<byte[]> audio = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(50)
    {
        FullMode = BoundedChannelFullMode.Wait, SingleReader = false, SingleWriter = false
    });
    private readonly TaskCompletionSource<Exception> audioFailure = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool ready;
    private bool paused;
    private bool accepting;
    private volatile bool stopRequested;
    private bool finished;
    public bool IsReady => ready;

    public MeetingClient(IMeetingTransport transport) => this.transport = transport;

    public async Task RunAsync(ClientSettings settings, Func<IAudioSource> createAudio,
        Action<ServerEvent> onEvent, Action<string> onStatus, CancellationToken token)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, stop.Token);
        IAudioSource? source = null;
        Task? sender = null;
        Task? receiver = null;
        try
        {
            lock (audioGate) paused = settings.TranscribeOnly;
            onStatus(settings.Mode == ConnectionMode.Production ? "Signing in / connecting..." : "Connecting...");
            await transport.ConnectAsync(settings, lifetime.Token);
            await transport.SendTextAsync(BuildStartMessage(settings), lifetime.Token);
            using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
            {
                handshake.CancelAfter(TimeSpan.FromSeconds(15));
                ServerEvent first = await transport.ReceiveAsync(handshake.Token);
                if (first.Type == "error") throw new IOException($"{first.Code}: {first.Text}");
                if (first.Type != "session.ready") throw new InvalidDataException("Expected session.ready before audio.");
                onEvent(first);
            }

            source = createAudio();
            source.Data += QueueAudio;
            source.Failed += FailAudio;
            lock (audioGate) accepting = true;
            ready = true;
            source.Start();
            onStatus(settings.Mode == ConnectionMode.Demo ? "DEMO - synthetic audio, no device capture" : "Connected - audio streaming");
            sender = SendAudioAsync(lifetime.Token);
            receiver = ReceiveAsync(onEvent, lifetime.Token);
            Task finished = await Task.WhenAny(sender, receiver, audioFailure.Task);
            if (finished == audioFailure.Task) throw await audioFailure.Task;
            await finished;
        }
        catch (Exception ex) when (stopRequested &&
            ex is OperationCanceledException or IOException or System.Net.WebSockets.WebSocketException) { }
        finally
        {
            ready = false;
            lock (audioGate) { accepting = false; paused = true; }
            lifetime.Cancel();
            if (source is not null)
            {
                source.Data -= QueueAudio;
                source.Failed -= FailAudio;
                try { await source.DisposeAsync(); }
                catch (Exception ex) { onEvent(new("error", Code: "audio_cleanup_failed", Text: ex.Message)); }
            }
            try
            {
                await ObserveShutdownAsync(sender, onEvent);
                await ObserveShutdownAsync(receiver, onEvent);
            }
            finally
            {
                await transport.DisposeAsync();
                lock (audioGate) { finished = true; stop.Dispose(); }
                while (audio.Reader.TryRead(out _)) { }
            }
        }
    }

    public static string BuildStartMessage(ClientSettings settings)
    {
        if (settings.Mode != ConnectionMode.Production && !settings.TranscribeOnly) return StartMessage;
        var options = new Dictionary<string, object?>
        {
            ["responseMode"] = settings.ResponseMode,
            ["topic"] = settings.Topic,
            ["transcribeOnly"] = settings.TranscribeOnly
        };
        if (settings.ProfileConfirmed)
        {
            options["profileConfirmed"] = true;
            options["profile"] = new Dictionary<string, string?>
            {
                ["name"] = settings.ProfileName,
                ["role"] = settings.ProfileRole,
                ["project"] = settings.ProfileProject
            };
        }
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            type = "session.start",
            protocolVersion = 1,
            audio = new { encoding = "pcm_s16le", sampleRate = 16000, channels = 1 },
            options
        });
    }

    private static async Task ObserveShutdownAsync(Task? task, Action<ServerEvent> onEvent)
    {
        if (task is null) return;
        try { await task; }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (task.IsFaulted)
        {
            onEvent(new("error", Code: "stream_ended", Text: ex.Message));
        }
    }

    private async Task ReceiveAsync(Action<ServerEvent> onEvent, CancellationToken token)
    {
        while (true)
        {
            var message = await transport.ReceiveAsync(token);
            onEvent(message);
            if (message.Type == "error" && !message.Retryable)
                throw new IOException($"{message.Code}: {message.Text}");
        }
    }

    private async Task SendAudioAsync(CancellationToken token)
    {
        await foreach (var frame in audio.Reader.ReadAllAsync(token))
        {
            await audioSendGate.WaitAsync(token);
            try
            {
                lock (audioGate) { if (paused) continue; }
                await transport.SendAudioAsync(frame, token);
            }
            finally { audioSendGate.Release(); }
        }
    }

    private void QueueAudio(byte[] bytes)
    {
        lock (audioGate)
        {
            if (!accepting || paused) return;
            if (bytes.Length % 2 != 0)
            {
                FailAudio(new InvalidDataException("Audio source produced an incomplete PCM16 sample."));
                return;
            }
            for (int offset = 0; offset < bytes.Length; offset += 640)
            {
                var frame = bytes.AsSpan(offset, Math.Min(640, bytes.Length - offset)).ToArray();
                if (!audio.Writer.TryWrite(frame))
                {
                    accepting = false;
                    paused = true;
                    FailAudio(new IOException("Audio upload cannot keep up (1 second queue limit). Session stopped; check the network and restart."));
                    return;
                }
            }
        }
    }

    private void FailAudio(Exception exception) => audioFailure.TrySetResult(exception);

    public async Task StopAsync()
    {
        stopRequested = true;
        lock (audioGate)
        {
            if (finished) return;
            accepting = false;
            paused = true;
        }
        try
        {
            if (ready)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await transport.SendTextAsync("""{"type":"session.stop"}""", timeout.Token);
            }
        }
        finally
        {
            // Receive cancellation aborts ClientWebSocket, so send session.stop before cancelling.
            lock (audioGate) { if (!finished) stop.Cancel(); }
        }
    }

    public async Task SetPausedAsync(bool value, CancellationToken token)
    {
        lock (audioGate)
        {
            paused = value;
            if (value) while (audio.Reader.TryRead(out _)) { }
        }
        if (value)
        {
            // Wait for an in-flight frame before returning; queued/pre-pause frames stay discarded.
            await audioSendGate.WaitAsync(token);
            audioSendGate.Release();
            if (ready) await CancelResponseAsync(token);
        }
    }

    public Task RequestResponseAsync(CancellationToken token)
    {
        if (!ready) throw new InvalidOperationException("Wait for the session to be ready.");
        lock (audioGate)
            if (paused) throw new InvalidOperationException("Resume audio before requesting a reply.");
        return transport.SendTextAsync("""{"type":"response.request"}""", token);
    }

    public Task CancelResponseAsync(CancellationToken token) =>
        transport.SendTextAsync("""{"type":"response.cancel"}""", token);
}
