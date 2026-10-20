using System.ClientModel.Primitives;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Azure;
using Azure.AI.OpenAI;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Identity;
using Azure.Search.Documents;
using Microsoft.Extensions.Logging;
using VoiceAssistant.Api;
using Xunit;

namespace VoiceAssistant.Api.Tests;

/// <summary>A warm-up that succeeds at once, so Azure-mode test hosts are ready without reaching Azure.</summary>
internal sealed class InstantWarmup : IDependencyWarmup
{
    public Task WarmAsync(CancellationToken cancellation) => Task.CompletedTask;
}

/// <summary>
/// A warm-up that waits for its gate (when given), then fails with the cause until <see cref="Succeed"/> is set. Its optional
/// (Search) part likewise waits for <see cref="OptionalGate"/> and fails with <see cref="OptionalFailure"/> until
/// <see cref="OptionalSucceed"/> is set.
/// </summary>
internal sealed class ScriptedWarmup(Exception? failure = null) : IDependencyWarmup
{
    private readonly Exception cause = failure ?? new InvalidOperationException("Synthetic warm-up failure.");
    private int attempts;
    private int optionalAttempts;
    public int Attempts => Volatile.Read(ref attempts);
    public int OptionalAttempts => Volatile.Read(ref optionalAttempts);
    public volatile bool Succeed = failure is null;
    public volatile bool OptionalSucceed;
    public TaskCompletionSource? Gate { get; init; }
    public TaskCompletionSource? OptionalGate { get; init; }
    public Exception? OptionalFailure { get; init; }

    public async Task WarmAsync(CancellationToken cancellation)
    {
        Interlocked.Increment(ref attempts);
        if (Gate is not null) await Gate.Task.WaitAsync(cancellation);
        if (!Succeed) throw cause;
    }

    public async Task WarmOptionalAsync(CancellationToken cancellation)
    {
        Interlocked.Increment(ref optionalAttempts);
        if (OptionalGate is not null) await OptionalGate.Task.WaitAsync(cancellation);
        if (OptionalFailure is not null && !OptionalSucceed) throw OptionalFailure;
    }
}

public sealed class ReadinessTests
{
    private const string PrivateDetail = "private-cloud-body";

    [Fact]
    public async Task WarmupFailingForAMinuteReportsNotReadyOnceWithoutContentThenRecovers()
    {
        var clock = new ManualClock();
        var readiness = new Readiness();
        var logger = new ListLogger();
        var warmup = new ScriptedWarmup(new WarmupException("speech_connection",
            new HttpRequestException(PrivateDetail, null, HttpStatusCode.ServiceUnavailable)));
        using var service = new StartupWarmup(warmup, readiness, clock, logger);
        await service.StartAsync(CancellationToken.None);
        await clock.WaitForTimersAsync(1);
        Assert.Equal("warming", readiness.Status);

        // Capped exponential backoff: 1, 2, 4, 8, 15, 15, 15 s; the attempt after a minute of failures reports not-ready.
        foreach (var delay in new[] { 1, 2, 4, 8, 15, 15, 15 })
        {
            Assert.False(readiness.IsReady);
            var timers = clock.TimersCreated;
            clock.Advance(TimeSpan.FromSeconds(delay));
            // The next failed attempt schedules its retry on a new timer.
            await clock.WaitForTimersAsync(timers + 1);
        }
        Assert.Equal(8, warmup.Attempts);
        Assert.False(readiness.IsReady);
        Assert.Equal("not_ready", readiness.Status);
        var report = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Error);
        Assert.Contains("not completed after 60 s", report.Message);
        Assert.Contains("stage speech_connection, HttpRequestException, HTTP status 503", report.Message);
        Assert.Equal(7, logger.Entries.Count(entry => entry.Level == LogLevel.Warning));
        Assert.DoesNotContain(logger.Entries, entry => entry.Message.Contains(PrivateDetail, StringComparison.Ordinal));

