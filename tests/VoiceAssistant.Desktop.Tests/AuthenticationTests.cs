using System.Net;
using System.Net.Http;
using VoiceAssistant.Desktop.Protocol;

namespace VoiceAssistant.Desktop.Tests;

public sealed class AuthenticationTests
{
    [Fact]
    public async Task TicketPostUsesBearerAndExactConfiguredOriginThenReturnsOneUseTicketUrl()
    {
        string expires = DateTimeOffset.UtcNow.AddSeconds(20).ToString("O");
        var handler = new RecordingHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent($$"""{"ticket":"0123456789012345678901234567890123456789012","expiresAt":"{{expires}}"}""")
            });
        var settings = new ClientSettings { Mode = ConnectionMode.Production };
        using var api = new AuthenticatedApiClient(settings, new FakeTokenProvider(), handler);

        Uri socket = await api.GetMeetingSocketAsync(CancellationToken.None);

        Assert.Equal("wss", socket.Scheme);
        Assert.Equal("/api/meeting", socket.AbsolutePath);
        Assert.Equal("?ticket=0123456789012345678901234567890123456789012", socket.Query);
        Assert.DoesNotContain("test-access-token", socket.ToString());
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("/api/session/ticket", handler.Path);
        Assert.Equal(settings.Origin, handler.Origin);
        Assert.Equal("Bearer test-access-token", handler.Authorization);
    }

    [Fact]
    public async Task AuthenticatedPracticeRequestsCarryBearerAndOriginAndBoundRequestBodies()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent("""{"correctedEnglish":"I can help.","easierEnglish":"I can help.","feedbackKo":"잘했어요.","points":[],"clarity":4}""")
        });
        var settings = new ClientSettings { Mode = ConnectionMode.Production };
        using var api = new AuthenticatedApiClient(settings, new FakeTokenProvider(), handler);

        using var response = await api.PostJsonAsync("/api/practice/feedback",
            new { scenario = new { kind = "sales", description = "", difficulty = 2 }, question = "Can you help?", answer = "Yes." },
            CancellationToken.None);

        Assert.Equal(settings.Origin, handler.Origin);
        Assert.Equal("Bearer test-access-token", handler.Authorization);
        Assert.Equal("/api/practice/feedback", handler.Path);
        Assert.NotNull(handler.Body);
        Assert.Contains("\"question\":\"Can you help?\"", handler.Body);
    }

    [Fact]
    public async Task TicketResponseBodyIsBoundedBeforeJsonParsing()
    {
        var handler = new RecordingHandler(_ =>
        {
            var content = new StringContent($$"""{"ticket":"{{new string('a', 65540)}}","expiresAt":"{{DateTimeOffset.UtcNow.AddSeconds(20):O}}"}""");
            content.Headers.ContentType = new("application/json");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        using var api = new AuthenticatedApiClient(new ClientSettings { Mode = ConnectionMode.Production },
            new FakeTokenProvider(), handler);

        await Assert.ThrowsAsync<InvalidDataException>(() => api.GetMeetingSocketAsync(CancellationToken.None));
    }

    [Fact]
    public async Task OversizedErrorBodyDoesNotReplaceSafeStatusErrorOrReadUnboundedJson()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent(new string('x', 100000))
        });
        using var api = new AuthenticatedApiClient(new ClientSettings { Mode = ConnectionMode.Production },
            new FakeTokenProvider(), handler);

        var error = await Assert.ThrowsAsync<ApiRequestException>(() =>
            api.PostJsonAsync("/api/practice/feedback", new { question = "q" }, CancellationToken.None));
        Assert.Equal("provider_unavailable", error.Code);
        Assert.DoesNotContain(new string('x', 20), error.Message);
    }

    [Fact]
    public void ProductionStartOptionsCarryGroundingModeAndOnlyConfirmedProfileValues()
    {
        var settings = new ClientSettings
        {
            Mode = ConnectionMode.Production,
            ResponseMode = "grounded",
            Topic = "Quarterly planning",
            ProfileName = "Ari",
            ProfileRole = "Product lead",
            ProfileProject = "Northstar",
            ProfileConfirmed = true
        };

        using var document = System.Text.Json.JsonDocument.Parse(MeetingClient.BuildStartMessage(settings));
        var options = document.RootElement.GetProperty("options");
        Assert.Equal("grounded", options.GetProperty("responseMode").GetString());
        Assert.Equal(1100, options.GetProperty("endSilenceMs").GetInt32());
        Assert.True(options.GetProperty("semanticSegmentation").GetBoolean());
        Assert.Equal("Quarterly planning", options.GetProperty("topic").GetString());
        Assert.True(options.GetProperty("profileConfirmed").GetBoolean());
        Assert.Equal("Ari", options.GetProperty("profile").GetProperty("name").GetString());
        Assert.False(options.GetProperty("transcribeOnly").GetBoolean());
    }

    private sealed class FakeTokenProvider : IAccessTokenProvider
    {
        public Task<string> AcquireTokenAsync(bool interactive, CancellationToken cancellationToken = default)
        {
            Assert.False(interactive);
            return Task.FromResult("test-access-token");
        }
    }

    private static StringContent JsonContent(string value)
    {
        var content = new StringContent(value);
        content.Headers.ContentType = new("application/json");
        return content;
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }
        public string? Path { get; private set; }
        public string? Origin { get; private set; }
        public string? Authorization { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method;
            Path = request.RequestUri!.AbsolutePath;
            Origin = request.Headers.GetValues("Origin").Single();
            Authorization = request.Headers.Authorization?.ToString();
            if (request.Content is not null)
                Body = await request.Content.ReadAsStringAsync(cancellationToken);
            return response(request);
        }
    }
}
