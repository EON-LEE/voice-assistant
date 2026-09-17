using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace VoiceAssistant.Desktop.Audio;

public interface IAudioSource : IAsyncDisposable
{
    event Action<byte[]>? Data;
    event Action<Exception>? Failed;
    void Start();
}

public sealed record RenderEndpoint(string Id, string Name);

public sealed class LoopbackAudioSource : IAudioSource
{
    private readonly MMDevice device;
    private readonly WasapiLoopbackCapture capture;
    private readonly Pcm16Converter converter;
    private bool disposing;
    public event Action<byte[]>? Data;
    public event Action<Exception>? Failed;

    public static IReadOnlyList<RenderEndpoint> ListEndpoints()
    {
        using var enumerator = new MMDeviceEnumerator();
        var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        var endpoints = new List<RenderEndpoint>();
        foreach (var endpoint in devices)
        {
            using (endpoint) endpoints.Add(new(endpoint.ID, endpoint.FriendlyName));
        }
        return endpoints;
    }

    public LoopbackAudioSource(string endpointId)
    {
        using var enumerator = new MMDeviceEnumerator();
        device = enumerator.GetDevice(endpointId);
        try
        {
            if (device.DataFlow != DataFlow.Render)
                throw new InvalidOperationException("Only output/render endpoints are allowed; microphone capture is disabled.");
            capture = new WasapiLoopbackCapture(device);
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
            byte[] pcm = converter.Convert(e.Buffer.AsSpan(0, e.BytesRecorded));
            if (pcm.Length != 0) Data?.Invoke(pcm);
        }
        catch (Exception ex) { Failed?.Invoke(ex); }
    }

    private void OnStopped(object? sender, StoppedEventArgs e)
    {
        if (!disposing)
            Failed?.Invoke(e.Exception ?? new IOException("The selected output device stopped capturing. Check the device and start again."));
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