        // The dependency comes back: the next attempt marks the replica ready, and the warm-up never runs again.
        warmup.Succeed = true;
        clock.Advance(TimeSpan.FromSeconds(15));
        await WaitAsync(() => readiness.IsReady);
        Assert.Equal("ready", readiness.Status);
        Assert.Equal(9, warmup.Attempts);
        warmup.Succeed = false;
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.True(readiness.IsReady);
        Assert.Equal(9, warmup.Attempts);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task SearchWarmupFailuresNeverDelayReadinessAndAreRetriedInTheBackground()
    {
        using var capture = new MetricCapture();
        var clock = new ManualClock();
        var readiness = new Readiness();
        var logger = new ListLogger();
        var warmup = new ScriptedWarmup
        {
            OptionalFailure = new WarmupException("search_connection", new RequestFailedException(0, PrivateDetail))
        };
        using var service = new StartupWarmup(warmup, readiness, clock, logger);

        await service.StartAsync(CancellationToken.None);

        // Ready as soon as the required part succeeded, although Search is still failing.
        await WaitAsync(() => readiness.IsReady);
        await clock.WaitForTimersAsync(1);
        Assert.Equal(1, warmup.OptionalAttempts);
        var timers = clock.TimersCreated;
        clock.Advance(StartupWarmup.FirstDelay);
        await clock.WaitForTimersAsync(timers + 1);
        Assert.Equal(2, warmup.OptionalAttempts);
        warmup.OptionalSucceed = true;
        clock.Advance(StartupWarmup.FirstDelay * 2);
        await WaitAsync(() => warmup.OptionalAttempts == 3);
        clock.Advance(TimeSpan.FromMinutes(5));

        Assert.Equal(3, warmup.OptionalAttempts);
        Assert.Equal(1, warmup.Attempts);
        Assert.True(readiness.IsReady);
        Assert.Equal(2, capture.Named("voiceassistant.warmup.failures").Count(measurement =>
            measurement.Tags.Contains(new("stage", "search_connection")) && measurement.Tags.Contains(new("required", "false"))));
        Assert.Equal(2, logger.Entries.Count(entry => entry.Level == LogLevel.Warning &&
            entry.Message.Contains("stage search_connection, RequestFailedException, HTTP status none; readiness does not wait", StringComparison.Ordinal)));
        Assert.DoesNotContain(logger.Entries, entry => entry.Level == LogLevel.Error);
        Assert.DoesNotContain(logger.Entries, entry => entry.Message.Contains(PrivateDetail, StringComparison.Ordinal));
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task RequiredWarmupNeverTouchesSearchWhileTheOptionalWarmupDoes()
    {
        var credential = new ScopeCredential();
        using var transport = new EndpointRecorder(HttpStatusCode.NotFound);
        using var http = new HttpClient(transport);
        var warmup = Warmup(credential, http, search: true);

        await warmup.WarmAsync(CancellationToken.None);

        Assert.Contains(AzureDependencyWarmup.CognitiveServicesScope, credential.Scopes);
        Assert.Contains("example.openai.azure.com", transport.Hosts);
        Assert.Contains("koreacentral.stt.speech.microsoft.com", transport.Hosts);
        Assert.DoesNotContain(AzureDependencyWarmup.SearchScope, credential.Scopes);
        Assert.DoesNotContain("example.search.windows.net", transport.Hosts);

        await warmup.WarmOptionalAsync(CancellationToken.None);

        Assert.Contains(AzureDependencyWarmup.SearchScope, credential.Scopes);
        Assert.Contains("example.search.windows.net", transport.Hosts);
        // The OpenAI and Search requests go through the authenticated client pipelines, warming their token caches too.
        Assert.All(transport.Requests.Where(request => !request.Host.Contains("speech", StringComparison.Ordinal)),
            request => Assert.True(request.Authorized, request.Host));
    }

    [Fact]
    public async Task AzureWarmupWithoutSearchSkipsTheSearchTokenAndEndpoint()
    {
        var credential = new ScopeCredential();
        using var transport = new EndpointRecorder(HttpStatusCode.Forbidden);
        using var http = new HttpClient(transport);
        var warmup = Warmup(credential, http, search: false);

        await warmup.WarmAsync(CancellationToken.None);
        await warmup.WarmOptionalAsync(CancellationToken.None);

        Assert.DoesNotContain(AzureDependencyWarmup.SearchScope, credential.Scopes);
        Assert.DoesNotContain("example.search.windows.net", transport.Hosts);
        Assert.Contains("example.openai.azure.com", transport.Hosts);
    }

    [Fact]
    public async Task OnlyRequiredStagesFailTheWarmupAndEveryFailureNamesOnlyItsStage()
    {
        // Search unreachable, or its token missing: the required warm-up still succeeds; only the optional one fails.
        using (var transport = new EndpointRecorder(HttpStatusCode.NotFound) { Unreachable = "example.search.windows.net" })
        using (var http = new HttpClient(transport))
        {
            var warmup = Warmup(new ScopeCredential(), http, search: true);
            await warmup.WarmAsync(CancellationToken.None);
            var unreachable = await Assert.ThrowsAsync<WarmupException>(() => warmup.WarmOptionalAsync(CancellationToken.None));
            Assert.Equal(("search_connection", nameof(RequestFailedException), "none"), StartupWarmup.Describe(unreachable));
        }
        using (var transport = new EndpointRecorder(HttpStatusCode.NotFound))
        using (var http = new HttpClient(transport))
        {
            var warmup = Warmup(new ScopeCredential { FailScope = AzureDependencyWarmup.SearchScope }, http, search: true);
            await warmup.WarmAsync(CancellationToken.None);
            var noSearchToken = await Assert.ThrowsAsync<WarmupException>(() => warmup.WarmOptionalAsync(CancellationToken.None));
            Assert.Equal(("search_token", nameof(CredentialUnavailableException), "none"), StartupWarmup.Describe(noSearchToken));
        }

        // Speech unreachable or no Cognitive Services token: readiness has to wait.
        using (var transport = new EndpointRecorder(HttpStatusCode.NotFound) { Unreachable = "koreacentral.stt.speech.microsoft.com" })
        using (var http = new HttpClient(transport))
        {
            var unreachable = await Assert.ThrowsAsync<WarmupException>(() =>
                Warmup(new ScopeCredential(), http, search: true).WarmAsync(CancellationToken.None));
            Assert.Equal(("speech_connection", nameof(HttpRequestException), "none"), StartupWarmup.Describe(unreachable));

            var noToken = await Assert.ThrowsAsync<WarmupException>(() =>
                Warmup(new ScopeCredential { FailScope = AzureDependencyWarmup.CognitiveServicesScope }, http, search: true)
                    .WarmAsync(CancellationToken.None));
            Assert.Equal(("speech_openai_token", nameof(CredentialUnavailableException), "none"), StartupWarmup.Describe(noToken));
            Assert.DoesNotContain(PrivateDetail, noToken.Message);
        }
    }

    [Fact]
    public void SpeechWarmupTargetsTheConfiguredEndpointOrTheRegionalSpeechHost()
    {
        Assert.Equal(new Uri("https://koreacentral.stt.speech.microsoft.com/"),
            AzureDependencyWarmup.SpeechEndpoint(new ServiceSettings { SpeechRegion = "KoreaCentral" }));
        Assert.Equal(new Uri("https://example-speech.cognitiveservices.azure.com/"), AzureDependencyWarmup.SpeechEndpoint(
            new ServiceSettings { SpeechRegion = "koreacentral", SpeechEndpoint = "https://example-speech.cognitiveservices.azure.com/" }));
    }

    private static AzureDependencyWarmup Warmup(TokenCredential credential, HttpClient http, bool search)
    {
        var settings = new ServiceSettings
        {
            OpenAIEndpoint = "https://example.openai.azure.com/", SpeechRegion = "koreacentral",
            SearchEndpoint = search ? "https://example.search.windows.net" : "", SearchIndex = search ? "meeting" : ""
        };
        var openAI = new AzureOpenAIClient(new Uri(settings.OpenAIEndpoint), credential,
            new AzureOpenAIClientOptions { Transport = new HttpClientPipelineTransport(http), RetryPolicy = new ClientRetryPolicy(0) });
        SearchClient? searchClient = null;
        if (search)
        {
            var options = new SearchClientOptions { Transport = new HttpClientTransport(http) };
            options.Retry.MaxRetries = 0;
            searchClient = new SearchClient(new Uri(settings.SearchEndpoint), settings.SearchIndex, credential, options);
        }
        return new AzureDependencyWarmup(settings, credential, openAI, searchClient, http);
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Condition was not met.");
            await Task.Delay(10);
        }
    }

