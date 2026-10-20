using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.Wave;

namespace VoiceAssistant.WebOverlay.HostAudio;

internal sealed record HostAudioTiming(TimeSpan Poll, TimeSpan StallAfter, TimeSpan Tick)
{
    public static readonly HostAudioTiming Default =
        new(TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(20));
}

/// <summary>
/// Runs at most one host audio capture for the page: Teams process loopback, or loopback of the default playback
/// device only when the page asks for "system". Never falls back from Teams to system audio by itself.
/// While Teams is not running it reports teams-not-running and looks again every <see cref="HostAudioTiming.Poll"/>;
/// when Teams restarts it resolves the new root process and activates again. Capture failures become "error" statuses
/// and are retried, and so does Teams sound that the capture only receives as silence. Frames keep flowing (silence
/// while nothing is captured) so the meeting session stays alive.
/// </summary>
internal sealed class HostAudioService : IDisposable
{
    private readonly object gate = new();
    private readonly IHostAudioPlatform platform;
    private readonly HostAudioTiming timing;
    private readonly Func<TimeSpan> clock;
    private Run? current;

    public HostAudioService(IHostAudioPlatform platform, HostAudioOutbox? outbox = null, HostAudioTiming? timing = null,
        Func<TimeSpan>? clock = null)
    {
        this.platform = platform;
        Outbox = outbox ?? new HostAudioOutbox();
        this.timing = timing ?? HostAudioTiming.Default;
        long origin = Stopwatch.GetTimestamp();
        this.clock = clock ?? (() => Stopwatch.GetElapsedTime(origin));
    }

    public HostAudioOutbox Outbox { get; }

    /// <summary>Raised on any thread when messages are waiting in <see cref="Outbox"/>.</summary>
    public event Action? MessagesAvailable;

    public string? CurrentId { get { lock (gate) return current?.Id; } }

    public bool IsActive { get { lock (gate) return current is not null; } }

    /// <summary>Starts a capture for the page, replacing any previous one; seq restarts at 0.</summary>
    public void Start(HostAudioKind kind, string? id)
    {
        Run run;
        lock (gate)
        {
            current?.Cancel();
            run = current = new Run(this, kind, id, Outbox.Reset());
        }
        run.Begin();
    }

    /// <summary>Stops the capture. A stop that names another capture id is ignored.</summary>
    /// <param name="notify">Queue a final "stopped" status; false when the page that asked for audio is gone.</param>
    public void Stop(string? id, bool notify = true)
    {
        lock (gate)
        {
            var run = current;
            if (run is null || id is not null && run.Id is not null && id != run.Id) return;
            current = null;
            run.Cancel();
            long dropped = Outbox.Dropped;
            int generation = Outbox.Reset();
            if (!notify) return;
            Outbox.EnqueueStatus(generation, HostAudioProtocol.Status(HostAudioState.Stopped,
                dropped == 0 ? "Capture stopped." : $"Capture stopped. {dropped} frames were dropped because the page did not keep up.",
                run.Kind, run.Id));
        }
        Signal();
    }

