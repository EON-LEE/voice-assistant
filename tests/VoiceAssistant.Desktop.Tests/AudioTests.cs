using System.Buffers.Binary;
using NAudio.Wave;
using VoiceAssistant.Desktop.Audio;

namespace VoiceAssistant.Desktop.Tests;

public sealed class AudioTests
{
    [Theory]
    [InlineData(16000)]
    [InlineData(44100)]
    [InlineData(48000)]
    [InlineData(96000)]
    [InlineData(192000)]
    public void ExactlyOneSecondBecomes16000MonoLittleEndianSamples(int rate)
    {
        var converter = new Pcm16Converter(WaveFormat.CreateIeeeFloatWaveFormat(rate, 2));
        byte[] input = FloatAudio(rate, 2, (_, _) => 0.5f);
        byte[] output = converter.Convert(input);
        Assert.Equal(32000, output.Length);
        Assert.All(Enumerable.Range(1000, 15000), i =>
            Assert.InRange(BinaryPrimitives.ReadInt16LittleEndian(output.AsSpan(i * 2)), (short)16383, (short)16385));
        Assert.Equal(0, output[output.Length - 2]);
        Assert.Equal(64, output[output.Length - 1]);
    }

    [Fact]
    public void ArbitraryByteChunksPreserveSampleContinuity()
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);
        var bytes = FloatAudio(44100, 2, (i, c) => (float)(0.4 * Math.Sin(i * .1 + c)));
        byte[] expected = new Pcm16Converter(format).Convert(bytes);
        var streaming = new Pcm16Converter(format);
        var actual = new List<byte>();
        for (int offset = 0; offset < bytes.Length; offset += 137)
            actual.AddRange(streaming.Convert(bytes.AsSpan(offset, Math.Min(137, bytes.Length - offset))));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void OppositeStereoChannelsCancelAndNonfiniteFloatsAreSafe()
    {
        var converter = new Pcm16Converter(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2));
        Assert.All(converter.Convert(FloatAudio(4800, 2, (_, c) => c == 0 ? .7f : -.7f)), b => Assert.Equal(0, b));
        Assert.All(converter.Convert(FloatAudio(4800, 2, (_, _) => float.NaN)), b => Assert.Equal(0, b));
    }

    [Fact]
    public void DownsamplingRejectsUltrasonicAlias()
    {
        double Rms(int hz)
        {
            byte[] output = new Pcm16Converter(WaveFormat.CreateIeeeFloatWaveFormat(48000, 1))
                .Convert(FloatAudio(48000, 1, (i, _) => (float)(0.5 * Math.Sin(2 * Math.PI * hz * i / 48000))));
            return Math.Sqrt(Enumerable.Range(1000, 15000)
                .Average(i => Math.Pow(BinaryPrimitives.ReadInt16LittleEndian(output.AsSpan(i * 2)) / 32768d, 2)));
        }
        Assert.InRange(Rms(1000), 0.34, 0.36);
        Assert.True(Rms(12000) < 0.001, "12 kHz input must be attenuated before 16 kHz downsampling.");
    }

    [Theory]
    [InlineData(16)]
    [InlineData(24)]
    [InlineData(32)]
    public void PcmFormatsPreserveNegativeSign(int bits)
    {
        var format = new WaveFormat(48000, bits, 1);
        byte[] input = new byte[4800 * bits / 8];
        for (int i = 0; i < 4800; i++)
            input[i * (bits / 8) + bits / 8 - 1] = 0xC0;
        byte[] output = new Pcm16Converter(format).Convert(input);
        Assert.Equal(-16384, BinaryPrimitives.ReadInt16LittleEndian(output.AsSpan(output.Length - 2)));
    }

    [Fact]
    public void RejectsUnsupportedFormats()
    {
        Assert.Throws<NotSupportedException>(() => new Pcm16Converter(new WaveFormat(8000, 16, 1)));
        Assert.Throws<NotSupportedException>(() => new Pcm16Converter(new WaveFormat(48000, 8, 1)));
    }

    internal static byte[] FloatAudio(int frames, int channels, Func<int, int, float> sample)
    {
        byte[] bytes = new byte[frames * channels * 4];
        for (int i = 0; i < frames; i++)
            for (int c = 0; c < channels; c++)
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan((i * channels + c) * 4),
                    BitConverter.SingleToInt32Bits(sample(i, c)));
        return bytes;
    }
}
