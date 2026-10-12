using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace VoiceAssistant.Desktop.Audio;

public interface IAudioSource : IAsyncDisposable
{
    event Action<byte[]>? Data;
    event Action<Exception>? Failed;
    void Start();
}

public interface IAudioLevelSource
{
    event Action<double>? LevelChanged;
}

public sealed record CaptureEndpoint(string Id, string Name);

public sealed class MicrophoneAudioSource : IAudioSource, IAudioLevelSource
{
    private readonly MMDevice device;
    private readonly WasapiCapture capture;
    private readonly Pcm16Converter converter;
    private bool disposing;
    public event Action<byte[]>? Data;
    public event Action<Exception>? Failed;
    public event Action<double>? LevelChanged;

    public static IReadOnlyList<CaptureEndpoint> ListEndpoints()
    {
        using var enumerator = new MMDeviceEnumerator();
        var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
        var endpoints = new List<CaptureEndpoint>();
        foreach (var endpoint in devices)
        {
            using (endpoint) endpoints.Add(new(endpoint.ID, endpoint.FriendlyName));
        }
        return endpoints;
    }

    public MicrophoneAudioSource(string endpointId)
    {
        using var enumerator = new MMDeviceEnumerator();
        device = enumerator.GetDevice(endpointId);
        try
        {
            if (device.DataFlow != DataFlow.Capture)
                throw new InvalidOperationException("Only physical capture/microphone endpoints are allowed.");
            capture = new WasapiCapture(device);
            try { converter = new Pcm16Converter(capture.WaveFormat); }
            catch { capture.Dispose(); throw; }
        }
        catch { device.Dispose(); throw; }
        capture.DataAvailable += OnData;
        capture.RecordingStopped += OnStopped;
    }

    public void Start() => capture.StartRecording();

    private void OnData(object? sender, WaveInEventArgs e)
    {
        try
        {
            var pcm = converter.Convert(e.Buffer.AsSpan(0, e.BytesRecorded));
            if (pcm.Length == 0) return;
            int peak = 0;
            for (var i = 0; i < pcm.Length; i += 2)
                peak = Math.Max(peak, Math.Abs((int)System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i))));
            LevelChanged?.Invoke(peak / 32768d);
            Data?.Invoke(pcm);
        }
        catch (Exception ex) { Failed?.Invoke(ex); }
    }

    private void OnStopped(object? sender, StoppedEventArgs e)
    {
        if (!disposing)
            Failed?.Invoke(e.Exception ?? new IOException("The selected microphone stopped capturing. Check the device and start again."));
    }

    public ValueTask DisposeAsync()
    {
        disposing = true;
        try { capture.StopRecording(); }
        finally
        {
            try { capture.Dispose(); }
            finally { device.Dispose(); }
        }
        return ValueTask.CompletedTask;
    }
}

public static class AudioSessionCoordinator
{
    private static int active;
    public static IDisposable? TryAcquire()
    {
        if (Interlocked.CompareExchange(ref active, 1, 0) != 0) return null;
        return new Lease();
    }
    private sealed class Lease : IDisposable
    {
        private int disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0) Interlocked.Exchange(ref active, 0);
        }
    }
}

public static class MicrophoneLevelCheck
{
    public static async Task RunLocallyAsync(string endpointId, Action<double> onLevel,
        CancellationToken cancellationToken)
    {
        var lease = AudioSessionCoordinator.TryAcquire()
            ?? throw new InvalidOperationException("Another meeting or practice session is using the microphone.");
        MicrophoneAudioSource? source = null;
        var failed = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnFailed(Exception exception) => failed.TrySetResult(exception);
        try
        {
            source = new MicrophoneAudioSource(endpointId);
            source.LevelChanged += onLevel;
            source.Failed += OnFailed;
            source.Start();
            var duration = Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
            if (await Task.WhenAny(duration, failed.Task) == failed.Task)
                throw await failed.Task;
            await duration;
        }
        finally
        {
            try
            {
                if (source is not null)
                {
                    source.LevelChanged -= onLevel;
                    source.Failed -= OnFailed;
                    await source.DisposeAsync();
                }
            }
            finally { lease.Dispose(); }
        }
    }
}

/// <summary>Explicit synthetic silence for offline protocol tests; never opens an audio device.</summary>
public sealed class SyntheticAudioSource : IAudioSource
{
    private readonly CancellationTokenSource lifetime = new();
    private Task? task;
    public event Action<byte[]>? Data;
    public event Action<Exception>? Failed;
    public void Start() => task = ProduceAsync();

    private async Task ProduceAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(20));
            while (await timer.WaitForNextTickAsync(lifetime.Token))
                Data?.Invoke(new byte[640]);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { Failed?.Invoke(ex); }
    }

    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        if (task is not null) await task;
        lifetime.Dispose();
    }
}
