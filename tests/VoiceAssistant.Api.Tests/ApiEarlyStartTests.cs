using Microsoft.Extensions.Configuration;
using VoiceAssistant.Api;
using Xunit;

namespace VoiceAssistant.Api.Tests;

public sealed partial class ApiTests
{
    private const string RolloutQuestion = "What is the rollout plan?";

    private static Dictionary<string, string?> EarlyStartOn() => new() { ["Session:EarlyStartAtFinal"] = "true" };

    private static async Task StartEarlySessionAsync(System.Net.WebSockets.ClientWebSocket socket)
    {
        await Send(socket, WithOptions(new { responseMode = "grounded", replySettleMs = 1000 }));
        Assert.Equal("session.ready", TypeOf(await Receive(socket)));
    }

    [Fact]
    public async Task EarlyStartAnswersACompleteQuestionAtItsFinalAndOnlyOnce()
    {
        var clock = new ManualClock();
        var provider = new ControlledProvider { AutomaticRetrieval = true };
        await using var host = await Host.StartAsync(provider: provider, clock: clock, configuration: EarlyStartOn());
        using var socket = await host.ConnectAsync();
        await StartEarlySessionAsync(socket);
        var timers = clock.TimersCreated;

        provider.Emit(new("q", 1, RolloutQuestion, true));

        Assert.Equal("transcript.final", TypeOf(await Receive(socket)));
        // The reply starts at the final itself: the clock has not moved, so no settle window has passed.
        var started = await Receive(socket);
        Assert.Equal("response.started", TypeOf(started));
        Assert.Equal("q", started.GetProperty("turnId").GetString());
        await ReceiveUntilAsync(socket, Completed);
        // The settle still runs for the question; nothing changed, so it adds no second reply.
        await clock.WaitForTimersAsync(timers + 1);
        clock.Advance(TimeSpan.FromMilliseconds(300));
        await Send(socket, """{"type":"session.stop"}""");
        var rest = await ReceiveUntilCloseAsync(socket);
        Assert.DoesNotContain(rest, message => TypeOf(message) is "response.started" or "response.cancelled");
        Assert.Equal(1, provider.Answers);
    }

    [Fact]
    public async Task AFollowUpThatChangesTheTargetReplacesTheEarlyReplyAtTheSettle()
    {
        var clock = new ManualClock();
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new ControlledProvider { AutomaticRetrieval = true, Alternative = "Let's check the next step.", HoldAlternative = hold };
        await using var host = await Host.StartAsync(provider: provider, clock: clock, configuration: EarlyStartOn());
        using var socket = await host.ConnectAsync();
        await StartEarlySessionAsync(socket);
        var timers = clock.TimersCreated;

        provider.Emit(new("q", 1, RolloutQuestion, true));
        Assert.Equal("q", (await Until(socket, "response.started")).GetProperty("turnId").GetString());
        // An unfinished continuation re-arms the full window instead of starting another reply at once.
        provider.Emit(new("more", 1, "And for the Korean", true));
        Assert.Equal("more", (await Until(socket, "transcript.final")).GetProperty("turnId").GetString());
        await clock.WaitForTimersAsync(timers + 2);
        clock.Advance(TimeSpan.FromMilliseconds(1000));

        // The settle finds a changed target: the early reply is cancelled and the whole gathered speech is answered.
        Assert.Equal("q", (await Until(socket, "response.cancelled")).GetProperty("turnId").GetString());
        Assert.Equal("more", (await Until(socket, "response.started")).GetProperty("turnId").GetString());
        hold.SetResult();
        Assert.Equal("more", (await Until(socket, "response.completed")).GetProperty("turnId").GetString());
        var history = Assert.IsType<ConversationTurn[]>(provider.LastHistory);
        Assert.Equal([RolloutQuestion, "And for the Korean"], history.Where(turn => turn.CurrentResponseTarget).Select(turn => turn.Text));
        Assert.Equal(2, provider.Answers);
        await Send(socket, """{"type":"session.stop"}""");
    }

    [Fact]
    public async Task AFollowUpQuestionInTheWindowSupersedesTheEarlyReplyAtItsOwnFinal()
    {
        var clock = new ManualClock();
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new ControlledProvider { AutomaticRetrieval = true, Alternative = "Let's check the next step.", HoldAlternative = hold };
        await using var host = await Host.StartAsync(provider: provider, clock: clock, configuration: EarlyStartOn());
        using var socket = await host.ConnectAsync();
        await StartEarlySessionAsync(socket);
        var timers = clock.TimersCreated;

        provider.Emit(new("q", 1, RolloutQuestion, true));
        Assert.Equal("q", (await Until(socket, "response.started")).GetProperty("turnId").GetString());
        provider.Emit(new("q2", 1, "And what does it cost?", true));

        // A second complete question changes the target, so it supersedes the early reply at once, like a newer request.
        Assert.Equal("q", (await Until(socket, "response.cancelled")).GetProperty("turnId").GetString());
        Assert.Equal("q2", (await Until(socket, "response.started")).GetProperty("turnId").GetString());
        hold.SetResult();
        Assert.Equal("q2", (await Until(socket, "response.completed")).GetProperty("turnId").GetString());
        // Its settle then finds nothing new and keeps it.
        await clock.WaitForTimersAsync(timers + 2);
        clock.Advance(TimeSpan.FromMilliseconds(300));
        await Send(socket, """{"type":"session.stop"}""");
        Assert.DoesNotContain(await ReceiveUntilCloseAsync(socket), message => TypeOf(message) is "response.started" or "response.cancelled");
        Assert.Equal(2, provider.Answers);
    }

