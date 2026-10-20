using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace VoiceAssistant.WebOverlay.HostAudio;

internal delegate void CaptureDataHandler(ReadOnlySpan<byte> data, WaveFormat format);

/// <summary>A running capture.</summary>
internal interface ICaptureSession : IAsyncDisposable
{
    /// <summary>Completes when the capture ends by itself: null when it should simply be started again (for example a
    /// new default device), otherwise the failure.</summary>
    Task<Exception?> Completion { get; }
    string Description { get; }
}

/// <summary>Operating-system services the host audio capture needs; tests replace them.</summary>
internal interface IHostAudioPlatform
{
    bool ProcessLoopbackSupported { get; }
    string OsDescription { get; }
    int? FindTeamsRoot();
    Task<ICaptureSession> StartProcessCaptureAsync(int processId, CaptureDataHandler onData, CancellationToken cancellationToken);
    Task<ICaptureSession> StartSystemCaptureAsync(CaptureDataHandler onData, CancellationToken cancellationToken);

    /// <summary>Loudest Windows peak meter (0 to 1) of the active output sessions of a process and its descendants,
    /// or null when it has none. Only levels are read, never audio.</summary>
    double? RenderPeak(int rootProcessId);
}

/// <summary>A failure with a message that is safe and useful to show on the page.</summary>
internal sealed class HostAudioException(string message) : Exception(message);

internal sealed class WindowsHostAudioPlatform : IHostAudioPlatform
{
    private static readonly TimeSpan ActivationTimeout = TimeSpan.FromSeconds(5);

    public bool ProcessLoopbackSupported => ProcessLoopbackCapture.IsSupported(Environment.OSVersion.Version);

    public string OsDescription => $"Windows build {Environment.OSVersion.Version.Build}";

    public int? FindTeamsRoot() => TeamsProcessLocator.FindRoot();

    public async Task<ICaptureSession> StartProcessCaptureAsync(int processId, CaptureDataHandler onData, CancellationToken cancellationToken) =>
        await ProcessLoopbackCapture.StartAsync(processId, includeProcessTree: true, onData,
            $"Teams process tree (PID {processId})", ActivationTimeout, cancellationToken);

    public Task<ICaptureSession> StartSystemCaptureAsync(CaptureDataHandler onData, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<ICaptureSession>(SystemLoopbackCapture.Start(onData));
    }

    public double? RenderPeak(int rootProcessId)
    {
        var tree = new HashSet<int> { rootProcessId };
        var processes = ProcessSnapshot.Take();
        for (bool grew = true; grew;)
        {
            grew = false;
            foreach (var process in processes)
                if (process.ParentProcessId != process.ProcessId && tree.Contains(process.ParentProcessId) && tree.Add(process.ProcessId))
                    grew = true;
        }
        double? peak = null;
        using var enumerator = new MMDeviceEnumerator();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            using (device)
            {
                var manager = device.AudioSessionManager;
                try
                {
                    var sessions = manager.Sessions;
                    for (int i = 0; i < sessions.Count; i++)
                    {
                        using var session = sessions[i];
                        if (session.State == AudioSessionState.AudioSessionStateActive && tree.Contains((int)session.GetProcessID))
                            peak = Math.Max(peak ?? 0, session.AudioMeterInformation.MasterPeakValue);
                    }
                }
                finally { manager.Dispose(); }
            }
        }
        return peak;
    }
}
