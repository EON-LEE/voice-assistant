using System.Text.Json;
using VoiceAssistant.WebOverlay.HostAudio;

namespace VoiceAssistant.WebOverlay.Tests;

/// <summary>A host-to-page message as the page would read it.</summary>
internal sealed record PageMessage(string Type, long Seq, byte[] Pcm, string? State, string? Id, string? Detail, string? Source = null,
    string? Code = null)
{
    public int Frames => Pcm.Length / 640;

    public static PageMessage Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        string type = root.GetProperty("type").GetString()!;
        return type == "host-audio.frame"
            ? new(type, root.GetProperty("seq").GetInt64(), Convert.FromBase64String(root.GetProperty("pcm").GetString()!), null, null, null)
            : new(type, -1, [], root.GetProperty("state").GetString(), root.TryGetProperty("id", out var id) ? id.GetString() : null,
                root.GetProperty("detail").GetString(), root.GetProperty("source").GetString(),
                root.TryGetProperty("code", out var code) ? code.GetString() : null);
    }

    public static List<PageMessage> TakeAll(HostAudioOutbox outbox)
    {
        var messages = new List<PageMessage>();
        while (outbox.TryTake(out var json)) messages.Add(Parse(json));
        return messages;
    }
}

public sealed class HostAudioOutboxTests
{
    private static byte[] Frame(byte marker) => Enumerable.Repeat(marker, 640).ToArray();

    [Fact]
    public void BatchesUpToFiveConsecutiveFramesWithTheFirstSeq()
    {
        var outbox = new HostAudioOutbox();
        int generation = outbox.Reset();
        for (byte i = 0; i < 12; i++) Assert.True(outbox.EnqueueFrame(generation, Frame(i)));
        var messages = PageMessage.TakeAll(outbox);
        Assert.Equal(new long[] { 0, 5, 10 }, messages.Select(m => m.Seq));
        Assert.Equal(new[] { 5, 5, 2 }, messages.Select(m => m.Frames));
        Assert.Equal(Enumerable.Range(0, 12).Select(i => (byte)i), messages.SelectMany(m => m.Pcm.Chunk(640).Select(f => f[0])));
        Assert.Equal(12, outbox.InFlightFrames);
    }

    [Fact]
    public void UnacknowledgedWindowHoldsFramesUntilThePageAcknowledges()
    {
        var outbox = new HostAudioOutbox(capacityFrames: 200, windowFrames: 10);
        int generation = outbox.Reset();
        for (byte i = 0; i < 30; i++) outbox.EnqueueFrame(generation, Frame(i));
        Assert.Equal(new long[] { 0, 5 }, PageMessage.TakeAll(outbox).Select(m => m.Seq));
        Assert.False(outbox.TryTake(out _));
        outbox.Ack(3);
        Assert.False(outbox.TryTake(out _));
        outbox.Ack(4);
        Assert.Equal(new long[] { 10 }, PageMessage.TakeAll(outbox).Select(m => m.Seq));
        outbox.Ack(1000);
        Assert.Equal(0, outbox.InFlightFrames);
        Assert.Equal(new long[] { 15, 20 }, PageMessage.TakeAll(outbox).Select(m => m.Seq));
        Assert.Equal(5, outbox.QueuedFrames);
    }

    [Fact]
    public void FullQueueDropsTheOldestFramesAndCountsThem()
    {
        var outbox = new HostAudioOutbox(capacityFrames: 4);
        int generation = outbox.Reset();
        for (byte i = 0; i < 7; i++) outbox.EnqueueFrame(generation, Frame(i));
        Assert.Equal(3, outbox.Dropped);
        Assert.Equal(4, outbox.QueuedFrames);
        var message = Assert.Single(PageMessage.TakeAll(outbox));
        Assert.Equal(3, message.Seq);
        Assert.Equal(new byte[] { 3, 4, 5, 6 }, message.Pcm.Chunk(640).Select(f => f[0]));
    }

    [Fact]
    public void StatusesKeepTheirPlaceAreNeverCountedAsFramesAndPassAFullWindowOnlyAtTheHead()
    {
        var outbox = new HostAudioOutbox(capacityFrames: 3, windowFrames: 1);
        int generation = outbox.Reset();
        outbox.EnqueueFrame(generation, Frame(1));
        outbox.EnqueueStatus(generation, HostAudioProtocol.Status(HostAudioState.Capturing, "a", HostAudioKind.Teams, "x"));
        outbox.EnqueueFrame(generation, Frame(2));
        outbox.EnqueueFrame(generation, Frame(3));
        Assert.Equal(0, outbox.Dropped);
        var first = PageMessage.TakeAll(outbox);
        Assert.Equal(new[] { "host-audio.frame", "host-audio.status" }, first.Select(m => m.Type));
        Assert.Equal(1, first[0].Frames);
        Assert.False(outbox.TryTake(out _));
        outbox.Ack(0);
        Assert.Equal(1, Assert.Single(PageMessage.TakeAll(outbox)).Seq);
        for (byte i = 4; i <= 6; i++) outbox.EnqueueFrame(generation, Frame(i));
        Assert.Equal(1, outbox.Dropped);
        outbox.Ack(1);
        Assert.Equal(3, Assert.Single(PageMessage.TakeAll(outbox)).Seq);
    }

    [Fact]
    public void WaitingStatusesAreBounded()
    {
        var outbox = new HostAudioOutbox(windowFrames: 1);
        int generation = outbox.Reset();
        outbox.EnqueueFrame(generation, Frame(0));
        outbox.EnqueueFrame(generation, Frame(1));
        Assert.True(outbox.TryTake(out _));
        for (int i = 0; i < 20; i++)
            outbox.EnqueueStatus(generation, HostAudioProtocol.Status(HostAudioState.Error, $"#{i}", HostAudioKind.Teams, null));
        outbox.Ack(0);
        var statuses = PageMessage.TakeAll(outbox).Where(m => m.Type == "host-audio.status").ToList();
        Assert.Equal(16, statuses.Count);
        Assert.Equal("#4", statuses[0].Detail);
    }

    [Fact]
    public void ResetStartsANewCaptureAndRejectsOlderProducers()
    {
        var outbox = new HostAudioOutbox(capacityFrames: 1);
        int old = outbox.Reset();
        outbox.EnqueueFrame(old, Frame(1));
        outbox.EnqueueFrame(old, Frame(2));
        Assert.True(outbox.TryTake(out _));
        int current = outbox.Reset();
        Assert.NotEqual(old, current);
        Assert.Equal(0, outbox.Dropped);
        Assert.Equal(0, outbox.InFlightFrames);
        Assert.False(outbox.EnqueueFrame(old, Frame(3)));
        Assert.False(outbox.EnqueueStatus(old, "{}"));
        Assert.True(outbox.EnqueueFrame(current, Frame(4)));
        Assert.Equal(0, Assert.Single(PageMessage.TakeAll(outbox)).Seq);
        Assert.Throws<ArgumentException>(() => outbox.EnqueueFrame(current, new byte[639]));
    }
}
