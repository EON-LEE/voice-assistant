using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Text.RegularExpressions;
using Azure.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using VoiceAssistant.Api;
using Xunit;

namespace VoiceAssistant.Api.Tests;

/// <summary>Captures every measurement of the app's meter, of any numeric type, with its tags.</summary>
internal sealed class MetricCapture : IDisposable
{
    private readonly MeterListener listener = new();
    public ConcurrentQueue<(string Name, double Value, KeyValuePair<string, object?>[] Tags)> Measurements { get; } = new();

    public MetricCapture()
    {
        listener.InstrumentPublished = (instrument, owner) =>
        {
            if (instrument.Meter.Name == MeetingMetrics.MeterName) owner.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Add(instrument, value, tags));
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Add(instrument, value, tags));
        listener.SetMeasurementEventCallback<int>((instrument, value, tags, _) => Add(instrument, value, tags));
        listener.Start();
    }

    private void Add(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags) =>
        Measurements.Enqueue((instrument.Name, value, tags.ToArray()));

    public IEnumerable<(string Name, double Value, KeyValuePair<string, object?>[] Tags)> Named(string name) =>
        Measurements.Where(measurement => measurement.Name == name);

    /// <summary>The current value of the active-session gauge.</summary>
    public double ActiveSessions()
    {
        listener.RecordObservableInstruments();
        return Named("voiceassistant.sessions.active").Last().Value;
    }

    public void Dispose() => listener.Dispose();
}

public sealed class TelemetryTests
{
    private static IConfiguration Configuration(params (string Key, string? Value)[] values) => new ConfigurationBuilder()
        .AddInMemoryCollection(values.Select(value => new KeyValuePair<string, string?>(value.Key, value.Value))).Build();

    [Fact]
    public void ExporterIsRegisteredOnlyWhenAConnectionStringIsConfigured()
    {
        foreach (var empty in new[] { null, "", "   " })
        {
            var services = new ServiceCollection();
            Assert.False(ApiTelemetry.Configure(services, Configuration((ApiTelemetry.ConnectionStringKey, empty))));
            Assert.Empty(services);
        }
        var configured = new ServiceCollection();
        // Registration only: the provider and its exporter are never built here, so nothing is sent anywhere.
        Assert.True(ApiTelemetry.Configure(configured, Configuration((ApiTelemetry.ConnectionStringKey,
            "InstrumentationKey=00000000-0000-0000-0000-000000000000;IngestionEndpoint=https://example.invalid/"))));
        Assert.Contains(configured, descriptor => descriptor.ServiceType == typeof(MeterProvider));
    }

    [Fact]
    public void ServiceNameAndCredentialFollowTheEnvironment()
    {
        Assert.Equal("voice-web", ApiTelemetry.ServiceName(Configuration()));
        Assert.Equal("voice-web", ApiTelemetry.ServiceName(Configuration(("OTEL_SERVICE_NAME", "  "))));
        Assert.Equal("meeting-api", ApiTelemetry.ServiceName(Configuration(("OTEL_SERVICE_NAME", "meeting-api"))));
        Assert.IsType<ManagedIdentityCredential>(ApiTelemetry.Credential(Configuration(
            ("AZURE_CLIENT_ID", "11111111-1111-1111-1111-111111111111"), ("IDENTITY_ENDPOINT", "http://localhost:42356/msi/token"))));
        Assert.IsType<DefaultAzureCredential>(ApiTelemetry.Credential(Configuration(("AZURE_CLIENT_ID", "11111111-1111-1111-1111-111111111111"))));
        Assert.IsType<DefaultAzureCredential>(ApiTelemetry.Credential(Configuration()));
    }

    [Fact]
    public async Task RetrievalFailuresAreCountedByClassKindAndStatusClass()
    {
        using var capture = new MetricCapture();
        using var transport = new FaultTransport().Search(HttpStatusCode.ServiceUnavailable);
        using var http = new HttpClient(transport);
        await Assert.ThrowsAsync<ProviderException>(() =>
            FaultTransport.Provider(http).RetrieveAsync("What is the delivery plan?", "e59048e0-9a10-433e-8e0e-beb279c0c234", CancellationToken.None));
        var failure = Assert.Single(capture.Named("voiceassistant.retrieval.failures"));
        Assert.Equal(1, failure.Value);
        Assert.Equal(new KeyValuePair<string, object?>[]
        {
            new("class", "transient"), new("kind", "server_error"), new("status_class", "5xx"), new("retried", "true")
        }, failure.Tags);
    }

    [Theory]
    [InlineData("Interesting", "other")]
    [InlineData("grounding_unavailable", "grounding_unavailable")]
    [InlineData("Could the zebra rollout start?", "other")]
    [InlineData("", "other")]
    public void TagValuesOutsideTheFixedVocabularyBecomeOther(string value, string expected) =>
        Assert.Equal(expected, MeetingMetrics.Bounded(value));
}

