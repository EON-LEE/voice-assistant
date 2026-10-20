using System.Net.WebSockets;
using System.Text.Json;
using VoiceAssistant.Api;
using Xunit;

namespace VoiceAssistant.Api.Tests;

public sealed partial class ApiTests
{
    private const string DeliveryQuestion = "What is our customer delivery plan?";

    private static string UpdateCommand(object options) => JsonSerializer.Serialize(new { type = "session.update", options });

    [Fact]
    public async Task ReadyAdvertisesSessionUpdateExceptForTranscribeOnlySessions()
    {
        await using (var host = await Host.StartAsync())
        using (var socket = await host.ConnectAsync())
        {
            await Send(socket, Start);
            Assert.True((await Receive(socket)).GetProperty("sessionUpdate").GetBoolean());
        }
        await using var transcribeHost = await Host.StartAsync();
        using var transcribeOnly = await transcribeHost.ConnectAsync();
        await Send(transcribeOnly, WithOptions(new { transcribeOnly = true, endSilenceMs = 500 }));
        Assert.False((await Receive(transcribeOnly)).TryGetProperty("sessionUpdate", out _));
    }

    [Fact]
    public async Task SessionUpdateIsAcknowledgedWithTheEffectiveSettings()
    {
        await using var host = await Host.StartAsync(provider: new ControlledProvider());
        using var socket = await host.ConnectAsync();
        await Send(socket, WithOptions(new { responseMode = "balanced", replyKorean = true }));
        await Receive(socket);

        await Send(socket, UpdateCommand(new { responseMode = "conversation" }));
        var mode = await Receive(socket);
        Assert.Equal("session.updated", TypeOf(mode));
        Assert.Equal("conversation", mode.GetProperty("responseMode").GetString());
        Assert.True(mode.GetProperty("replyKorean").GetBoolean());

        await Send(socket, UpdateCommand(new { replyKorean = false }));
        var korean = await Receive(socket);
        Assert.Equal("session.updated", TypeOf(korean));
        Assert.Equal("conversation", korean.GetProperty("responseMode").GetString());
        Assert.False(korean.GetProperty("replyKorean").GetBoolean());
        await Send(socket, """{"type":"session.stop"}""");
    }

    [Fact]
    public async Task DocumentSearchSwitchAppliesFromTheNextReplyAndBackAgain()
    {
        var provider = new ControlledProvider { AutomaticRetrieval = true };
        await using var host = await Host.StartAsync(provider: provider);
        using var socket = await host.ConnectAsync();
        await Send(socket, WithOptions(new { responseMode = "grounded", replySettleMs = 300 }));
        await Receive(socket);

        await Send(socket, UpdateCommand(new { responseMode = "conversation" }));
        await Until(socket, "session.updated");
        provider.Emit(new("off", 1, KoreanQuestion, true));
        var withoutSearch = await Until(socket, "response.completed");
        Assert.Equal("disabled", withoutSearch.GetProperty("grounding").GetString());
        Assert.Equal("conversation", provider.AnswerOptions!.ResponseMode);
        Assert.Equal(0, provider.Retrievals);

        await Send(socket, UpdateCommand(new { responseMode = "grounded" }));
        await Until(socket, "session.updated");
        provider.Emit(new("on", 1, DeliveryQuestion, true));
        var withSearch = await Until(socket, "response.completed");
        Assert.Equal("no_matches", withSearch.GetProperty("grounding").GetString());
        Assert.Equal("grounded", provider.AnswerOptions.ResponseMode);
        Assert.True(provider.Retrievals >= 1);
        await Send(socket, """{"type":"session.stop"}""");
    }

