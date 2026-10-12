using System.Buffers.Binary;
using NAudio.Wave;

namespace VoiceAssistant.Desktop.Audio;

/// <summary>Streaming downmix and band-limited resampling. Never stores audio outside memory.</summary>
public sealed class Pcm16Converter
{
    private const int OutputRate = 16000;
    private const int Taps = 127;
    private readonly WaveFormat format;
    private readonly bool isFloat;
    private readonly double[] kernel = new double[Taps];
    private readonly double[] history = new double[Taps];
    private readonly byte[] pending;
    private int pendingCount;
    private int position;
    private int phase;
    private double previous;

    public Pcm16Converter(WaveFormat format)
    {
        this.format = format;
        var encoding = format.Encoding;
        if (format is WaveFormatExtensible extensible)
        {
            encoding = extensible.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71")
                ? WaveFormatEncoding.IeeeFloat
                : extensible.SubFormat == new Guid("00000001-0000-0010-8000-00aa00389b71")
                    ? WaveFormatEncoding.Pcm : throw new NotSupportedException("Unsupported endpoint audio subformat.");
        }
        isFloat = encoding == WaveFormatEncoding.IeeeFloat;
        if (format.SampleRate < OutputRate || format.SampleRate > 192000 ||
            format.Channels is < 1 or > 32 ||
            (isFloat ? format.BitsPerSample != 32 : encoding != WaveFormatEncoding.Pcm ||
                format.BitsPerSample is not (16 or 24 or 32)) ||
            format.BlockAlign != format.Channels * (format.BitsPerSample / 8))
            throw new NotSupportedException("Endpoint must provide 16-192 kHz float32 or PCM16/24/32 audio.");
        pending = new byte[format.BlockAlign];
        double cutoff = Math.Min(7200d / format.SampleRate, 0.45);
        double sum = 0;
        for (int i = 0; i < Taps; i++)
        {
            int n = i - (Taps - 1) / 2;
            double sinc = n == 0 ? 2 * cutoff : Math.Sin(2 * Math.PI * cutoff * n) / (Math.PI * n);
            kernel[i] = sinc * (0.42 - 0.5 * Math.Cos(2 * Math.PI * i / (Taps - 1)) +
                0.08 * Math.Cos(4 * Math.PI * i / (Taps - 1)));
            sum += kernel[i];
        }
        for (int i = 0; i < Taps; i++) kernel[i] /= sum;
    }

    public byte[] Convert(ReadOnlySpan<byte> input)
    {
        var output = new List<byte>((input.Length / format.BlockAlign + 1) * 2);
        while (!input.IsEmpty)
        {
            int take = Math.Min(pending.Length - pendingCount, input.Length);
            input[..take].CopyTo(pending.AsSpan(pendingCount));
            pendingCount += take;
            input = input[take..];
            if (pendingCount != pending.Length) continue;
            pendingCount = 0;
            double mono = 0;
            int bytes = format.BitsPerSample / 8;
            for (int channel = 0; channel < format.Channels; channel++)
            {
                var sample = pending.AsSpan(channel * bytes, bytes);
                double value = isFloat ? BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(sample))
                    : bytes == 2 ? BinaryPrimitives.ReadInt16LittleEndian(sample) / 32768d
                    : bytes == 3 ? ((sample[0] | sample[1] << 8 | sample[2] << 16) << 8 >> 8) / 8388608d
                    : BinaryPrimitives.ReadInt32LittleEndian(sample) / 2147483648d;
                mono += double.IsFinite(value) ? Math.Clamp(value, -1, 1) : 0;
            }
            history[position] = mono / format.Channels;
            double filtered = 0;
            for (int i = 0; i < Taps; i++)
                filtered += kernel[i] * history[(position - i + Taps) % Taps];
            position = (position + 1) % Taps;
            phase += OutputRate;
            if (phase >= format.SampleRate)
            {
                phase -= format.SampleRate;
                double fraction = 1d - phase / (double)OutputRate;
                double value = previous + fraction * (filtered - previous);
                short pcm = (short)Math.Clamp(Math.Round(value * 32768), short.MinValue, short.MaxValue);
                output.Add((byte)(pcm & 255));
                output.Add((byte)((pcm >> 8) & 255));
            }
            previous = filtered;
        }
        return output.ToArray();
    }
}
