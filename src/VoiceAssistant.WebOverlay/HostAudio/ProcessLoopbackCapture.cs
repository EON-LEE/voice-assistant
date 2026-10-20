using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wasapi.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace VoiceAssistant.WebOverlay.HostAudio;

/// <summary>
/// WASAPI process loopback (Windows 10 build 20348 or later): captures only the audio that one process and its child
/// processes render, whichever output device they use. Audio is only handed to <see cref="CaptureDataHandler"/> in memory.
/// </summary>
internal sealed class ProcessLoopbackCapture : ICaptureSession
{
    public const int MinimumWindowsBuild = 20348;
    private const string VirtualAudioDeviceProcessLoopback = "VAD\\Process_Loopback";
    private const int ActivationTypeProcessLoopback = 1;
    private const int IncludeTargetProcessTree = 0, ExcludeTargetProcessTree = 1;
    private const ushort VtBlob = 65;
    private const long BufferDuration = 1_000_000; // 100 ms in 100-ns units
    private const AudioClientStreamFlags Converting = AudioClientStreamFlags.Loopback | AudioClientStreamFlags.EventCallback |
        AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality;

    // The virtual loopback device has no mix format of its own; float stereo at 48 kHz avoids clipping and extra
    // conversion. The last entry is the format and flags of Microsoft's ApplicationLoopback sample.
    private static readonly (WaveFormat Format, AudioClientStreamFlags Flags)[] Attempts =
    [
        (WaveFormat.CreateIeeeFloatWaveFormat(48000, 2), Converting),
        (new WaveFormat(48000, 16, 2), Converting),
        (new WaveFormat(44100, 16, 2), AudioClientStreamFlags.Loopback | AudioClientStreamFlags.EventCallback),
    ];

    private readonly AudioClient client;
    private readonly CaptureDataHandler onData;
    private readonly ManualResetEvent stop = new(false);
    private readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<Exception?> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long packets, silentPackets;

    private ProcessLoopbackCapture(AudioClient client, WaveFormat format, CaptureDataHandler onData, string description)
    {
        this.client = client;
        this.onData = onData;
        Format = format;
        Description = description;
    }

    public static bool IsSupported(Version windows) => windows.Major > 10 || windows.Major == 10 && windows.Build >= MinimumWindowsBuild;

    public string Description { get; }
    public WaveFormat Format { get; }
    public Task<Exception?> Completion => completion.Task;
    public long Packets => Interlocked.Read(ref packets);
    public long SilentPackets => Interlocked.Read(ref silentPackets);

    public static async Task<ProcessLoopbackCapture> StartAsync(int processId, bool includeProcessTree, CaptureDataHandler onData,
        string description, TimeSpan activationTimeout, CancellationToken cancellationToken)
    {
        Exception? rejected = null;
        foreach (var (format, flags) in Attempts)
        {
            var client = await ActivateAsync(processId, includeProcessTree, activationTimeout, cancellationToken);
            try
            {
                client.Initialize(AudioClientShareMode.Shared, flags, BufferDuration, 0, format, Guid.Empty);
            }
            catch (Exception ex) when (ex is COMException or ArgumentException or NotSupportedException or NotImplementedException)
            {
                rejected = ex;
                client.Dispose();
                continue;
            }
            var capture = new ProcessLoopbackCapture(client, format, onData, description);
            try
            {
                await capture.StartThreadAsync();
            }
            catch
            {
                capture.stop.Dispose();
                throw;
            }
            return capture;
        }
        throw rejected!;
    }

