using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using VoiceAssistant.Api;
using VoiceAssistant.Api.Knowledge;
using Xunit;

namespace VoiceAssistant.Api.Tests;

public sealed partial class ApiTests
{
    [Fact]
    public async Task KnowledgeProductionAnonymousFailsClosedAndOriginProtectsMutations()
    {
        await using var host = await Host.StartAsync(azure: true, knowledge: new InMemoryKnowledgeStore());
        using var client = host.Client;
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/knowledge")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/api/knowledge", JsonNotes())).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.DeleteAsync("/api/knowledge/" + new string('a', 32))).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", host.Token());
        client.DefaultRequestHeaders.Remove("Origin");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/knowledge")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/api/knowledge", JsonNotes())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync("/api/knowledge/" + new string('a', 32))).StatusCode);
        client.DefaultRequestHeaders.Add("Origin", "https://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/knowledge")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/api/knowledge", JsonNotes())).StatusCode);
    }

    [Fact]
    public async Task RealHttpUploadListDeleteIsOwnerScopedAndIdempotent()
    {
        await using var host = await Host.StartAsync(azure: true, knowledge: new InMemoryKnowledgeStore());
        using var client = host.Client;
        client.DefaultRequestHeaders.Authorization = new("Bearer", host.Token());
        var upload = await client.PostAsync("/api/knowledge", JsonNotes());
        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
        using var result = JsonDocument.Parse(await upload.Content.ReadAsStringAsync());
        var id = result.RootElement.GetProperty("id").GetString()!;
        Assert.True(KnowledgeIds.ValidDocument(id));
        Assert.Equal("Original notes", result.RootElement.GetProperty("title").GetString());
        Assert.Equal(1, result.RootElement.GetProperty("chunks").GetInt32());
        Assert.True(result.RootElement.GetProperty("characters").GetInt32() > 0);

        using var list = JsonDocument.Parse(await client.GetStringAsync("/api/knowledge"));
        Assert.Equal(1, list.RootElement.GetProperty("usage").GetProperty("documents").GetInt32());
        Assert.Equal(5242880, list.RootElement.GetProperty("limits").GetProperty("maxFileBytes").GetInt32());
        Assert.Contains(".pdf", list.RootElement.GetProperty("extensions").EnumerateArray().Select(item => item.GetString()));
        Assert.DoesNotContain("Original private contents", list.RootElement.ToString());

        client.DefaultRequestHeaders.Authorization = new("Bearer", host.Token(oid: "11111111-1111-1111-1111-111111111111"));
        using var other = JsonDocument.Parse(await client.GetStringAsync("/api/knowledge"));
        Assert.Empty(other.RootElement.GetProperty("documents").EnumerateArray());
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/knowledge/" + id)).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", host.Token());
        using var stillThere = JsonDocument.Parse(await client.GetStringAsync("/api/knowledge"));
        Assert.Single(stillThere.RootElement.GetProperty("documents").EnumerateArray());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.DeleteAsync("/api/knowledge/kb-" + id + "-0000")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/knowledge/" + id)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/knowledge/" + id)).StatusCode);
        using var empty = JsonDocument.Parse(await client.GetStringAsync("/api/knowledge"));
        Assert.Empty(empty.RootElement.GetProperty("documents").EnumerateArray());
    }

    [Fact]
    public async Task FakeUsesFixedLoopbackOwnerWithoutEntraButRejectsForeignOriginHost()
    {
        await using var host = await Host.StartAsync();
        using var client = host.Client;
        client.DefaultRequestHeaders.Remove("Origin");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/knowledge")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/api/knowledge", JsonNotes())).StatusCode);
        client.DefaultRequestHeaders.Add("Origin", "http://localhost:5173");
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsync("/api/knowledge", JsonNotes())).StatusCode);
        client.DefaultRequestHeaders.Remove("Origin");
        client.DefaultRequestHeaders.Add("Origin", "https://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/knowledge")).StatusCode);
        client.DefaultRequestHeaders.Remove("Origin");
        client.DefaultRequestHeaders.Host = "evil.example";
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/knowledge")).StatusCode);

        var context = new DefaultHttpContext();
        context.Request.Host = new HostString("localhost");
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.1");
        Assert.False(KnowledgeEndpoints.Allowed(context, new() { Mode = "Fake" }, false));
        context.Request.Headers.Origin = "http://localhost";
        Assert.False(KnowledgeEndpoints.Allowed(context, new() { Mode = "Fake" }, true));
    }

    [Fact]
    public async Task MultipartReadsRealDocxPdfAndTextInMemory()
    {
        await using var host = await Host.StartAsync();
        using var client = host.Client;
        client.DefaultRequestHeaders.Remove("Origin");
        client.DefaultRequestHeaders.Add("Origin", "http://localhost:5173");
        foreach (var file in new[]
        {
            (Name: "notes.md", Bytes: Encoding.UTF8.GetBytes("# Original\nOriginal contents")),
            (Name: "slides.pdf", Bytes: MaterialExtractionTests.Pdf(1, true)),
            (Name: "minutes.docx", Bytes: MaterialExtractionTests.Zip(new Dictionary<string,string>
            { ["word/document.xml"] = "<document><p><t>Original minutes</t></p></document>" }))
        })
        {
            using var content = new MultipartFormDataContent();
            content.Add(new ByteArrayContent(file.Bytes), "file", file.Name);
            content.Add(new StringContent("Safe original title"), "title");
            var response = await client.PostAsync("/api/knowledge", content);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("Safe original title", json.RootElement.GetProperty("title").GetString());
        }
    }

    [Theory]
    [InlineData("""{"title":"x","text":"notes","owner":"11111111-1111-1111-1111-111111111111"}""", 400)]
    [InlineData("""{"title":"x","text":"notes","allowedPrincipalIds":["someone"]}""", 400)]
    [InlineData("""{"title":"","text":"notes"}""", 400)]
    [InlineData("""{"title":"x","text":" "}""", 422)]
    [InlineData("""{"title":"x","title":"y","text":"notes"}""", 400)]
    [InlineData("""{"title":"x","text":123}""", 400)]
    public async Task InvalidJsonMaterialsAreSafeErrors(string body, int status)
    {
        await using var host = await Host.StartAsync();
        using var client = host.Client;
        client.DefaultRequestHeaders.Remove("Origin");
        client.DefaultRequestHeaders.Add("Origin", "http://localhost:5173");
        var response = await client.PostAsync("/api/knowledge", new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(status, (int)response.StatusCode);
        using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(error.RootElement.TryGetProperty("error", out _));
        Assert.DoesNotContain("someone", error.RootElement.ToString());
    }

    [Fact]
    public async Task MultipartRejectsExtraFileInvalidEncodingUnsupportedAndOversize()
    {
        await using var host = await Host.StartAsync();
        using var client = host.Client;
        client.DefaultRequestHeaders.Remove("Origin");
        client.DefaultRequestHeaders.Add("Origin", "http://localhost:5173");
        using var duplicate = new MultipartFormDataContent();
        duplicate.Add(new StringContent("one"), "file", "one.txt");
        duplicate.Add(new StringContent("two"), "file", "two.txt");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/knowledge", duplicate)).StatusCode);
        foreach (var file in new[]
        {
            (Name: "bad.txt", Bytes: new byte[] { 0xff }, Status: 415),
            (Name: "bad.doc", Bytes: new byte[] { 1, 2, 3 }, Status: 415),
            (Name: "empty.pdf", Bytes: MaterialExtractionTests.Pdf(1, false), Status: 422),
            (Name: "big.txt", Bytes: new byte[KnowledgeLimits.MaxFileBytes + 1], Status: 413)
        })
        {
            using var content = new MultipartFormDataContent();
            content.Add(new ByteArrayContent(file.Bytes), "file", file.Name);
            Assert.Equal(file.Status, (int)(await client.PostAsync("/api/knowledge", content)).StatusCode);
        }
    }

    [Fact]
    public async Task ThirdConcurrentUploadReturnsBusyAndProviderErrorIsRedacted()
    {
        var store = new BlockingKnowledgeStore();
        await using var host = await Host.StartAsync(knowledge: store);
        using var client = host.Client;
        client.DefaultRequestHeaders.Remove("Origin");
        client.DefaultRequestHeaders.Add("Origin", "http://localhost:5173");
        var first = client.PostAsync("/api/knowledge", JsonNotes());
        await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = client.PostAsync("/api/knowledge", JsonNotes());
        // Third attempt must not wait behind the per-owner mutation lock.
        await Task.Delay(100);
        try
        {
            var third = await client.PostAsync("/api/knowledge", JsonNotes()).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal((HttpStatusCode)429, third.StatusCode);
        }
        finally { store.Release.TrySetException(new InvalidOperationException("private-document-content secret-token Azure raw body")); }
        foreach (var task in new[] { first, second })
        {
            var response = await task;
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            var error = await response.Content.ReadAsStringAsync();
            Assert.Contains("knowledge_unavailable", error);
            Assert.DoesNotContain("secret-token", error);
            Assert.DoesNotContain("private-document", error);
        }
    }

    [Fact]
    public async Task QuotaExceededReturnsConflictWithoutStoringAnotherDocument()
    {
        var store = new InMemoryKnowledgeStore();
        const string localOwner = "00000000-0000-0000-0000-000000000001";
        for (var i = 0; i < 300; i++)
        {
            var id = i.ToString("x32");
            await store.UploadAsync(localOwner, new(id, "Original", 1, DateTimeOffset.UtcNow),
                [new($"kb-{id}-0000", "Original note")], CancellationToken.None);
        }
        await using var host = await Host.StartAsync(knowledge: store);
        using var client = host.Client;
        client.DefaultRequestHeaders.Remove("Origin");
        client.DefaultRequestHeaders.Add("Origin", "http://localhost:5173");
        var response = await client.PostAsync("/api/knowledge", JsonNotes());
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("quota_exceeded", await response.Content.ReadAsStringAsync());
        Assert.Equal(300, (await store.ListAsync(localOwner, CancellationToken.None)).Count);
    }

    [Theory]
    [InlineData("4")]
    [InlineData("181")]
    [InlineData("30.5")]
    [InlineData("invalid")]
    [InlineData("")]
    public async Task InvalidSessionDurationFailsStartup(string value)
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => Host.StartAsync(configuration:
            new Dictionary<string, string?> { ["Session:MaxMinutes"] = value }));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(30)]
    [InlineData(180)]
    public void SessionDurationAcceptsOnlyDocumentedRange(int minutes)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Session:MaxMinutes"] = minutes.ToString() }).Build();
        Assert.Equal(minutes, ServiceSettings.ReadMaxSessionMinutes(config));
        Assert.Equal(30, ServiceSettings.ReadMaxSessionMinutes(new ConfigurationBuilder().Build()));
    }

    [Fact]
    public async Task SessionMaximumEmitsDistinctFatalErrorWithoutChangingIdleTimeout()
    {
        var clock = new DurationClock();
        await using var host = await Host.StartAsync(clock: clock, configuration: new Dictionary<string, string?> { ["Session:MaxMinutes"] = "5" });
        using var socket = await host.ConnectAsync();
        await Send(socket, Start);
        Assert.Equal("session.ready", (await Receive(socket)).GetProperty("type").GetString());
        Assert.Equal(TimeSpan.FromMinutes(5), clock.Duration);
        clock.Fire();
        var error = await Receive(socket);
        Assert.Equal("session_time_limit", error.GetProperty("code").GetString());
        Assert.Equal("Session reached its time limit. Start a new session to continue.", error.GetProperty("message").GetString());
        Assert.False(error.GetProperty("retryable").GetBoolean());
    }

    private static StringContent JsonNotes() => new("""{"title":"Original notes","text":"Original private contents about an invented project."}""", Encoding.UTF8, "application/json");

    private sealed class BlockingKnowledgeStore : IKnowledgeStore
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<IReadOnlyList<KnowledgeDocument>> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IReadOnlyList<KnowledgeDocument>> ListAsync(string owner, CancellationToken cancellation)
        { Entered.TrySetResult(); return Release.Task; }
        public Task UploadAsync(string owner, KnowledgeDocument document, IReadOnlyList<KnowledgeChunk> chunks, CancellationToken cancellation) => throw new NotSupportedException();
        public Task DeleteAsync(string owner, string documentId, CancellationToken cancellation) => throw new NotSupportedException();
    }

    private sealed class DurationClock : TimeProvider
    {
        public TimeSpan Duration { get; private set; }
        private TimerCallback? callback;
        private object? state;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            this.callback = callback; this.state = state; Duration = dueTime;
            return new Timer();
        }
        public void Fire() => callback!(state);
        private sealed class Timer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
