using System.Text.Json;
using VoiceAssistant.WebOverlay.HostAudio;

namespace VoiceAssistant.WebOverlay.Tests;

public sealed class HostAudioProtocolTests
{
    [Theory]
    [InlineData("""{"type":"host-audio.start","source":"teams","id":"a-1_B"}""", "Teams", "a-1_B")]
    [InlineData("""{"type":"host-audio.start","source":"system"}""", "System", null)]
    [InlineData("""{"source":"teams","type":"host-audio.start","extra":[1,2]}""", "Teams", null)]
    public void ParsesStart(string json, string kind, string? id)
    {
        var start = Assert.IsType<HostAudioStart>(HostAudioProtocol.ParseCommand(json));
        Assert.Equal(Enum.Parse<HostAudioKind>(kind), start.Kind);
        Assert.Equal(id, start.Id);
    }

    [Fact]
    public void ParsesStopAndAck()
    {
        Assert.Equal(new HostAudioStop("x"), HostAudioProtocol.ParseCommand("""{"type":"host-audio.stop","id":"x"}"""));
        Assert.Equal(new HostAudioStop(null), HostAudioProtocol.ParseCommand("""{"type":"host-audio.stop"}"""));
        Assert.Equal(new HostAudioAck(41, "x"), HostAudioProtocol.ParseCommand("""{"type":"host-audio.ack","seq":41,"id":"x"}"""));
    }

    [Theory]
    [InlineData("drag")]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("[1]")]
    [InlineData("{}")]
    [InlineData("""{"type":"host-audio.start"}""")]
    [InlineData("""{"type":"host-audio.start","source":"screen"}""")]
    [InlineData("""{"type":"host-audio.start","source":"Teams"}""")]
    [InlineData("""{"type":"host-audio.start","source":"teams","id":"bad id"}""")]
    [InlineData("""{"type":"host-audio.start","source":"teams","id":""}""")]
    [InlineData("""{"type":"host-audio.start","source":"teams","id":7}""")]
    [InlineData("""{"type":"host-audio.ack","seq":-1}""")]
    [InlineData("""{"type":"host-audio.ack","seq":"5"}""")]
    [InlineData("""{"type":"host-audio.ack","seq":1.5}""")]
    [InlineData("""{"type":"host-audio.ack"}""")]
    [InlineData("""{"type":"host-audio.frame","seq":0,"pcm":""}""")]
    [InlineData("""{"type":"host-audio.start","source":"teams"} trailing""")]
    [InlineData("""{"type":5}""")]
    public void IgnoresAnythingElse(string message) => Assert.Null(HostAudioProtocol.ParseCommand(message));

    [Fact]
    public void RejectsOversizedCommandsAndIds()
    {
        string longId = new('a', HostAudioProtocol.MaxIdChars + 1);
        Assert.Null(HostAudioProtocol.ParseCommand($$"""{"type":"host-audio.stop","id":"{{longId}}"}"""));
        Assert.NotNull(HostAudioProtocol.ParseCommand($$"""{"type":"host-audio.stop","id":"{{longId[1..]}}"}"""));
        string padded = "{\"type\":\"host-audio.stop\",\"pad\":\"" + new string(' ', HostAudioProtocol.MaxCommandChars) + "\"}";
        Assert.Null(HostAudioProtocol.ParseCommand(padded));
    }

    [Fact]
    public void FrameMessagesCarryBase64OfWholeFramesAndTheFirstSeq()
    {
        byte[] pcm = Enumerable.Range(0, 640 * 2).Select(i => (byte)(i * 7)).ToArray();
        using var json = JsonDocument.Parse(HostAudioProtocol.Frames(9007199254740990, pcm));
        Assert.Equal("host-audio.frame", json.RootElement.GetProperty("type").GetString());
        Assert.Equal(9007199254740990, json.RootElement.GetProperty("seq").GetInt64());
        Assert.Equal(pcm, Convert.FromBase64String(json.RootElement.GetProperty("pcm").GetString()!));
        Assert.Throws<ArgumentException>(() => HostAudioProtocol.Frames(0, []));
        Assert.Throws<ArgumentException>(() => HostAudioProtocol.Frames(0, new byte[641]));
        Assert.Throws<ArgumentException>(() => HostAudioProtocol.Frames(0, new byte[640 * 6]));
    }

    [Fact]
    public void StatusMessagesAreEscapedJsonWithWireNames()
    {
        string detail = "Device \"Speakers\" <changed> & 한국어 \\ path";
        using var json = JsonDocument.Parse(HostAudioProtocol.Status(HostAudioState.TeamsNotRunning, detail, HostAudioKind.Teams, "id-1"));
        var root = json.RootElement;
        Assert.Equal("host-audio.status", root.GetProperty("type").GetString());
        Assert.Equal("id-1", root.GetProperty("id").GetString());
        Assert.Equal("teams", root.GetProperty("source").GetString());
        Assert.Equal("teams-not-running", root.GetProperty("state").GetString());
        Assert.Equal(detail, root.GetProperty("detail").GetString());
        using var anonymous = JsonDocument.Parse(HostAudioProtocol.Status(HostAudioState.Stopped, "", HostAudioKind.System, null));
        Assert.False(anonymous.RootElement.TryGetProperty("id", out _));
        Assert.Equal("system", anonymous.RootElement.GetProperty("source").GetString());
        Assert.Equal(new[] { "capturing", "teams-not-running", "unsupported-os", "error", "stopped" },
            Enum.GetValues<HostAudioState>().Select(state => HostAudioProtocol.Wire(state)));
    }

    [Theory]
    [InlineData("https://voice.example.com/live.html", true)]
    [InlineData("https://VOICE.Example.com/other?x=1#y", true)]
    [InlineData("https://voice.example.com:443/", true)]
    [InlineData("http://voice.example.com/live.html", false)]
    [InlineData("https://voice.example.com:8443/live.html", false)]
    [InlineData("https://evil.example.com/live.html", false)]
    [InlineData("https://voice.example.com.evil.net/live.html", false)]
    [InlineData("https://user@voice.example.net/", false)]
    [InlineData("about:blank", false)]
    [InlineData("not a url", false)]
    [InlineData(null, false)]
    public void OnlyTheHostedPageOriginIsTrusted(string? candidate, bool trusted) =>
        Assert.Equal(trusted, PageOrigin.Matches(candidate, new Uri("https://voice.example.com/live.html")));

    [Fact]
    public void ShellScriptAdvertisesHostAudioOnlyInsideTheOriginCheck()
    {
        string script = ShellScript.Build("https://voice.example.com", 0.8, ["teams", "system"]);
        Assert.Contains("""hostAudio: {"version":1,"sources":["teams","system"]}""", script);
        Assert.True(script.IndexOf("location.origin !== \"https://voice.example.com\"", StringComparison.Ordinal) <
            script.IndexOf("hostAudio", StringComparison.Ordinal));
        Assert.Contains("""{"version":1,"sources":["system"]}""", ShellScript.Build("https://voice.example.com", 0.8, ["system"]));
    }
}
