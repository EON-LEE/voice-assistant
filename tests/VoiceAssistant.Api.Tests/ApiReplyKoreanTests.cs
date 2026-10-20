using System.Net.WebSockets;
using System.Text.Json;
using VoiceAssistant.Api;
using Xunit;

namespace VoiceAssistant.Api.Tests;

public sealed partial class ApiTests
{
    private const string KoreanQuestion = "Can we deliver it to our customer tomorrow?";
    private const string KoreanPrimaryReply = "Here is a concise response grounded only in the supplied context.";
    private const string KoreanAlternative = "Let's check the next step.";

    private static string TypeOf(JsonElement message) => message.GetProperty("type").GetString()!;
    private static bool IsKorean(JsonElement message, int index) =>
        TypeOf(message) == "response.korean" && message.GetProperty("index").GetInt32() == index;
    private static bool Completed(List<JsonElement> messages) => messages.Any(message => TypeOf(message) == "response.completed");
    private static Task<string?> Prefixed(string english, TranslationContext context, CancellationToken cancellation) =>
        Task.FromResult<string?>("한국어: " + english);

    private static async Task<List<JsonElement>> ReceiveUntilAsync(ClientWebSocket socket, Func<List<JsonElement>, bool> done)
    {
        var messages = new List<JsonElement>();
        while (!done(messages))
        {
            if (messages.Count >= 60) throw new InvalidOperationException("Expected events were not received.");
            messages.Add(await Receive(socket));
        }
        return messages;
    }