    private static async Task<AudioClient> ActivateAsync(int processId, bool includeProcessTree, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        int size = Marshal.SizeOf<ActivationParameters>();
        IntPtr parameters = Marshal.AllocHGlobal(size);
        IntPtr variant = Marshal.AllocHGlobal(Marshal.SizeOf<BlobPropVariant>());
        var handler = new ActivationHandler();
        IActivateAudioInterfaceAsyncOperation? operation = null;
        bool pending = false;
        try
        {
            Marshal.StructureToPtr(new ActivationParameters
            {
                ActivationType = ActivationTypeProcessLoopback,
                TargetProcessId = (uint)processId,
                ProcessLoopbackMode = includeProcessTree ? IncludeTargetProcessTree : ExcludeTargetProcessTree,
            }, parameters, false);
            Marshal.StructureToPtr(new BlobPropVariant { Type = VtBlob, Size = size, Data = parameters }, variant, false);
            ActivateAudioInterfaceAsync(VirtualAudioDeviceProcessLoopback, typeof(IAudioClient).GUID, variant, handler, out operation);
            pending = true;
            var activated = await handler.Result.WaitAsync(timeout, cancellationToken);
            return new AudioClient((IAudioClient)activated);
        }
        catch (TimeoutException)
        {
            throw new HostAudioException($"Windows did not start the process audio capture within {timeout.TotalSeconds:0} seconds.");
        }
        finally
        {
            if (operation is not null) Marshal.ReleaseComObject(operation);
            // Windows may read the parameters until the activation completes, so an abandoned activation keeps them.
            if (!pending || handler.Result.IsCompleted)
            {
                Marshal.FreeHGlobal(variant);
                Marshal.FreeHGlobal(parameters);
            }
        }
    }

    private Task StartThreadAsync()
    {
        var thread = new Thread(CaptureLoop) { IsBackground = true, Name = "LiveCoach process loopback", Priority = ThreadPriority.AboveNormal };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        return started.Task;
    }

    private void CaptureLoop()
    {
        Exception? failure = null;
        try
        {
            using var ready = new AutoResetEvent(false);
            client.SetEventHandle(ready.SafeWaitHandle.DangerousGetHandle());
            var capture = client.AudioCaptureClient;
            client.Start();
            started.TrySetResult();
            var buffer = new byte[Format.AverageBytesPerSecond / 10];
            var waits = new WaitHandle[] { stop, ready };
            while (WaitHandle.WaitAny(waits, 200) != 0)
            {
                for (int packet = capture.GetNextPacketSize(); packet > 0; packet = capture.GetNextPacketSize())
                {
                    var data = capture.GetBuffer(out int frames, out var flags);
                    int bytes = frames * Format.BlockAlign;
                    if (buffer.Length < bytes) buffer = new byte[bytes];
                    bool silent = (flags & AudioClientBufferFlags.Silent) != 0;
                    if (silent) Array.Clear(buffer, 0, bytes);
                    else Marshal.Copy(data, buffer, 0, bytes);
                    capture.ReleaseBuffer(frames);
                    Interlocked.Increment(ref packets);
                    if (silent) Interlocked.Increment(ref silentPackets);
                    onData(buffer.AsSpan(0, bytes), Format);
                }
            }
            client.Stop();
        }
        catch (Exception ex)
        {
            failure = ex;
            started.TrySetException(ex);
        }
        finally
        {
            client.Dispose();
            completion.TrySetResult(failure);
        }
    }

    public async ValueTask DisposeAsync()
    {
        stop.Set();
        try
        {
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(2));
            stop.Dispose();
        }
        catch (TimeoutException) { }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ActivationParameters
    {
        public int ActivationType;
        public uint TargetProcessId;
        public int ProcessLoopbackMode;
    }

    /// <summary>A PROPVARIANT holding a VT_BLOB; the sequential layout matches the native union on x86, x64 and ARM64.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct BlobPropVariant
    {
        public ushort Type;
        public ushort Reserved1, Reserved2, Reserved3;
        public int Size;
        public IntPtr Data;
    }

    [ComImport, Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAgileObject { }

    /// <summary>Windows calls this on an MTA worker thread when activation finishes; it must be agile.</summary>
    private sealed class ActivationHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        private readonly TaskCompletionSource<object> result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<object> Result => result.Task;

        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation)
        {
            try
            {
                activateOperation.GetActivateResult(out int hresult, out object activated);
                if (hresult < 0) result.TrySetException(Marshal.GetExceptionForHR(hresult) ?? new COMException(null, hresult));
                else result.TrySetResult(activated);
            }
            catch (Exception ex) { result.TrySetException(ex); }
        }
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    private static extern void ActivateAudioInterfaceAsync([MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid, IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler, out IActivateAudioInterfaceAsyncOperation activationOperation);
}
