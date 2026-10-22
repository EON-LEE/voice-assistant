using System.Text.Json;
using VoiceAssistant.Api;
using Xunit;

namespace VoiceAssistant.Api.Tests;

// replyOn: every gathered statement worth a reply ("all", the default) or only questions and direct requests ("questions").
public sealed partial class ApiTests
{
    // Fifteen words, neither a question nor a request: with replyOn "all" a statement this long gets its own reply.
    private const string MigrationStatement = "We finished the migration last week and the dashboards are now live for every team.";
    private const string BudgetStatement = "The budget stays the same and the team keeps the current scope for this quarter.";
    private const string SupportStatement = "Our support team handled every ticket within a day, and customers noticed the faster replies.";
    private const string PlanRequest = "Tell me about your plan.";

    private static string StartReplyingOn(string? replyOn, int settleMs = 300) => replyOn is null
        ? WithOptions(new { responseMode = "grounded", replySettleMs = settleMs })
        : WithOptions(new { responseMode = "grounded", replySettleMs = settleMs, replyOn });

    private static string[] Targets(ControlledProvider provider) =>
        Assert.IsType<ConversationTurn[]>(provider.LastHistory).Where(turn => turn.CurrentResponseTarget).Select(turn => turn.Text).ToArray();

    private static string StartedTurn(List<JsonElement> messages) =>
        messages.Single(message => TypeOf(message) == "response.started").GetProperty("turnId").GetString()!;

    [Theory]
    [InlineData(null)]
    [InlineData("all")]
    public async Task ByDefaultALongStatementIsAnsweredAsBefore(string? replyOn)
    {
        var clock = new ManualClock();
        var provider = new ControlledProvider { AutomaticRetrieval = true };
        await using var host = await Host.StartAsync(provider: provider, clock: clock);
        using var socket = await host.ConnectAsync();
        await Send(socket, StartReplyingOn(replyOn));
        Assert.Equal("session.ready", TypeOf(await Receive(socket)));

        await EmitFinalAndSettleAsync(socket, provider, clock, "statement", MigrationStatement);
        Assert.Equal("statement", StartedTurn(await ReceiveUntilAsync(socket, Completed)));
        Assert.Equal([MigrationStatement], Targets(provider));

        // A client that never sent replyOn still learns the effective value with any other change.
        await Send(socket, UpdateCommand(new { responseMode = "conversation" }));
        var updated = await Until(socket, "session.updated");
        Assert.Equal("all", updated.GetProperty("replyOn").GetString());
        Assert.Equal("conversation", updated.GetProperty("responseMode").GetString());
        await Send(socket, """{"type":"session.stop"}""");
    }

    [Fact]
    public async Task QuestionsOnlyNeverAnswersAStatementButAnswersQuestionsAndRequests()
    {
        var clock = new ManualClock();
        var provider = new ControlledProvider { AutomaticRetrieval = true };
        await using var host = await Host.StartAsync(provider: provider, clock: clock);
        using var socket = await host.ConnectAsync();
        await Send(socket, StartReplyingOn("questions"));
        Assert.Equal("session.ready", TypeOf(await Receive(socket)));

        // The statement's settle comes before the question's final, so a reply to it would be started before that final.
        await EmitFinalAndSettleAsync(socket, provider, clock, "statement", MigrationStatement);
        var timers = clock.TimersCreated;
        provider.Emit(new("question", 1, RolloutQuestion, true));
        var heard = await ReceiveUntilAsync(socket, found => found.Any(message => TypeOf(message) == "transcript.final"));
        Assert.DoesNotContain(heard, message => TypeOf(message) == "response.started");
        await clock.WaitForTimersAsync(timers + 1);
        clock.Advance(TimeSpan.FromMilliseconds(300));
        Assert.Equal("question", StartedTurn(await ReceiveUntilAsync(socket, Completed)));
        // The dropped statement is still the context of the reply, only no longer its target.
        Assert.Equal([RolloutQuestion], Targets(provider));
        Assert.Contains(provider.LastHistory!, turn => turn.Text == MigrationStatement && !turn.CurrentResponseTarget);

        await EmitFinalAndSettleAsync(socket, provider, clock, "request", PlanRequest);
        Assert.Equal("request", StartedTurn(await ReceiveUntilAsync(socket, Completed)));
        Assert.Equal([PlanRequest], Targets(provider));
        Assert.Equal(2, provider.Answers);
        await Send(socket, """{"type":"session.stop"}""");
        Assert.DoesNotContain(await ReceiveUntilCloseAsync(socket), message => TypeOf(message) == "response.started");
    }

