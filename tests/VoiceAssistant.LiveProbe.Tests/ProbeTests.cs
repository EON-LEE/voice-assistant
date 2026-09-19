using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Microsoft.Extensions.Configuration;
using VoiceAssistant.Api;
using VoiceAssistant.LiveProbe;
using Xunit;

namespace VoiceAssistant.LiveProbe.Tests;

public sealed class ProbeTests
{
    [Fact]
    public void WaveParserUsesSampleAnnotationNotSilenceOrSynthesisBoundary()
    {
        var wav = Wave();
        var known = AudioFixture.Parse(wav, Metadata(wav));
        Assert.Equal(16480, known.Pcm.Length / 2);
        Assert.Equal(30, known.SpeechEndSample * 1000d / 16000);
        var unknown = AudioFixture.Parse(wav, Metadata(wav) with { SpeechEndSample = null });
        Assert.Null(unknown.SpeechEndSample);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(20)]
    [InlineData(22)]
    [InlineData(24)]
    [InlineData(28)]
    [InlineData(32)]
    [InlineData(34)]
    [InlineData(40)]
    public void WaveParserRejectsInvalidHeaders(int index)
    {
        var wav = Wave();
        wav[index] ^= 1;
        Assert.Throws<InvalidDataException>(() => AudioFixture.Parse(wav, Metadata(wav)));
    }

    [Fact]
    public void WaveRequiresApprovedMatchingHashAndValidGroundTruth()
    {
        var wav = Wave();
        foreach (var metadata in new[]
        {
            Metadata(wav) with { Synthetic = false }, Metadata(wav) with { ApprovedForLiveUse = false },
            Metadata(wav) with { Sha256 = new string('0', 64) }, Metadata(wav) with { Language = "fr-FR" },
            Metadata(wav) with { SpeechEndSample = -1 }, Metadata(wav) with { SpeechEndSample = 100000 }
        })
            Assert.Throws<InvalidDataException>(() => AudioFixture.Parse(wav, metadata));
    }

    [Fact]
    public void WaveRejectsTruncationDuplicateChunksAndLackOfTrailingSilence()
    {
        var truncated = Wave()[..^1];
        Assert.Throws<InvalidDataException>(() => AudioFixture.Parse(truncated, Metadata(truncated)));
        var noSilence = Wave();
        noSilence[^1] = 1;
        Assert.Throws<InvalidDataException>(() => AudioFixture.Parse(noSilence, Metadata(noSilence)));
        var duplicate = Wave().Concat("data"u8.ToArray()).Concat(new byte[] { 2, 0, 0, 0, 1, 1 }).ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(duplicate.AsSpan(4), duplicate.Length - 8);
        Assert.Throws<InvalidDataException>(() => AudioFixture.Parse(duplicate, Metadata(duplicate)));
    }

    [Fact]
    public async Task FramesArePacedAtSampleEndAndGroundTruthIsRoundedToDeliveryFrame()
    {
        var clock = new Clock();
        var writes = new List<(double At, int Bytes)>();
        double? speechEnd = null;
        var fixture = new AudioFixture(new byte[1600], 480);
        await FramePacer.PlayAsync(fixture, bytes => writes.Add((clock.ElapsedMs, bytes.Length)), clock,
            _ => { }, end => speechEnd = end, _ => { }, CancellationToken.None);
        Assert.Equal(new[] { (20d, 640), (40d, 640), (50d, 320) }, writes);
        Assert.Equal(40, speechEnd);
    }

    [Fact]
    public async Task LateSchedulerDoesNotBurstAudioToCatchUp()
    {
        var clock = new Clock { DelayExtraMs = 50 };
        var writes = new List<double>();
        var lateness = 0d;
        await FramePacer.PlayAsync(new(new byte[1920], null), _ => writes.Add(clock.ElapsedMs), clock,
            _ => { }, _ => Assert.Fail("Unknown speech end must stay unknown."), late => lateness = late, CancellationToken.None);
        Assert.Equal(new[] { 70d, 140d, 210d }, writes);
        Assert.Equal(150, lateness);
    }

