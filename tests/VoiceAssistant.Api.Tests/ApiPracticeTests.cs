using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using VoiceAssistant.Api.Practice;
using Xunit;

namespace VoiceAssistant.Api.Tests;

public sealed partial class ApiTests
{
    public static IEnumerable<object[]> PracticePayloads()
    {
        yield return ["/api/assist/enrich", """{"kind":"reply","text":"Let me check the goal."}"""];
        yield return ["/api/assist/speak", """{"text":"Let me check the goal.","voice":"partner","rate":"slow"}"""];
        yield return ["/api/practice/turn", """{"scenario":""" + PracticeSchemaTests.Scenario + ""","topic":"Original topic","useMaterials":false,"maxTurns":3,"history":[]}"""];
        yield return ["/api/practice/suggest", """{"scenario":""" + PracticeSchemaTests.Scenario + ""","topic":"","useMaterials":false,"question":"What is the main goal?"}"""];
        yield return ["/api/practice/feedback", """{"scenario":""" + PracticeSchemaTests.Scenario + ""","question":"What is the main goal?","answer":""}"""];
        yield return ["/api/practice/summary", """{"scenario":""" + PracticeSchemaTests.Scenario + ""","topic":"","turns":[{"question":"What is the main goal?","answer":"Learn English."}]}"""];
    }