    [Theory]
    [InlineData("all", true)]
    [InlineData("questions", false)]
    public async Task SpeechGatheredForMoreThanTwelveSecondsIsAnsweredOnlyWhenEveryStatementCounts(string replyOn, bool answered)
    {
        var clock = new ManualClock();
        var provider = new ControlledProvider { AutomaticRetrieval = true };
        await using var host = await Host.StartAsync(provider: provider, clock: clock);
        using var socket = await host.ConnectAsync();
        await Send(socket, StartReplyingOn(replyOn, settleMs: 1000));
        Assert.Equal("session.ready", TypeOf(await Receive(socket)));
        var timers = clock.TimersCreated;

        provider.Emit(new("short", 1, "We moved the launch to March.", true));
        Assert.Equal("short", (await Until(socket, "transcript.final")).GetProperty("turnId").GetString());
        await clock.WaitForTimersAsync(++timers);
        // The next speaker talks on for 12.6 s without a pause as long as the 1000 ms settle: each partial re-arms it.
        var words = BudgetStatement.Split(' ');
        for (var revision = 1; revision <= 14; revision++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(900));
            provider.Emit(new("long", revision, string.Join(' ', words.Take(Math.Min(revision, words.Length - 1))), false));
            Assert.Equal("transcript.partial", TypeOf(await Receive(socket)));
            await clock.WaitForTimersAsync(++timers);
        }
        provider.Emit(new("long", 15, BudgetStatement, true));
        Assert.Equal("long", (await Until(socket, "transcript.final")).GetProperty("turnId").GetString());
        await clock.WaitForTimersAsync(++timers);
        // Past the 12 s gather limit the speaker's next short pause is enough to settle.
        clock.Advance(TimeSpan.FromMilliseconds(300));

