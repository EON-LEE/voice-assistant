namespace VoiceAssistant.WebOverlay.HostAudio;

/// <summary>
/// Keeps the page's audio continuous like the browser worklet, which always delivers 50 frames per second. When capture
/// delivers nothing for <c>stallAfter</c> (Teams closed or not rendering, a device switching, a capture restarting),
/// it asks for silent frames at real-time pace until captured frames arrive again. The server ends sessions that send
/// nothing for 90 seconds.
/// </summary>
internal sealed class SilencePacer(TimeSpan stallAfter, int maxCatchUpFrames = 25)
{
    public static readonly TimeSpan Frame = TimeSpan.FromMilliseconds(20);
    private TimeSpan last;
    private bool stalled;

    public void Start(TimeSpan now) => OnFrame(now);

    public void OnFrame(TimeSpan now)
    {
        last = now;
        stalled = false;
    }

    /// <summary>Silent frames to emit at <paramref name="now"/>. After a long suspension only a bounded catch-up is emitted.</summary>
    public int Due(TimeSpan now)
    {
        var idle = now - last;
        if (!stalled && idle < stallAfter) return 0;
        stalled = true;
        long frames = idle.Ticks / Frame.Ticks;
        if (frames > maxCatchUpFrames)
        {
            last = now;
            return maxCatchUpFrames;
        }
        last += TimeSpan.FromTicks(Frame.Ticks * frames);
        return (int)frames;
    }
}
