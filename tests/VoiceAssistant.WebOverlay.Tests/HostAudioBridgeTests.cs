using NAudio.Wave;
using VoiceAssistant.WebOverlay.HostAudio;
using static VoiceAssistant.WebOverlay.Tests.HostAudioServiceTests;

namespace VoiceAssistant.WebOverlay.Tests;

public sealed class HostAudioBridgeTests
{
    private static readonly HostAudioTiming Fast = new(TimeSpan.FromMilliseconds(40), TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(10));

    private sealed class Harness : IDisposable
    {
        private readonly Queue<Action> dispatched = new();
        public Harness(int windowFrames = 50)
        {
            Service = new HostAudioService(Platform, new HostAudioOutbox(windowFrames: windowFrames), Fast);
            Bridge = new HostAudioBridge(Service, json => { if (Throw) throw new InvalidOperationException("closed"); Posted.Add(PageMessage.Parse(json)); },
                () => Trusted, action => { lock (dispatched) dispatched.Enqueue(action); });
        }

        public FakePlatform Platform { get; } = new() { Teams = 42 };
        public HostAudioService Service { get; }
        public HostAudioBridge Bridge { get; }
        public List<PageMessage> Posted { get; } = [];
        public bool Trusted { get; set; } = true;
        public bool Throw { get; set; }

        /// <summary>Runs what the bridge queued for the UI thread.</summary>
        public void RunUi()
        {
            while (true)
            {
                Action? action;
                lock (dispatched) if (!dispatched.TryDequeue(out action)) return;
                action();
            }
        }

        public async Task WaitAsync(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (true)
            {
                RunUi();
                if (condition()) return;
                if (DateTime.UtcNow > deadline) throw new TimeoutException();
                await Task.Delay(5);
            }
        }

        public void Dispose() => Service.Dispose();
    }

    [Fact]
    public async Task PageCommandsDriveCaptureAndMessagesArePostedInOrderOnTheUiThread()
    {
        using var h = new Harness(windowFrames: 2);
        Assert.True(h.Bridge.HandlePageMessage("""{"type":"host-audio.start","source":"teams","id":"p1"}"""));
        await h.WaitAsync(() => h.Posted.Count == 1);
        Assert.Equal(("host-audio.status", "capturing", "p1"), (h.Posted[0].Type, h.Posted[0].State, h.Posted[0].Id));
        h.Platform.Captures[0].Emit(new byte[640], new WaveFormat(16000, 16, 1));
        await h.WaitAsync(() => h.Posted.Count == 2);
        for (int i = 0; i < 3; i++) h.Platform.Captures[0].Emit(new byte[640], new WaveFormat(16000, 16, 1));
        await h.WaitAsync(() => h.Service.Outbox.InFlightFrames == 2);
        int posted = h.Posted.Count;
        Assert.True(h.Bridge.HandlePageMessage("""{"type":"host-audio.ack","seq":3,"id":"stale"}"""));
        h.RunUi();
        Assert.Equal(posted, h.Posted.Count);
        Assert.True(h.Bridge.HandlePageMessage("""{"type":"host-audio.ack","seq":1,"id":"p1"}"""));
        await h.WaitAsync(() => h.Posted.Count > posted);
        Assert.Equal(new long[] { 0, 1, 2 }, h.Posted.Where(m => m.Type == "host-audio.frame").Select(m => m.Seq).Take(3));
        Assert.True(h.Bridge.HandlePageMessage("""{"type":"host-audio.stop","id":"p1"}"""));
        await h.WaitAsync(() => h.Posted[^1].State == "stopped");
        Assert.False(h.Service.IsActive);
    }

    [Fact]
    public async Task NothingIsPostedOnceTheDocumentLeftTheTrustedOrigin()
    {
        using var h = new Harness();
        h.Trusted = false;
        h.Bridge.HandlePageMessage("""{"type":"host-audio.start","source":"system","id":"p1"}""");
        await h.WaitAsync(() => !h.Service.IsActive);
        Assert.Empty(h.Posted);
    }

    [Fact]
    public async Task AClosedWebViewStopsTheCapture()
    {
        using var h = new Harness { Throw = true };
        h.Bridge.HandlePageMessage("""{"type":"host-audio.start","source":"teams","id":"p1"}""");
        await h.WaitAsync(() => !h.Service.IsActive);
        Assert.Empty(h.Posted);
    }

    [Fact]
    public async Task LeavingThePageStopsSilentlyAndOtherMessagesAreNotCommands()
    {
        using var h = new Harness();
        Assert.False(h.Bridge.HandlePageMessage("drag"));
        Assert.False(h.Bridge.HandlePageMessage("""{"type":"something-else"}"""));
        h.Bridge.HandlePageMessage("""{"type":"host-audio.start","source":"teams","id":"p1"}""");
        await h.WaitAsync(() => h.Posted.Count == 1);
        h.Bridge.PageLeaving();
        h.RunUi();
        Assert.False(h.Service.IsActive);
        Assert.Single(h.Posted);
    }
}
