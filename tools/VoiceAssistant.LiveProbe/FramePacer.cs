using System.Diagnostics;

namespace VoiceAssistant.LiveProbe;

internal interface IProbeClock
{
    double ElapsedMs { get; }
    Task DelayAsync(TimeSpan delay, CancellationToken cancellation);
}

internal sealed class ProbeClock : IProbeClock
{
    private readonly long start = Stopwatch.GetTimestamp();
    public double ElapsedMs => Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellation) => Task.Delay(delay, cancellation);
}

internal static class FramePacer
{
    internal const int FrameBytes = 640;

    internal static async Task PlayAsync(AudioFixture fixture, Action<byte[]> write, IProbeClock clock,
        Action<double> started, Action<double> speechEndDelivered, Action<double> maximumLateness, CancellationToken cancellation)
    {
        var start = clock.ElapsedMs;
        started(start);
        var latestWrite = start;
        for (var offset = 0; offset < fixture.Pcm.Length; offset += FrameBytes)
        {
            cancellation.ThrowIfCancellationRequested();
            var length = Math.Min(FrameBytes, fixture.Pcm.Length - offset);
            // Pace at sample-end (capture availability), never burst to catch up after a scheduler stall.
            var frameDuration = length * 1000d / AudioFixture.BytesPerSecond;
            var target = Math.Max(start + (offset + length) * 1000d / AudioFixture.BytesPerSecond, latestWrite + frameDuration);
            while (clock.ElapsedMs < target)
                await clock.DelayAsync(TimeSpan.FromMilliseconds(target - clock.ElapsedMs), cancellation);
            cancellation.ThrowIfCancellationRequested();
            latestWrite = clock.ElapsedMs;
            maximumLateness(latestWrite - (start + (offset + length) * 1000d / AudioFixture.BytesPerSecond));
            if (fixture.SpeechEndSample is { } end && offset < end * 2 && offset + length >= end * 2)
                speechEndDelivered(latestWrite);
            write(fixture.Pcm.AsSpan(offset, length).ToArray());
        }
    }
}
