using System.ClientModel;
using System.ClientModel.Primitives;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Azure;
using Azure.AI.OpenAI;
using Azure.Core.Pipeline;
using Azure.Search.Documents;
using Microsoft.Extensions.Configuration;
using VoiceAssistant.Api;
using Xunit;

namespace VoiceAssistant.Api.Tests;

public sealed class SemanticGroundingTests
{
    private const string Oid = "e59048e0-9a10-433e-8e0e-beb279c0c234";
    private const string Lighthouse = "The fictional Project Lighthouse plan is to review the prototype on Friday.";

    [Theory]
    [InlineData(0, false)]
    [InlineData(1.99, false)]
    [InlineData(2, true)]
    [InlineData(3.5, true)]
    [InlineData(4, true)]
    public void GateUsesSemanticScoreNotCandidateRank(double score, bool accepted) =>
        Assert.Equal(accepted, AzureMeetingProvider.IsRelevantSemanticScore(score, 2));

    [Theory]
    [InlineData(null)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-1d)]
    [InlineData(4.1)]
    public void InvalidOrMissingScoreFailsClosed(double? score)
    {
        var error = Assert.Throws<ProviderException>(() => AzureMeetingProvider.IsRelevantSemanticScore(score, 2));
        Assert.Equal("grounding_unavailable", error.Code);
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-0.1")]
    [InlineData("4.1")]
    [InlineData("2,0")]
    [InlineData("")]
    public void InvalidConfiguredThresholdFailsStartup(string value)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Azure:SearchMinimumRerankerScore"] = value }).Build();
        Assert.Throws<InvalidOperationException>(() => ServiceSettings.ReadMinimumRerankerScore(config));
    }

    [Fact]
    public void ConfigurationDefaultsAndInvariantThresholdAreValidated()
    {
        var config = new ConfigurationBuilder().Build();
        Assert.Equal(2, ServiceSettings.ReadMinimumRerankerScore(config));
        Assert.Equal("meeting-semantic", new ServiceSettings().SearchSemanticConfiguration);
        foreach (var value in new[] { "0", "2.5", "4" })
        {
            var values = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["Azure:SearchMinimumRerankerScore"] = value }).Build();
            Assert.Equal(double.Parse(value, CultureInfo.InvariantCulture), ServiceSettings.ReadMinimumRerankerScore(values));
        }
        Assert.Throws<InvalidOperationException>(() => new ServiceSettings { SearchSemanticConfiguration = " " }.ValidateSearchRelevance());
    }

    [Theory]
    [InlineData("NASA is launching a spacecraft to explore the Moon.", 0.4, "no_matches", 0)]
    [InlineData("What is the fictional Project Lighthouse plan?", 3.2, "grounded", 1)]
    public async Task RealSdkHybridRequestKeepsAclAndOnlyRelevantSourcesReachModel(
        string query, double score, string status, int sources)
    {
        using var handler = new FixtureTransport(SearchResponse(score));
        using var http = new HttpClient(handler);
        var provider = Provider(http);
        var grounding = await provider.RetrieveAsync(query, Oid, CancellationToken.None);
        Assert.Equal(status, grounding.Status);
        Assert.Equal(sources, grounding.Documents.Count);
        using var request = JsonDocument.Parse(handler.SearchBody!);
        var root = request.RootElement;
        Assert.Equal(query, root.GetProperty("search").GetString());
        Assert.Equal("semantic", root.GetProperty("queryType").GetString());
        Assert.Equal("meeting-semantic", root.GetProperty("semanticConfiguration").GetString());
        Assert.Equal("fail", root.GetProperty("semanticErrorHandling").GetString());
        Assert.Equal($"allowedPrincipalIds/any(p: p eq '{Oid}')", root.GetProperty("filter").GetString());
        Assert.Equal("preFilter", root.GetProperty("vectorFilterMode").GetString());
        Assert.Equal(50, root.GetProperty("top").GetInt32());
        var vector = root.GetProperty("vectorQueries")[0];
        Assert.Equal(50, vector.GetProperty("k").GetInt32());
        Assert.Equal("contentVector", vector.GetProperty("fields").GetString());
        Assert.Equal(1536, vector.GetProperty("vector").GetArrayLength());

        var deltas = new List<string>();
        await foreach (var delta in provider.AnswerAsync([new(query)], grounding, CancellationToken.None)) deltas.Add(delta);
        Assert.Equal(["A conversational response."], deltas);
        Assert.Equal(1, handler.ChatCalls);
        using var chat = JsonDocument.Parse(handler.ChatBody!);
        var messages = chat.RootElement.GetProperty("messages").EnumerateArray()
            .Select(message => message.GetProperty("content").GetString()!).ToArray();
        Assert.Contains(query, messages);
        Assert.Contains("using only the meeting transcript", messages[0]);
        var evidence = messages[^1];
        if (sources == 0) Assert.DoesNotContain(Lighthouse, evidence);
        else Assert.Contains(Lighthouse, evidence);
    }

    [Fact]
    public async Task IntroductionDoesNotTreatAnInterviewQuestionAsVerifiedPersonalExperience()
    {
        using var handler = new FixtureTransport("""{"value":[]}""");
        using var http = new HttpClient(handler);
        const string question = "Introduce yourself and the project you work on. How did you first encounter JMAP?";
        await foreach (var _ in Provider(http).AnswerAsync([new(question)], new("no_matches", []), CancellationToken.None)) { }
        using var body = JsonDocument.Parse(handler.ChatBody!);
        var messages = body.RootElement.GetProperty("messages").EnumerateArray().ToArray();
        var policy = messages[0].GetProperty("content").GetString()!;
        Assert.Contains("other participants' speech, not a verified profile", policy);
        Assert.Contains("NO verified personal profile", policy);
        Assert.Contains("do NOT generate a self-introduction", policy);
        Assert.Contains("presupposition, not evidence", policy);
        Assert.Contains("Never invent the user's name, employer, role, current project, experience", policy);
        Assert.Contains("another speaker's first-person statements", policy);
        Assert.Contains("With missing personal details, ask a brief", policy);
        Assert.Contains(question, messages.Select(message => message.GetProperty("content").GetString()));
        Assert.Equal(1, handler.ChatCalls);
    }

    [Fact]
    public async Task CapsAcceptedSourcesAtFiveButRejectsMissingScoresEvenBeyondCap()
    {
        using var handler = new FixtureTransport(SearchResponse(3, count: 8));
        using var http = new HttpClient(handler);
        Assert.Equal(5, (await Provider(http).RetrieveAsync("Lighthouse plan", Oid, CancellationToken.None)).Documents.Count);
        handler.ResponseJson = SearchResponse(3, count: 8, lastScoreMissing: true);
        var failure = await Assert.ThrowsAsync<ProviderException>(() =>
            Provider(http).RetrieveAsync("Lighthouse plan", Oid, CancellationToken.None));
        Assert.Equal("grounding_unavailable", failure.Code);
    }

    [Theory]
    [InlineData("""{"value":[{"@search.score":100,"title":"Fictional","content":"Text","url":"https://example.test"}]}""")]
    [InlineData("""{"@search.semanticPartialResponseReason":"maxWaitExceeded","value":[]}""")]
    [InlineData("""{"@search.semanticPartialResponseType":"baseResults","value":[]}""")]
    public async Task MissingOrPartialSemanticScoresNeverBecomeGrounded(string response)
    {
        using var handler = new FixtureTransport(response);
        using var http = new HttpClient(handler);
        var failure = await Assert.ThrowsAsync<ProviderException>(() =>
            Provider(http).RetrieveAsync("NASA spacecraft", Oid, CancellationToken.None));
        Assert.Equal("grounding_unavailable", failure.Code);
        Assert.Equal(0, handler.ChatCalls);
    }

    [Theory]
    [InlineData(206)]
    [InlineData(400)]
    public async Task SemanticServiceFailureDoesNotUseBaseResults(int status)
    {
        using var handler = new FixtureTransport(SearchResponse(3), (HttpStatusCode)status);
        using var http = new HttpClient(handler);
        var failure = await Assert.ThrowsAsync<ProviderException>(() =>
            Provider(http).RetrieveAsync("Lighthouse plan", Oid, CancellationToken.None));
        Assert.Equal("grounding_unavailable", failure.Code);
    }

    [Fact]
    public async Task NoMatchesDiscardsUnexpectedDocumentsAndUnavailableNeverInvokesModel()
    {
        using var handler = new FixtureTransport("""{"value":[]}""");
        using var http = new HttpClient(handler);
        var provider = Provider(http);
        var unexpected = new Evidence(Lighthouse, new("Lighthouse", "https://example.test", null));
        await foreach (var _ in provider.AnswerAsync([new("NASA spacecraft")], new("no_matches", [unexpected]), CancellationToken.None)) { }
        Assert.DoesNotContain(Lighthouse, handler.ChatBody!);
        await Assert.ThrowsAsync<ProviderException>(async () =>
        {
            await foreach (var _ in provider.AnswerAsync([new("NASA")], new("unavailable", []), CancellationToken.None)) { }
        });
        Assert.Equal(1, handler.ChatCalls);
    }

    [Fact]
    public async Task NoMatchesStillRequiresSuccessfulModelCallAndDoesNotReturnFixedSuccess()
    {
        using var handler = new FixtureTransport("""{"value":[]}""") { ChatStatus = HttpStatusCode.BadRequest };
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<ClientResultException>(async () =>
        {
            await foreach (var _ in Provider(http).AnswerAsync([new("NASA spacecraft")], new("no_matches", []), CancellationToken.None)) { }
        });
        Assert.Equal(1, handler.ChatCalls);
    }

    [Fact]
    public async Task EmptySemanticResultIsNoMatchesNotGrounded()
    {
        using var handler = new FixtureTransport("""{"value":[]}""");
        using var http = new HttpClient(handler);
        var grounding = await Provider(http).RetrieveAsync("NASA spacecraft", Oid, CancellationToken.None);
        Assert.Equal("no_matches", grounding.Status);
        Assert.Empty(grounding.Documents);
    }

    private static AzureMeetingProvider Provider(HttpClient http)
    {
        var settings = new ServiceSettings { ChatDeployment = "chat", EmbeddingDeployment = "embedding" };
        var openAI = new AzureOpenAIClient(new Uri("https://example.openai.azure.com"),
            new ApiKeyCredential("test-only"), new AzureOpenAIClientOptions { Transport = new HttpClientPipelineTransport(http) });
        var search = new SearchClient(new Uri("https://example.search.windows.net"), "meeting", new AzureKeyCredential("test-only"),
            new SearchClientOptions { Transport = new HttpClientTransport(http) });
        return new(settings, openAI, search);
    }

    private static string SearchResponse(double score, int count = 1, bool lastScoreMissing = false)
    {
        var values = Enumerable.Range(0, count).Select(index =>
        {
            var document = new Dictionary<string, object?>
            {
                ["@search.score"] = 100, ["title"] = "Fictional Lighthouse", ["content"] = Lighthouse,
                ["url"] = $"https://example.test/{index}", ["updatedAt"] = "2026-09-20T00:00:00Z"
            };
            if (!lastScoreMissing || index != count - 1) document["@search.rerankerScore"] = score;
            return document;
        });
        return JsonSerializer.Serialize(new { value = values });
    }

    private sealed class FixtureTransport(string responseJson, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public string ResponseJson { get; set; } = responseJson;
        public string? SearchBody { get; private set; }
        public string? ChatBody { get; private set; }
        public int ChatCalls { get; private set; }
        public HttpStatusCode ChatStatus { get; init; } = HttpStatusCode.OK;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            if (request.RequestUri!.Host.Contains("search.windows.net", StringComparison.Ordinal))
            {
                SearchBody = body;
                return Json(ResponseJson, status);
            }
            if (request.RequestUri.AbsolutePath.EndsWith("/embeddings", StringComparison.Ordinal))
                return Json(JsonSerializer.Serialize(new
                {
                    @object = "list", model = "embedding",
                    data = new[] { new { @object = "embedding", index = 0, embedding = Convert.ToBase64String(new byte[1536 * sizeof(float)]) } },
                    usage = new { prompt_tokens = 1, total_tokens = 1 }
                }));
            ChatCalls++;
            ChatBody = body;
            if (ChatStatus != HttpStatusCode.OK)
                return Json("""{"error":{"code":"invalid_request_error","message":"Synthetic model failure"}}""", ChatStatus);
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    data: {"id":"test","object":"chat.completion.chunk","created":1,"model":"test","choices":[{"index":0,"delta":{"content":"A conversational response."},"finish_reason":null}]}

                    data: [DONE]


                    """, Encoding.UTF8, "text/event-stream")
            };
        }
        private static HttpResponseMessage Json(string value, HttpStatusCode code = HttpStatusCode.OK) =>
            new(code) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    }
}