    [Fact]
    public async Task KoreanSwitchedOnAndOffMidSessionOnlyAffectsLaterReplies()
    {
        var provider = new ControlledProvider { AutomaticRetrieval = true, Translate = Prefixed };
        await using var host = await Host.StartAsync(provider: provider);
        using var socket = await host.ConnectAsync();
        await Send(socket, WithOptions(new { responseMode = "grounded", replySettleMs = 300 }));
        Assert.False((await Receive(socket)).TryGetProperty("replyKorean", out _));

        provider.Emit(new("one", 1, KoreanQuestion, true));
        var untranslated = await ReceiveUntilAsync(socket, Completed);
        Assert.DoesNotContain(untranslated, message => TypeOf(message) == "response.korean");
        Assert.Empty(provider.Translations);

        await Send(socket, UpdateCommand(new { replyKorean = true }));
        Assert.True((await Until(socket, "session.updated")).GetProperty("replyKorean").GetBoolean());
        provider.Emit(new("two", 1, DeliveryQuestion, true));
        var translated = await ReceiveUntilAsync(socket, found => Completed(found) && found.Any(message => IsKorean(message, 0)));
        Assert.Equal("한국어: " + KoreanPrimaryReply,
            translated.Single(message => IsKorean(message, 0)).GetProperty("korean").GetString());
        Assert.Single(provider.Translations);

        await Send(socket, UpdateCommand(new { replyKorean = false }));
        Assert.False((await Until(socket, "session.updated")).GetProperty("replyKorean").GetBoolean());
        provider.Emit(new("three", 1, "Can we ship the beta to the customer on Friday?", true));
        var stopped = await ReceiveUntilAsync(socket, Completed);
        await Send(socket, """{"type":"session.stop"}""");
        stopped.AddRange(await ReceiveUntilCloseAsync(socket));
        Assert.DoesNotContain(stopped, message => TypeOf(message) == "response.korean");
        Assert.Single(provider.Translations);
    }

    [Fact]
    public async Task ReplyAlreadyGeneratingKeepsItsSettingsWhenTheyChange()
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
        await ReceiveUntilAsync(socket, found => found.Any(IsPrimaryDone));
        await Send(socket, UpdateCommand(new { responseMode = "conversation", replyKorean = false }));
        await Until(socket, "session.updated");

        alternative.SetResult();
        var rest = await ReceiveUntilAsync(socket, found => Completed(found) && found.Any(message => IsKorean(message, 0)) &&
            found.Any(message => IsKorean(message, 1)));
        Assert.Equal("no_matches", rest.Single(message => TypeOf(message) == "response.completed").GetProperty("grounding").GetString());
        Assert.Equal("한국어: " + KoreanPrimaryReply, rest.Single(message => IsKorean(message, 0)).GetProperty("korean").GetString());
        Assert.Equal("한국어: " + KoreanAlternative, rest.Single(message => IsKorean(message, 1)).GetProperty("korean").GetString());

        var retrievals = provider.Retrievals;
        provider.Emit(new("next", 1, DeliveryQuestion, true));
        var next = await ReceiveUntilAsync(socket, Completed);
        Assert.Equal("disabled", next.Single(message => TypeOf(message) == "response.completed").GetProperty("grounding").GetString());
        Assert.Equal(retrievals, provider.Retrievals);
        await Send(socket, """{"type":"session.stop"}""");
        next.AddRange(await ReceiveUntilCloseAsync(socket));
        Assert.DoesNotContain(next, message => TypeOf(message) == "response.korean");
        Assert.Equal(2, provider.Translations.Count);
    }

    [Fact]
    public async Task TranscribeOnlySessionRejectsSessionUpdateWithoutEnding()
    {
        var provider = new ControlledProvider();
        await using var host = await Host.StartAsync(provider: provider);
        using var socket = await host.ConnectAsync();
        await Send(socket, WithOptions(new { transcribeOnly = true, endSilenceMs = 500 }));
        await Receive(socket);

        await Send(socket, UpdateCommand(new { replyKorean = true }));
        var error = await Receive(socket);
        Assert.Equal("transcribe_only", error.GetProperty("code").GetString());
        Assert.False(error.GetProperty("retryable").GetBoolean());

        provider.Emit(new("one", 1, "Hello there.", true));
        Assert.Equal("transcript.final", (await Receive(socket)).GetProperty("type").GetString());
        await Send(socket, """{"type":"session.stop"}""");
    }

    [Theory]
    [InlineData("""{"type":"session.update"}""")]
    [InlineData("""{"type":"session.update","options":null}""")]
    [InlineData("""{"type":"session.update","options":{}}""")]
    [InlineData("""{"type":"session.update","options":{"responseMode":"fast"}}""")]
    [InlineData("""{"type":"session.update","options":{"replyKorean":"yes"}}""")]
    [InlineData("""{"type":"session.update","options":{"topic":"changed"}}""")]
    public async Task MalformedSessionUpdateEndsTheSessionWithInvalidCommand(string command)
    {
        await using var host = await Host.StartAsync();
        using var socket = await host.ConnectAsync();
        await Send(socket, Start);
        await Receive(socket);

        await Send(socket, command);
        Assert.Equal("invalid_command", (await Receive(socket)).GetProperty("code").GetString());
        Assert.DoesNotContain(await ReceiveUntilCloseAsync(socket), message => TypeOf(message) == "session.updated");
    }
}
