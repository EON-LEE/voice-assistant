using System.Buffers.Binary;
using NAudio.Wave;
using VoiceAssistant.Audio;
using VoiceAssistant.WebOverlay.HostAudio;

namespace VoiceAssistant.WebOverlay.Tests;

public sealed class FramePipelineTests
{
    private static readonly TimeSpan Ms = TimeSpan.FromMilliseconds(1);

    [Fact]
    public void FramerEmitsExact640ByteFramesAcrossArbitraryChunks()
    {
        byte[] input = Enumerable.Range(0, 2000).Select(i => (byte)(i % 251)).ToArray();
        var framer = new Pcm16Framer();
        var frames = new List<byte[]>();
        for (int offset = 0; offset < input.Length; offset += 7)
            framer.Push(input.AsSpan(offset, Math.Min(7, input.Length - offset)), frames.Add);
        Assert.Equal(3, frames.Count);
        Assert.All(frames, frame => Assert.Equal(640, frame.Length));
        Assert.Equal(input[..1920], frames.SelectMany(f => f));
        Assert.Equal(80, framer.PendingBytes);
        var padded = framer.FlushPadded()!;
        Assert.Equal(input[1920..], padded[..80]);
        Assert.All(padded[80..], b => Assert.Equal(0, b));
        Assert.Null(framer.FlushPadded());
        framer.Push(new byte[10], frames.Add);
        framer.Reset();
        Assert.Equal(0, framer.PendingBytes);
    }

    [Theory]
    [InlineData(48000, true)]
    [InlineData(44100, false)]
    public void CapturedAudioBecomes50FramesPerSecondOf16kMonoPcm(int rate, bool floating)
    {
        var (outbox, generation, pipeline, _) = Create();
        pipeline.Enable();
        var format = floating ? WaveFormat.CreateIeeeFloatWaveFormat(rate, 2) : new WaveFormat(rate, 16, 2);
        byte[] second = Tone(format, rate, 1000, 0.5);
        for (int offset = 0; offset < second.Length; offset += format.BlockAlign * 441)
            pipeline.Push(second.AsSpan(offset, Math.Min(format.BlockAlign * 441, second.Length - offset)), format);
        var messages = PageMessage.TakeAll(outbox);
        byte[] pcm = messages.SelectMany(m => m.Pcm).ToArray();
        Assert.Equal(50, pcm.Length / 640);
        Assert.Equal(0, messages[0].Seq);
        double rms = Math.Sqrt(Enumerable.Range(1000, 15000)
            .Average(i => Math.Pow(BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i * 2)) / 32768d, 2)));
        Assert.InRange(rms, 0.34, 0.36);
        Assert.Equal(50, pipeline.CapturedFrames);
        Assert.True(generation > 0);
    }

    [Fact]
    public void DisabledPipelineDiscardsAudioAndFormatChangesRestartTheFilter()
    {
        var (outbox, _, pipeline, _) = Create();
        var floatFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        Assert.False(pipeline.Push(Tone(floatFormat, 4800, 440, 0.5), floatFormat));
        pipeline.Enable();
        // 100 ms plus 1.5 ms: five frames and 48 bytes waiting for the next frame.
        Assert.True(pipeline.Push(Tone(floatFormat, 4872, 440, 0.5), floatFormat));
        Assert.Equal(5, PageMessage.TakeAll(outbox).Sum(m => m.Frames));
        // A new format gets a new converter, and the old partial frame is not glued to the new audio.
        var pcmFormat = new WaveFormat(16000, 16, 1);
        Assert.True(pipeline.Push(new byte[640], pcmFormat));
        Assert.False(pipeline.Push(new byte[592], pcmFormat));
        Assert.Equal(1, PageMessage.TakeAll(outbox).Sum(m => m.Frames));
        pipeline.Disable();
        Assert.False(pipeline.Push(new byte[640], pcmFormat));
    }

    [Fact]
    public void PacerAddsSilenceOnlyWhenCaptureStallsAndBoundsCatchUp()
    {
        var pacer = new SilencePacer(TimeSpan.FromMilliseconds(200));
        pacer.Start(TimeSpan.Zero);
        for (int t = 20; t <= 400; t += 20)
        {
            pacer.OnFrame(t * Ms);
            Assert.Equal(0, pacer.Due(t * Ms + 5 * Ms));
        }
        Assert.Equal(0, pacer.Due(599 * Ms));
        Assert.Equal(10, pacer.Due(600 * Ms));
        Assert.Equal(0, pacer.Due(610 * Ms));
        Assert.Equal(1, pacer.Due(620 * Ms));
        Assert.Equal(2, pacer.Due(665 * Ms));
        pacer.OnFrame(670 * Ms);
        Assert.Equal(0, pacer.Due(860 * Ms));
        Assert.Equal(25, pacer.Due(60_000 * Ms));
        Assert.Equal(1, pacer.Due(60_020 * Ms));
    }

    [Fact]
    public void StalledCaptureCompletesThePartialFrameAndKeeps50FramesPerSecond()
    {
        var (outbox, _, pipeline, clock) = Create();
        pipeline.Enable();
        var format = new WaveFormat(16000, 16, 1);
        byte[] partial = Enumerable.Repeat((byte)0x11, 300).ToArray();
        Assert.False(pipeline.Push(partial, format));
        clock.Now = TimeSpan.FromMilliseconds(199);
        Assert.False(pipeline.Tick());
        clock.Now = TimeSpan.FromMilliseconds(1000);
        Assert.True(pipeline.Tick());
        var frames = PageMessage.TakeAll(outbox);
        Assert.Equal(25, frames.Sum(m => m.Frames));
        byte[] first = frames[0].Pcm[..640];
        Assert.True(first[..280].Any(b => b != 0), "The captured samples lead the completed frame.");
        Assert.All(first[300..], b => Assert.Equal(0, b));
        Assert.Equal(25, pipeline.SilentFrames);
    }

    private static (HostAudioOutbox Outbox, int Generation, FramePipeline Pipeline, FakeClock Clock) Create()
    {
        var outbox = new HostAudioOutbox(capacityFrames: 1000, windowFrames: 1000);
        int generation = outbox.Reset();
        var clock = new FakeClock();
        return (outbox, generation, new FramePipeline(outbox, generation, new SilencePacer(TimeSpan.FromMilliseconds(200)), () => clock.Now), clock);
    }

    private static byte[] Tone(WaveFormat format, int frames, double hz, double amplitude)
    {
        var bytes = new byte[frames * format.BlockAlign];
        for (int i = 0; i < frames; i++)
        {
            double value = amplitude * Math.Sin(2 * Math.PI * hz * i / format.SampleRate);
            for (int c = 0; c < format.Channels; c++)
            {
                var slot = bytes.AsSpan((i * format.Channels + c) * (format.BitsPerSample / 8));
                if (format.Encoding == WaveFormatEncoding.IeeeFloat) BinaryPrimitives.WriteSingleLittleEndian(slot, (float)value);
                else BinaryPrimitives.WriteInt16LittleEndian(slot, (short)Math.Round(value * 32767));
            }
        }
        return bytes;
    }

    private sealed class FakeClock
    {
        public TimeSpan Now { get; set; }
    }
}
