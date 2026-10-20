using System.ClientModel;
using System.ClientModel.Primitives;
using System.Diagnostics;
using System.Net;
using VoiceAssistant.Api;
using Xunit;

namespace VoiceAssistant.Api.Tests;

public sealed class LiveChatPolicyTests
{
    private static readonly ConversationTurn[] Question = [new("What is the delivery plan for the rollout?")];
    /// <summary>Far below the 20 s the service asked for, and below the SDK default's three waits.</summary>
    private static readonly TimeSpan FailFast = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task ThrottledAnswerWithALongRetryAfterFailsAtOnce()
    {
        using var capture = new MetricCapture();
        using var transport = new FaultTransport().Chat(HttpStatusCode.TooManyRequests, retryAfter: "20");
        using var http = new HttpClient(transport);
        var started = Stopwatch.GetTimestamp();

        var failure = await Assert.ThrowsAsync<ClientResultException>(() => AnswerAsync(FaultTransport.Provider(http)));

        Assert.Equal(429, failure.Status);
        Assert.InRange(Stopwatch.GetElapsedTime(started), TimeSpan.Zero, FailFast);
        Assert.Equal(1, transport.ChatCalls);
        Assert.Contains(capture.Named("voiceassistant.chat.throttled"), measurement => measurement.Tags.Contains(new("operation", "answer")));
        Assert.Empty(capture.Named("voiceassistant.chat.retries"));
    }

    [Fact]
    public async Task ThrottledAnswerAskingForAShortWaitIsRetriedOnceAndStreams()
    {
        using var capture = new MetricCapture();
        using var transport = new FaultTransport().Chat(HttpStatusCode.TooManyRequests, retryAfterMs: "0").Chat(HttpStatusCode.OK);
        using var http = new HttpClient(transport);

        Assert.Equal("A general reply for now.", await AnswerAsync(FaultTransport.Provider(http)));

        Assert.Equal(2, transport.ChatCalls);
        var retry = Assert.Single(capture.Named("voiceassistant.chat.retries"));
        Assert.Equal(new KeyValuePair<string, object?>[] { new("operation", "answer") }, retry.Tags);
        Assert.Single(capture.Named("voiceassistant.chat.throttled"));
    }

    [Fact]
    public async Task ALiveCallIsRetriedAtMostOnce()
    {
        using var transport = new FaultTransport().Chat(HttpStatusCode.ServiceUnavailable, retryAfterMs: "0");
        using var http = new HttpClient(transport);

        var failure = await Assert.ThrowsAsync<ClientResultException>(() => AnswerAsync(FaultTransport.Provider(http)));

        Assert.Equal(503, failure.Status);
        Assert.Equal(2, transport.ChatCalls);
    }

    [Fact]
    public async Task ClassifierAndTranslationAlsoFailFastOnALongRetryAfter()
    {
        using var capture = new MetricCapture();
        using var transport = new FaultTransport { ChatStatus = HttpStatusCode.TooManyRequests, ChatRetryAfter = "20" };
        using var http = new HttpClient(transport);
        var provider = FaultTransport.Provider(http);
        var started = Stopwatch.GetTimestamp();

        Assert.Equal(429, (await Assert.ThrowsAsync<ClientResultException>(() => provider.ShouldRespondAsync(Question, CancellationToken.None))).Status);
        Assert.Equal(429, (await Assert.ThrowsAsync<ClientResultException>(() =>
            provider.TranslateReplyAsync("Let's check the next step.", TranslationContext.Empty, CancellationToken.None))).Status);

        Assert.InRange(Stopwatch.GetElapsedTime(started), TimeSpan.Zero, FailFast);
        Assert.Equal(2, transport.ChatCalls);
        var operations = capture.Named("voiceassistant.chat.throttled").Select(measurement => (string?)measurement.Tags.Single().Value).ToArray();
        Assert.Equal(new[] { "classifier", "translation" }, operations);
    }

