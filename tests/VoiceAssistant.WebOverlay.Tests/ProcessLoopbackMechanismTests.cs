using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using NAudio.CoreAudioApi;
using VoiceAssistant.Audio;
using VoiceAssistant.WebOverlay.HostAudio;
using Xunit.Abstractions;

namespace VoiceAssistant.WebOverlay.Tests;

/// <summary>A test that needs a real Windows audio stack; it runs only when LIVECOACH_MANUAL_AUDIO=1.</summary>
public sealed class ManualFactAttribute : FactAttribute
{
    public const string Variable = "LIVECOACH_MANUAL_AUDIO";

    public ManualFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(Variable) != "1")
            Skip = $"Manual audio mechanism check. Set {Variable}=1 on a Windows desktop with an output device to run it.";
    }
}

/// <summary>
/// Dev-only mechanism check for Teams capture without a Teams call: child processes play quiet generated tones
/// (audible if the default output device is not muted) and the test captures them by process id. Run with
/// <c>$env:LIVECOACH_MANUAL_AUDIO='1'; dotnet test tests\VoiceAssistant.WebOverlay.Tests --filter Category=Manual --logger "console;verbosity=detailed"</c>.
/// The tone files are synthetic and deleted afterwards; captured audio stays in memory. Only the processes the test
/// started are stopped, by their process id.
/// </summary>
[Trait("Category", "Manual")]
public sealed class ProcessLoopbackMechanismTests(ITestOutputHelper output)
{
    // Quiet tones (-34 dBFS): the default output device may be audible.
    private const double ToneAmplitude = 0.02, ToneRms = 0.005, SilenceRms = 0.0002, Present = 0.008, Absent = 0.0008;

