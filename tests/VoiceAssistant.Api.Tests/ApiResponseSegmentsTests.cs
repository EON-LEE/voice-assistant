using System.Text.Json;
using VoiceAssistant.Api;
using Xunit;

namespace VoiceAssistant.Api.Tests;

// response.started says how many finalized utterances a reply answers (segments): its turnId names only the latest of them.
public sealed partial class ApiTests
{
    // Semantic segmentation ends a final at each sentence, so a name and the question it introduces can be two finals.
    private const string NameOpener = "Eon, quick question.";
    private const string MigrationQuestion = "When does the migration finish?";

    private static int SegmentsOf(JsonElement started) => started.GetProperty("segments").GetInt32();

    [Fact]
    public async Task AReplyToGatheredFinalsCountsThemAndAManualRequestAnswersOne()
    {
        var clock = new ManualClock();
        var provider = new ControlledProvider { AutomaticRetrieval = true };
        await using var host = await Host.StartAsync(provider: provider, clock: clock);
        using var socket = await host.ConnectAsync();
        await Send(socket, WithOptions(new { responseMode = "conversation", replySettleMs = 1000 }));
        Assert.Equal("session.ready", TypeOf(await Receive(socket)));
        var timers = clock.TimersCreated;

        provider.Emit(new("opener", 1, NameOpener, true));
        Assert.Equal("opener", (await Until(socket, "transcript.final")).GetProperty("turnId").GetString());
        provider.Emit(new("question", 1, MigrationQuestion, true));
        Assert.Equal("question", (await Until(socket, "transcript.final")).GetProperty("turnId").GetString());
        await clock.WaitForTimersAsync(timers + 2);
        clock.Advance(TimeSpan.FromMilliseconds(300));
        var gathered = await ReceiveUntilAsync(socket, Completed);
        var started = gathered.Single(message => TypeOf(message) == "response.started");
        Assert.Equal("question", started.GetProperty("turnId").GetString());
        Assert.Equal(2, SegmentsOf(started));
        Assert.Equal([NameOpener, MigrationQuestion], Targets(provider));

        await Send(socket, """{"type":"response.request"}""");
        var manual = await Until(socket, "response.started");
        Assert.Equal("question", manual.GetProperty("turnId").GetString());
        Assert.Equal(1, SegmentsOf(manual));
        await Send(socket, """{"type":"session.stop"}""");
    }

    [Fact]
    public async Task AnEarlyReplyAndTheReplyThatSupersedesItAtTheSettleCountTheirGatheredFinals()
    {
        var clock = new ManualClock();
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new ControlledProvider { AutomaticRetrieval = true, Alternative = "Let's check the next step.", HoldAlternative = hold };
        await using var host = await Host.StartAsync(provider: provider, clock: clock, configuration: EarlyStartOn());
        using var socket = await host.ConnectAsync();
        await StartEarlySessionAsync(socket);
        var timers = clock.TimersCreated;

        provider.Emit(new("opener", 1, NameOpener, true));
        Assert.Equal("opener", (await Until(socket, "transcript.final")).GetProperty("turnId").GetString());
        provider.Emit(new("question", 1, MigrationQuestion, true));
        var early = await Until(socket, "response.started");
        Assert.Equal("question", early.GetProperty("turnId").GetString());
        Assert.Equal(2, SegmentsOf(early));
        // An unfinished follow-up changes the target, so the settle answers the whole gathered speech instead.
        provider.Emit(new("more", 1, "And for the beta", true));
        Assert.Equal("more", (await Until(socket, "transcript.final")).GetProperty("turnId").GetString());
        await clock.WaitForTimersAsync(timers + 3);
        clock.Advance(TimeSpan.FromMilliseconds(1000));
        Assert.Equal("question", (await Until(socket, "response.cancelled")).GetProperty("turnId").GetString());
        var superseding = await Until(socket, "response.started");
        Assert.Equal("more", superseding.GetProperty("turnId").GetString());
        Assert.Equal(3, SegmentsOf(superseding));
        hold.SetResult();
        Assert.Equal("more", (await Until(socket, "response.completed")).GetProperty("turnId").GetString());
        await Send(socket, """{"type":"session.stop"}""");
    }

    [Fact]
    public async Task AnAdoptedSpeculativeReplyCountsItsGatheredFinals()
    {
        var clock = new ManualClock();
        var provider = new SpeculationProvider();
        await using var host = await Host.StartAsync(provider: provider, clock: clock, configuration: SpeculationOn());
        using var socket = await host.ConnectAsync();
        await StartSpeculativeSessionAsync(socket);

        provider.Emit(new("opener", 1, NameOpener, true));
        Assert.Equal("transcript.final", TypeOf(await Receive(socket)));
        await PauseOnPartialAsync(socket, provider, clock, new("question", 1, "when does the migration finish", false));
        await NextAsync(provider.Finished);
        provider.Emit(new("question", 2, MigrationQuestion, true));
        var messages = await ReceiveUntilAsync(socket, Completed);
        var started = messages.Single(message => TypeOf(message) == "response.started");
        Assert.Equal("question", started.GetProperty("turnId").GetString());
        Assert.Equal(2, SegmentsOf(started));
        // Adopted, not generated again at the final.
        Assert.Equal(1, provider.Answers);
        await Send(socket, """{"type":"session.stop"}""");
    }

    [Fact]
    public async Task WithoutASettleWindowEveryReplyAnswersOneFinalAProfileIntroductionToo()
    {
        var provider = new ControlledProvider { AutomaticRetrieval = true };
        await using var host = await Host.StartAsync(provider: provider);
        using var socket = await host.ConnectAsync();
        await Send(socket, WithOptions(new
        {
            responseMode = "balanced", profileConfirmed = true,
            profile = new { name = "Mina", role = "Software engineer", project = "Evaluating email client interoperability" }
        }));
        Assert.Equal("session.ready", TypeOf(await Receive(socket)));

        provider.Emit(new("intro", 1, "Could you introduce yourself?", true));
        var introduction = await ReceiveUntilAsync(socket, Completed);
        Assert.Equal(1, SegmentsOf(introduction.Single(message => TypeOf(message) == "response.started")));
        Assert.Equal("profile", introduction.Single(message => TypeOf(message) == "response.completed").GetProperty("responseRoute").GetString());

        provider.Emit(new("question", 1, MigrationQuestion, true));
        var answer = await ReceiveUntilAsync(socket, Completed);
        Assert.Equal(1, SegmentsOf(answer.Single(message => TypeOf(message) == "response.started")));
        await Send(socket, """{"type":"session.stop"}""");
    }
}
