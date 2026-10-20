using System.Runtime.InteropServices;
using NAudio.Wave;
using VoiceAssistant.WebOverlay.HostAudio;

namespace VoiceAssistant.WebOverlay.Tests;

public sealed class HostAudioServiceTests
{
    private static readonly HostAudioTiming Fast = new(TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(60), TimeSpan.FromMilliseconds(10));
    private static readonly WaveFormat Pcm16k = new(16000, 16, 1);

    [Fact]
    public async Task UnsupportedWindowsReportsItWithoutCapturingOrFallingBack()
    {
        var platform = new FakePlatform { Supported = false, Teams = 42 };
        using var service = new HostAudioService(platform, timing: Fast);
        var page = new FakePage(service);
        service.Start(HostAudioKind.Teams, "a");
        var status = await page.NextStatusAsync();
        Assert.Equal(("unsupported-os", "a"), (status.State, status.Id));
        Assert.Contains("20348", status.Detail);
        Assert.Contains("build 19045", status.Detail);
        await Task.Delay(150);
        Assert.Empty(page.Take());
        Assert.Empty(platform.Started);
    }

    [Fact]
    public async Task WaitsForTeamsWithSilenceThenCapturesItsRootProcess()
    {
        var platform = new FakePlatform();
        using var service = new HostAudioService(platform, timing: Fast);
        var page = new FakePage(service);
        service.Start(HostAudioKind.Teams, "a");
        var waiting = await page.NextStatusAsync();
        Assert.Equal("teams-not-running", waiting.State);
        await page.WaitForFramesAsync(5);
        Assert.All(page.Frames, frame => Assert.All(frame, b => Assert.Equal(0, b)));
        platform.Teams = 42;
        var capturing = await page.NextStatusAsync();
        Assert.Equal(("capturing", "Teams process tree (PID 42)", "a"), (capturing.State, capturing.Detail, capturing.Id));
        Assert.Equal(new[] { "teams:42" }, platform.Started);
        platform.Captures[^1].Emit(Enumerable.Repeat((byte)1, 640 * 3).ToArray(), Pcm16k);
        await page.WaitUntilAsync(() => page.Frames.Any(frame => frame.Any(b => b != 0)));
        Assert.Equal(0, page.SeqGaps);
    }

    [Fact]
    public async Task TeamsRestartIsResolvedAndActivatedAgain()
    {
        var platform = new FakePlatform { Teams = 42 };
        using var service = new HostAudioService(platform, timing: Fast);
        var page = new FakePage(service);
        service.Start(HostAudioKind.Teams, "a");
        Assert.Equal("Teams process tree (PID 42)", (await page.NextStatusAsync()).Detail);
        platform.Teams = null;
        Assert.Equal("teams-not-running", (await page.NextStatusAsync()).State);
        await page.WaitUntilAsync(() => platform.Captures[0].Disposed);
        platform.Teams = 77;
        Assert.Equal("Teams process tree (PID 77)", (await page.NextStatusAsync()).Detail);
        platform.Teams = 78;
        Assert.Equal("Teams process tree (PID 78)", (await page.NextStatusAsync()).Detail);
        Assert.Equal(new[] { "teams:42", "teams:77", "teams:78" }, platform.Started);
        Assert.True(platform.Captures[1].Disposed);
    }

    [Fact]
    public async Task CaptureFailuresBecomeErrorStatusesAndAreRetried()
    {
        var platform = new FakePlatform { Teams = 42 };
        platform.Failures.Enqueue(new COMException("device gone", unchecked((int)0x88890004)));
        platform.Failures.Enqueue(new UnauthorizedAccessException("no"));
        using var service = new HostAudioService(platform, timing: Fast);
        var page = new FakePage(service);
        service.Start(HostAudioKind.Teams, "a");
        var first = await page.NextStatusAsync();
        Assert.Equal(("error", "The audio device was removed or changed (0x88890004)."), (first.State, first.Detail));
        var second = await page.NextStatusAsync();
        Assert.Equal(("error", "Windows denied access to audio capture (0x80070005)."), (second.State, second.Detail));
        Assert.Equal("capturing", (await page.NextStatusAsync()).State);
        platform.Captures[^1].Fail(new InvalidOperationException("broken stream"));
        var failed = await page.NextStatusAsync();
        Assert.Equal("error", failed.State);
        Assert.Contains("broken stream", failed.Detail);
        Assert.Equal("capturing", (await page.NextStatusAsync()).State);
        Assert.Equal(4, platform.Started.Count);
    }

