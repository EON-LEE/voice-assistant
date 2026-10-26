using System.ClientModel;
using System.ClientModel.Primitives;
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Azure.AI.OpenAI;
using Microsoft.Extensions.Configuration;
using OpenAI.Chat;
using VoiceAssistant.Api;
using Xunit;

namespace VoiceAssistant.Api.Tests;

public sealed class ReasoningEffortTests
{
    private static readonly ConversationTurn[] Question = [new("What is the delivery plan for the rollout?")];

    private static IConfiguration Configuration(string key, string? value) => new ConfigurationBuilder()
        .AddInMemoryCollection([new KeyValuePair<string, string?>(key, value)]).Build();

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("  ", null)]
    [InlineData("minimal", "minimal")]
    [InlineData(" Low ", "low")]
    [InlineData("MEDIUM", "medium")]
    public void ReasoningEffortIsUnsetOrOneOfTheSupportedValues(string? value, string? expected) =>
        Assert.Equal(expected, ServiceSettings.ReadReasoningEffort(Configuration("Azure:ChatReasoningEffort", value), "Azure:ChatReasoningEffort"));

    [Theory]
    [InlineData("high")]
    [InlineData("none")]
    [InlineData("fast")]
    public void AnUnsupportedReasoningEffortPreventsStartupInsteadOfBeingIgnored(string value)
    {
        var failure = Assert.Throws<InvalidOperationException>(() =>
            ServiceSettings.ReadReasoningEffort(Configuration("Azure:ChatAuxiliaryReasoningEffort", value), "Azure:ChatAuxiliaryReasoningEffort"));
        Assert.Contains("Azure:ChatAuxiliaryReasoningEffort", failure.Message);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("2025-04-01-preview", "2025-04-01-preview")]
    [InlineData(" 2024-10-21 ", "2024-10-21")]
    public void ChatApiVersionIsUnsetOrADatedVersion(string? value, string? expected) =>
        Assert.Equal(expected, ServiceSettings.ReadApiVersion(Configuration("Azure:ChatApiVersion", value)));

    [Theory]
    [InlineData("latest")]
    [InlineData("v1")]
    [InlineData("2025-04-01&x=1")]
    public void AMalformedChatApiVersionPreventsStartup(string value) =>
        Assert.Throws<InvalidOperationException>(() => ServiceSettings.ReadApiVersion(Configuration("Azure:ChatApiVersion", value)));

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData(" meeting-answer ", "meeting-answer")]
    [InlineData("gpt-5.6_terra", "gpt-5.6_terra")]
    public void TheAnswerDeploymentIsUnsetOrADeploymentName(string? value, string? expected) =>
        Assert.Equal(expected, ServiceSettings.ReadDeploymentName(Configuration("Azure:AnswerDeployment", value), "Azure:AnswerDeployment"));

    [Theory]
    [InlineData("meeting answer")]
    [InlineData("../chat")]
    [InlineData("-answer")]
    [InlineData("answer?api-version=1")]
    public void AMalformedAnswerDeploymentPreventsStartup(string value)
    {
        var failure = Assert.Throws<InvalidOperationException>(() =>
            ServiceSettings.ReadDeploymentName(Configuration("Azure:AnswerDeployment", value), "Azure:AnswerDeployment"));
        Assert.Contains("Azure:AnswerDeployment", failure.Message);
    }

    [Fact]
    public async Task OnlyTheAnswerGoesToTheAnswerDeploymentWithTheAnswerEffort()
    {
        using var transport = new FaultTransport();
        using var http = new HttpClient(transport);
        var provider = FaultTransport.Provider(http, new ServiceSettings
        {
            ChatDeployment = "chat", AnswerDeployment = "answer", EmbeddingDeployment = "embedding", ChatReasoningEffort = "low"
        });

        await AnswerAsync(provider);
        await provider.ShouldRespondAsync(Question, CancellationToken.None);
        await provider.TranslateReplyAsync("Let's check the next step.", TranslationContext.Empty, CancellationToken.None);

        var calls = transport.ChatBodies.Zip(transport.ChatPaths, (body, path) =>
        {
            using var json = JsonDocument.Parse(body);
            var streamed = json.RootElement.TryGetProperty("stream", out var stream) && stream.GetBoolean();
            var effort = json.RootElement.TryGetProperty("reasoning_effort", out var value) ? value.GetString() : null;
            return (Streamed: streamed, Path: path, Effort: effort);
        }).ToArray();
        var answer = Assert.Single(calls, call => call.Streamed);
        Assert.Contains("/deployments/answer/", answer.Path);
        Assert.Equal("low", answer.Effort);
        // The turn classifier and the Korean translation (tried twice, because the fake does not answer in Korean) stay on the
        // chat deployment, without the answer's effort.
        var auxiliary = calls.Where(call => !call.Streamed).ToArray();
        Assert.True(auxiliary.Length >= 2);
        Assert.All(auxiliary, call =>
        {
            Assert.Contains("/deployments/chat/", call.Path);
            Assert.Null(call.Effort);
        });
    }

    [Fact]
    public async Task WithoutAnAnswerDeploymentTheAnswerUsesTheChatDeployment()
    {
        using var transport = new FaultTransport();
        using var http = new HttpClient(transport);
        var provider = FaultTransport.Provider(http);

        await AnswerAsync(provider);

        Assert.Contains("/deployments/chat/", Assert.Single(transport.ChatPaths));
    }

    [Fact]
    public async Task TheAnswerAndTheAuxiliaryCallsEachSendTheirOwnEffort()
    {
        using var transport = new FaultTransport();
        using var http = new HttpClient(transport);
        var provider = FaultTransport.Provider(http, new ServiceSettings
        {
            ChatDeployment = "chat", EmbeddingDeployment = "embedding", ChatReasoningEffort = "minimal", ChatAuxiliaryReasoningEffort = "low"
        });

        await AnswerAsync(provider);
        await provider.ShouldRespondAsync(Question, CancellationToken.None);
        await provider.TranslateReplyAsync("Let's check the next step.", TranslationContext.Empty, CancellationToken.None);

        var efforts = transport.ChatBodies.Select(body =>
        {
            using var json = JsonDocument.Parse(body);
            var streamed = json.RootElement.TryGetProperty("stream", out var stream) && stream.GetBoolean();
            return (Streamed: streamed, Effort: json.RootElement.GetProperty("reasoning_effort").GetString());
        }).ToArray();
        Assert.Equal("minimal", Assert.Single(efforts, effort => effort.Streamed).Effort);
        // The classifier and the (twice-tried, because the fake does not answer in Korean) translation use the auxiliary effort.
        Assert.All(efforts.Where(effort => !effort.Streamed), effort => Assert.Equal("low", effort.Effort));
        Assert.True(efforts.Count(effort => !effort.Streamed) >= 2);
    }

    [Fact]
    public async Task UnsetEffortsSendNoReasoningEffortAtAll()
    {
        using var transport = new FaultTransport();
        using var http = new HttpClient(transport);
        var provider = FaultTransport.Provider(http);

        await AnswerAsync(provider);
        await provider.ShouldRespondAsync(Question, CancellationToken.None);

        Assert.NotEmpty(transport.ChatBodies);
        Assert.All(transport.ChatBodies, body => Assert.DoesNotContain("reasoning_effort", body));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheCommitmentPolicyInstructionIsSentOnlyWhenSwitchedOn(bool switchedOn)
    {
        using var transport = new FaultTransport();
        using var http = new HttpClient(transport);
        var provider = FaultTransport.Provider(http, new ServiceSettings
        {
            ChatDeployment = "chat", EmbeddingDeployment = "embedding", CommitmentPolicy = switchedOn
        });
        var policy = new Grounding("grounded",
            [new("Standard delivery takes ten business days.", new("Fictional delivery policy", "https://example.test/policy", null))]);

        await foreach (var _ in provider.AnswerWithSuggestionsAsync([new("Can you guarantee delivery by Friday?")], policy,
            SessionOptions.Legacy, "knowledge", CancellationToken.None)) { }

        var answer = Assert.Single(transport.ChatBodies, body => body.Contains("\"stream\":true", StringComparison.Ordinal));
        Assert.Equal(switchedOn, answer.Contains("do not promise or agree on their behalf", StringComparison.Ordinal));
        Assert.Equal(switchedOn, answer.Contains("state it exactly as written instead", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("2025-04-01-preview")]
    public async Task AConfiguredApiVersionReplacesTheSdkDefaultOnLiveChatRequests(string? version)
    {
        using var handler = new UriCapture();
        using var http = new HttpClient(handler);
        var options = AzureMeetingProvider.LiveChatOptions(apiVersion: version);
        options.Transport = new HttpClientPipelineTransport(http);
        var client = new AzureOpenAIClient(new Uri("https://example.openai.azure.com"), new ApiKeyCredential("test-only"), options);

        await client.GetChatClient("chat").CompleteChatAsync([new UserChatMessage("Original synthetic test prompt.")]);

        var query = Assert.Single(handler.Uris).Query;
        var versions = query.TrimStart('?').Split('&').Where(part => part.StartsWith("api-version=", StringComparison.Ordinal)).ToArray();
        var sent = Assert.Single(versions)["api-version=".Length..];
        // Unset keeps the pinned SDK's default api-version.
        Assert.Equal(version ?? "2024-10-21", sent);
    }

    private static async Task AnswerAsync(AzureMeetingProvider provider)
    {
        await foreach (var _ in provider.AnswerWithSuggestionsAsync(Question, new Grounding("disabled", []), SessionOptions.Legacy,
            "transcript", CancellationToken.None)) { }
    }

    private sealed class UriCapture : HttpMessageHandler
    {
        public ConcurrentQueue<Uri> Uris { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uris.Enqueue(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"id":"test","object":"chat.completion","created":1,"model":"test","choices":[{"index":0,"message":{"role":"assistant","content":"OK"},"finish_reason":"stop"}]}""",
                    Encoding.UTF8, "application/json")
            });
        }
    }
}

public sealed partial class ApiTests
{
    [Theory]
    [InlineData("Azure:ChatReasoningEffort", "high")]
    [InlineData("Azure:ChatAuxiliaryReasoningEffort", "fast")]
    [InlineData("Azure:ChatApiVersion", "latest")]
    [InlineData("Azure:AnswerDeployment", "bad name")]
    [InlineData("Speech:MaterialPhraseHints", "on")]
    [InlineData("Reply:CommitmentPolicy", "maybe")]
    public async Task AnUnsupportedLiveChatSwitchValueStopsTheAppFromStarting(string key, string value) =>
        await Assert.ThrowsAsync<InvalidOperationException>(() => Host.StartAsync(azure: true, configuration: new() { [key] = value }));
}
