namespace VoiceAssistant.Audio;

/// <summary>Cuts a 16 kHz mono PCM16 stream into exact 20 ms frames (640 bytes), the unit the meeting protocol sends.</summary>
public sealed class Pcm16Framer
{
    public const int FrameBytes = 640;
    private readonly byte[] partial = new byte[FrameBytes];
    private int count;

    /// <summary>Bytes waiting for the rest of their frame.</summary>
    public int PendingBytes => count;

    public void Push(ReadOnlySpan<byte> pcm, Action<byte[]> emit)
    {
        while (!pcm.IsEmpty)
        {
            int take = Math.Min(FrameBytes - count, pcm.Length);
            pcm[..take].CopyTo(partial.AsSpan(count));
            count += take;
            pcm = pcm[take..];
            if (count < FrameBytes) return;
            count = 0;
            emit(partial.ToArray());
        }
    }

    /// <summary>Completes a partial frame with silence, or returns null when nothing is pending.</summary>
    public byte[]? FlushPadded()
    {
        if (count == 0) return null;
        var frame = new byte[FrameBytes];
        partial.AsSpan(0, count).CopyTo(frame);
        count = 0;
        return frame;
    }

    public void Reset() => count = 0;
}