    [Fact]
    public async Task RetryHappensOnlyWhenTheDeadlineLeavesTwoSecondsAfterTheWait()
    {
        var clock = new ManualClock();
        using var transport = new FaultTransport()
            .Chat(HttpStatusCode.ServiceUnavailable).Chat(HttpStatusCode.ServiceUnavailable).Chat(HttpStatusCode.OK);
        using var http = new HttpClient(transport);
        var pipeline = ClientPipeline.Create(new ClientPipelineOptions
        {
            Transport = new HttpClientPipelineTransport(http), RetryPolicy = new LiveChatRetryPolicy(clock)
        });

        // 1.5 s left: even the default 250 ms pause would leave less than 2 s, so the failure surfaces at once.
        using (LiveChatCall.Begin("answer", TimeSpan.FromMilliseconds(1500), clock))
            Assert.Equal(503, await SendAsync(pipeline));
        Assert.Equal(1, transport.ChatCalls);

        // 10 s left: one retry, after the default pause measured on the injected clock.
        using (LiveChatCall.Begin("answer", TimeSpan.FromSeconds(10), clock))
        {
            var timers = clock.TimersCreated;
            var sending = SendAsync(pipeline);
            await clock.WaitForTimersAsync(timers + 1);
            Assert.Equal(2, transport.ChatCalls);
            Assert.False(sending.IsCompleted);
            clock.Advance(LiveChatRetryPolicy.DefaultRetryDelay);
            Assert.Equal(200, await sending);
        }
        Assert.Equal(3, transport.ChatCalls);
    }

    [Theory]
    [InlineData("retry-after-ms", "250", 250)]
    [InlineData("x-ms-retry-after-ms", "1500", 1500)]
    [InlineData("Retry-After", "20", 20000)]
    [InlineData("Retry-After", "Thu, 01 Jan 2026 00:00:30 GMT", 30000)]
    public async Task TheServiceRequestedWaitIsReadFromItsHeaders(string header, string value, int milliseconds)
    {
        using var client = new HttpClient(new HeaderEcho(header, value));
        using var transport = new HttpClientPipelineTransport(client);
        using var message = transport.CreateMessage();
        message.Request.Method = "GET";
        message.Request.Uri = new Uri("https://example.openai.azure.com/");
        await transport.ProcessAsync(message);
        // ManualClock starts at 2026-01-01T00:00:00Z, so the date form is 30 s ahead.
        Assert.Equal(TimeSpan.FromMilliseconds(milliseconds), new LiveChatRetryPolicy(new ManualClock()).ServerDelay(message.Response));
    }

    private static async Task<string> AnswerAsync(AzureMeetingProvider provider)
    {
        var text = "";
        await foreach (var update in provider.AnswerWithSuggestionsAsync(Question, new Grounding("disabled", []), SessionOptions.Legacy,
            "transcript", CancellationToken.None))
            text += update.Text;
        return text;
    }

    private static async Task<int> SendAsync(ClientPipeline pipeline)
    {
        using var message = pipeline.CreateMessage();
        message.Request.Method = "POST";
        message.Request.Uri = new Uri("https://example.openai.azure.com/openai/deployments/chat/chat/completions");
        message.Request.Content = BinaryContent.Create(BinaryData.FromString("{}"));
        await pipeline.SendAsync(message);
        return message.Response!.Status;
    }

    private sealed class HeaderEcho(string header, string value) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("{}") };
            response.Headers.TryAddWithoutValidation(header, value);
            return Task.FromResult(response);
        }
    }
}

public sealed partial class ApiTests
{
    [Fact]
    public async Task ThrottledLiveAnswerShowsModelBusyAtOnceInsteadOfWaitingOutRetryAfter()
    {
        using var transport = new FaultTransport { ChatStatus = HttpStatusCode.TooManyRequests, ChatRetryAfter = "20" };
        using var http = new HttpClient(transport);
        var provider = new AzureBackedProvider(FaultTransport.Provider(http));
        await using var host = await Host.StartAsync(provider: provider);
        using var socket = await host.ConnectAsync();
        await Send(socket, WithOptions(new { responseMode = "grounded" }));
        Assert.Equal("session.ready", TypeOf(await Receive(socket)));
        var started = Stopwatch.GetTimestamp();

        provider.Emit(new("question", 1, "What is our delivery plan for the rollout?", true));
        var messages = await ReceiveUntilAsync(socket, found => found.Any(message => TypeOf(message) == "response.cancelled"));

        Assert.InRange(Stopwatch.GetElapsedTime(started), TimeSpan.Zero, TimeSpan.FromSeconds(5));
        var error = Assert.Single(messages, message => TypeOf(message) == "error");
        Assert.Equal("model_busy", error.GetProperty("code").GetString());
        Assert.True(error.GetProperty("retryable").GetBoolean());
        Assert.DoesNotContain(messages, message => TypeOf(message) == "response.completed");
        // The answer request was sent once: no retry after a 20-second Retry-After.
        Assert.Single(transport.ChatBodies, body => body.Contains("\"stream\":true", StringComparison.Ordinal));
        await Send(socket, """{"type":"session.stop"}""");
    }
}