    private sealed class ScopeCredential : TokenCredential
    {
        public ConcurrentQueue<string> Scopes { get; } = new();
        /// <summary>The one scope whose token cannot be acquired.</summary>
        public string? FailScope { get; init; }

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            if (FailScope is not null && requestContext.Scopes.Contains(FailScope)) throw new CredentialUnavailableException(PrivateDetail);
            foreach (var scope in requestContext.Scopes) Scopes.Enqueue(scope);
            return new("test-only", DateTimeOffset.UtcNow.AddHours(1));
        }

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new(GetToken(requestContext, cancellationToken));
    }

    /// <summary>Answers every request with one status and records its host; an unreachable host fails like a network error.</summary>
    private sealed class EndpointRecorder(HttpStatusCode status) : HttpMessageHandler
    {
        public ConcurrentQueue<(string Host, bool Authorized)> Requests { get; } = new();
        public IEnumerable<string> Hosts => Requests.Select(request => request.Host);
        public string? Unreachable { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var host = request.RequestUri!.Host;
            Requests.Enqueue((host, request.Headers.Authorization?.Scheme == "Bearer"));
            if (host == Unreachable) throw new HttpRequestException("Synthetic network failure.");
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("{}") });
        }
    }

    private sealed class ListLogger : ILogger<StartupWarmup>
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
    [Fact]
    public async Task AzureModeIsReadyWhileTheSearchWarmupIsStillPending()
    {
        // The optional (Search) warm-up never finishes here; readiness only needs the token, Azure OpenAI and Speech.
        var warmup = new ScriptedWarmup { OptionalGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var host = await Host.StartAsync(azure: true, warmup: warmup);
        using var client = host.Client;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        HttpStatusCode status;
        do
        {
            using var response = await client.GetAsync("/health/ready");
            status = response.StatusCode;
            if (status != HttpStatusCode.OK) await Task.Delay(10);
        } while (status != HttpStatusCode.OK && DateTime.UtcNow < deadline);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(1, warmup.Attempts);
        Assert.Equal(1, warmup.OptionalAttempts);
    }

    [Fact]
    public async Task FakeModeIsReadyAtOnceAndLivenessNeverDependsOnWarmup()
    {
        await using var host = await Host.StartAsync();
        using var client = host.Client;
        using var ready = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal("ready", (await ready.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
        using var live = await client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
    }

    [Fact]
    public async Task AzureModeIsNotReadyUntilTheFirstWarmupCompletesAndThenStaysReady()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var warmup = new ScriptedWarmup { Gate = gate };
        await using var host = await Host.StartAsync(azure: true, warmup: warmup);
        using var client = host.Client;
        using (var warming = await client.GetAsync("/health/ready"))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, warming.StatusCode);
            Assert.Equal("warming", (await warming.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
        }
        using (var live = await client.GetAsync("/health/live")) Assert.Equal(HttpStatusCode.OK, live.StatusCode);

        gate.SetResult();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        HttpStatusCode status;
        do
        {
            using var response = await client.GetAsync("/health/ready");
            status = response.StatusCode;
            if (status != HttpStatusCode.OK) await Task.Delay(10);
        } while (status != HttpStatusCode.OK && DateTime.UtcNow < deadline);
        Assert.Equal(HttpStatusCode.OK, status);

        // Readiness is never re-checked, so a later token or network failure cannot take the replica out of rotation.
        warmup.Succeed = false;
        for (var probe = 0; probe < 3; probe++)
        {
            using var response = await client.GetAsync("/health/ready");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        Assert.Equal(1, warmup.Attempts);
    }
}
