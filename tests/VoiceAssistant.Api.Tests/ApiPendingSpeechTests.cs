using System.Net.WebSockets;
using System.Text.Json;
using VoiceAssistant.Api;
using Xunit;

namespace VoiceAssistant.Api.Tests;

public sealed partial class ApiTests
{
    // A short remark keeps every settle at Wait; the backchannels that follow push it out of the 12-turn history.
    private static readonly string[] RemarkThenBackchannels =
        ["Sorry, go ahead.", "Yeah.", "Okay.", "Mm-hmm.", "Right.", "Yes.", "Sure.", "Yeah.", "OK.", "Uh-huh.", "Right.", "Yep.", "Okay."];

    [Fact]
    public async Task PendingSpeechThatLeftTheHistoryIsDroppedAndTheNextQuestionIsAnswered()
    {
        var clock = new ManualClock();
        var provider = new ControlledProvider { AutomaticRetrieval = true };
        await using var host = await Host.StartAsync(provider: provider, clock: clock);
        using var socket = await host.ConnectAsync();
        await Send(socket, WithOptions(new { responseMode = "grounded", replySettleMs = 300 }));
        Assert.Equal("session.ready", TypeOf(await Receive(socket)));

        for (var index = 0; index < RemarkThenBackchannels.Length; index++)
            await EmitFinalAndSettleAsync(socket, provider, clock, $"t{index:00}", RemarkThenBackchannels[index]);
        Assert.Equal(0, provider.Answers);
        await EmitFinalAndSettleAsync(socket, provider, clock, "question", "What do you think?");

        var completed = await CompletedWithoutErrorAsync(socket);
        Assert.Equal("question", completed.GetProperty("turnId").GetString());
        var history = Assert.IsType<ConversationTurn[]>(provider.LastHistory);
        // Once the remark fell out of the history only backchannels were pending, so they were discarded.
        Assert.Equal(["What do you think?"], history.Where(turn => turn.CurrentResponseTarget).Select(turn => turn.Text));
        await Send(socket, """{"type":"session.stop"}""");
    }

    [Fact]
    public async Task ReplyToMorePendingSegmentsThanTheHistoryHoldsTargetsOnlyTheRetainedTurns()
    {
        var clock = new ManualClock();
        var provider = new ControlledProvider { AutomaticRetrieval = true };
        await using var host = await Host.StartAsync(provider: provider, clock: clock);
        using var socket = await host.ConnectAsync();
        await Send(socket, WithOptions(new { responseMode = "grounded", replySettleMs = 300 }));
        Assert.Equal("session.ready", TypeOf(await Receive(socket)));
        var timers = clock.TimersCreated;

        // No settle fires in between, so all 14 finals are still pending when the question settles.
        for (var index = 0; index < RemarkThenBackchannels.Length; index++)
        {
            provider.Emit(new($"t{index:00}", 1, RemarkThenBackchannels[index], true));
            Assert.Equal($"t{index:00}", (await Until(socket, "transcript.final")).GetProperty("turnId").GetString());
        }
        await clock.WaitForTimersAsync(timers + RemarkThenBackchannels.Length);
        await EmitFinalAndSettleAsync(socket, provider, clock, "question", "What do you think?");

        var completed = await CompletedWithoutErrorAsync(socket);
        Assert.Equal("question", completed.GetProperty("turnId").GetString());
        var history = Assert.IsType<ConversationTurn[]>(provider.LastHistory);
        var targets = history.Where(turn => turn.CurrentResponseTarget).Select(turn => turn.Text).ToArray();
        Assert.Equal(12, targets.Length);
        Assert.Equal("What do you think?", targets[^1]);
        Assert.DoesNotContain("Sorry, go ahead.", targets);
        Assert.Equal("Sorry, go ahead.", history[0].Text);
        await Send(socket, """{"type":"session.stop"}""");
    }

    /// <summary>Emits one final, waits until its settle timer exists, then lets exactly the settle window pass.</summary>
    private static async Task EmitFinalAndSettleAsync(ClientWebSocket socket, ControlledProvider provider, ManualClock clock,
        string turnId, string text)
    {
        var timers = clock.TimersCreated;
        provider.Emit(new(turnId, 1, text, true));
        Assert.Equal(turnId, (await Until(socket, "transcript.final")).GetProperty("turnId").GetString());
        await clock.WaitForTimersAsync(timers + 1);
        clock.Advance(TimeSpan.FromMilliseconds(300));
    }

    private static async Task<JsonElement> CompletedWithoutErrorAsync(ClientWebSocket socket)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            var message = await Receive(socket);
            if (TypeOf(message) == "error") Assert.Fail("Unexpected error event: " + message.GetRawText());
            if (TypeOf(message) == "response.completed") return message;
        }
        throw new InvalidOperationException("response.completed was not received.");
    }
}