    [Fact]
    public async Task SystemAudioIsOnlyUsedWhenAskedAndRestartsOnDeviceChange()
    {
        var platform = new FakePlatform { Teams = 42 };
        using var service = new HostAudioService(platform, timing: Fast);
        var page = new FakePage(service);
        service.Start(HostAudioKind.System, "s");
        var status = await page.NextStatusAsync();
        Assert.Equal(("capturing", "System audio: Fake speakers", "system"), (status.State, status.Detail, status.Source));
        platform.Captures[0].Restart();
        await page.WaitUntilAsync(() => platform.Started.Count == 2);
        Assert.Equal(new[] { "system", "system" }, platform.Started);
        await Task.Delay(100);
        Assert.DoesNotContain(page.Statuses, s => s.State == "error");
    }

    [Fact]
    public async Task StopReportsStoppedOnceAndLateAudioIsIgnored()
    {
        var platform = new FakePlatform { Teams = 42 };
        using var service = new HostAudioService(platform, timing: Fast);
        var page = new FakePage(service);
        service.Start(HostAudioKind.Teams, "a");
        Assert.Equal("capturing", (await page.NextStatusAsync()).State);
        var capture = platform.Captures[0];
        service.Stop("other");
        Assert.True(service.IsActive);
        service.Stop("a");
        Assert.False(service.IsActive);
        var stopped = Assert.Single(page.Take());
        Assert.Equal(("host-audio.status", "stopped", "a"), (stopped.Type, stopped.State, stopped.Id));
        int statuses = page.Statuses.Count, frames = page.Frames.Count;
        capture.Emit(new byte[640 * 4], Pcm16k);
        await page.WaitUntilAsync(() => capture.Disposed);
        await Task.Delay(100);
        page.Take();
        service.Stop(null);
        page.Take();
        Assert.Equal((statuses, frames), (page.Statuses.Count, page.Frames.Count));
    }

    [Fact]
    public async Task StartingAgainReplacesTheCaptureAndRestartsSeq()
    {
        var platform = new FakePlatform { Teams = 42 };
        using var service = new HostAudioService(platform, timing: Fast);
        var page = new FakePage(service);
        service.Start(HostAudioKind.Teams, "a");
        Assert.Equal("a", (await page.NextStatusAsync()).Id);
        platform.Captures[0].Emit(new byte[640 * 3], Pcm16k);
        service.Start(HostAudioKind.System, "b");
        page.Forget();
        var status = await page.NextStatusAsync();
        Assert.Equal(("b", "system"), (status.Id, status.Source));
        platform.Captures[^1].Emit(new byte[640], Pcm16k);
        await page.WaitForFramesAsync(1);
        Assert.Equal(0, page.FirstSeq);
        await page.WaitUntilAsync(() => platform.Captures[0].Disposed);
    }

    [Fact]
    public async Task AudibleTeamsThatTheCaptureHearsAsSilenceBecomesAVisibleError()
    {
        var platform = new FakePlatform { Teams = 42, TeamsPeak = 0.4 };
        using var service = new HostAudioService(platform, timing: Fast);
        var page = new FakePage(service);
        service.Start(HostAudioKind.Teams, "a");
        Assert.Equal("capturing", (await page.NextStatusAsync()).State);
        var warning = await page.NextStatusAsync();
        Assert.Equal(("error", HostAudioService.SilentTeamsCapture, "teams-silent"), (warning.State, warning.Detail, warning.Code));
        Assert.Single(platform.Started);
        platform.Captures[0].Emit(Enumerable.Repeat((byte)1, 640 * 3).ToArray(), Pcm16k);
        var recovered = await page.NextStatusAsync();
        Assert.Equal(("capturing", null), (recovered.State, recovered.Code));
    }

    [Fact]
    public async Task QuietTeamsOrAudibleCaptureNeverWarns()
    {
        var platform = new FakePlatform { Teams = 42, TeamsPeak = 0.005 };
        using var service = new HostAudioService(platform, timing: Fast);
        var page = new FakePage(service);
        service.Start(HostAudioKind.Teams, "a");
        Assert.Equal("capturing", (await page.NextStatusAsync()).State);
        await Task.Delay(Fast.Poll * (HostAudioService.SilentPollsBeforeWarning + 3));
        platform.TeamsPeak = 0.5;
        var until = DateTime.UtcNow + Fast.Poll * (HostAudioService.SilentPollsBeforeWarning + 3);
        while (DateTime.UtcNow < until)
        {
            platform.Captures[0].Emit(Enumerable.Repeat((byte)1, 640).ToArray(), Pcm16k);
            await Task.Delay(5);
        }
        page.Take();
        Assert.Single(page.Statuses);
    }

