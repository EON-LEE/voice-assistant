using System.Buffers.Binary;
using NAudio.Wave;
using VoiceAssistant.Audio;

namespace VoiceAssistant.WebOverlay.HostAudio;

/// <summary>Captured audio in any WASAPI format to 640-byte 16 kHz mono PCM16 frames in the outbox, plus paced silence.</summary>
internal sealed class FramePipeline(HostAudioOutbox outbox, int generation, SilencePacer pacer, Func<TimeSpan> clock)
{
    private readonly object gate = new();
    private readonly Pcm16Framer framer = new();
    private Pcm16Converter? converter;
    private WaveFormat? format;
    private bool enabled;
    private int capturedPeak;

    public long CapturedFrames { get; private set; }
    public long SilentFrames { get; private set; }

    /// <summary>Largest captured sample (0 to 1) since the last call; paced silence does not count.</summary>
    public double TakeCapturedPeak()
    {
        lock (gate)
        {
            int peak = capturedPeak;
            capturedPeak = 0;
            return peak / 32768d;
        }
    }

    /// <summary>Starts producing frames; captured data that arrives before this is discarded.</summary>
    public void Enable()
    {
        lock (gate)
        {
            if (enabled) return;
            enabled = true;
            pacer.Start(clock());
        }
    }

    public void Disable()
    {
        lock (gate)
        {
            enabled = false;
            framer.Reset();
        }
    }

    /// <summary>Converts captured audio; returns true when at least one frame was queued.</summary>
    public bool Push(ReadOnlySpan<byte> data, WaveFormat dataFormat)
    {
        lock (gate)
        {
            if (!enabled) return false;
            if (converter is null || format is null || !SameFormat(dataFormat, format))
            {
                // A different device or format restarts the filter; a partial frame from the old format is not mixed in.
                converter = new Pcm16Converter(dataFormat);
                format = dataFormat;
                framer.Reset();
            }
            long before = CapturedFrames;
            var pcm = converter.Convert(data);
            for (int i = 0; i + 1 < pcm.Length; i += 2)
                capturedPeak = Math.Max(capturedPeak, Math.Abs((int)BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i))));
            framer.Push(pcm, frame =>
            {
                if (outbox.EnqueueFrame(generation, frame)) CapturedFrames++;
            });
            if (CapturedFrames == before) return false;
            pacer.OnFrame(clock());
            return true;
        }
    }

    /// <summary>Called about every 20 ms; returns true when silent frames were queued.</summary>
    public bool Tick()
    {
        lock (gate)
        {
            if (!enabled) return false;
            int due = pacer.Due(clock());
            if (due == 0) return false;
            SilentFrames += due;
            if (framer.FlushPadded() is { } padded)
            {
                outbox.EnqueueFrame(generation, padded);
                due--;
            }
            for (; due > 0; due--) outbox.EnqueueFrame(generation, new byte[Pcm16Framer.FrameBytes]);
            return true;
        }
    }

    private static bool SameFormat(WaveFormat a, WaveFormat b) =>
        ReferenceEquals(a, b) || a.Equals(b) && (a as WaveFormatExtensible)?.SubFormat == (b as WaveFormatExtensible)?.SubFormat;
}
