using Microsoft.Extensions.Configuration;
using VoiceAssistant.Api;
using Xunit;

namespace VoiceAssistant.Api.Tests;

public sealed partial class ApiTests
{
    // The other person's short answer to a question the user asked aloud: the app hears the meeting, not the user, so this
    // statement arrives on its own and is neither a question nor long enough to reply to at the settle.
    private const string ClarifiedAnswer = "Sure. The pricing first, and then the timeline.";

    private static Dictionary<string, string?> QuietReplyOn() => new() { ["Session:QuietReplyMs"] = "1500" };

    [Fact]
    public async Task AShortStatementNobodyFollowsUpIsAnsweredOnceTheRoomStaysQuiet()
    {
        var clock = new ManualClock();
        var provider = new ControlledProvider { AutomaticRetrieval = true };
        await using var host = await Host.StartAsync(provider: provider, clock: clock, configuration: QuietReplyOn());
        using var socket = await host.ConnectAsync();
        await StartEarlySessionAsync(socket);
        var timers = clock.TimersCreated;

        provider.Emit(new("clarified", 1, ClarifiedAnswer, true));
        Assert.Equal("clarified", (await Until(socket, "transcript.final")).GetProperty("turnId").GetString());
        await clock.WaitForTimersAsync(timers + 1);
        // The settle waits for more context and arms the quiet timer instead of replying.
        clock.Advance(TimeSpan.FromMilliseconds(1000));
        await clock.WaitForTimersAsync(timers + 2);
        clock.Advance(TimeSpan.FromMilliseconds(1499));
        Assert.Equal(0, provider.Answers);

        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal("clarified", (await Until(socket, "response.started")).GetProperty("turnId").GetString());
        Assert.Equal("clarified", (await Until(socket, "response.completed")).GetProperty("turnId").GetString());
        var history = Assert.IsType<ConversationTurn[]>(provider.LastHistory);
        Assert.Equal([ClarifiedAnswer], history.Where(turn => turn.CurrentResponseTarget).Select(turn => turn.Text));
        Assert.Equal(1, provider.Answers);
        await Send(socket, """{"type":"session.stop"}""");
    }

    [Fact]
    public async Task WithoutTheQuietSwitchAShortStatementStillWaitsForMoreSpeech()
    {
        var clock = new ManualClock();
        var provider = new ControlledProvider { AutomaticRetrieval = true };
        await using var host = await Host.StartAsync(provider: provider, clock: clock);
        using var socket = await host.ConnectAsync();
        await StartEarlySessionAsync(socket);
        var timers = clock.TimersCreated;

        provider.Emit(new("clarified", 1, ClarifiedAnswer, true));
        Assert.Equal("clarified", (await Until(socket, "transcript.final")).GetProperty("turnId").GetString());
        await clock.WaitForTimersAsync(timers + 1);
        clock.Advance(TimeSpan.FromMilliseconds(1000));
        clock.Advance(TimeSpan.FromSeconds(30));

        await Send(socket, """{"type":"session.stop"}""");
        Assert.DoesNotContain(await ReceiveUntilCloseAsync(socket), message => TypeOf(message) == "response.started");
        Assert.Equal(0, provider.Answers);
    }

    [Fact]
    public async Task SpeechInTheQuietWindowIsAnsweredTogetherWithTheStatementOnlyOnce()
    {
        var clock = new ManualClock();
        var provider = new ControlledProvider { AutomaticRetrieval = true };
        await using var host = await Host.StartAsync(provider: provider, clock: clock, configuration: QuietReplyOn());
        using var socket = await host.ConnectAsync();
        await StartEarlySessionAsync(socket);
        var timers = clock.TimersCreated;

        provider.Emit(new("statement", 1, "We tried a pilot last spring.", true));
        Assert.Equal("statement", (await Until(socket, "transcript.final")).GetProperty("turnId").GetString());
        await clock.WaitForTimersAsync(timers + 1);
        clock.Advance(TimeSpan.FromMilliseconds(1000));
        await clock.WaitForTimersAsync(timers + 2);
        clock.Advance(TimeSpan.FromMilliseconds(500));

        // New speech re-arms the settle, which voids the quiet timer: one reply answers both.
        provider.Emit(new("question", 1, "What changed since then?", true));
        Assert.Equal("question", (await Until(socket, "transcript.final")).GetProperty("turnId").GetString());
        await clock.WaitForTimersAsync(timers + 3);
        clock.Advance(TimeSpan.FromMilliseconds(300));
        Assert.Equal("question", (await Until(socket, "response.completed")).GetProperty("turnId").GetString());
        var history = Assert.IsType<ConversationTurn[]>(provider.LastHistory);
        Assert.Equal(["We tried a pilot last spring.", "What changed since then?"],
            history.Where(turn => turn.CurrentResponseTarget).Select(turn => turn.Text));

        // The voided quiet timer fires later and starts nothing.
        clock.Advance(TimeSpan.FromSeconds(5));
        await Send(socket, """{"type":"session.stop"}""");
        Assert.DoesNotContain(await ReceiveUntilCloseAsync(socket), message => TypeOf(message) == "response.started");
        Assert.Equal(1, provider.Answers);
    }

    [Fact]
    public async Task QuestionsOnlyStillDropsAShortStatementWithTheQuietSwitchOn()
    {
        var clock = new ManualClock();
        var provider = new ControlledProvider { AutomaticRetrieval = true };
        await using var host = await Host.StartAsync(provider: provider, clock: clock, configuration: QuietReplyOn());
        using var socket = await host.ConnectAsync();
        await Send(socket, WithOptions(new { responseMode = "grounded", replySettleMs = 1000, replyOn = "questions" }));
        Assert.Equal("session.ready", TypeOf(await Receive(socket)));
        var timers = clock.TimersCreated;

        provider.Emit(new("clarified", 1, ClarifiedAnswer, true));
        Assert.Equal("clarified", (await Until(socket, "transcript.final")).GetProperty("turnId").GetString());
        await clock.WaitForTimersAsync(timers + 1);
        clock.Advance(TimeSpan.FromMilliseconds(1000));
        clock.Advance(TimeSpan.FromSeconds(10));

        await Send(socket, """{"type":"session.stop"}""");
        Assert.DoesNotContain(await ReceiveUntilCloseAsync(socket), message => TypeOf(message) == "response.started");
        Assert.Equal(0, provider.Answers);
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("0", 0)]
    [InlineData("500", 500)]
    [InlineData("1500", 1500)]
    [InlineData("10000", 10000)]
    public void QuietReplyIsOffUnlessSetToAWindowFromHalfASecondToTenSeconds(string? value, int expected) =>
        Assert.Equal(expected, ServiceSettings.ReadQuietReplyMs(new ConfigurationBuilder()
            .AddInMemoryCollection([new KeyValuePair<string, string?>("Session:QuietReplyMs", value)]).Build()));

    [Theory]
    [InlineData("499")]
    [InlineData("10001")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("true")]
    public async Task AnInvalidQuietReplyWindowStopsTheAppFromStarting(string value) =>
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Host.StartAsync(azure: true, configuration: new() { ["Session:QuietReplyMs"] = value }));
}
