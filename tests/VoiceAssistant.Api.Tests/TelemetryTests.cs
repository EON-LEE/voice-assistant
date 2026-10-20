using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Text.RegularExpressions;
using Azure.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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

/// <summary>Restores the process-wide Statsbeat variable that enabling export may set.</summary>
internal sealed class StatsbeatVariable : IDisposable
{
    private readonly string? previous = Environment.GetEnvironmentVariable(ApiTelemetry.StatsbeatDisabledKey);

    public static StatsbeatVariable Preserve(string? value = null)
    {
        var preserved = new StatsbeatVariable();
        Environment.SetEnvironmentVariable(ApiTelemetry.StatsbeatDisabledKey, value);
        return preserved;
    }

    public void Dispose() => Environment.SetEnvironmentVariable(ApiTelemetry.StatsbeatDisabledKey, previous);
}

public sealed class TelemetryTests
{
    private const string ValidLooking = "InstrumentationKey=00000000-0000-0000-0000-000000000000;IngestionEndpoint=https://example.invalid/";

    private static IConfiguration Configuration(params (string Key, string? Value)[] values) => new ConfigurationBuilder()
        .AddInMemoryCollection(values.Select(value => new KeyValuePair<string, string?>(value.Key, value.Value))).Build();

    [Fact]
    public void ExporterIsRegisteredOnlyWhenAConnectionStringIsConfigured()
    {
        using var statsbeat = StatsbeatVariable.Preserve();
        foreach (var empty in new[] { null, "", "   " })
        {
            var services = new ServiceCollection();
            Assert.False(ApiTelemetry.Configure(services, Configuration((ApiTelemetry.ConnectionStringKey, empty))));
            Assert.Empty(services);
        }
        var configured = new ServiceCollection();
        // Registration only: the provider and its exporter are never built here, so nothing is sent anywhere.
        Assert.True(ApiTelemetry.Configure(configured, Configuration((ApiTelemetry.ConnectionStringKey, ValidLooking))));
        Assert.Contains(configured, descriptor => descriptor.ServiceType == typeof(IHostedService));
        Assert.DoesNotContain(configured, descriptor => descriptor.ServiceType == typeof(MeterProvider));
    }

    [Fact]
    public void EnablingExportSwitchesStatsbeatOffUnlessTheOperatorSetIt()
    {
        using var statsbeat = StatsbeatVariable.Preserve();

        ApiTelemetry.Configure(new ServiceCollection(), Configuration((ApiTelemetry.ConnectionStringKey, "")));
        Assert.Null(Environment.GetEnvironmentVariable(ApiTelemetry.StatsbeatDisabledKey));

        ApiTelemetry.Configure(new ServiceCollection(), Configuration((ApiTelemetry.ConnectionStringKey, ValidLooking)));
        Assert.Equal("true", Environment.GetEnvironmentVariable(ApiTelemetry.StatsbeatDisabledKey));

        Environment.SetEnvironmentVariable(ApiTelemetry.StatsbeatDisabledKey, "false");
        ApiTelemetry.Configure(new ServiceCollection(), Configuration((ApiTelemetry.ConnectionStringKey, ValidLooking)));
        Assert.Equal("false", Environment.GetEnvironmentVariable(ApiTelemetry.StatsbeatDisabledKey));
    }

    [Fact]
    public async Task AMalformedConnectionStringTurnsExportOffWithAContentFreeWarning()
    {
        using var statsbeat = StatsbeatVariable.Preserve("true");
        var logger = new ListLogger();
        using var export = new ApiTelemetry.TelemetryExport(() => ApiTelemetry.CreateProvider(
            "not-a-connection-string-private-value", ApiTelemetry.DefaultServiceName, new DefaultAzureCredential()), logger);

        await export.StartAsync(CancellationToken.None);

        Assert.False(export.Exporting);
        var warning = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.StartsWith("Metric export is off: the Azure Monitor exporter could not start", warning.Message);
        Assert.DoesNotContain("private-value", warning.Message);
        await export.StopAsync(CancellationToken.None);
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

    private sealed class ListLogger : ILogger<ApiTelemetry.TelemetryExport>
    {
        public ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Enqueue((logLevel, formatter(state, exception) + (exception is null ? "" : " " + exception)));
    }
}

public sealed partial class ApiTests
{
    private const string MetricTranscript = "Could the zebra quartz rollout start on Friday?";
    private static readonly Regex BoundedTag = new("^[a-z0-9_]{1,40}$", RegexOptions.CultureInvariant);

    [Fact]
    public async Task AMalformedTelemetryConnectionStringDoesNotStopTheApp()
    {
        using var statsbeat = StatsbeatVariable.Preserve();
        await using var host = await Host.StartAsync(configuration: new() { [ApiTelemetry.ConnectionStringKey] = "not-a-connection-string" });
        using var client = host.Client;
        using var live = await client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        using var ready = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
    }

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
