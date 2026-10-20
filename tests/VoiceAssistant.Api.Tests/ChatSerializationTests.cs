using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using Azure.AI.OpenAI;
using OpenAI.Chat;
using VoiceAssistant.Api;
using Xunit;

namespace VoiceAssistant.Api.Tests;

public sealed class ChatSerializationTests
{
    [Fact]
    public async Task TwoGroundedSuggestionsUseOneActualAzureRequestAndStreamOnlyPrimary()
    {
        var chunks = new[] { "We can use ", "JMAP for push updates.", "\n", "JMAP lets us ", "receive updates as they happen." };
        var sse = string.Concat(chunks.Select(text => "data: " + JsonSerializer.Serialize(new
        {
            id = "test", @object = "chat.completion.chunk", created = 1, model = "test",
            choices = new[] { new { index = 0, delta = new { content = text }, finish_reason = (string?)null } }
        }) + "\n\n")) + "data: [DONE]\n\n";
        using var handler = new CaptureHandler { ResponseBody = sse };
        using var http = new HttpClient(handler);
        var client = new AzureOpenAIClient(new Uri("https://example.openai.azure.com"), new ApiKeyCredential("test-only-key"),
            new AzureOpenAIClientOptions { Transport = new HttpClientPipelineTransport(http) });
        var provider = new AzureMeetingProvider(new ServiceSettings { ChatDeployment = "test" }, client, null);
        var updates = new List<ReplyUpdate>();
        await foreach (var update in provider.AnswerWithSuggestionsAsync(
            [new("Our rollout uses JMAP."), new("How does it help?")],
            new("grounded", [new("JMAP supports push updates.", new("Notes", "https://example.test/notes", null))]),
            SessionOptions.Legacy, "knowledge", CancellationToken.None))
            updates.Add(update);
        Assert.Equal(1, handler.Requests);
        Assert.Equal("We can use JMAP for push updates.", string.Concat(updates.Select(update => update.Text)));
        Assert.Equal("JMAP lets us receive updates as they happen.", updates.Last().Alternative);
        Assert.All(updates, update => Assert.DoesNotContain('\n', update.Text));
        Assert.Contains("JMAP supports push updates.", handler.RequestBody);
        Assert.Contains("TWO alternative English replies", handler.RequestBody);
        Assert.Contains("SAME supplied evidence", handler.RequestBody);
        Assert.Contains("DIFFERENT conversational", handler.RequestBody);
        Assert.Contains("NOT simply paraphrase", handler.RequestBody);
        Assert.Contains("ONE unit of meaning", handler.RequestBody);
        Assert.Contains("Missing documents alone", handler.RequestBody);
        Assert.Contains("follow-up question", handler.RequestBody);
        Assert.Contains("Korean-speaking IT engineer", handler.RequestBody);
        Assert.Contains("CEFR A2-B1", handler.RequestBody);
        Assert.Contains("at most 18 words", handler.RequestBody);
        Assert.Contains("preserve every compared number", handler.RequestBody);
        Assert.Contains("Never convert one or estimate an equivalent", handler.RequestBody);
        Assert.Contains("Answer every material clause", handler.RequestBody);
        Assert.Contains("use up to 24 words across both", handler.RequestBody);
        Assert.Contains("complete the evidence or uncertainty", handler.RequestBody);
        Assert.Contains("several material clauses", handler.RequestBody);
        Assert.DoesNotContain("three or more specific numbers", handler.RequestBody);
        Assert.Contains("missing detail is not in the material or needs verification", handler.RequestBody);
        Assert.Contains("Do not silently skip that clause", handler.RequestBody);
        Assert.Contains("unknown commitment, not ambiguity", handler.RequestBody);
        Assert.Contains("CURRENT RESPONSE TARGET", handler.RequestBody);
        Assert.Contains("OLDER CONTEXT", handler.RequestBody);
        Assert.Contains("Never substitute an answer to older context", handler.RequestBody);
        Assert.Contains("cover every named option in the primary reply", handler.RequestBody);
        Assert.Contains("silently check the primary reply against every material clause and named option", handler.RequestBody);
        Assert.Contains("The alternative does not count toward the primary reply", handler.RequestBody);
        Assert.Contains("Untrusted response focus", handler.RequestBody);
        Assert.Contains("independently safe and useful", handler.RequestBody);
        Assert.Contains("never use the alternative to defer required information", handler.RequestBody);
        Assert.Contains("How does it help?", handler.RequestBody);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("minimal")]
    [InlineData("low")]
    [InlineData("medium")]
    public async Task ActualAzureClientSerializesModernCompletionBudgetAndOnlyAConfiguredReasoningEffort(string? effort)
    {
        using var handler = new CaptureHandler();
        using var http = new HttpClient(handler);
        var client = new AzureOpenAIClient(new Uri("https://example.openai.azure.com"), new ApiKeyCredential("test-only-key"),
            new AzureOpenAIClientOptions { Transport = new HttpClientPipelineTransport(http) });
        var options = AzureMeetingProvider.CreateChatOptions(new ServiceSettings { ChatMaxOutputTokens = 2048 }, effort);
        await foreach (var _ in client.GetChatClient("test-deployment").CompleteChatStreamingAsync(
            [new UserChatMessage("Original synthetic test prompt.")], options)) { }
        using var json = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal(2048, json.RootElement.GetProperty("max_completion_tokens").GetInt32());
        Assert.False(json.RootElement.TryGetProperty("max_tokens", out _));
        Assert.False(json.RootElement.TryGetProperty("temperature", out _));
        // Unset sends nothing, so the deployment default applies exactly as before; a configured effort is sent as-is.
        if (effort is null) Assert.False(json.RootElement.TryGetProperty("reasoning_effort", out _));
        else Assert.Equal(effort, json.RootElement.GetProperty("reasoning_effort").GetString());
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public string? RequestBody { get; private set; }
        public string ResponseBody { get; init; } = "data: [DONE]\n\n";
        public int Requests { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            Requests++;
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(ResponseBody, Encoding.UTF8, "text/event-stream")
            };
        }
    }
}