    [ManualFact]
    public async Task ProcessLoopbackHearsOnlyTheTargetProcessTree()
    {
        var version = Environment.OSVersion.Version;
        output.WriteLine($"Windows {version} (build {version.Build}); process loopback supported: {ProcessLoopbackCapture.IsSupported(version)}");
        DescribeOutputs();
        Assert.True(ProcessLoopbackCapture.IsSupported(version), "Process loopback needs Windows 10 build 20348 or later.");
        RequireOutputDevice();
        await WithTonesAsync([1000, 600, 1500], async (wavs, children) =>
        {
            // Each player has its own frequency, so every capture shows exactly whose audio it contains.
            var player = Start(children, "powershell.exe", $"-NoProfile -NonInteractive -Command \"{Play(wavs[0])}\"");
            var idle = Start(children, "powershell.exe", "-NoProfile -NonInteractive -Command \"Start-Sleep -Seconds 30\"");
            var parent = Start(children, "cmd.exe", $"/d /c powershell.exe -NoProfile -NonInteractive -Command \"{Play(wavs[1])}\"");
            var grandparent = Start(children, "cmd.exe", $"/d /c cmd.exe /d /c powershell.exe -NoProfile -NonInteractive -Command \"{Play(wavs[2])}\"");
            await Task.Delay(TimeSpan.FromSeconds(2.5));
            Assert.False(player.HasExited, $"The tone player exited early (exit code {(player.HasExited ? player.ExitCode : 0)}).");
            var grandchild = Descendants(grandparent.Id).Single(p => p.ExeName.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase));
            output.WriteLine($"Process chain: cmd {grandparent.Id} -> {string.Join(" -> ", Descendants(grandparent.Id).Select(p => $"{p.ExeName} {p.ProcessId} (parent {p.ParentProcessId})"))}");

            var results = await Task.WhenAll(
                CaptureProcessAsync(player.Id, TimeSpan.FromSeconds(3)),
                CaptureProcessAsync(idle.Id, TimeSpan.FromSeconds(3)),
                CaptureProcessAsync(parent.Id, TimeSpan.FromSeconds(3)),
                CaptureProcessAsync(grandparent.Id, TimeSpan.FromSeconds(3)),
                CaptureProcessAsync(grandchild.ProcessId, TimeSpan.FromSeconds(3)),
                CaptureProcessAsync(Environment.ProcessId, TimeSpan.FromSeconds(3)),
                CaptureSystemAsync(TimeSpan.FromSeconds(3)));
            Describe($"1000 Hz player PID {player.Id}", results[0]);
            Describe($"silent process PID {idle.Id} (others playing)", results[1]);
            Describe($"cmd.exe PID {parent.Id}, parent of the 600 Hz player", results[2]);
            Describe($"cmd.exe PID {grandparent.Id}, grandparent of the 1500 Hz player", results[3]);
            Describe($"1500 Hz player PID {grandchild.ProcessId} itself", results[4]);
            Describe($"test host PID {Environment.ProcessId}: parent of 1000 Hz, grandparent of 600 Hz, great-grandparent of 1500 Hz", results[5]);
            Describe("system loopback of the default output device", results[6]);
            bool deeper = results[3].Level(1500) > Present || results[5].Level(600) > Present;
            output.WriteLine($"OBSERVATION: INCLUDE_TARGET_PROCESS_TREE includes direct children: {results[2].Level(600) > Present}; " +
                $"grandchildren or deeper: {deeper}.");
            var windows = new WindowsHostAudioPlatform();
            double? playerMeter = windows.RenderPeak(player.Id), idleMeter = windows.RenderPeak(idle.Id), treeMeter = windows.RenderPeak(grandparent.Id);
            output.WriteLine($"Session peak meters (used to detect silent Teams capture): 1000 Hz player {playerMeter:0.0000}, " +
                $"silent process {(idleMeter is null ? "no session" : idleMeter.Value.ToString("0.0000"))}, grandparent tree {treeMeter:0.0000}");

            AssertTones(results[0], 1000);
            Assert.True(results[1].Rms < SilenceRms, $"Expected silence from a process that plays nothing, RMS {results[1].Rms:0.000000}.");
            AssertTones(results[2], 600);
            AssertTones(results[4], 1500);
            Assert.True(results[5].Level(1000) > Present, "Expected the test host's direct child in its process tree capture.");
            Assert.True(playerMeter > ToneAmplitude / 2, "Expected the player's output session meter to show the tone.");
            Assert.Null(idleMeter);
        });
    }

    [ManualFact]
    public async Task HostAudioServiceStreamsAProcessTreeAsPageMessages()
    {
        RequireOutputDevice();
        await WithTonesAsync([1000], async (wavs, children) =>
        {
            var player = Start(children, "powershell.exe", $"-NoProfile -NonInteractive -Command \"{Play(wavs[0])}\"");
            await Task.Delay(TimeSpan.FromSeconds(2.5));
            Assert.False(player.HasExited, $"The tone player exited early (exit code {(player.HasExited ? player.ExitCode : 0)}).");
            using var service = new HostAudioService(new FixedProcessPlatform(player.Id));
            var clock = Stopwatch.StartNew();
            service.Start(HostAudioKind.Teams, "manual-check");
            var statuses = new List<string>();
            var pcm = new List<byte>();
            long expected = 0, gaps = 0;
            TimeSpan? capturing = null;
            while (capturing is null ? clock.Elapsed < TimeSpan.FromSeconds(10) : clock.Elapsed - capturing < TimeSpan.FromSeconds(3))
            {
                while (service.Outbox.TryTake(out var message))
                {
                    using var json = JsonDocument.Parse(message);
                    var root = json.RootElement;
                    if (root.GetProperty("type").GetString() == "host-audio.status")
                    {
                        Assert.Equal("manual-check", root.GetProperty("id").GetString());
                        statuses.Add($"{root.GetProperty("state").GetString()}: {root.GetProperty("detail").GetString()}");
                        capturing ??= clock.Elapsed;
                        continue;
                    }
                    long seq = root.GetProperty("seq").GetInt64();
                    var bytes = Convert.FromBase64String(root.GetProperty("pcm").GetString()!);
                    gaps += seq - expected;
                    expected = seq + bytes.Length / Pcm16Framer.FrameBytes;
                    pcm.AddRange(bytes);
                    service.Outbox.Ack(expected - 1);
                }
                await Task.Delay(20);
            }
            service.Stop("manual-check");
            var result = Measure([.. pcm]);
            output.WriteLine($"Statuses: {string.Join(" | ", statuses)} (first after {capturing?.TotalMilliseconds:0} ms)");
            output.WriteLine($"Page messages: {pcm.Count / Pcm16Framer.FrameBytes} frames in the 3 s after the status, seq gaps {gaps}, " +
                $"dropped {service.Outbox.Dropped}, RMS {result.Rms:0.0000}, peak {result.Peak:0.0000}, 1 kHz amplitude {ToneLevel([.. pcm], 1000):0.0000}");
            Assert.StartsWith("capturing: Teams process tree (PID", statuses[0]);
            Assert.InRange(pcm.Count / Pcm16Framer.FrameBytes, 135, 160);
            Assert.Equal(0, gaps);
            Assert.True(result.Rms > ToneRms, $"Expected the tone in the page frames, RMS {result.Rms:0.000000}.");
        });
    }

    private static string Play(string wav) => $"(New-Object System.Media.SoundPlayer '{wav}').PlaySync()";

    private void AssertTones(Capture capture, int expectedHz)
    {
        foreach (var (hz, level) in capture.Tones)
        {
            if (hz == expectedHz) Assert.True(level > Present, $"Expected {hz} Hz in the capture; amplitude {level:0.0000}.");
            else Assert.True(level < Absent, $"Did not expect {hz} Hz in the capture; amplitude {level:0.0000}.");
        }
    }

    private static IReadOnlyList<ProcessEntry> Descendants(int root)
    {
        var all = ProcessSnapshot.Take();
        var found = new List<ProcessEntry>();
        var parents = new HashSet<int> { root };
        for (bool added = true; added;)
        {
            added = false;
            foreach (var process in all)
            {
                if (parents.Contains(process.ParentProcessId) && !parents.Contains(process.ProcessId) && process.ParentProcessId != process.ProcessId)
                {
                    parents.Add(process.ProcessId);
                    found.Add(process);
                    added = true;
                }
            }
        }
        return found;
    }

    private async Task WithTonesAsync(int[] frequencies, Func<string[], List<Process>, Task> run)
    {
        var wavs = frequencies.Select(hz => Path.Combine(Path.GetTempPath(), $"livecoach-tone-{hz}-{Guid.NewGuid():N}.wav")).ToArray();
        var children = new List<Process>();
        try
        {
            for (int i = 0; i < wavs.Length; i++) WriteTone(wavs[i], TimeSpan.FromSeconds(12), frequencies[i], ToneAmplitude);
            await run(wavs, children);
        }
        finally
        {
            foreach (var child in children)
            {
                try
                {
                    // Only processes this test started (and their own children) are stopped, by their process id.
                    if (!child.HasExited) child.Kill(entireProcessTree: true);
                    child.WaitForExit(5000);
                }
                catch (InvalidOperationException) { }
                finally { child.Dispose(); }
            }
            foreach (var wav in wavs)
            {
                for (int attempt = 0; File.Exists(wav) && attempt < 20; attempt++)
                {
                    try { File.Delete(wav); }
                    catch (IOException) { await Task.Delay(250); }
                }
            }
            output.WriteLine($"Temporary tone files deleted: {wavs.All(wav => !File.Exists(wav))}");
        }
    }

    private static Process Start(List<Process> children, string file, string arguments)
    {
        var process = Process.Start(new ProcessStartInfo(file, arguments) { UseShellExecute = false, CreateNoWindow = true })
            ?? throw new InvalidOperationException($"Could not start {file}.");
        children.Add(process);
        return process;
    }

    private static void WriteTone(string path, TimeSpan duration, double hz, double amplitude)
    {
        const int rate = 48000;
        int samples = (int)(rate * duration.TotalSeconds);
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + samples * 2);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(rate);
        writer.Write(rate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(samples * 2);
        for (int i = 0; i < samples; i++)
            writer.Write((short)Math.Round(amplitude * short.MaxValue * Math.Sin(2 * Math.PI * hz * i / rate)));
    }

    private static async Task<Capture> CaptureProcessAsync(int processId, TimeSpan duration)
    {
        var sink = new FrameSink();
        var activation = Stopwatch.StartNew();
        var capture = await ProcessLoopbackCapture.StartAsync(processId, includeProcessTree: true, sink.Add,
            $"PID {processId}", TimeSpan.FromSeconds(5), CancellationToken.None);
        long activationMs = activation.ElapsedMilliseconds;
        await Task.Delay(duration);
        long packets = capture.Packets, silent = capture.SilentPackets;
        string format = $"{capture.Format.Encoding} {capture.Format.SampleRate} Hz {capture.Format.BitsPerSample}-bit x{capture.Format.Channels}";
        await capture.DisposeAsync();
        return sink.Result(format, packets, silent, activationMs);
    }

    private static async Task<Capture> CaptureSystemAsync(TimeSpan duration)
    {
        var sink = new FrameSink();
        var activation = Stopwatch.StartNew();
        var capture = await Task.Run(() => SystemLoopbackCapture.Start(sink.Add));
        long activationMs = activation.ElapsedMilliseconds;
        await Task.Delay(duration);
        await capture.DisposeAsync();
        return sink.Result("device mix format", -1, -1, activationMs);
    }

    private void Describe(string name, Capture result) =>
        output.WriteLine($"{name}: {result.Frames} frames, RMS {result.Rms:0.0000}, peak {result.Peak:0.0000}, tone amplitude " +
            string.Join(", ", result.Tones.Select(t => $"{t.Hz} Hz {t.Level:0.0000}")) +
            $"; format {result.Format}, packets {result.Packets} (silent-flagged {result.SilentPackets}), activation {result.ActivationMs} ms");

    private void DescribeOutputs()
    {
        using var enumerator = new MMDeviceEnumerator();
        foreach (var role in new[] { Role.Multimedia, Role.Communications })
        {
            if (!enumerator.HasDefaultAudioEndpoint(DataFlow.Render, role)) { output.WriteLine($"No default {role} output device."); continue; }
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, role);
            var volume = device.AudioEndpointVolume;
            output.WriteLine($"Default {role} output: muted {volume.Mute}, volume {volume.MasterVolumeLevelScalar:P0}");
        }
    }

    /// <summary>The tone players need an output device (it may be muted); process loopback itself does not.</summary>
    private static void RequireOutputDevice()
    {
        using var enumerator = new MMDeviceEnumerator();
        if (!enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
            Assert.Fail("No active audio output device, so the tone players cannot play. Connect or enable one (it may stay muted) and run again.");
    }

    private static (double Rms, double Peak) Measure(byte[] pcm)
    {
        double sum = 0, peak = 0;
        int count = pcm.Length / 2;
        for (int i = 0; i < count; i++)
        {
            double value = BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i * 2)) / 32768d;
            sum += value * value;
            peak = Math.Max(peak, Math.Abs(value));
        }
        return (count == 0 ? 0 : Math.Sqrt(sum / count), peak);
    }

    /// <summary>Amplitude of one sine frequency in 16 kHz PCM16 (single-bin DFT).</summary>
    private static double ToneLevel(byte[] pcm, int hz)
    {
        int count = pcm.Length / 2;
        if (count == 0) return 0;
        double re = 0, im = 0;
        for (int i = 0; i < count; i++)
        {
            double value = BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i * 2)) / 32768d;
            double angle = 2 * Math.PI * hz * i / 16000;
            re += value * Math.Cos(angle);
            im += value * Math.Sin(angle);
        }
        return 2 * Math.Sqrt(re * re + im * im) / count;
    }

    private static readonly int[] Frequencies = [600, 1000, 1500];

    private sealed record Capture(int Frames, double Rms, double Peak, (int Hz, double Level)[] Tones, string Format,
        long Packets, long SilentPackets, long ActivationMs)
    {
        public double Level(int hz) => Tones.Single(t => t.Hz == hz).Level;
    }

    private sealed class FrameSink
    {
        private readonly object gate = new();
        private readonly Pcm16Framer framer = new();
        private readonly List<byte> pcm = [];
        private Pcm16Converter? converter;

        public void Add(ReadOnlySpan<byte> data, NAudio.Wave.WaveFormat format)
        {
            lock (gate)
            {
                converter ??= new Pcm16Converter(format);
                framer.Push(converter.Convert(data), pcm.AddRange);
            }
        }

        public Capture Result(string format, long packets, long silent, long activationMs)
        {
            lock (gate)
            {
                byte[] bytes = [.. pcm];
                var (rms, peak) = Measure(bytes);
                return new Capture(bytes.Length / Pcm16Framer.FrameBytes, rms, peak,
                    Frequencies.Select(hz => (hz, ToneLevel(bytes, hz))).ToArray(), format, packets, silent, activationMs);
            }
        }
    }

    /// <summary>Treats one known process as "Teams" while using the real Windows capture.</summary>
    private sealed class FixedProcessPlatform(int processId) : IHostAudioPlatform
    {
        private readonly WindowsHostAudioPlatform windows = new();
        public bool ProcessLoopbackSupported => windows.ProcessLoopbackSupported;
        public string OsDescription => windows.OsDescription;
        public int? FindTeamsRoot() => processId;
        public double? RenderPeak(int rootProcessId) => windows.RenderPeak(rootProcessId);
        public Task<ICaptureSession> StartProcessCaptureAsync(int id, CaptureDataHandler onData, CancellationToken token) =>
            windows.StartProcessCaptureAsync(id, onData, token);
        public Task<ICaptureSession> StartSystemCaptureAsync(CaptureDataHandler onData, CancellationToken token) =>
            windows.StartSystemCaptureAsync(onData, token);
    }
}