    [Fact]
    public async Task OfflinePipelineReportsTestDoubleNotAzureAndNoContent()
    {
        var clock = new Clock();
        var provider = new Provider(clock);
        var fixture = new AudioFixture(new byte[1280], 160);
        var result = await new ProbeRunner(provider, clock).RunAsync(fixture, TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal("SUCCESS", result.Status);
        Assert.Equal("TestDouble", result.Provider);
        Assert.Equal(1, result.FinalEvents);
        Assert.Equal(1, result.PartialEvents);
        Assert.Equal(1, result.DeltaEvents);
        Assert.Equal(20, result.Timings!.InputSpeechEndMs);
        Assert.Equal(40, result.Timings.FinalSttMs);
        Assert.Equal(20, result.Timings.SpeechEndToFinalSttMs);
        Assert.Equal(100, result.Timings.FinalSttToFirstDeltaMs);
        Assert.Equal(150, result.Timings.FinalSttToCompletedMs);
        Assert.True(provider.Disposed);
        Assert.True(provider.AnswerCalled);
        var json = result.ToJson();
        Assert.DoesNotContain("private-transcript", json);
        Assert.DoesNotContain("private-model-content", json);
        Assert.DoesNotContain("secret-token", json);
        Assert.True(json.Length < 4096);
    }

    [Fact]
    public async Task UnknownGroundTruthNeverBecomesFinalSttLatency()
    {
        var clock = new Clock();
        var result = await new ProbeRunner(new Provider(clock), clock)
            .RunAsync(new(new byte[1280], null), TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Null(result.Timings!.InputSpeechEndMs);
        Assert.Null(result.Timings.SpeechEndToFinalSttMs);
        Assert.NotNull(result.Timings.FinalSttToFirstDeltaMs);
    }

    [Theory]
    [InlineData("startup", "provider_error")]
    [InlineData("speech", "speech_error")]
    [InlineData("answer", "provider_error")]
    [InlineData("empty", "empty_response")]
    [InlineData("cleanup", "cleanup_failed")]
    public async Task FailuresAreExplicitAndRedacted(string stage, string reason)
    {
        var clock = new Clock();
        var result = await new ProbeRunner(new Provider(clock, stage), clock)
            .RunAsync(new(new byte[1280], null), TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal("FAILED", result.Status);
        Assert.Equal(reason, result.Reason);
        Assert.DoesNotContain("secret-token", result.ToJson());
        Assert.NotEqual(0, result.ExitCode);
    }

    [Theory]
    [InlineData("startup_wait")]
    [InlineData("answer_wait")]
    [InlineData("no_final")]
    public async Task DeadlineStopsProviderStages(string stage)
    {
        var clock = new Clock();
        var result = await new ProbeRunner(new Provider(clock, stage), clock)
            .RunAsync(new(new byte[1280], null), TimeSpan.FromMilliseconds(50), CancellationToken.None);
        Assert.Equal("FAILED", result.Status);
        Assert.Equal("deadline_exceeded", result.Reason);
    }

    [Fact]
    public async Task ExternalCancellationIsNotReportedAsSuccessOrAuthBlock()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var result = await new ProbeRunner(new Provider(new Clock(), "startup_wait"))
            .RunAsync(new(new byte[1280], null), TimeSpan.FromSeconds(5), cancellation.Token);
        Assert.Equal("CANCELLED", result.Status);
        Assert.Equal(3, result.ExitCode);
    }

    [Fact]
    public async Task OptInAndHelpDoNotConstructProviderOrAuthenticate()
    {
        using var output = new StringWriter();
        Assert.Equal(2, await ProbeCommand.RunAsync([], output, CancellationToken.None));
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal("BLOCKED", json.RootElement.GetProperty("status").GetString());
        Assert.Equal("live_opt_in_required", json.RootElement.GetProperty("reason").GetString());
        Assert.Equal(0, json.RootElement.GetProperty("finalEvents").GetInt32());
        output.GetStringBuilder().Clear();
        Assert.Equal(0, await ProbeCommand.RunAsync(["--help"], output, CancellationToken.None));
        Assert.Contains("--live", output.ToString());
    }

    [Theory]
    [InlineData("--live", "--timeout-seconds", "0")]
    [InlineData("--live", "--timeout-seconds", "181")]
    [InlineData("--live", "--audio", "secret-token", "--audio", "second")]
    [InlineData("--live", "--unexpected", "secret-token")]
    public async Task InvalidArgumentsDoNotEchoInput(params string[] arguments)
    {
        using var output = new StringWriter();
        Assert.Equal(2, await ProbeCommand.RunAsync(arguments, output, CancellationToken.None));
        Assert.DoesNotContain("secret-token", output.ToString());
    }

    [Fact]
    public async Task MissingConfigAndFakeModeBlockBeforeCredentials()
    {
        using var output = new StringWriter();
        var missing = new ConfigurationBuilder().Build();
        Assert.Equal(2, await ProbeCommand.RunCoreAsync(["--live"], output, CancellationToken.None,
            () => missing, () => throw new InvalidOperationException("Must not request credentials.")));
        Assert.Contains("configuration_unavailable", output.ToString());
        output.GetStringBuilder().Clear();
        Assert.Equal(2, await ProbeCommand.RunCoreAsync(["--live"], output, CancellationToken.None,
            () => Configuration("Fake"), () => throw new InvalidOperationException("Must not request credentials.")));
        Assert.Contains("configuration_unavailable", output.ToString());
    }

    [Fact]
    public async Task MissingCredentialIsBlockedNotProviderFailureAndRedactsException()
    {
        var audioPath = Path.GetTempFileName();
        var metadataPath = Path.GetTempFileName();
        try
        {
            var wav = Wave();
            await File.WriteAllBytesAsync(audioPath, wav);
            await File.WriteAllTextAsync(metadataPath, JsonSerializer.Serialize(Metadata(wav), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            using var output = new StringWriter();
            var credential = new MissingCredential();
            Assert.Equal(2, await ProbeCommand.RunCoreAsync(["--live", "--audio", audioPath, "--metadata", metadataPath],
                output, CancellationToken.None, () => Configuration("Azure"), () => credential));
            Assert.Equal(1, credential.Calls);
            Assert.Contains("authentication_unavailable", output.ToString());
            Assert.DoesNotContain("secret-token", output.ToString());
            Assert.DoesNotContain(audioPath, output.ToString());
            using var json = JsonDocument.Parse(output.ToString());
            Assert.Equal(0, json.RootElement.GetProperty("finalEvents").GetInt32());
            Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("timings").ValueKind);
        }
        finally
        {
            File.Delete(audioPath);
            File.Delete(metadataPath);
        }
    }

    private static IConfiguration Configuration(string mode) => new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string, string?>
        {
            ["Provider:Mode"] = mode,
            ["Azure:SpeechRegion"] = "eastus",
            ["Azure:SpeechResourceId"] = "/resource",
            ["Azure:OpenAIEndpoint"] = "https://example.openai.azure.com",
            ["Azure:ChatDeployment"] = "chat"
        }).Build();

    private sealed class MissingCredential : TokenCredential
    {
        public int Calls { get; private set; }
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Calls++;
            throw new Azure.Identity.CredentialUnavailableException("secret-token");
        }
    }

    private static byte[] Wave()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, true);
        const int length = 32960;
        writer.Write("RIFF"u8); writer.Write(36 + length); writer.Write("WAVE"u8);
        writer.Write("fmt "u8); writer.Write(16); writer.Write((short)1); writer.Write((short)1);
        writer.Write(16000); writer.Write(32000); writer.Write((short)2); writer.Write((short)16);
        writer.Write("data"u8); writer.Write(length);
        var pcm = new byte[length];
        pcm[0] = 1;
        writer.Write(pcm);
        return stream.ToArray();
    }

    private static FixtureMetadata Metadata(byte[] bytes) =>
        new(1, true, true, "en-US", Convert.ToHexString(SHA256.HashData(bytes)), 480);

    private sealed class Clock : IProbeClock
    {
        public double ElapsedMs { get; private set; }
        public double DelayExtraMs { get; init; }
        public void Advance(double ms) => ElapsedMs += ms;
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            ElapsedMs += delay.TotalMilliseconds + DelayExtraMs;
            return Task.CompletedTask;
        }
    }

    private sealed class Provider(Clock clock, string stage = "") : IMeetingProvider
    {
        public bool Disposed { get; private set; }
        public bool AnswerCalled { get; private set; }
        public async Task<ISpeechStream> StartSpeechAsync(Action<Transcript> transcript, Action<ProviderException> error, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if (stage == "startup") throw new InvalidOperationException("secret-token");
            if (stage == "startup_wait") await Task.Delay(Timeout.Infinite, cancellation);
            var writes = 0;
            return new Stream(() =>
            {
                if (stage == "speech") { error(new("secret-token", "secret-token")); return; }
                if (++writes != 2 || stage == "no_final") return;
                transcript(new("turn", 1, "private-transcript", false));
                transcript(new("turn", 2, "private-transcript", true));
            }, () =>
            {
                Disposed = true;
                if (stage == "cleanup") throw new InvalidOperationException("secret-token");
            });
        }
        public Task<Grounding> RetrieveAsync(string query, string objectId, CancellationToken cancellation) =>
            throw new InvalidOperationException("Probe must not retrieve corporate documents.");
        public async IAsyncEnumerable<string> AnswerAsync(IReadOnlyList<ConversationTurn> conversation, Grounding grounding,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellation)
        {
            AnswerCalled = true;
            if (stage == "answer") throw new InvalidOperationException("secret-token");
            if (stage == "answer_wait") await Task.Delay(Timeout.Infinite, cancellation);
            if (stage == "empty") yield break;
            clock.Advance(100);
            yield return "private-model-content";
            clock.Advance(50);
        }
        private sealed class Stream(Action write, Action dispose) : ISpeechStream
        {
            public void Write(byte[] audio) => write();
            public ValueTask DisposeAsync() { dispose(); return ValueTask.CompletedTask; }
        }
    }
}