    [Fact]
    public async Task ABackchannelAfterTheQuestionKeepsTheEarlyReply()
    {
        var clock = new ManualClock();
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new ControlledProvider { AutomaticRetrieval = true, Alternative = "Let's check the next step.", HoldAlternative = hold };
        await using var host = await Host.StartAsync(provider: provider, clock: clock, configuration: EarlyStartOn());
        using var socket = await host.ConnectAsync();
        await StartEarlySessionAsync(socket);
        var timers = clock.TimersCreated;

        provider.Emit(new("q", 1, RolloutQuestion, true));
        Assert.Equal("q", (await Until(socket, "response.started")).GetProperty("turnId").GetString());
        provider.Emit(new("ok", 1, "Yeah.", true));
        Assert.Equal("ok", (await Until(socket, "transcript.final")).GetProperty("turnId").GetString());
        await clock.WaitForTimersAsync(timers + 2);
        clock.Advance(TimeSpan.FromMilliseconds(300));
        hold.SetResult();

        var messages = await ReceiveUntilAsync(socket, Completed);
        Assert.DoesNotContain(messages, message => TypeOf(message) is "response.cancelled" or "response.started");
        Assert.Equal("q", messages.Single(message => TypeOf(message) == "response.completed").GetProperty("turnId").GetString());
        await Send(socket, """{"type":"session.stop"}""");
        Assert.DoesNotContain(await ReceiveUntilCloseAsync(socket), message => TypeOf(message) == "response.started");
        Assert.Equal(1, provider.Answers);
    }

    [Fact]
    public async Task EarlyStartKeepsHoldingAndQueueingANewQuestionBehindAnActiveReply()
    {
        var clock = new ManualClock();
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new ControlledProvider { AutomaticRetrieval = true, Alternative = "Let's check the next step.", HoldAlternative = hold };
        await using var host = await Host.StartAsync(provider: provider, clock: clock, configuration: EarlyStartOn());
        using var socket = await host.ConnectAsync();
        await StartEarlySessionAsync(socket);
        var timers = clock.TimersCreated;

        provider.Emit(new("a", 1, RolloutQuestion, true));
        Assert.Equal("a", (await Until(socket, "response.started")).GetProperty("turnId").GetString());
        await clock.WaitForTimersAsync(timers + 1);
        clock.Advance(TimeSpan.FromMilliseconds(300));
        // A new question in a later window, while the earlier reply is still held, is queued as today, not started early.
        provider.Emit(new("b", 1, "When is the review meeting?", true));
        Assert.Equal("b", (await Until(socket, "transcript.final")).GetProperty("turnId").GetString());
        await clock.WaitForTimersAsync(timers + 2);
        clock.Advance(TimeSpan.FromMilliseconds(300));
        hold.SetResult();

        var messages = await ReceiveUntilAsync(socket, found =>
            found.Any(message => TypeOf(message) == "response.completed" && message.GetProperty("turnId").GetString() == "b"));
        Assert.DoesNotContain(messages, message => TypeOf(message) == "response.cancelled");
        var completedA = messages.FindIndex(message => TypeOf(message) == "response.completed" && message.GetProperty("turnId").GetString() == "a");
        var startedB = messages.FindIndex(message => TypeOf(message) == "response.started" && message.GetProperty("turnId").GetString() == "b");
        Assert.InRange(completedA, 0, startedB - 1);
        Assert.Equal(2, provider.Answers);
        await Send(socket, """{"type":"session.stop"}""");
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("true", true)]
    [InlineData(" False ", false)]
    [InlineData("TRUE", true)]
    public void ExperimentSwitchesAreOffUnlessSetToTrue(string? value, bool expected) =>
        Assert.Equal(expected, ServiceSettings.ReadSwitch(new ConfigurationBuilder()
            .AddInMemoryCollection([new KeyValuePair<string, string?>("Session:EarlyStartAtFinal", value)]).Build(), "Session:EarlyStartAtFinal"));

    [Fact]
    public async Task AnInvalidExperimentSwitchStopsTheAppFromStarting() =>
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Host.StartAsync(azure: true, configuration: new() { ["Session:EarlyStartAtFinal"] = "yes" }));
}