    [Theory]
    [MemberData(nameof(PracticePayloads))]
    public async Task PracticeEndpointsAreRealHttpFakeOnlyOnLoopbackAndAlwaysProtectAzure(string path, string json)
    {
        await using (var host = await Host.StartAsync())
        {
            using var client = host.Client;
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync(path, PracticeBody(json))).StatusCode);
            client.DefaultRequestHeaders.Remove("Origin");
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync(path, PracticeBody(json))).StatusCode);
            client.DefaultRequestHeaders.Add("Origin", "http://localhost:5173");
            var success = await client.PostAsync(path, PracticeBody(json));
            Assert.Equal(HttpStatusCode.OK, success.StatusCode);
            Assert.Contains("no-store", success.Headers.CacheControl!.ToString());
            if (path.EndsWith("speak"))
            {
                Assert.Equal("audio/wav", success.Content.Headers.ContentType!.MediaType);
                Assert.Equal("RIFF", Encoding.ASCII.GetString((await success.Content.ReadAsByteArrayAsync())[..4]));
            }
            else using (JsonDocument.Parse(await success.Content.ReadAsStringAsync())) { }
            client.DefaultRequestHeaders.Host = "remote.example";
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync(path, PracticeBody(json))).StatusCode);
        }
        await using (var host = await Host.StartAsync(azure: true, practiceModel: new FixedPracticeModel(), practiceSpeech: new FakePracticeSpeech()))
        {
            using var client = host.Client;
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync(path, PracticeBody(json))).StatusCode);
            client.DefaultRequestHeaders.Authorization = new("Bearer", host.Token());
            client.DefaultRequestHeaders.Remove("Origin");
            client.DefaultRequestHeaders.Add("Origin", "https://evil.example");
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync(path, PracticeBody(json))).StatusCode);
        }
    }

    [Fact]
    public async Task ProductionPracticeUsesMeetingPolicyAndRejectsAppOnlyOrInvalidOid()
    {
        await using var host = await Host.StartAsync(azure: true, practiceModel: new FixedPracticeModel());
        using var client = host.Client;
        const string body = """{"kind":"question","text":"What is the goal?"}""";
        foreach (var token in new[] { host.Token(scope: "Other"), host.Token(oid: "bad-owner") })
        {
            client.DefaultRequestHeaders.Authorization = new("Bearer", token);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/api/assist/enrich", PracticeBody(body))).StatusCode);
        }
        client.DefaultRequestHeaders.Authorization = new("Bearer", host.Token());
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/assist/enrich", PracticeBody(body))).StatusCode);
    }

    [Fact]
    public async Task PracticeBodyLimitsStrictTypesAndInvalidUtf8ReturnSafeErrors()
    {
        await using var host = await Host.StartAsync();
        using var client = host.Client;
        client.DefaultRequestHeaders.Remove("Origin");
        client.DefaultRequestHeaders.Add("Origin", "http://localhost:5173");
        var big = await client.PostAsync("/api/assist/enrich", PracticeBody(new string(' ', 16385)));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, big.StatusCode);
        Assert.Contains("too_large", await big.Content.ReadAsStringAsync());
        foreach (var json in new[] { "{", """{"kind":"question","text":"secret-token","owner":"evil"}""",
            """{"kind":"question","text":"secret-token","text":"duplicate"}""", """{"kind":"reply","text":null}""" })
        {
            var response = await client.PostAsync("/api/assist/enrich", PracticeBody(json));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.DoesNotContain("secret-token", await response.Content.ReadAsStringAsync());
        }
        using var wrongEncoding = new StringContent("{}", Encoding.Unicode, "application/json");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/assist/enrich", wrongEncoding)).StatusCode);
        using var utf8 = new ByteArrayContent([123, 34, 0xff, 34, 58, 49, 125]);
        utf8.Headers.ContentType = new("application/json");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/assist/enrich", utf8)).StatusCode);
    }

    [Theory]
    [InlineData(false, 502, "provider_unavailable")]
    [InlineData(true, 504, "provider_timeout")]
    public async Task PracticeProviderErrorsNeverEchoRawTextOrPretendSuccess(bool timeout, int status, string code)
    {
        await using var host = await Host.StartAsync(practiceModel: new FixedPracticeModel
        {
            Failure = timeout ? new OperationCanceledException("secret-token") : new InvalidOperationException("private-content secret-token")
        });
        using var client = host.Client;
        client.DefaultRequestHeaders.Remove("Origin");
        client.DefaultRequestHeaders.Add("Origin", "http://localhost:5173");
        var response = await client.PostAsync("/api/assist/enrich", PracticeBody("""{"kind":"question","text":"Original question?"}"""));
        Assert.Equal(status, (int)response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        Assert.Contains(code, json);
        Assert.DoesNotContain("secret-token", json);
        Assert.DoesNotContain("private-content", json);
    }

    [Fact]
    public async Task PracticeRateLimitReturns429RetryAfterAndRollingWindowExpires()
    {
        var clock = new PracticeClock();
        await using var host = await Host.StartAsync(clock: clock);
        using var client = host.Client;
        client.DefaultRequestHeaders.Remove("Origin");
        client.DefaultRequestHeaders.Add("Origin", "http://localhost:5173");
        for (var i = 0; i < 60; i++)
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/assist/enrich", PracticeBody("""{"kind":"question","text":"Original question?"}"""))).StatusCode);
        var busy = await client.PostAsync("/api/practice/feedback", PracticeBody("{}"));
        Assert.Equal((HttpStatusCode)429, busy.StatusCode);
        Assert.NotNull(busy.Headers.RetryAfter);
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/assist/enrich", PracticeBody("""{"kind":"question","text":"Original question?"}"""))).StatusCode);
        for (var i = 0; i < 30; i++)
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/assist/speak", PracticeBody("""{"text":"Hello"}"""))).StatusCode);
        Assert.Equal((HttpStatusCode)429, (await client.PostAsync("/api/assist/speak", PracticeBody("""{"text":"Hello"}"""))).StatusCode);
    }

    [Fact]
    public void PracticeConcurrentLimitsAreSeparateAndPerOwner()
    {
        var limiter = new PracticeLimiter(new PracticeClock());
        var general = Enumerable.Range(0, 8).Select(_ => limiter.Enter("a", false)).ToArray();
        var speech = Enumerable.Range(0, 3).Select(_ => limiter.Enter("a", true)).ToArray();
        Assert.Equal(429, Assert.Throws<PracticeException>(() => limiter.Enter("a", false)).Status);
        Assert.Equal(429, Assert.Throws<PracticeException>(() => limiter.Enter("a", true)).Status);
        using var other = limiter.Enter("b", false);
        general[0].Dispose(); general[0].Dispose();
        using var replacement = limiter.Enter("a", false);
        foreach (var lease in general.Concat(speech)) lease.Dispose();
    }

    [Theory]
    [InlineData(false, 8)]
    [InlineData(true, 3)]
    public async Task ConcurrentHttpProviderCallsReturnBusyAtDocumentedLimit(bool speak, int maximum)
    {
        var blocker = new BlockingPracticeProvider(maximum);
        await using var host = await Host.StartAsync(practiceModel: blocker, practiceSpeech: blocker);
        using var client = host.Client;
        client.DefaultRequestHeaders.Remove("Origin");
        client.DefaultRequestHeaders.Add("Origin", "http://localhost:5173");
        var path = speak ? "/api/assist/speak" : "/api/practice/suggest";
        var body = speak ? """{"text":"Hello"}""" :
            """{"scenario":""" + PracticeSchemaTests.Scenario + ""","topic":"","useMaterials":false,"question":"What is the goal?"}""";
        var requests = Enumerable.Range(0, maximum).Select(_ => client.PostAsync(path, PracticeBody(body))).ToArray();
        await blocker.AllEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var busy = await client.PostAsync(path, PracticeBody(body));
            Assert.Equal((HttpStatusCode)429, busy.StatusCode);
            Assert.NotNull(busy.Headers.RetryAfter);
            Assert.Equal(maximum, blocker.Calls);
        }
        finally { blocker.Release.TrySetResult(); }
        foreach (var request in requests) Assert.Equal(HttpStatusCode.OK, (await request).StatusCode);
    }

    [Theory]
    [InlineData(false, 20)]
    [InlineData(true, 15)]
    public async Task ActualDeadlineTimerCancelsProviderAndReturns504(bool speak, int seconds)
    {
        var clock = new DurationClock();
        var blocker = new BlockingPracticeProvider(1);
        await using var host = await Host.StartAsync(clock: clock, practiceModel: blocker, practiceSpeech: blocker);
        using var client = host.Client;
        client.DefaultRequestHeaders.Remove("Origin");
        client.DefaultRequestHeaders.Add("Origin", "http://localhost:5173");
        var request = client.PostAsync(speak ? "/api/assist/speak" : "/api/assist/enrich",
            PracticeBody(speak ? """{"text":"Hello"}""" : """{"kind":"question","text":"Hello?"}"""));
        await blocker.AllEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(TimeSpan.FromSeconds(seconds), clock.Duration);
        clock.Fire();
        var response = await request.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.Contains("provider_timeout", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("1")]
    [InlineData("\"true\"")]
    public void TranscribeOnlyRejectsNonBoolean(string value)
    {
        var json = Start[..^1] + ",\"options\":{\"transcribeOnly\":" + value + "}}";
        Assert.False(MeetingSession.IsValidStart(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public async Task RecognitionOnlySocketKeepsTranscriptsButNeverSearchesOrGenerates()
    {
        var provider = new ControlledProvider();
        await using var host = await Host.StartAsync(provider: provider);
        using var socket = await host.ConnectAsync();
        await Send(socket, WithOptions(new { transcribeOnly = true, endSilenceMs = 500, phrases = new[] { "JMAP" } }));
        await Receive(socket);
        Assert.True(provider.SpeechOptions!.TranscribeOnly);
        Assert.Equal(500, provider.SpeechOptions.EndSilenceMs);
        await Send(socket, """{"type":"response.request"}""");
        var error = await Receive(socket);
        Assert.Equal("transcribe_only", error.GetProperty("code").GetString());
        Assert.False(error.GetProperty("retryable").GetBoolean());
        provider.Emit(new("one", 1, "What is our customer delivery plan?", false));
        Assert.Equal("transcript.partial", (await Receive(socket)).GetProperty("type").GetString());
        await Task.Delay(300); // Stable enough to prefetch in ordinary mode.
        provider.Emit(new("one", 2, "What is our customer delivery plan?", true));
        Assert.Equal("transcript.final", (await Receive(socket)).GetProperty("type").GetString());
        await Send(socket, """{"type":"response.request"}""");
        Assert.Equal("transcribe_only", (await Receive(socket)).GetProperty("code").GetString());
        provider.Emit(new("two", 1, "Another answer.", true));
        Assert.Equal("transcript.final", (await Receive(socket)).GetProperty("type").GetString());
        await Send(socket, """{"type":"response.cancel"}""");
        await Send(socket, """{"type":"session.stop"}""");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        Assert.Equal(WebSocketMessageType.Close, (await socket.ReceiveAsync(new byte[4096], timeout.Token)).MessageType);
        Assert.Equal(0, provider.Retrievals);
        Assert.Equal(0, provider.Answers);
        Assert.True(provider.Disposed);
    }

    private static StringContent PracticeBody(string json) => new(json, Encoding.UTF8, "application/json");
    private sealed class FixedPracticeModel : IPracticeModel
    {
        public Exception? Failure { get; init; }
        public Task<string> GenerateAsync(PracticeRequest request, Grounding grounding, bool retry, CancellationToken cancellation)
        {
            if (Failure is not null) throw Failure;
            return Task.FromResult("""{"korean":"목표가 무엇인가요?","pronunciation":null}""");
        }
    }
    private sealed class PracticeClock : TimeProvider
    {
        private long timestamp;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => timestamp;
        public void Advance(TimeSpan value) => timestamp += (long)value.TotalMilliseconds;
    }
    private sealed class BlockingPracticeProvider(int expected) : IPracticeModel, IPracticeSpeech
    {
        public int Calls;
        public TaskCompletionSource AllEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private async Task Wait(CancellationToken cancellation)
        {
            if (Interlocked.Increment(ref Calls) == expected) AllEntered.TrySetResult();
            await Release.Task.WaitAsync(cancellation);
        }
        public async Task<string> GenerateAsync(PracticeRequest request, Grounding grounding, bool retry, CancellationToken cancellation)
        {
            await Wait(cancellation);
            return """{"text":"Let me check the goal first."}""";
        }
        public async Task<PracticeAudio> SpeakAsync(PracticeRequest request, CancellationToken cancellation)
        {
            await Wait(cancellation);
            return await new FakePracticeSpeech().SpeakAsync(request, cancellation);
        }
    }
}