        if (answered)
        {
            Assert.Equal("long", StartedTurn(await ReceiveUntilAsync(socket, Completed)));
            Assert.Equal(["We moved the launch to March.", BudgetStatement], Targets(provider));
        }
        else
        {
            provider.Emit(new("question", 1, RolloutQuestion, true));
            var heard = await ReceiveUntilAsync(socket, found => found.Any(message => TypeOf(message) == "transcript.final"));
            Assert.DoesNotContain(heard, message => TypeOf(message) == "response.started");
            await clock.WaitForTimersAsync(++timers);
            clock.Advance(TimeSpan.FromMilliseconds(300));
            Assert.Equal("question", StartedTurn(await ReceiveUntilAsync(socket, Completed)));
            Assert.Equal([RolloutQuestion], Targets(provider));
            Assert.Contains(provider.LastHistory!, turn => turn.Text == BudgetStatement);
        }
        Assert.Equal(1, provider.Answers);
        await Send(socket, """{"type":"session.stop"}""");
    }

    [Fact]
    public async Task AManualRequestStillAnswersTheLatestStatementWhenOnlyQuestionsAreAnswered()
    {
        var clock = new ManualClock();
        var provider = new ControlledProvider { AutomaticRetrieval = true };
        await using var host = await Host.StartAsync(provider: provider, clock: clock);
        using var socket = await host.ConnectAsync();
        await Send(socket, StartReplyingOn("questions"));
        Assert.Equal("session.ready", TypeOf(await Receive(socket)));

        await EmitFinalAndSettleAsync(socket, provider, clock, "statement", MigrationStatement);
        await Send(socket, """{"type":"response.request"}""");
        Assert.Equal("statement", StartedTurn(await ReceiveUntilAsync(socket, Completed)));
        Assert.Equal([MigrationStatement], Targets(provider));
        Assert.Equal(1, provider.Answers);
        await Send(socket, """{"type":"session.stop"}""");
    }

    [Fact]
    public async Task ReplyOnSwitchesFromTheNextSettleWhileARunningReplyKeepsGoing()
    {
        var clock = new ManualClock();
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new ControlledProvider { AutomaticRetrieval = true, Alternative = "Let's check the next step.", HoldAlternative = hold };
        await using var host = await Host.StartAsync(provider: provider, clock: clock);
        using var socket = await host.ConnectAsync();
        await Send(socket, StartReplyingOn(null));
        Assert.Equal("session.ready", TypeOf(await Receive(socket)));

        await EmitFinalAndSettleAsync(socket, provider, clock, "first", MigrationStatement);
        await ReceiveUntilAsync(socket, found => found.Any(IsPrimaryDone));
        await Send(socket, UpdateCommand(new { replyOn = "questions" }));
        var updated = (await ReceiveUntilAsync(socket, found => found.Any(message => TypeOf(message) == "session.updated")))
            .Single(message => TypeOf(message) == "session.updated");
        Assert.Equal("questions", updated.GetProperty("replyOn").GetString());
        Assert.Equal("grounded", updated.GetProperty("responseMode").GetString());
        Assert.False(updated.GetProperty("replyKorean").GetBoolean());
        hold.SetResult();
        var first = await ReceiveUntilAsync(socket, Completed);
        Assert.DoesNotContain(first, message => TypeOf(message) == "response.cancelled");
        Assert.Equal("first", first.Single(message => TypeOf(message) == "response.completed").GetProperty("turnId").GetString());

        // The next statement settles under the new choice: no reply.
        await EmitFinalAndSettleAsync(socket, provider, clock, "second", BudgetStatement);
        await Send(socket, UpdateCommand(new { replyOn = "all" }));
        var switchedBack = await ReceiveUntilAsync(socket, found => found.Any(message => TypeOf(message) == "session.updated"));
        Assert.DoesNotContain(switchedBack, message => TypeOf(message) == "response.started");
        Assert.Equal("all", switchedBack.Single(message => TypeOf(message) == "session.updated").GetProperty("replyOn").GetString());

        await EmitFinalAndSettleAsync(socket, provider, clock, "third", SupportStatement);
        Assert.Equal("third", StartedTurn(await ReceiveUntilAsync(socket, Completed)));
        Assert.Equal([SupportStatement], Targets(provider));
        Assert.Contains(provider.LastHistory!, turn => turn.Text == BudgetStatement && !turn.CurrentResponseTarget);
        Assert.Equal(2, provider.Answers);
        await Send(socket, """{"type":"session.stop"}""");
    }

    [Fact]
    public async Task WithoutASettleWindowOnlyQuestionsAndRequestsAreAnswered()
    {
        var provider = new ControlledProvider { AutomaticRetrieval = true };
        await using var host = await Host.StartAsync(provider: provider);
        using var socket = await host.ConnectAsync();
        await Send(socket, WithOptions(new { responseMode = "grounded", replyOn = "questions" }));
        Assert.Equal("session.ready", TypeOf(await Receive(socket)));

        // Answered, the statement would start a reply and hold the question back until it completes.
        provider.Emit(new("statement", 1, MigrationStatement, true));
        provider.Emit(new("request", 1, PlanRequest, true));
        Assert.Equal("request", StartedTurn(await ReceiveUntilAsync(socket, Completed)));
        await Send(socket, """{"type":"session.stop"}""");
        Assert.DoesNotContain(await ReceiveUntilCloseAsync(socket), message => TypeOf(message) == "response.started");
        Assert.Equal(1, provider.Answers);
    }

    [Theory]
    [InlineData("\"questions only\"")]
    [InlineData("\"Questions\"")]
    [InlineData("null")]
    [InlineData("true")]
    public async Task AnUnknownReplyOnFailsTheStart(string replyOn)
    {
        await using var host = await Host.StartAsync();
        using var socket = await host.ConnectAsync();
        await Send(socket, """{"type":"session.start","protocolVersion":1,"audio":{"encoding":"pcm_s16le","sampleRate":16000,"channels":1},"options":{"replyOn":"""
            + replyOn + "}}");
        Assert.Equal("invalid_start", (await Receive(socket)).GetProperty("code").GetString());
    }
}
