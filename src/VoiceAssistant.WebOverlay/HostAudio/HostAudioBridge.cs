namespace VoiceAssistant.WebOverlay.HostAudio;

/// <summary>
/// Connects the page's host-audio web messages to <see cref="HostAudioService"/> and posts queued messages back on the
/// UI thread. The caller only passes messages from the trusted page origin, and nothing is posted unless the current
/// document is still on that origin; otherwise the capture stops.
/// </summary>
internal sealed class HostAudioBridge
{
    private readonly HostAudioService service;
    private readonly Action<string> post;
    private readonly Func<bool> pageTrusted;
    private readonly Action<Action> dispatch;
    private int drainScheduled;

    /// <param name="post">Posts one JSON string to the page (CoreWebView2.PostWebMessageAsString); UI thread only.</param>
    /// <param name="pageTrusted">Whether the current document is on the trusted page origin.</param>
    /// <param name="dispatch">Runs an action later on the UI thread.</param>
    public HostAudioBridge(HostAudioService service, Action<string> post, Func<bool> pageTrusted, Action<Action> dispatch)
    {
        this.service = service;
        this.post = post;
        this.pageTrusted = pageTrusted;
        this.dispatch = dispatch;
        service.MessagesAvailable += ScheduleDrain;
    }

    /// <summary>Handles a JSON web message from the trusted page; returns false when it is not a host-audio command.</summary>
    public bool HandlePageMessage(string json)
    {
        switch (HostAudioProtocol.ParseCommand(json))
        {
            case HostAudioStart start:
                service.Start(start.Kind, start.Id);
                return true;
            case HostAudioStop stop:
                service.Stop(stop.Id);
                return true;
            case HostAudioAck ack:
                if (ack.Id == service.CurrentId)
                {
                    service.Outbox.Ack(ack.Seq);
                    ScheduleDrain();
                }
                return true;
            default:
                return false;
        }
    }

    /// <summary>The document that asked for audio is going away (navigation, reload or renderer failure).</summary>
    public void PageLeaving() => service.Stop(null, notify: false);

    private void ScheduleDrain()
    {
        if (Interlocked.Exchange(ref drainScheduled, 1) == 0) dispatch(Drain);
    }

    /// <summary>Posts every message the outbox releases. Runs on the UI thread.</summary>
    internal void Drain()
    {
        Volatile.Write(ref drainScheduled, 0);
        if (!service.Outbox.TryTake(out var message)) return;
        if (!pageTrusted())
        {
            service.Stop(null, notify: false);
            return;
        }
        do
        {
            try { post(message); }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                // The WebView is closing; nothing can be delivered any more.
                service.Stop(null, notify: false);
                return;
            }
        }
        while (service.Outbox.TryTake(out message));
    }
}
