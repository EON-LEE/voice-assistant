using System.Diagnostics.CodeAnalysis;
using VoiceAssistant.Audio;

namespace VoiceAssistant.WebOverlay.HostAudio;

/// <summary>
/// Messages waiting to be posted to the page, in order. Frames get consecutive seq numbers. At most
/// <see cref="CapacityFrames"/> frames wait (4 s by default); when the page does not keep up, the oldest frame is dropped
/// and counted. At most <see cref="WindowFrames"/> posted frames may be unacknowledged, so a stalled page cannot make
/// WebView2 buffer audio without limit and a recovered page never receives more than one second at once.
/// Status messages are not subject to the window and are kept in order with the frames.
/// </summary>
internal sealed class HostAudioOutbox
{
    private const int MaxQueuedStatuses = 16;
    private readonly object gate = new();
    private readonly LinkedList<Item> items = new();
    private readonly Queue<(long Last, int Count)> inFlight = new();
    private int queuedFrames, queuedStatuses, inFlightFrames, generation;
    private long nextSeq, dropped;

    public HostAudioOutbox(int capacityFrames = 200, int windowFrames = 50, int maxBatchFrames = HostAudioProtocol.MaxFramesPerMessage)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacityFrames, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(windowFrames, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBatchFrames, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxBatchFrames, HostAudioProtocol.MaxFramesPerMessage);
        CapacityFrames = capacityFrames;
        WindowFrames = windowFrames;
        MaxBatchFrames = maxBatchFrames;
    }

    public int CapacityFrames { get; }
    public int WindowFrames { get; }
    public int MaxBatchFrames { get; }

    /// <summary>Frames dropped since the last <see cref="Reset"/> because the page did not consume them.</summary>
    public long Dropped { get { lock (gate) return dropped; } }
    public int QueuedFrames { get { lock (gate) return queuedFrames; } }
    public int InFlightFrames { get { lock (gate) return inFlightFrames; } }
    public int Generation { get { lock (gate) return generation; } }

    /// <summary>Forgets every waiting and unacknowledged message, restarts seq at 0 and invalidates older producers.</summary>
    public int Reset()
    {
        lock (gate)
        {
            items.Clear();
            inFlight.Clear();
            queuedFrames = queuedStatuses = inFlightFrames = 0;
            nextSeq = dropped = 0;
            return ++generation;
        }
    }

    /// <summary>Queues a 640-byte frame; returns false when <paramref name="producer"/> belongs to an older capture.</summary>
    public bool EnqueueFrame(int producer, byte[] frame)
    {
        if (frame.Length != Pcm16Framer.FrameBytes) throw new ArgumentException("Frames must be 640 bytes.", nameof(frame));
        lock (gate)
        {
            if (producer != generation) return false;
            if (queuedFrames == CapacityFrames)
            {
                for (var node = items.First; node is not null; node = node.Next)
                {
                    if (node.Value.Frame is null) continue;
                    items.Remove(node);
                    queuedFrames--;
                    dropped++;
                    break;
                }
            }
            items.AddLast(new Item(nextSeq++, frame, null));
            queuedFrames++;
            return true;
        }
    }

    public bool EnqueueStatus(int producer, string json)
    {
        lock (gate)
        {
            if (producer != generation) return false;
            if (queuedStatuses == MaxQueuedStatuses)
            {
                for (var node = items.First; node is not null; node = node.Next)
                {
                    if (node.Value.Status is null) continue;
                    items.Remove(node);
                    queuedStatuses--;
                    break;
                }
            }
            items.AddLast(new Item(0, null, json));
            queuedStatuses++;
            return true;
        }
    }

    /// <summary>Takes the next message to post, or returns false when nothing is waiting or the window is full.</summary>
    public bool TryTake([NotNullWhen(true)] out string? message)
    {
        lock (gate)
        {
            message = null;
            var first = items.First;
            if (first is null) return false;
            if (first.Value.Status is { } status)
            {
                items.RemoveFirst();
                queuedStatuses--;
                message = status;
                return true;
            }
            int room = Math.Min(MaxBatchFrames, WindowFrames - inFlightFrames);
            if (room <= 0) return false;
            long firstSeq = first.Value.Seq, next = firstSeq;
            var batch = new List<byte[]>(room);
            while (batch.Count < room && items.First is { } node && node.Value.Frame is { } frame && node.Value.Seq == next)
            {
                batch.Add(frame);
                items.RemoveFirst();
                next++;
            }
            queuedFrames -= batch.Count;
            var pcm = new byte[batch.Count * Pcm16Framer.FrameBytes];
            for (int i = 0; i < batch.Count; i++) batch[i].CopyTo(pcm, i * Pcm16Framer.FrameBytes);
            inFlight.Enqueue((next - 1, batch.Count));
            inFlightFrames += batch.Count;
            message = HostAudioProtocol.Frames(firstSeq, pcm);
            return true;
        }
    }

    /// <summary>The page handled every frame up to and including <paramref name="seq"/>.</summary>
    public void Ack(long seq)
    {
        lock (gate)
        {
            while (inFlight.TryPeek(out var head) && head.Last <= seq)
            {
                inFlight.Dequeue();
                inFlightFrames -= head.Count;
            }
        }
    }

    private readonly record struct Item(long Seq, byte[]? Frame, string? Status);
}
