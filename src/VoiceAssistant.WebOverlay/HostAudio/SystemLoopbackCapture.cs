using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace VoiceAssistant.WebOverlay.HostAudio;

/// <summary>
/// Explicit fallback: WASAPI loopback of the default playback device (everything that device plays, not only Teams).
/// It does not hear a separate headset or communications device. A default-device change ends the capture without an
/// error so that the caller restarts on the new default device.
/// </summary>
internal sealed class SystemLoopbackCapture : ICaptureSession, IMMNotificationClient
{
    private readonly MMDeviceEnumerator enumerator;
    private readonly MMDevice device;
    private readonly string deviceId;
    private readonly WasapiLoopbackCapture capture;
    private readonly TaskCompletionSource<Exception?> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int disposed;

    private SystemLoopbackCapture(MMDeviceEnumerator enumerator, MMDevice device, CaptureDataHandler onData)
    {
        this.enumerator = enumerator;
        this.device = device;
        deviceId = device.ID;
        Description = $"System audio: {device.FriendlyName}";
        capture = new WasapiLoopbackCapture(device);
        var format = capture.WaveFormat;
        capture.DataAvailable += (_, e) => onData(e.Buffer.AsSpan(0, e.BytesRecorded), format);
        capture.RecordingStopped += (_, e) => completion.TrySetResult(
            e.Exception ?? (Volatile.Read(ref disposed) == 0 ? new HostAudioException("System audio capture stopped.") : null));
        enumerator.RegisterEndpointNotificationCallback(this);
        try { capture.StartRecording(); }
        catch
        {
            enumerator.UnregisterEndpointNotificationCallback(this);
            capture.Dispose();
            throw;
        }
    }

    public string Description { get; }
    public Task<Exception?> Completion => completion.Task;

    /// <summary>Starts on the calling thread, which must be an MTA thread such as a thread-pool thread.</summary>
    public static SystemLoopbackCapture Start(CaptureDataHandler onData)
    {
        var enumerator = new MMDeviceEnumerator();
        MMDevice? device = null;
        try
        {
            if (!enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
                throw new HostAudioException("No default playback device is available.");
            device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return new SystemLoopbackCapture(enumerator, device, onData);
        }
        catch
        {
            device?.Dispose();
            enumerator.Dispose();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 1) return;
        await Task.Run(() =>
        {
            enumerator.UnregisterEndpointNotificationCallback(this);
            capture.StopRecording();
            capture.Dispose();
            device.Dispose();
            enumerator.Dispose();
        });
        completion.TrySetResult(null);
    }

    private void Restart() => completion.TrySetResult(null);

    void IMMNotificationClient.OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (flow == DataFlow.Render && role == Role.Multimedia && defaultDeviceId != deviceId) Restart();
    }

    void IMMNotificationClient.OnDeviceStateChanged(string deviceId, DeviceState newState)
    {
        if (deviceId == this.deviceId && newState != DeviceState.Active) Restart();
    }

    void IMMNotificationClient.OnDeviceRemoved(string deviceId)
    {
        if (deviceId == this.deviceId) Restart();
    }

    void IMMNotificationClient.OnDeviceAdded(string pwstrDeviceId) { }

    void IMMNotificationClient.OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
}