    private static async Task<List<JsonElement>> ReceiveUntilCloseAsync(ClientWebSocket socket)
    {
        var messages = new List<JsonElement>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var buffer = new byte[32768];
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, timeout.Token);
            if (result.MessageType == WebSocketMessageType.Close) return messages;
            using var json = JsonDocument.Parse(buffer.AsMemory(0, result.Count));
            messages.Add(json.RootElement.Clone());
        }
    }

    [Fact]
    public async Task OptedInClientGetsKoreanForEachLineOnlyOnceTheStreamHasEnded()
    {
        var alternative = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new ControlledProvider
        {
            AutomaticRetrieval = true, Alternative = KoreanAlternative, HoldAlternative = alternative, Translate = Prefixed
        };
        await using var host = await Host.StartAsync(provider: provider);
        using var socket = await host.ConnectAsync();
        await Send(socket, WithOptions(new { responseMode = "grounded", replySettleMs = 300, replyKorean = true }));
        var ready = await Receive(socket);
        Assert.True(ready.GetProperty("replyKorean").GetBoolean());

        provider.Emit(new("q", 1, KoreanQuestion, true));
        // The primary line is complete and shown, but the content filter can still stop the stream: nothing is translated yet.
        var streaming = await ReceiveUntilAsync(socket, messages => messages.Any(IsPrimaryDone));
        await Task.Delay(200);
        Assert.Empty(provider.Translations);

        alternative.SetResult();
        var rest = await ReceiveUntilAsync(socket, messages => Completed(messages) && messages.Any(message => IsKorean(message, 0)) &&
            messages.Any(message => IsKorean(message, 1)));
        Assert.DoesNotContain(streaming, message => TypeOf(message) == "response.korean");
        var completed = rest.Single(message => TypeOf(message) == "response.completed");
        var primary = rest.Single(message => IsKorean(message, 0));
        var second = rest.Single(message => IsKorean(message, 1));
        Assert.Equal("한국어: " + KoreanPrimaryReply, primary.GetProperty("korean").GetString());
        Assert.False(primary.TryGetProperty("failed", out _));
        Assert.Equal(completed.GetProperty("responseId").GetString(), primary.GetProperty("responseId").GetString());
        Assert.Equal(completed.GetProperty("responseId").GetString(), second.GetProperty("responseId").GetString());
        Assert.Equal(completed.GetProperty("turnId").GetString(), second.GetProperty("turnId").GetString());
        Assert.Equal("한국어: " + KoreanAlternative, second.GetProperty("korean").GetString());
        Assert.Equal([KoreanPrimaryReply, KoreanAlternative],
            completed.GetProperty("suggestions").EnumerateArray().Select(item => item.GetString()));
        // Both lines start together when the stream ends, in either order.
        Assert.Equal([KoreanPrimaryReply, KoreanAlternative], provider.Translations.Select(item => item.English).Order());
        await Send(socket, """{"type":"session.stop"}""");
    }

    [Fact]
    public async Task ClientThatDidNotOptInGetsNoKoreanEventsAndCostsNoTranslationCalls()
    {
        var provider = new ControlledProvider { AutomaticRetrieval = true, Alternative = KoreanAlternative, Translate = Prefixed };
        await using var host = await Host.StartAsync(provider: provider);
        using var socket = await host.ConnectAsync();
        await Send(socket, WithOptions(new { responseMode = "grounded", replySettleMs = 300 }));
        var ready = await Receive(socket);
        Assert.False(ready.TryGetProperty("replyKorean", out _));

        provider.Emit(new("q", 1, KoreanQuestion, true));
        var messages = await ReceiveUntilAsync(socket, Completed);
        await Send(socket, """{"type":"session.stop"}""");
        messages.AddRange(await ReceiveUntilCloseAsync(socket));
        Assert.DoesNotContain(messages, message => TypeOf(message) == "response.korean");
        Assert.Empty(provider.Translations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UntranslatableLineIsReportedAsFailedAndTheReplyStillCompletes(bool throws)
    {
        var provider = new ControlledProvider
        {
            AutomaticRetrieval = true,
            Translate = (_, _, _) => throws ? throw new InvalidOperationException("private model detail") : Task.FromResult<string?>(null)
        };
        await using var host = await Host.StartAsync(provider: provider);
        using var socket = await host.ConnectAsync();
        await Send(socket, WithOptions(new { responseMode = "grounded", replySettleMs = 300, replyKorean = true }));
        await Receive(socket);

        provider.Emit(new("q", 1, KoreanQuestion, true));
        var messages = await ReceiveUntilAsync(socket, found => Completed(found) && found.Any(message => IsKorean(message, 0)));
        var line = messages.Single(message => IsKorean(message, 0));
        Assert.True(line.GetProperty("failed").GetBoolean());
        Assert.False(line.TryGetProperty("korean", out _));
        Assert.Equal(KoreanPrimaryReply, messages.Single(message => TypeOf(message) == "response.completed").GetProperty("text").GetString());
        await Send(socket, """{"type":"session.stop"}""");
    }

    [Fact]
    public async Task KoreanOfACompletedReplyStillArrivesAfterANewerResponseStarted()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var provider = new ControlledProvider
        {
            AutomaticRetrieval = true,
            Translate = async (english, _, _) =>
            {
                if (Interlocked.Increment(ref calls) == 1) await gate.Task;
                return "한국어: " + english;
            }
        };
        await using var host = await Host.StartAsync(provider: provider);
        using var socket = await host.ConnectAsync();
        await Send(socket, WithOptions(new { responseMode = "grounded", replySettleMs = 300, replyKorean = true }));
        await Receive(socket);

        provider.Emit(new("q1", 1, KoreanQuestion, true));
        var first = await ReceiveUntilAsync(socket, Completed);
        var firstId = first.Single(message => TypeOf(message) == "response.completed").GetProperty("responseId").GetString();

        provider.Emit(new("q2", 1, "And when does the contract end?", true));
        var second = await ReceiveUntilAsync(socket, messages => messages.Any(message => TypeOf(message) == "response.started"));
        Assert.NotEqual(firstId, second.Single(message => TypeOf(message) == "response.started").GetProperty("responseId").GetString());

        gate.SetResult();
        var late = await ReceiveUntilAsync(socket, messages =>
            messages.Any(message => IsKorean(message, 0) && message.GetProperty("responseId").GetString() == firstId));
        Assert.Equal("한국어: " + KoreanPrimaryReply,
            late.Last(message => IsKorean(message, 0)).GetProperty("korean").GetString());
        await Send(socket, """{"type":"session.stop"}""");
    }

    [Fact]
    public async Task ResponseCancelledBeforeItCompletedPublishesNoKorean()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var translated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new ControlledProvider
        {
            AutomaticRetrieval = true, Alternative = KoreanAlternative,
            // The stream has ended, so its lines are being translated, but the reply still waits for the turn classifier.
            TurnSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
            Translate = async (english, _, _) =>
            {
                await gate.Task;
                translated.TrySetResult();
                return "한국어: " + english;
            }
        };
        await using var host = await Host.StartAsync(provider: provider);
        using var socket = await host.ConnectAsync();
        await Send(socket, WithOptions(new { responseMode = "grounded", replySettleMs = 300, replyKorean = true }));
        await Receive(socket);

        provider.Emit(new("q", 1, KoreanQuestion, true));
        var messages = await ReceiveUntilAsync(socket, found => found.Any(IsPrimaryDone));
        await WaitUntilAsync(() => provider.Translations.Count == 2, TimeSpan.FromSeconds(3));
        Assert.Equal(2, provider.Translations.Count);

        await Send(socket, """{"type":"response.cancel"}""");
        messages.AddRange(await ReceiveUntilAsync(socket, found => found.Any(message => TypeOf(message) == "response.cancelled")));
        gate.SetResult();
        await translated.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await Task.Delay(200);

        await Send(socket, """{"type":"session.stop"}""");
        messages.AddRange(await ReceiveUntilCloseAsync(socket));
        Assert.DoesNotContain(messages, message => TypeOf(message) is "response.korean" or "response.completed");
    }

    [Fact]
    public async Task ResponseCancelledWhileItStreamsStartsNoTranslation()
    {
        var alternative = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new ControlledProvider
        {
            AutomaticRetrieval = true, Alternative = KoreanAlternative, HoldAlternative = alternative, Translate = Prefixed
        };
        await using var host = await Host.StartAsync(provider: provider);
        using var socket = await host.ConnectAsync();
        await Send(socket, WithOptions(new { responseMode = "grounded", replySettleMs = 300, replyKorean = true }));
        await Receive(socket);

        provider.Emit(new("q", 1, KoreanQuestion, true));
        var messages = await ReceiveUntilAsync(socket, found => found.Any(IsPrimaryDone));
        await Send(socket, """{"type":"response.cancel"}""");
        messages.AddRange(await ReceiveUntilAsync(socket, found => found.Any(message => TypeOf(message) == "response.cancelled")));
        alternative.SetResult();
        await Task.Delay(200);

        await Send(socket, """{"type":"session.stop"}""");
        messages.AddRange(await ReceiveUntilCloseAsync(socket));
        Assert.DoesNotContain(messages, message => TypeOf(message) is "response.korean" or "response.completed");
        Assert.Empty(provider.Translations);
    }

    [Fact]
    public async Task UnavailableGroundingStillTranslatesTheGeneralReply()
    {
        var provider = new ControlledProvider { Translate = Prefixed };
        await using var host = await Host.StartAsync(provider: provider);
        using var socket = await host.ConnectAsync();
        await Send(socket, WithOptions(new { responseMode = "balanced", replyKorean = true }));
        await Receive(socket);

        provider.Emit(new("turn", 1, "What is our customer delivery plan?", false));
        await provider.RetrievalStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        provider.Emit(new("turn", 2, "What is our customer delivery plan?", true));
        await Until(socket, "response.started");
        provider.Release.SetException(new InvalidOperationException("private provider detail"));

        var messages = await ReceiveUntilAsync(socket, found => Completed(found) && found.Any(message => IsKorean(message, 0)));
        var completed = messages.Single(message => TypeOf(message) == "response.completed");
        Assert.Equal("unavailable", completed.GetProperty("grounding").GetString());
        Assert.Equal(KoreanPrimaryReply, completed.GetProperty("text").GetString());
        var line = messages.Single(message => IsKorean(message, 0));
        Assert.Equal("한국어: " + KoreanPrimaryReply, line.GetProperty("korean").GetString());
        Assert.Equal(completed.GetProperty("responseId").GetString(), line.GetProperty("responseId").GetString());
        await Send(socket, """{"type":"session.stop"}""");
    }

    [Fact]
    public async Task UnavailableFallbackTellsTheOptedInClientThereIsNothingToWaitFor()
    {
        var provider = new ControlledProvider { Translate = Prefixed, FailFirstAnswer = true };
        await using var host = await Host.StartAsync(provider: provider);
        using var socket = await host.ConnectAsync();
        await Send(socket, WithOptions(new { responseMode = "balanced", replyKorean = true }));
        await Receive(socket);

        provider.Emit(new("turn", 1, "What is our customer delivery plan?", false));
        await provider.RetrievalStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        provider.Emit(new("turn", 2, "What is our customer delivery plan?", true));
        await Until(socket, "response.started");
        provider.Release.SetException(new InvalidOperationException("private provider detail"));
        // The general reply fails as well, so the fixed notice follows the search error.
        await provider.FirstAnswerStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        provider.FirstAnswerRelease.SetResult();

        var messages = await ReceiveUntilAsync(socket, found => Completed(found) && found.Any(message => IsKorean(message, 0)));
        Assert.Equal("grounding_unavailable", Assert.Single(messages, message => TypeOf(message) == "error").GetProperty("code").GetString());
        var completed = messages.Single(message => TypeOf(message) == "response.completed");
        Assert.Equal("unavailable", completed.GetProperty("grounding").GetString());
        var line = messages.Single(message => IsKorean(message, 0));
        Assert.True(line.GetProperty("failed").GetBoolean());
        Assert.Equal(completed.GetProperty("responseId").GetString(), line.GetProperty("responseId").GetString());
        Assert.Empty(provider.Translations);
        await Send(socket, """{"type":"session.stop"}""");
    }

    [Fact]
    public async Task ProfileIntroductionIsTranslatedWithTheConfiguredTopicAndNames()
    {
        var provider = new ControlledProvider { Translate = Prefixed };
        await using var host = await Host.StartAsync(provider: provider);
        using var socket = await host.ConnectAsync();
        await Send(socket, WithOptions(new
        {
            responseMode = "balanced", profileConfirmed = true, replyKorean = true, topic = "Email", phrases = new[] { "JMAP" },
            profile = new { name = "Mina", role = "Software engineer", project = "Evaluating email client interoperability" }
        }));
        await Receive(socket);

        provider.Emit(new("turn", 1,
            "And I would like everybody to maybe shortly introduce himself and the project and first touchpoint with the technology.", true));
        var messages = await ReceiveUntilAsync(socket, found => Completed(found) && found.Any(message => IsKorean(message, 0)));
        const string introduction = "My name is Mina; my role is Software engineer. My current project is Evaluating email client interoperability.";
        Assert.Equal(introduction, messages.Single(message => TypeOf(message) == "response.completed").GetProperty("text").GetString());
        Assert.Equal("한국어: " + introduction, messages.Single(message => IsKorean(message, 0)).GetProperty("korean").GetString());
        var (english, context) = Assert.Single(provider.Translations);
        Assert.Equal(introduction, english);
        Assert.Equal("Email", context.Topic);
        Assert.Equal(["JMAP"], context.Terms);
        await Send(socket, """{"type":"session.stop"}""");
    }

    [Fact]
    public async Task TranslationReceivesTheMeetingTopicAndTheNamesFromSettingsAndEvidence()
    {
        var provider = new ControlledProvider { Translate = Prefixed };
        await using var host = await Host.StartAsync(provider: provider);
        using var socket = await host.ConnectAsync();
        await Send(socket, WithOptions(new
        {
            responseMode = "grounded", replySettleMs = 300, replyKorean = true, topic = "Email", phrases = new[] { "JMAP", "Lighthouse" }
        }));
        await Receive(socket);

        provider.Emit(new("q", 1, KoreanQuestion, true));
        await provider.RetrievalStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        provider.Release.SetResult(new("grounded", [new("Delivery is Friday.", new("Delivery plan", "https://example.test/plan", null))]));
        await ReceiveUntilAsync(socket, messages => messages.Any(message => IsKorean(message, 0)));
        var (_, context) = Assert.Single(provider.Translations);
        Assert.Equal("Email", context.Topic);
        Assert.Equal(["JMAP", "Lighthouse", "Delivery plan"], context.Terms);
        await Send(socket, """{"type":"session.stop"}""");
    }

    [Fact]
    public async Task TurnSignalStartsWhileRetrievalIsStillRunning()
    {
        using var release = new ManualResetEventSlim();
        var provider = new ControlledProvider { BlockRetrieval = release };
        await using var host = await Host.StartAsync(provider: provider);
        using var socket = await host.ConnectAsync();
        await Send(socket, Start);
        await Receive(socket);
        provider.Emit(new("turn", 1, "What is our delivery plan?", true));
        try
        {
            await provider.RetrievalStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await provider.TurnSignalStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally
        {
            release.Set();
            provider.Release.TrySetResult(new("disabled", []));
        }
        await Send(socket, """{"type":"session.stop"}""");
    }
}