    public void Dispose()
    {
        Run? run;
        lock (gate)
        {
            run = current;
            current = null;
            run?.Cancel();
            Outbox.Reset();
        }
        try { run?.Task.Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { }
    }

    private void Signal() => MessagesAvailable?.Invoke();

    private async Task RunAsync(Run run, CancellationToken token)
    {
        try
        {
            if (run.Kind == HostAudioKind.Teams && !platform.ProcessLoopbackSupported)
            {
                run.Report(HostAudioState.UnsupportedOs,
                    $"Capturing only Teams needs Windows 10 build {ProcessLoopbackCapture.MinimumWindowsBuild} or later; this PC runs {platform.OsDescription}.");
                return;
            }
            while (!token.IsCancellationRequested)
            {
                ICaptureSession? capture = null;
                try
                {
                    int? teams = null;
                    if (run.Kind == HostAudioKind.Teams)
                    {
                        teams = platform.FindTeamsRoot();
                        if (teams is null)
                        {
                            run.Report(HostAudioState.TeamsNotRunning, "Waiting for Microsoft Teams (ms-teams.exe) to start.");
                            await Task.Delay(timing.Poll, token);
                            continue;
                        }
                    }
                    capture = teams is { } processId
                        ? await platform.StartProcessCaptureAsync(processId, run.OnData, token)
                        : await platform.StartSystemCaptureAsync(run.OnData, token);
                    var started = clock();
                    run.Report(HostAudioState.Capturing, capture.Description);
                    var failure = await SuperviseAsync(run, capture, teams, token);
                    if (failure is not null)
                    {
                        run.Report(HostAudioState.Error, Describe(failure));
                        // A capture that ran for a while is restarted at once; one that keeps failing waits between attempts.
                        if (clock() - started < timing.Poll) await Task.Delay(timing.Poll, token);
                    }
                }
                catch (Exception ex) when (!token.IsCancellationRequested)
                {
                    run.Report(HostAudioState.Error, Describe(ex));
                    await Task.Delay(timing.Poll, token);
                }
                finally
                {
                    if (capture is not null)
                    {
                        try { await capture.DisposeAsync(); }
                        catch (Exception ex) { Debug.WriteLine($"Host audio capture cleanup failed: {ex.Message}"); }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { run.Report(HostAudioState.Error, Describe(ex)); }
    }

    /// <summary>
    /// Waits until the capture ends (its failure, or null for a plain restart) or Teams' root process changes (null).
    /// While Teams plays audible sound that the capture never hears, it reports an error: the new Teams app is known
    /// to render meeting audio that process loopback receives only as silence.
    /// </summary>
    private async Task<Exception?> SuperviseAsync(Run run, ICaptureSession capture, int? teams, CancellationToken token)
    {
        int audibleButSilent = 0;
        bool warned = false;
        run.TakeCapturedPeak();
        while (true)
        {
            var poll = Task.Delay(timing.Poll, token);
            if (await Task.WhenAny(capture.Completion, poll) == capture.Completion) return await capture.Completion;
            await poll;
            if (teams is not { } processId) continue;
            if (platform.FindTeamsRoot() != processId) return null;
            if (run.TakeCapturedPeak() > 0)
            {
                audibleButSilent = 0;
                if (warned) run.Report(HostAudioState.Capturing, capture.Description);
                warned = false;
            }
            else if (TeamsPeak(processId) >= AudibleTeamsPeak && ++audibleButSilent >= SilentPollsBeforeWarning && !warned)
            {
                warned = true;
                run.Report(HostAudioState.Error, SilentTeamsCapture);
            }
        }
    }

    internal const double AudibleTeamsPeak = 0.01;
    internal const int SilentPollsBeforeWarning = 3;
    internal const string SilentTeamsCapture =
        "Teams is playing sound, but Windows gives Live Coach only silence for the Teams app. Choose system audio instead.";

    private double TeamsPeak(int processId)
    {
        try { return platform.RenderPeak(processId) ?? 0; }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return 0;
        }
    }

    internal static string Describe(Exception exception) => exception switch
    {
        HostAudioException => exception.Message,
        _ when KnownError(exception.HResult) is { } known => $"{known} (0x{exception.HResult:X8}).",
        _ => $"{exception.GetType().Name} (0x{exception.HResult:X8}): {exception.Message}",
    };

    private static string? KnownError(int hresult) => unchecked((uint)hresult) switch
    {
        0x88890004 => "The audio device was removed or changed",
        0x88890008 => "Windows rejected the capture format",
        0x8889000A => "The audio device is in use",
        0x88890010 => "The Windows Audio service is not running",
        0x80070005 => "Windows denied access to audio capture",
        0x80070057 => "Windows rejected the capture request",
        0x80004001 => "This Windows version does not support this audio capture",
        _ => null,
    };

    private sealed class Run
    {
        private readonly HostAudioService owner;
        private readonly CancellationTokenSource cancellation = new();
        private readonly FramePipeline pipeline;
        private readonly object sync = new();
        private Timer? ticker;
        private (HostAudioState State, string Detail)? reported;

        public Run(HostAudioService owner, HostAudioKind kind, string? id, int generation)
        {
            this.owner = owner;
            Kind = kind;
            Id = id;
            Generation = generation;
            pipeline = new FramePipeline(owner.Outbox, generation, new SilencePacer(owner.timing.StallAfter), owner.clock);
        }

        public HostAudioKind Kind { get; }
        public string? Id { get; }
        public int Generation { get; }
        public Task Task { get; private set; } = Task.CompletedTask;

        public void Begin()
        {
            lock (sync)
            {
                if (cancellation.IsCancellationRequested) return;
                ticker = new Timer(_ => { if (pipeline.Tick()) owner.Signal(); }, null, owner.timing.Tick, owner.timing.Tick);
                Task = Task.Run(() => owner.RunAsync(this, cancellation.Token));
            }
        }

        public void Cancel()
        {
            lock (sync)
            {
                cancellation.Cancel();
                ticker?.Dispose();
                ticker = null;
            }
            pipeline.Disable();
        }

        /// <summary>Queues a status once per change. Frames flow only while the capture is live or being retried.</summary>
        public void Report(HostAudioState state, string detail)
        {
            lock (sync)
            {
                if (cancellation.IsCancellationRequested || reported == (state, detail)) return;
                reported = (state, detail);
            }
            if (!owner.Outbox.EnqueueStatus(Generation, HostAudioProtocol.Status(state, detail, Kind, Id))) return;
            if (state is HostAudioState.Capturing or HostAudioState.TeamsNotRunning or HostAudioState.Error) pipeline.Enable();
            else pipeline.Disable();
            owner.Signal();
        }

        public void OnData(ReadOnlySpan<byte> data, WaveFormat format)
        {
            if (pipeline.Push(data, format)) owner.Signal();
        }

        public double TakeCapturedPeak() => pipeline.TakeCapturedPeak();
    }
}
