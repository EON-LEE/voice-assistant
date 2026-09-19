using System.Diagnostics.Metrics;
using Azure.Core;
using Microsoft.CognitiveServices.Speech;
using Microsoft.Extensions.Configuration;
using VoiceAssistant.Api;
using Xunit;

namespace VoiceAssistant.Api.Tests;

public sealed class ProviderInstrumentationTests
{
    [Fact]
    public void ChatOptionsOmitTemperatureForReasoningModelCompatibility()
    {
        var options = AzureMeetingProvider.CreateChatOptions(new ServiceSettings { ChatMaxOutputTokens = 3072 });
        Assert.Null(options.Temperature);
        Assert.Equal(3072, options.MaxOutputTokenCount);
        Assert.Equal(2048, ServiceSettings.ReadChatTokenLimit(new ConfigurationBuilder().Build()));
    }

    [Theory]
    [InlineData("not-a-number")]
    [InlineData("0")]
    [InlineData("4097")]
    public void InvalidChatTokenBudgetFailsClosed(string value)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Azure:ChatMaxOutputTokens"] = value }).Build();
        Assert.Throws<InvalidOperationException>(() => ServiceSettings.ReadChatTokenLimit(config));
    }

    [Fact]
    public void PortalHttpsRootIsPreservedByPinnedSpeechSdk()
    {
        var config = AzureMeetingProvider.CreateSpeechConfig(new ServiceSettings
        {
            SpeechEndpoint = "https://example.cognitiveservices.azure.com/",
            SpeechRegion = "eastus"
        }, "test-only-token");
        Assert.Equal("https://example.cognitiveservices.azure.com/",
            config.GetProperty(PropertyId.SpeechServiceConnection_Endpoint));
        Assert.Equal("en-US", config.SpeechRecognitionLanguage);
        Assert.Equal("700", config.GetProperty(PropertyId.Speech_SegmentationSilenceTimeoutMs));
        Assert.Equal("test-only-token", config.AuthorizationToken);
    }

    [Fact]
    public async Task AzureStartupRequiresCredentialBeforeNativeSpeechConnection()
    {
        var credential = new Credential { Failure = new InvalidOperationException("sensitive-credential-error") };
        var provider = new AzureMeetingProvider(new ServiceSettings
        {
            SpeechRegion = "eastus",
            SpeechResourceId = "/resource",
            OpenAIEndpoint = "https://example.openai.azure.com",
            ChatDeployment = "test"
        }, credential);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.StartSpeechAsync(_ => Assert.Fail(), _ => Assert.Fail(), CancellationToken.None));
        Assert.Equal(1, credential.Calls);
        Assert.Equal(SpeechAuthorization.Scope, credential.LastScope);
    }

    [Fact]
    public async Task CancelledStartupDoesNotAcquireTokenOrCreateRecognizer()
    {
        var credential = new Credential();
        var provider = new AzureMeetingProvider(new ServiceSettings
        {
            OpenAIEndpoint = "https://example.openai.azure.com",
            ChatDeployment = "test"
        }, credential);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.StartSpeechAsync(_ => Assert.Fail(), _ => Assert.Fail(), cancellation.Token));
        Assert.Equal(0, credential.Calls);
    }

    [Fact]
    public async Task RenewalAppliesNewTokenAndStopsOnCancellation()
    {
        var credential = new Credential();
        using var cancellation = new CancellationTokenSource();
        var applied = new List<string>();
        var delayCalls = 0;
        await SpeechAuthorization.RefreshAsync(credential, "/resource", token =>
        {
            applied.Add(token);
            cancellation.Cancel();
        }, _ => Assert.Fail(), cancellation.Token, (duration, token) =>
        {
            Assert.Equal(TimeSpan.FromMinutes(5), duration);
            delayCalls++;
            token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        });
        Assert.Equal(["aad#/resource#test-token-1"], applied);
        Assert.Equal(1, credential.Calls);
        Assert.Equal(2, delayCalls);
    }

    [Fact]
    public async Task RenewalFailureIsSafeAndExplicit()
    {
        var errors = new List<ProviderException>();
        await SpeechAuthorization.RefreshAsync(new Credential { Failure = new InvalidOperationException("secret-token") },
            "/resource", _ => Assert.Fail(), errors.Add, CancellationToken.None, (_, _) => Task.CompletedTask);
        var error = Assert.Single(errors);
        Assert.Equal("speech_authentication_failed", error.Code);
        Assert.DoesNotContain("secret-token", error.Message);
    }

    [Fact]
    public async Task CancellationDuringRenewalDoesNotApplyObsoleteToken()
    {
        using var cancellation = new CancellationTokenSource();
        var credential = new Credential { BeforeReturn = cancellation.Cancel };
        await SpeechAuthorization.RefreshAsync(credential, "/resource", _ => Assert.Fail(),
            _ => Assert.Fail(), cancellation.Token, (_, _) => Task.CompletedTask);
        Assert.Equal(1, credential.Calls);
    }

    [Fact]
    public void ResponseMetricsUseMonotonicFinalSttOriginAndBoundedTagsOnly()
    {
        var values = new List<(string Name, double Value, KeyValuePair<string, object?>[] Tags)>();
        using var listener = Listen(values);
        var clock = new Clock();
        var response = new MeetingMetrics.ResponseMeasurement(clock.GetTimestamp(), "secret-identity", manual: true, clock);
        clock.Ticks += 100;
        response.FirstDeltaSent();
        clock.Ticks += 50;
        response.FirstDeltaSent();
        response.CompletedSent();
        response.CompletedSent();
        Assert.Equal(2, values.Count);
        Assert.Equal(100, values[0].Value);
        Assert.Equal(150, values[1].Value);
        Assert.Equal("voiceassistant.stt_final_to_first_delta", values[0].Name);
        Assert.Equal("voiceassistant.stt_final_to_completed", values[1].Name);
        foreach (var value in values)
        {
            Assert.Equal(2, value.Tags.Length);
            Assert.Contains(new("provider", "TestDouble"), value.Tags);
            Assert.Contains(new("trigger", "manual"), value.Tags);
            Assert.DoesNotContain(value.Tags, tag => Equals(tag.Value, "secret-identity"));
        }
    }

    [Fact]
    public async Task RetrievalMeasuresSuccessFailureAndCancellationWithoutQueryOrIdentity()
    {
        var values = new List<(string Name, double Value, KeyValuePair<string, object?>[] Tags)>();
        using var listener = Listen(values);
        await MeetingMetrics.MeasureRetrievalAsync(new FakeMeetingProvider(), "private-query", "private-identity", CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => MeetingMetrics.MeasureRetrievalAsync(
            new RetrievalProvider(false), "private-query", "private-identity", CancellationToken.None));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MeetingMetrics.MeasureRetrievalAsync(
            new RetrievalProvider(true), "private-query", "private-identity", CancellationToken.None));
        var measurements = values.Where(value => value.Name == "voiceassistant.retrieval_duration").ToArray();
        Assert.Equal(3, measurements.Length);
        Assert.Contains(new("outcome", "disabled"), measurements[0].Tags);
        Assert.Contains(new("outcome", "failed"), measurements[1].Tags);
        Assert.Contains(new("outcome", "cancelled"), measurements[2].Tags);
        foreach (var measurement in measurements)
        {
            Assert.True(measurement.Value >= 0);
            Assert.Equal(2, measurement.Tags.Length);
            Assert.DoesNotContain(measurement.Tags, tag => tag.Key is "query" or "objectId" or "turnId" or "responseId");
        }
    }

    private static MeterListener Listen(List<(string, double, KeyValuePair<string, object?>[])> values)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, owner) =>
            {
                if (instrument.Meter.Name == MeetingMetrics.MeterName) owner.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => values.Add((instrument.Name, value, tags.ToArray())));
        listener.Start();
        return listener;
    }

    private sealed class Clock : TimeProvider
    {
        public long Ticks { get; set; }
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.MaxValue;
    }

    private sealed class Credential : TokenCredential
    {
        public int Calls { get; private set; }
        public string? LastScope { get; private set; }
        public Exception? Failure { get; init; }
        public Action? BeforeReturn { get; init; }
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Calls++;
            LastScope = Assert.Single(requestContext.Scopes);
            if (Failure is not null) throw Failure;
            BeforeReturn?.Invoke();
            return ValueTask.FromResult(new AccessToken($"test-token-{Calls}", DateTimeOffset.UtcNow.AddHours(1)));
        }
    }

    private sealed class RetrievalProvider(bool cancel) : IMeetingProvider
    {
        public Task<ISpeechStream> StartSpeechAsync(Action<Transcript> transcript, Action<ProviderException> error, CancellationToken cancellation) =>
            throw new NotSupportedException();
        public Task<Grounding> RetrieveAsync(string query, string objectId, CancellationToken cancellation) =>
            cancel ? throw new OperationCanceledException() : throw new InvalidOperationException("private-error");
        public IAsyncEnumerable<string> AnswerAsync(IReadOnlyList<ConversationTurn> conversation, Grounding grounding, CancellationToken cancellation) =>
            throw new NotSupportedException();
    }
}
