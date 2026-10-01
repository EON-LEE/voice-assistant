using System.IdentityModel.Tokens.Jwt;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using VoiceAssistant.Api;
using Xunit;

namespace VoiceAssistant.Api.Tests;

public sealed partial class ApiTests
{
    private const string Start = """{"type":"session.start","protocolVersion":1,"audio":{"encoding":"pcm_s16le","sampleRate":16000,"channels":1}}""";
    private const string ObjectId = "e59048e0-9a10-433e-8e0e-beb279c0c234";

    [Fact]
    public async Task FakeStreamsOverRealWebSocketAndSupportsManualResponse()
    {
        var firstDeltaMetric = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completedMetric = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, owner) =>
            {
                if (instrument.Meter.Name == MeetingMetrics.MeterName) owner.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
        {
            if (!tags.ToArray().Contains(new KeyValuePair<string, object?>("provider", "Fake"))) return;
            if (instrument.Name == "voiceassistant.stt_final_to_first_delta") firstDeltaMetric.TrySetResult(value);
            if (instrument.Name == "voiceassistant.stt_final_to_completed") completedMetric.TrySetResult(value);
        });
        listener.Start();
        await using var host = await Host.StartAsync();
        using var socket = await host.ConnectAsync();
        await Send(socket, Start);
        Assert.Equal("session.ready", (await Receive(socket)).GetProperty("type").GetString());
        await socket.SendAsync(new byte[320].Select(_ => (byte)1).ToArray(), WebSocketMessageType.Binary, true, CancellationToken.None);
        await socket.SendAsync(new byte[320].Select(_ => (byte)1).ToArray(), WebSocketMessageType.Binary, true, CancellationToken.None);
        var partial = await Receive(socket);
        Assert.Equal("transcript.partial", partial.GetProperty("type").GetString());
        var final = await Receive(socket);
        Assert.Equal(FakeMeetingProvider.TranscriptText, final.GetProperty("text").GetString());
        Assert.Equal(partial.GetProperty("turnId").GetString(), final.GetProperty("turnId").GetString());
        var response = await Until(socket, "response.completed");
        Assert.Equal(FakeMeetingProvider.AnswerText, response.GetProperty("text").GetString());
        Assert.Equal("disabled", response.GetProperty("grounding").GetString());
        Assert.Equal(0, response.GetProperty("sources").GetArrayLength());
        Assert.True(await firstDeltaMetric.Task.WaitAsync(TimeSpan.FromSeconds(5)) >= 0);
        Assert.True(await completedMetric.Task.WaitAsync(TimeSpan.FromSeconds(5)) >= 0);
        await Send(socket, """{"type":"response.request"}""");
        Assert.Equal(FakeMeetingProvider.AnswerText, (await Until(socket, "response.completed")).GetProperty("text").GetString());
        await Send(socket, """{"type":"session.stop"}""");
    }

    [Fact]
    public async Task FakeProducesOnlyOneFinalUntilSufficientSilence()
    {
        var transcripts = new List<Transcript>();
        await using var stream = await new FakeMeetingProvider().StartSpeechAsync(transcripts.Add, _ => Assert.Fail(), CancellationToken.None);
        stream.Write(new byte[640]);
        Assert.Empty(transcripts);
        var audio = Enumerable.Repeat((byte)1, 640).ToArray();
        stream.Write(audio);
        for (var i = 0; i < 100; i++) stream.Write(audio);
        Assert.Single(transcripts, item => item.Final);
        stream.Write(new byte[16000]);
        stream.Write(audio);
        Assert.Equal(2, transcripts.Count(item => item.Final));
    }

    [Theory]
    [InlineData("""{"type":"session.start","protocolVersion":2,"audio":{"encoding":"pcm_s16le","sampleRate":16000,"channels":1}}""")]
    [InlineData("""{"type":"session.start","protocolVersion":1,"audio":{"encoding":"pcm_s16le","sampleRate":48000,"channels":1}}""")]
    [InlineData("""{"type":"response.request"}""")]
    [InlineData("[]")]
    [InlineData("{")]
    public async Task InvalidHandshakeFailsVisibly(string start)
    {
        await using var host = await Host.StartAsync();
        using var socket = await host.ConnectAsync();
        await Send(socket, start);
        Assert.Equal("invalid_start", (await Receive(socket)).GetProperty("code").GetString());
    }

    [Theory]
    [InlineData(1, "invalid_audio")]
    [InlineData(32770, "message_limit")]
    public async Task InvalidAudioRejected(int length, string error)
    {
        await using var host = await Host.StartAsync();
        using var socket = await host.ConnectAsync();
        await Send(socket, Start);
        await Receive(socket);
        await socket.SendAsync(new byte[length], WebSocketMessageType.Binary, true, CancellationToken.None);
        Assert.Equal(error, (await Receive(socket)).GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("null")]
    [InlineData("")]
    public async Task FakeRejectsForeignOrMissingOrigin(string origin)
    {
        await using var host = await Host.StartAsync();
        await Assert.ThrowsAsync<WebSocketException>(() => host.ConnectAsync(origin: origin));
    }

    [Fact]
    public async Task FakeRejectsRemoteHostAndCrossSite()
    {
        await using var host = await Host.StartAsync();
        using var client = host.Client;
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/meeting");
        request.Headers.Host = "evil.example";
        request.Headers.Add("Origin", "http://localhost:5173");
        request.Headers.Add("Connection", "Upgrade");
        request.Headers.Add("Upgrade", "websocket");
        request.Headers.Add("Sec-WebSocket-Version", "13");
        request.Headers.Add("Sec-WebSocket-Key", Convert.ToBase64String(new byte[16]));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(request)).StatusCode);
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Origin", "http://localhost:5173");
        socket.Options.SetRequestHeader("Sec-Fetch-Site", "cross-site");
        await Assert.ThrowsAsync<WebSocketException>(() => socket.ConnectAsync(host.WebSocketUri, CancellationToken.None));
    }

    [Fact]
    public async Task ProductionAuthValidatesIssuerAudienceLifetimeScopeAndTicket()
    {
        await using var host = await Host.StartAsync(azure: true);
        using var client = host.Client;
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/api/session/ticket", null)).StatusCode);
        foreach (var token in new[]
        {
            host.Token(audience: "wrong"), host.Token(issuer: "https://evil.example"),
            host.Token(expired: true), "not-a-token"
        })
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/api/session/ticket", null)).StatusCode);
        }
        client.DefaultRequestHeaders.Authorization = new("Bearer", host.Token(scope: "Other.Scope"));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/api/session/ticket", null)).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", host.Token(oid: "x' or true"));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/api/session/ticket", null)).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", host.Token());
        using var response = await client.PostAsync("/api/session/ticket", null);
        response.EnsureSuccessStatusCode();
        var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var ticket = body.RootElement.GetProperty("ticket").GetString()!;
        Assert.Equal(43, ticket.Length);
        using var socket = await host.ConnectAsync(ticket, "https://meeting.example");
        await Send(socket, Start);
        Assert.Equal("session.ready", (await Receive(socket)).GetProperty("type").GetString());
        await Assert.ThrowsAsync<WebSocketException>(() => host.ConnectAsync(ticket, "https://meeting.example"));
        await Assert.ThrowsAsync<WebSocketException>(() => host.ConnectAsync(origin: "https://meeting.example"));
    }

    [Fact]
    public void TicketsAreAtomicOneUseExpiringAndOriginBound()
    {
        var clock = new TestClock();
        var store = new TicketStore(clock);
        var ticket = store.Issue(ObjectId, "https://meeting.example")!;
        var successes = 0;
        Parallel.For(0, 20, _ => { if (store.Consume(ticket.Ticket, "https://meeting.example") is not null) Interlocked.Increment(ref successes); });
        Assert.Equal(1, successes);
        ticket = store.Issue(ObjectId, "https://meeting.example")!;
        Assert.Null(store.Consume(ticket.Ticket, "https://evil.example"));
        Assert.Null(store.Consume(ticket.Ticket, "https://meeting.example"));
        ticket = store.Issue(ObjectId, "https://meeting.example")!;
        clock.Now += TimeSpan.FromSeconds(30);
        Assert.Null(store.Consume(ticket.Ticket, "https://meeting.example"));
    }

    [Fact]
    public void AclFilterRejectsIdentityInjection()
    {
        Assert.Equal($"allowedPrincipalIds/any(p: p eq '{ObjectId}')", AzureMeetingProvider.AclFilter(ObjectId));
        Assert.Throws<ArgumentException>(() => AzureMeetingProvider.AclFilter("x') or true or ('x"));
        Assert.Throws<ArgumentException>(() => AzureMeetingProvider.AclFilter(""));
    }

    [Fact]
    public async Task FakeCannotStartInProduction()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => Host.StartAsync(environment: "Production"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledGenerationCannotLeakLateResults(bool newUtterance)
    {
        var provider = new DelayedProvider();
        await using var host = await Host.StartAsync(provider: provider);
        using var socket = await host.ConnectAsync();
        await Send(socket, Start);
        await Receive(socket);
        await socket.SendAsync(Enumerable.Repeat((byte)1, 640).ToArray(), WebSocketMessageType.Binary, true, CancellationToken.None);
        var first = await Until(socket, "response.started");
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (newUtterance)
        {
            provider.Emit(new("new-turn", 1, "A new utterance", false));
            provider.Emit(new("new-turn", 2, "A new utterance", true));
        }
        else await Send(socket, """{"type":"response.request"}""");
        var cancelled = await Until(socket, "response.cancelled");
        Assert.Equal("response.cancelled", cancelled.GetProperty("type").GetString());
        Assert.Equal(first.GetProperty("responseId").GetString(), cancelled.GetProperty("responseId").GetString());
        var second = await Until(socket, "response.started");
        Assert.Equal("response.started", second.GetProperty("type").GetString());
        provider.Release.TrySetResult();
        while (true)
        {
            var message = await Receive(socket);
            if (message.TryGetProperty("responseId", out var id))
                Assert.Equal(second.GetProperty("responseId").GetString(), id.GetString());
            if (message.GetProperty("type").GetString() == "response.completed") break;
        }
    }

    [Fact]
    public async Task SearchFailureIsVisibleAndDoesNotInvokeModel()
    {
        await using var host = await Host.StartAsync(provider: new UnavailableSearchProvider());
        using var socket = await host.ConnectAsync();
        await Send(socket, Start);
        await Receive(socket);
        await socket.SendAsync(Enumerable.Repeat((byte)1, 640).ToArray(), WebSocketMessageType.Binary, true, CancellationToken.None);
        Assert.Equal("grounding_unavailable", (await Until(socket, "error")).GetProperty("code").GetString());
        var completed = await Until(socket, "response.completed");
        Assert.Equal("unavailable", completed.GetProperty("grounding").GetString());
        Assert.Equal(0, completed.GetProperty("sources").GetArrayLength());
    }

    [Theory]
    [InlineData("no_matches", 2)]
    [InlineData("disabled", 2)]
    [InlineData("grounded", 2)]
    public async Task ResponseMetricsIncludeTranscriptOnlyNoMatchesModel(string grounding, int expectedMeasurements)
    {
        var measurements = new System.Collections.Concurrent.ConcurrentBag<string>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, owner) =>
            {
                if (instrument.Meter.Name == MeetingMetrics.MeterName &&
                    instrument.Name.StartsWith("voiceassistant.stt_final_to_", StringComparison.Ordinal))
                    owner.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((instrument, _, _, _) => measurements.Add(instrument.Name));
        listener.Start();
        await using var host = await Host.StartAsync(provider: new GroundingProvider(grounding));
        using var socket = await host.ConnectAsync();
        await Send(socket, Start);
        await Receive(socket);
        await socket.SendAsync(Enumerable.Repeat((byte)1, 640).ToArray(), WebSocketMessageType.Binary, true, CancellationToken.None);
        var completion = await Until(socket, "response.completed");
        Assert.Equal(grounding, completion.GetProperty("grounding").GetString());
        await Send(socket, """{"type":"session.stop"}""");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var closed = await socket.ReceiveAsync(new byte[32768], timeout.Token);
        Assert.Equal(WebSocketMessageType.Close, closed.MessageType);
        Assert.Equal(expectedMeasurements, measurements.Count);
        if (expectedMeasurements > 0)
        {
            Assert.Contains("voiceassistant.stt_final_to_first_delta", measurements);
            Assert.Contains("voiceassistant.stt_final_to_completed", measurements);
        }
    }

    private sealed class GroundingProvider(string status) : IMeetingProvider
    {
        private readonly FakeMeetingProvider fake = new();
        public Task<ISpeechStream> StartSpeechAsync(Action<Transcript> transcript, Action<ProviderException> error, CancellationToken cancellation) =>
            fake.StartSpeechAsync(transcript, error, cancellation);
        public Task<Grounding> RetrieveAsync(string query, string objectId, CancellationToken cancellation) =>
            Task.FromResult(new Grounding(status, []));
        public IAsyncEnumerable<string> AnswerAsync(IReadOnlyList<ConversationTurn> conversation, Grounding grounding, CancellationToken cancellation) =>
            fake.AnswerAsync(conversation, grounding, cancellation);
    }

    private static Task Send(ClientWebSocket socket, string text) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);
    private static async Task<JsonElement> Receive(ClientWebSocket socket)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var buffer = new byte[32768];
        var result = await socket.ReceiveAsync(buffer, timeout.Token);
        Assert.Equal(WebSocketMessageType.Text, result.MessageType);
        Assert.True(result.EndOfMessage);
        using var json = JsonDocument.Parse(buffer.AsMemory(0, result.Count));
        return json.RootElement.Clone();
    }
    private static async Task<JsonElement> Until(ClientWebSocket socket, string type)
    {
        for (var i = 0; i < 30; i++)
        {
            var message = await Receive(socket);
            if (message.GetProperty("type").GetString() == type) return message;
        }
        throw new InvalidOperationException("Expected event not received.");
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class DelayedProvider : IMeetingProvider
    {
        private readonly FakeMeetingProvider fake = new();
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Action<Transcript> Emit { get; private set; } = _ => throw new InvalidOperationException("Speech is not started.");
        public Task<ISpeechStream> StartSpeechAsync(Action<Transcript> transcript, Action<ProviderException> error, CancellationToken cancellation)
        {
            Emit = transcript;
            return fake.StartSpeechAsync(transcript, error, cancellation);
        }
        public Task<Grounding> RetrieveAsync(string query, string objectId, CancellationToken cancellation) => fake.RetrieveAsync(query, objectId, cancellation);
        public async IAsyncEnumerable<string> AnswerAsync(IReadOnlyList<ConversationTurn> conversation, Grounding grounding,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellation)
        {
            Started.TrySetResult();
            await Release.Task;
            yield return "A deliberately late result.";
        }
    }

    private sealed class UnavailableSearchProvider : IMeetingProvider
    {
        public Task<ISpeechStream> StartSpeechAsync(Action<Transcript> transcript, Action<ProviderException> error, CancellationToken cancellation) =>
            new FakeMeetingProvider().StartSpeechAsync(transcript, error, cancellation);
        public Task<Grounding> RetrieveAsync(string query, string objectId, CancellationToken cancellation) =>
            throw new ProviderException("grounding_unavailable", "Grounding is unavailable.");
        public IAsyncEnumerable<string> AnswerAsync(IReadOnlyList<ConversationTurn> conversation, Grounding grounding, CancellationToken cancellation) =>
            throw new InvalidOperationException("Must not invoke a model on Search failure.");
    }

    private sealed class Host(WebApplication app, string address) : IAsyncDisposable
    {
        private static readonly SymmetricSecurityKey Key = new(Encoding.UTF8.GetBytes("test-only-256-bit-signing-key-not-for-production-123456"));
        private const string Tenant = "10000000-0000-0000-0000-000000000001";
        private const string Audience = "api://test";
        public HttpClient Client
        {
            get
            {
                var client = new HttpClient { BaseAddress = new Uri(address) };
                client.DefaultRequestHeaders.Add("Origin", "https://meeting.example");
                return client;
            }
        }
        public Uri WebSocketUri => new(address.Replace("http:", "ws:") + "/api/meeting");
        public string Token(string scope = "Meeting.Access", string audience = Audience, string? issuer = null, bool expired = false, string oid = ObjectId)
        {
            var now = DateTime.UtcNow;
            return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
                issuer ?? $"https://login.microsoftonline.com/{Tenant}/v2.0", audience,
                [new Claim("oid", oid), new Claim("scp", scope)],
                now.AddMinutes(-10), expired ? now.AddMinutes(-5) : now.AddMinutes(5),
                new SigningCredentials(Key, SecurityAlgorithms.HmacSha256)));
        }
        public static async Task<Host> StartAsync(bool azure = false, string environment = "Development", IMeetingProvider? provider = null,
            VoiceAssistant.Api.Knowledge.IKnowledgeStore? knowledge = null, TimeProvider? clock = null,
            Dictionary<string, string?>? configuration = null)
        {
            var app = ApiApplication.Build([], builder =>
            {
                builder.Environment.EnvironmentName = environment;
                builder.WebHost.UseUrls("http://127.0.0.1:0");
                builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Provider:Mode"] = azure ? "Azure" : "Fake",
                    ["Authentication:TenantId"] = Tenant,
                    ["Authentication:Audience"] = Audience,
                    ["Authentication:ClientId"] = Tenant,
                    ["Authentication:Scope"] = Audience + "/Meeting.Access",
                    ["Azure:SpeechRegion"] = "eastus",
                    ["Azure:SpeechResourceId"] = "/subscriptions/test",
                    ["Azure:OpenAIEndpoint"] = "https://example.openai.azure.com",
                    ["Azure:ChatDeployment"] = "chat",
                    ["Security:AllowedOrigins:0"] = "https://meeting.example"
                });
                if (configuration is not null) builder.Configuration.AddInMemoryCollection(configuration);
                if (knowledge is not null) builder.Services.AddSingleton(knowledge);
                if (clock is not null) builder.Services.AddSingleton(clock);
                builder.Services.AddSingleton<IMeetingProvider>(provider ?? new FakeMeetingProvider());
                builder.Services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                {
                    options.Configuration = new Microsoft.IdentityModel.Protocols.OpenIdConnect.OpenIdConnectConfiguration
                    { Issuer = $"https://login.microsoftonline.com/{Tenant}/v2.0" };
                    options.TokenValidationParameters.IssuerSigningKey = Key;
                });
            });
            await app.StartAsync();
            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!;
            return new Host(app, addresses.Addresses.Single());
        }
        public async Task<ClientWebSocket> ConnectAsync(string? ticket = null, string origin = "http://localhost:5173")
        {
            var socket = new ClientWebSocket();
            socket.Options.SetRequestHeader("Origin", origin);
            try
            {
                await socket.ConnectAsync(ticket is null ? WebSocketUri : new Uri(WebSocketUri + "?ticket=" + ticket), CancellationToken.None);
                return socket;
            }
            catch { socket.Dispose(); throw; }
        }
        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
