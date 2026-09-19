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
    public async Task ActualAzureClientSerializesModernCompletionBudgetWithoutLegacyTokensOrTemperature()
    {
        using var handler = new CaptureHandler();
        using var http = new HttpClient(handler);
        var client = new AzureOpenAIClient(new Uri("https://example.openai.azure.com"), new ApiKeyCredential("test-only-key"),
            new AzureOpenAIClientOptions { Transport = new HttpClientPipelineTransport(http) });
        var options = AzureMeetingProvider.CreateChatOptions(new ServiceSettings { ChatMaxOutputTokens = 2048 });
        await foreach (var _ in client.GetChatClient("test-deployment").CompleteChatStreamingAsync(
            [new UserChatMessage("Original synthetic test prompt.")], options)) { }
        using var json = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal(2048, json.RootElement.GetProperty("max_completion_tokens").GetInt32());
        Assert.False(json.RootElement.TryGetProperty("max_tokens", out _));
        Assert.False(json.RootElement.TryGetProperty("temperature", out _));
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public string? RequestBody { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent("data: [DONE]\n\n", Encoding.UTF8, "text/event-stream")
            };
        }
    }
}
