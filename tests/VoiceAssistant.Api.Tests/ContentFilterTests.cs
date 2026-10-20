using System.Net;
using VoiceAssistant.Api;
using Xunit;

namespace VoiceAssistant.Api.Tests;

/// <summary>Azure OpenAI's content filter stopping a streamed live reply (<c>finish_reason: "content_filter"</c>).</summary>
public sealed class ContentFilterTests
{
    /// <summary>The documented asynchronous-filter verdict that stops a stream: an annotation without a delta.</summary>
    internal const string AsyncVerdict = """{"id":"","object":"","created":0,"model":"","choices":[{"index":0,"finish_reason":"content_filter","content_filter_results":{"protected_material_text":{"detected":true,"filtered":true}},"content_filter_offsets":{"check_offset":65,"start_offset":65,"end_offset":1056}}],"usage":null}""";
    /// <summary>A default-mode stop: the verdict on an empty delta of the completion itself.</summary>
    internal const string Verdict = """{"id":"test","object":"chat.completion.chunk","created":1,"model":"test","choices":[{"index":0,"delta":{},"finish_reason":"content_filter","content_filter_results":{"violence":{"filtered":true,"severity":"medium"}}}]}""";
    internal const string Stop = """{"id":"test","object":"chat.completion.chunk","created":1,"model":"test","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}""";
    private static readonly ConversationTurn[] Question = [new("What is the delivery plan for the rollout?")];

    public static IEnumerable<object[]> Stops()
    {
        yield return [new[] { Verdict }];
        yield return [new[] { AsyncVerdict }];
        // Asynchronous filtering can still annotate text after the completion reported "stop".
        yield return [new[] { Stop, AsyncVerdict }];
    }

    [Theory]
    [MemberData(nameof(Stops))]
    public async Task AStreamTheFilterStopsFailsAsResponseFilteredAfterItsStreamedText(string[] trailer)
    {
        using var transport = new FaultTransport { ChatChunks = ["Synthetic primary", " line.\nSynthetic alternative."], ChatTrailer = trailer };
        using var http = new HttpClient(transport);
        var provider = FaultTransport.Provider(http);
        var streamed = "";
        var failure = await Assert.ThrowsAsync<ProviderException>(async () =>
        {
            await foreach (var update in provider.AnswerWithSuggestionsAsync(Question, new Grounding("disabled", []), SessionOptions.Legacy,
                "transcript", CancellationToken.None))
                streamed += update.Text;
        });
        Assert.Equal("response_filtered", failure.Code);
        Assert.Equal(AzureMeetingProvider.FilteredMessage, failure.Message);
        // The primary line had streamed before the verdict arrived, as it can with asynchronous filtering.
        Assert.Equal("Synthetic primary line.", streamed);
    }

    [Fact]
    public async Task TheLegacyAnswerStreamStopsTheSameWay()
    {
        using var transport = new FaultTransport { ChatChunks = ["Synthetic answer."], ChatTrailer = [AsyncVerdict] };
        using var http = new HttpClient(transport);
        var failure = await Assert.ThrowsAsync<ProviderException>(async () =>
        {
            await foreach (var _ in FaultTransport.Provider(http).AnswerAsync(Question, new Grounding("disabled", []), CancellationToken.None)) { }
        });
        Assert.Equal("response_filtered", failure.Code);
    }

    [Fact]
    public async Task AStreamThatStopsNormallyStillCompletes()
    {
        using var transport = new FaultTransport { ChatTrailer = [Stop] };
        using var http = new HttpClient(transport);
        var updates = new List<ReplyUpdate>();
        await foreach (var update in FaultTransport.Provider(http).AnswerWithSuggestionsAsync(Question, new Grounding("disabled", []),
            SessionOptions.Legacy, "transcript", CancellationToken.None))
            updates.Add(update);
        Assert.Equal("A general reply for now.", string.Concat(updates.Select(update => update.Text)));
        Assert.Equal("The exact details still need to be confirmed.", updates[^1].Alternative);
    }
}

public sealed partial class ApiTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AReplyTheContentFilterStopsIsWithdrawnWithoutCompletionTranslationOrLoggedText(bool searchFails)
    {
        const string question = "What is our delivery plan for the rollout?";
        var logs = new CollectingLogs();
        using var capture = new MetricCapture();
        using var transport = new FaultTransport
        {
            ChatChunks = ["Withheld synthetic", " primary line.\nWithheld synthetic alternative."],
            ChatTrailer = [ContentFilterTests.Stop, ContentFilterTests.AsyncVerdict]
        };
        if (searchFails) transport.Search(HttpStatusCode.ServiceUnavailable);
        using var http = new HttpClient(transport);
        var provider = new AzureBackedProvider(FaultTransport.Provider(http));
        await using var host = await Host.StartAsync(provider: provider, logs: logs);
        using var socket = await host.ConnectAsync();
        await Send(socket, WithOptions(new { responseMode = "grounded", replyKorean = true }));
        Assert.Equal("session.ready", TypeOf(await Receive(socket)));

        provider.Emit(new("question", 1, question, true));
        var messages = await ReceiveUntilAsync(socket, found => found.Any(message => TypeOf(message) == "response.cancelled"));
        await Send(socket, """{"type":"session.stop"}""");
        messages.AddRange(await ReceiveUntilCloseAsync(socket));

        // The primary line had already streamed; then exactly the non-fatal error and the cancellation of that same reply.
        var filtered = messages.FindIndex(message => TypeOf(message) == "error" && message.GetProperty("code").GetString() == "response_filtered");
        Assert.InRange(messages.FindIndex(message => TypeOf(message) == "response.delta"), 0, filtered - 1);
        Assert.True(messages[filtered].GetProperty("retryable").GetBoolean());
        Assert.Equal(AzureMeetingProvider.FilteredMessage, messages[filtered].GetProperty("message").GetString());
        Assert.Equal("response.cancelled", TypeOf(messages[filtered + 1]));
        Assert.Equal(messages.Single(message => TypeOf(message) == "response.started").GetProperty("responseId").GetString(),
            messages[filtered + 1].GetProperty("responseId").GetString());
        // An earlier search failure stays the only other error, and its fixed completion never replaces the withdrawal.
        string[] errors = searchFails ? ["grounding_unavailable", "response_filtered"] : ["response_filtered"];
        Assert.Equal(errors, messages.Where(message => TypeOf(message) == "error").Select(message => message.GetProperty("code").GetString()));
        Assert.DoesNotContain(messages, message => TypeOf(message) is "response.completed" or "response.korean");
        Assert.DoesNotContain(transport.ChatBodies, body => body.Contains("You translate one English line", StringComparison.Ordinal));

        Assert.Contains(capture.Named("voiceassistant.session.errors"), measurement => measurement.Tags.Contains(new("code", "response_filtered")));
        Assert.Contains(logs.Messages, message => message.Contains("Meeting response withdrawn: code response_filtered, generation ", StringComparison.Ordinal) &&
            message.EndsWith($", after search failure {searchFails}.", StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Messages, message => message.Contains("Withheld synthetic", StringComparison.Ordinal) ||
            message.Contains(question, StringComparison.Ordinal) || message.Contains(AzureMeetingProvider.FilteredMessage, StringComparison.Ordinal));
    }
}