    [Fact]
    public void DescribesUnknownFailuresWithTheirTypeAndCode()
    {
        Assert.Equal("Plain words", HostAudioService.Describe(new HostAudioException("Plain words")));
        Assert.Equal("The Windows Audio service is not running (0x88890010).",
            HostAudioService.Describe(new COMException("x", unchecked((int)0x88890010))));
        Assert.StartsWith("InvalidOperationException (0x80131509): ", HostAudioService.Describe(new InvalidOperationException("x")));
    }

    internal sealed class FakePlatform : IHostAudioPlatform
    {
        private readonly object gate = new();
        private int? teams;
        public bool Supported { get; set; } = true;
        public int? Teams { get { lock (gate) return teams; } set { lock (gate) teams = value; } }
        public Queue<Exception> Failures { get; } = new();
        public List<string> Started { get { lock (gate) return [.. started]; } }
        public List<FakeCapture> Captures { get { lock (gate) return [.. captures]; } }
        private readonly List<string> started = [];
        private readonly List<FakeCapture> captures = [];

        public bool ProcessLoopbackSupported => Supported;
        public string OsDescription => "Windows build 19045";
        public int? FindTeamsRoot() => Teams;
        public double? TeamsPeak { get; set; }
        public double? RenderPeak(int rootProcessId) => rootProcessId == Teams ? TeamsPeak : null;

        public Task<ICaptureSession> StartProcessCaptureAsync(int processId, CaptureDataHandler onData, CancellationToken cancellationToken) =>
            Start($"teams:{processId}", $"Teams process tree (PID {processId})", onData);

        public Task<ICaptureSession> StartSystemCaptureAsync(CaptureDataHandler onData, CancellationToken cancellationToken) =>
            Start("system", "System audio: Fake speakers", onData);

        private Task<ICaptureSession> Start(string name, string description, CaptureDataHandler onData)
        {
            lock (gate)
            {
                started.Add(name);
                if (Failures.TryDequeue(out var failure)) return Task.FromException<ICaptureSession>(failure);
                var capture = new FakeCapture(description, onData);
                captures.Add(capture);
                return Task.FromResult<ICaptureSession>(capture);
            }
        }
    }

    internal sealed class FakeCapture(string description, CaptureDataHandler onData) : ICaptureSession
    {
        private readonly TaskCompletionSource<Exception?> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile bool disposed;
        public string Description => description;
        public Task<Exception?> Completion => completion.Task;
        public bool Disposed => disposed;
        public void Emit(byte[] data, WaveFormat format) => onData(data, format);
        public void Fail(Exception failure) => completion.TrySetResult(failure);
        public void Restart() => completion.TrySetResult(null);

        public ValueTask DisposeAsync()
        {
            disposed = true;
            completion.TrySetResult(null);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Reads the outbox like the page does: statuses, frames, seq gaps and acknowledgements.</summary>
    internal sealed class FakePage
    {
        private readonly HostAudioService service;
        private readonly object gate = new();
        private readonly Queue<PageMessage> pendingStatuses = new();
        private long? expected;

        public FakePage(HostAudioService service)
        {
            this.service = service;
        }

        public List<PageMessage> Statuses { get; } = [];
        public List<byte[]> Frames { get; } = [];
        public long SeqGaps { get; private set; }
        public long? FirstSeq { get; private set; }

        /// <summary>Takes everything waiting, acknowledging frames, and returns it.</summary>
        public List<PageMessage> Take()
        {
            lock (gate)
            {
                var messages = PageMessage.TakeAll(service.Outbox);
                foreach (var message in messages)
                {
                    if (message.Type == "host-audio.status")
                    {
                        Statuses.Add(message);
                        pendingStatuses.Enqueue(message);
                        continue;
                    }
                    FirstSeq ??= message.Seq;
                    if (expected is { } next && message.Seq > next) SeqGaps += message.Seq - next;
                    expected = message.Seq + message.Frames;
                    Frames.AddRange(message.Pcm.Chunk(640));
                    service.Outbox.Ack(message.Seq + message.Frames - 1);
                }
                return messages;
            }
        }

        public void Forget()
        {
            lock (gate)
            {
                pendingStatuses.Clear();
                Frames.Clear();
                expected = FirstSeq = null;
            }
        }

        public async Task<PageMessage> NextStatusAsync()
        {
            PageMessage? status = null;
            await WaitUntilAsync(() => { lock (gate) return pendingStatuses.TryDequeue(out status); });
            return status!;
        }

        public Task WaitForFramesAsync(int count) => WaitUntilAsync(() => { lock (gate) return Frames.Count >= count; });

        public async Task WaitUntilAsync(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (true)
            {
                Take();
                if (condition()) return;
                if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition was not met within 5 seconds.");
                await Task.Delay(5);
            }
        }
    }
}