public sealed partial class ApiTests
{
    private const string MetricTranscript = "Could the zebra quartz rollout start on Friday?";
    private static readonly Regex BoundedTag = new("^[a-z0-9_]{1,40}$", RegexOptions.CultureInvariant);

    [Fact]
    public async Task ReplyRecordsEveryStageWithContentFreeBoundedTags()
    {
        using var capture = new MetricCapture();
        var clock = new ManualClock();
        var provider = new ControlledProvider
        {
            AutomaticRetrieval = true, Alternative = "Let's check the next step.", Translate = Prefixed
        };
        await using var host = await Host.StartAsync(provider: provider, clock: clock);
        using var socket = await host.ConnectAsync();
        await Send(socket, WithOptions(new { responseMode = "grounded", replySettleMs = 300, replyKorean = true }));
        await Receive(socket);
        await EmitFinalAndSettleAsync(socket, provider, clock, "q", MetricTranscript);
        await ReceiveUntilAsync(socket, found => Completed(found) && found.Any(message => IsKorean(message, 0)) &&
            found.Any(message => IsKorean(message, 1)));
        await Send(socket, """{"type":"session.stop"}""");
        await ReceiveUntilCloseAsync(socket);
        await WaitUntilAsync(() => capture.Named("voiceassistant.session.ended").Any(), TimeSpan.FromSeconds(3));

        foreach (var name in new[]
        {
            "voiceassistant.reply.settle_wait", "voiceassistant.reply.retrieval_wait", "voiceassistant.reply.first_token",
            "voiceassistant.stt_final_to_first_delta", "voiceassistant.reply.final_to_primary_done", "voiceassistant.stt_final_to_completed",
            "voiceassistant.reply.classifier_wait", "voiceassistant.reply.korean_ready", "voiceassistant.reply.prefetch",
            "voiceassistant.reply.grounding", "voiceassistant.retrieval_duration", "voiceassistant.session.ended"
        })
            Assert.Contains(capture.Measurements, measurement => measurement.Name == name);
        // The settle window passed on the manual clock, so the reply started exactly 300 ms after its final.
        Assert.Contains(capture.Named("voiceassistant.reply.settle_wait"), measurement => measurement.Value == 300);
        Assert.Contains(capture.Named("voiceassistant.reply.grounding"), measurement =>
            measurement.Tags.Contains(new("grounding", "no_matches")) && measurement.Tags.Contains(new("route", "knowledge")) &&
            measurement.Tags.Contains(new("response_mode", "grounded")));
        Assert.Equal(2, capture.Named("voiceassistant.reply.korean_ready").Count(measurement => measurement.Tags.Contains(new("outcome", "translated"))));
        Assert.Contains(capture.Named("voiceassistant.session.ended"), measurement => measurement.Tags.Contains(new("reason", "stopped")));

        // The distinctive words of the transcript never appear in any tag of any measurement.
        string[] words = ["zebra", "quartz", "rollout", "friday"];
        foreach (var (name, _, tags) in capture.Measurements)
            foreach (var (key, value) in tags)
            {
                var text = Assert.IsType<string>(value);
                Assert.True(key == "provider" ? text is "Azure" or "Fake" or "TestDouble" : BoundedTag.IsMatch(text), $"{name} {key}={text}");
                Assert.DoesNotContain(words, word => text.Contains(word, StringComparison.OrdinalIgnoreCase));
            }
    }

    [Fact]
    public async Task SessionEndsErrorsAndActiveSessionsAreCountedByReasonAndCode()
    {
        using var capture = new MetricCapture();
        await using var host = await Host.StartAsync();
        var before = capture.ActiveSessions();
        using (var socket = await host.ConnectAsync())
        {
            await Send(socket, Start);
            Assert.Equal("session.ready", TypeOf(await Receive(socket)));
            Assert.Equal(before + 1, capture.ActiveSessions());
            await Send(socket, """{"type":"session.stop"}""");
            await ReceiveUntilCloseAsync(socket);
        }
        using (var socket = await host.ConnectAsync())
        {
            await Send(socket, """{"type":"session.start","protocolVersion":2}""");
            Assert.Equal("invalid_start", (await Receive(socket)).GetProperty("code").GetString());
        }
        await WaitUntilAsync(() => capture.Named("voiceassistant.session.ended").Count() >= 2 && capture.ActiveSessions() == before,
            TimeSpan.FromSeconds(3));
        Assert.Equal(before, capture.ActiveSessions());
        Assert.Contains(capture.Named("voiceassistant.session.ended"), measurement => measurement.Tags.Contains(new("reason", "stopped")));
        Assert.Contains(capture.Named("voiceassistant.session.ended"), measurement => measurement.Tags.Contains(new("reason", "invalid_start")));
        Assert.Contains(capture.Named("voiceassistant.session.errors"), measurement => measurement.Tags.Contains(new("code", "invalid_start")));
    }
}
