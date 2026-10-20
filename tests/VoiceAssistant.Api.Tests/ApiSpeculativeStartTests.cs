using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using VoiceAssistant.Api;
using Xunit;

namespace VoiceAssistant.Api.Tests;

public sealed partial class ApiTests
{
    // A speech partial has no capitals or punctuation; its final has both.
    private const string RolloutPartial = "what is the rollout plan";

    private static Dictionary<string, string?> SpeculationOn() => new()
    {
        ["Session:EarlyStartAtFinal"] = "true", ["Session:SpeculativeStart"] = "true"
    };

    private static async Task StartSpeculativeSessionAsync(ClientWebSocket socket, string mode = "conversation", int settleMs = 1000,
        bool korean = false)
    {
        await Send(socket, WithOptions(new { responseMode = mode, replySettleMs = settleMs, replyKorean = korean }));
        Assert.Equal("session.ready", TypeOf(await Receive(socket)));
    }

    /// <summary>Emits a partial and lets it stay unchanged for the speculation pause on the manual clock.</summary>
    private static async Task PauseOnPartialAsync(ClientWebSocket socket, SpeculationProvider provider, ManualClock clock, Transcript partial)
    {
        var timers = clock.TimersCreated;
        provider.Emit(partial);
        Assert.Equal("transcript.partial", TypeOf(await Receive(socket)));
        await clock.WaitForTimersAsync(timers + 1);
        clock.Advance(MeetingSession.SpeculationPause);
    }

    private static bool PrimaryDone(JsonElement message) => TypeOf(message) == "response.delta" && message.TryGetProperty("primaryDone", out _);

    private static string[] DeltaTexts(IEnumerable<JsonElement> messages) =>
        messages.Where(message => TypeOf(message) == "response.delta").Select(message => message.GetProperty("text").GetString()!).ToArray();

    private static IEnumerable<KeyValuePair<string, object?>[]> Speculations(MetricCapture capture, string outcome) =>
        capture.Named("voiceassistant.reply.speculation").Select(measurement => measurement.Tags)
            .Where(tags => tags.Contains(new("outcome", outcome)));

    [Fact]
    public async Task AnAdoptedSpeculativeReplySendsExactlyTheEventsOfAReplyStartedAtItsFinal()
    {
        var (atFinal, _) = await RunAsync(speculative: false);
        var (adopted, provider) = await RunAsync(speculative: true);

        Assert.Equal(atFinal, adopted);
        Assert.Contains(atFinal, message => message.Contains("\"response.started\"", StringComparison.Ordinal));
        // The final generated nothing new: it adopted the reply that had already run to its end.
        Assert.Equal(1, provider.Answers);

        static async Task<(List<string> Messages, SpeculationProvider Provider)> RunAsync(bool speculative)
        {
            var clock = new ManualClock();
            var provider = new SpeculationProvider();
            await using var host = await Host.StartAsync(provider: provider, clock: clock,
                configuration: speculative ? SpeculationOn() : EarlyStartOn());
            using var socket = await host.ConnectAsync();
            await StartSpeculativeSessionAsync(socket);
            var timers = clock.TimersCreated;
            var messages = new List<JsonElement>();

            provider.Emit(new("q", 1, RolloutPartial, false));
            messages.Add(await Receive(socket));
            if (speculative)
            {
                await clock.WaitForTimersAsync(timers + 1);
                clock.Advance(MeetingSession.SpeculationPause);
                Assert.Equal(RolloutPartial, await NextAsync(provider.Finished));
            }
            else clock.Advance(MeetingSession.SpeculationPause);
            provider.Emit(new("q", 2, RolloutQuestion, true));
            messages.AddRange(await ReceiveUntilAsync(socket, Completed));
            await Send(socket, """{"type":"session.stop"}""");
            messages.AddRange(await ReceiveUntilCloseAsync(socket));
            // Response IDs are random; everything else must be the same.
            return (messages.Select(message => JsonSerializer.Serialize(message.EnumerateObject()
                .Where(property => property.Name != "responseId").ToDictionary(property => property.Name, property => property.Value))).ToList(), provider);
        }
    }

    [Fact]
    public async Task AnAdoptedSpeculativeReplySendsWhatItHasAtOnceThenKeepsStreaming()
    {
        using var capture = new MetricCapture();
        var clock = new ManualClock();
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new SpeculationProvider { Hold = hold };
        await using var host = await Host.StartAsync(provider: provider, clock: clock, configuration: SpeculationOn());
        using var socket = await host.ConnectAsync();
        await StartSpeculativeSessionAsync(socket, korean: true);

        await PauseOnPartialAsync(socket, provider, clock, new("q", 1, RolloutPartial, false));
        Assert.Equal(RolloutPartial, await NextAsync(provider.FirstLines));
        clock.Advance(TimeSpan.FromMilliseconds(400));
        provider.Emit(new("q", 2, RolloutQuestion, true));

        // Nothing of the speculative reply was sent before its final; the first line comes right after response.started.
        var messages = await ReceiveUntilAsync(socket, found => found.Any(PrimaryDone));
        Assert.Equal(["transcript.final", "response.started"], messages.Take(2).Select(TypeOf));
        var responseId = messages[1].GetProperty("responseId").GetString();
        Assert.Equal("q", messages[1].GetProperty("turnId").GetString());
        Assert.Equal([SpeculationProvider.ReplyTo(RolloutPartial), ""], DeltaTexts(messages));
        hold.SetResult();
        messages.AddRange(await ReceiveUntilAsync(socket, found =>
            Completed(found) && messages.Concat(found).Count(message => TypeOf(message) == "response.korean") == 2));
        var completed = messages.Single(message => TypeOf(message) == "response.completed");
        Assert.Equal(SpeculationProvider.ReplyTo(RolloutPartial), completed.GetProperty("text").GetString());
        // Its Korean lines, translated while it was held, belong to the adopted response.
        Assert.All(messages.Where(message => TypeOf(message) is "response.delta" or "response.completed" or "response.korean"),
            message => Assert.Equal(responseId, message.GetProperty("responseId").GetString()));
        Assert.Equal(1, provider.Answers);

        await Send(socket, """{"type":"session.stop"}""");
        await ReceiveUntilCloseAsync(socket);
        Assert.Single(Speculations(capture, "started"));
        Assert.Single(Speculations(capture, "adopted"));
        Assert.Empty(Speculations(capture, "discarded"));
        // It started 400 ms before its final, which counts as its start: no settle wait, first line at once.
        Assert.Equal(400, Assert.Single(capture.Named("voiceassistant.reply.speculative_head_start")).Value);
        Assert.Equal(0, Assert.Single(capture.Named("voiceassistant.reply.settle_wait")).Value);
        Assert.Equal(0, Assert.Single(capture.Named("voiceassistant.stt_final_to_first_delta")).Value);
        Assert.Single(capture.Named("voiceassistant.reply.grounding"));
    }

    [Fact]
    public async Task AFinalWithOtherWordsDiscardsTheSpeculativeReplyUnseen()
    {
        using var capture = new MetricCapture();
        var clock = new ManualClock();
        var provider = new SpeculationProvider();
        await using var host = await Host.StartAsync(provider: provider, clock: clock, configuration: SpeculationOn());
        using var socket = await host.ConnectAsync();
        await StartSpeculativeSessionAsync(socket, korean: true);
        const string final = "What is the rollout plan for Q3?";

        await PauseOnPartialAsync(socket, provider, clock, new("q", 1, RolloutPartial, false));
        Assert.Equal(RolloutPartial, await NextAsync(provider.Finished));
        provider.Emit(new("q", 2, final, true));

        var messages = await ReceiveUntilAsync(socket, found =>
            Completed(found) && found.Count(message => TypeOf(message) == "response.korean") == 2);
        // Only the reply started at the final is seen: its own lines and its own Korean.
        var started = Assert.Single(messages, message => TypeOf(message) == "response.started");
        Assert.Equal("transcript.final", TypeOf(messages[0]));
        Assert.Equal([SpeculationProvider.ReplyTo(final), ""], DeltaTexts(messages));
        Assert.All(messages.Where(message => TypeOf(message) == "response.korean"), message =>
        {
            Assert.Equal(started.GetProperty("responseId").GetString(), message.GetProperty("responseId").GetString());
            Assert.NotEqual("KO " + SpeculationProvider.ReplyTo(RolloutPartial), message.GetProperty("korean").GetString());
        });
        Assert.Equal(2, provider.Answers);

        await Send(socket, """{"type":"session.stop"}""");
        Assert.DoesNotContain(await ReceiveUntilCloseAsync(socket), message => TypeOf(message).StartsWith("response.", StringComparison.Ordinal));
        Assert.Contains(Speculations(capture, "discarded"), tags => tags.Contains(new("reason", "mismatch")));
        Assert.Empty(Speculations(capture, "adopted"));
        // The discarded reply measured its model call and classifier wait, but none of it counts as a reply.
        foreach (var name in new[] { "voiceassistant.reply.first_token", "voiceassistant.reply.classifier_wait", "voiceassistant.reply.grounding" })
            Assert.Single(capture.Named(name), measurement => measurement.Tags.Contains(new("response_mode", "conversation")));
    }

    [Fact]
    public async Task NewWordsDiscardTheSpeculativeReplyAndAPauseOnThemStartsTheNextOne()
    {
        using var capture = new MetricCapture();
        var clock = new ManualClock();
        var provider = new SpeculationProvider();
        await using var host = await Host.StartAsync(provider: provider, clock: clock, configuration: SpeculationOn());
        using var socket = await host.ConnectAsync();
        await StartSpeculativeSessionAsync(socket);
        const string longer = "what is the rollout plan for the launch";

        await PauseOnPartialAsync(socket, provider, clock, new("q", 1, RolloutPartial, false));
        Assert.Equal(RolloutPartial, await NextAsync(provider.FirstLines));
        await PauseOnPartialAsync(socket, provider, clock, new("q", 2, longer, false));
        Assert.Equal(longer, await NextAsync(provider.FirstLines));
        provider.Emit(new("q", 3, "What is the rollout plan for the launch?", true));

        var messages = await ReceiveUntilAsync(socket, Completed);
        Assert.Single(messages, message => TypeOf(message) == "response.started");
        Assert.Equal([SpeculationProvider.ReplyTo(longer), ""], DeltaTexts(messages));
        Assert.Equal(2, provider.Answers);
        await Send(socket, """{"type":"session.stop"}""");
        await ReceiveUntilCloseAsync(socket);
        Assert.Equal(2, Speculations(capture, "started").Count());
        Assert.Single(Speculations(capture, "discarded"), tags => tags.Contains(new("reason", "target_changed")));
        Assert.Single(Speculations(capture, "adopted"));
    }

    [Fact]
    public async Task ABackchannelAfterTheSpeculatedQuestionKeepsTheSpeculativeReply()
    {
        using var capture = new MetricCapture();
        var clock = new ManualClock();
        var provider = new SpeculationProvider();
        await using var host = await Host.StartAsync(provider: provider, clock: clock, configuration: SpeculationOn());
        using var socket = await host.ConnectAsync();
        await StartSpeculativeSessionAsync(socket);

        await PauseOnPartialAsync(socket, provider, clock, new("q", 1, RolloutPartial, false));
        Assert.Equal(RolloutPartial, await NextAsync(provider.FirstLines));
        // As for an early reply, words that only acknowledge do not change what it answers.
        await PauseOnPartialAsync(socket, provider, clock, new("q", 2, RolloutPartial + " okay", false));
        // Speech segmentation can still end the utterance before the acknowledgement.
        provider.Emit(new("q", 3, RolloutQuestion, true));

        var messages = await ReceiveUntilAsync(socket, Completed);
        Assert.Equal([SpeculationProvider.ReplyTo(RolloutPartial), ""], DeltaTexts(messages));
        Assert.Equal(1, provider.Answers);
        await Send(socket, """{"type":"session.stop"}""");
        await ReceiveUntilCloseAsync(socket);
        Assert.Single(Speculations(capture, "adopted"));
        Assert.Empty(Speculations(capture, "discarded"));
    }

    [Fact]
    public async Task APauseRequestDiscardsTheSpeculativeReplyAndItsFinalIsAnsweredAsUsual()
    {
        using var capture = new MetricCapture();
        var clock = new ManualClock();
        var provider = new SpeculationProvider { Hold = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var host = await Host.StartAsync(provider: provider, clock: clock, configuration: SpeculationOn());
        using var socket = await host.ConnectAsync();
        await StartSpeculativeSessionAsync(socket);

        await PauseOnPartialAsync(socket, provider, clock, new("q", 1, RolloutPartial, false));
        Assert.Equal(RolloutPartial, await NextAsync(provider.FirstLines));
        await Send(socket, """{"type":"response.cancel"}""");
        // The held generation is cancelled, not left running.
        Assert.Equal(RolloutPartial, await NextAsync(provider.Finished));
        provider.Hold = null;
        provider.Emit(new("q", 2, RolloutQuestion, true));

        var messages = await ReceiveUntilAsync(socket, Completed);
        // No reply was visible, so there is nothing to cancel on the client; the final then starts its own reply.
        Assert.Equal(["transcript.final", "response.started"], messages.Take(2).Select(TypeOf));
        Assert.DoesNotContain(messages, message => TypeOf(message) == "response.cancelled");
        Assert.Equal(2, provider.Answers);
        await Send(socket, """{"type":"session.stop"}""");
        await ReceiveUntilCloseAsync(socket);
        Assert.Single(Speculations(capture, "discarded"), tags => tags.Contains(new("reason", "cancelled")));
        Assert.Empty(Speculations(capture, "adopted"));
    }

    [Fact]
    public async Task StoppingTheSessionDiscardsTheSpeculativeReplyUnseen()
    {
        using var capture = new MetricCapture();
        var clock = new ManualClock();
        var provider = new SpeculationProvider { Hold = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var host = await Host.StartAsync(provider: provider, clock: clock, configuration: SpeculationOn());
        using var socket = await host.ConnectAsync();
        await StartSpeculativeSessionAsync(socket);

        await PauseOnPartialAsync(socket, provider, clock, new("q", 1, RolloutPartial, false));
        Assert.Equal(RolloutPartial, await NextAsync(provider.FirstLines));
        await Send(socket, """{"type":"session.stop"}""");

        Assert.DoesNotContain(await ReceiveUntilCloseAsync(socket), message => TypeOf(message).StartsWith("response.", StringComparison.Ordinal));
        Assert.Equal(RolloutPartial, await NextAsync(provider.Finished));
        await WaitUntilAsync(() => Speculations(capture, "discarded").Any(), TimeSpan.FromSeconds(3));
        Assert.Single(Speculations(capture, "discarded"), tags => tags.Contains(new("reason", "cancelled")));
    }

    [Theory]
    [InlineData(false, 1000)]
    [InlineData(true, 0)]
    public async Task WithoutTheSwitchOrASettleWindowAPausedPartialStartsNothing(bool switchedOn, int settleMs)
    {
        using var capture = new MetricCapture();
        var clock = new ManualClock();
        var provider = new SpeculationProvider();
        await using var host = await Host.StartAsync(provider: provider, clock: clock,
            configuration: switchedOn ? SpeculationOn() : EarlyStartOn());
        using var socket = await host.ConnectAsync();
        await StartSpeculativeSessionAsync(socket, settleMs: settleMs);
        var timers = clock.TimersCreated;

        provider.Emit(new("q", 1, RolloutPartial, false));
        Assert.Equal("transcript.partial", TypeOf(await Receive(socket)));
        clock.Advance(MeetingSession.SpeculationPause);
        provider.Emit(new("q", 2, RolloutQuestion, true));

        var messages = await ReceiveUntilAsync(socket, Completed);
        Assert.Equal(["transcript.final", "response.started"], messages.Take(2).Select(TypeOf));
        Assert.Equal([SpeculationProvider.ReplyTo(RolloutQuestion), ""], DeltaTexts(messages));
        Assert.Equal(1, provider.Answers);
        // No pause timer was armed for the partial: only the final's settle timer, when there is a settle window.
        Assert.Equal(timers + (settleMs > 0 ? 1 : 0), clock.TimersCreated);
        await Send(socket, """{"type":"session.stop"}""");
        await ReceiveUntilCloseAsync(socket);
        Assert.Empty(capture.Named("voiceassistant.reply.speculation"));
    }

    [Fact]
    public async Task OnlyOneSpeculativeGenerationRunsAtATime()
    {
        using var capture = new MetricCapture();
        var clock = new ManualClock();
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new SpeculationProvider { Hold = hold, IgnoreCancellation = true };
        await using var host = await Host.StartAsync(provider: provider, clock: clock, configuration: SpeculationOn());
        using var socket = await host.ConnectAsync();
        await StartSpeculativeSessionAsync(socket);
        const string longer = "what is the rollout plan for the launch";

        await PauseOnPartialAsync(socket, provider, clock, new("q", 1, RolloutPartial, false));
        Assert.Equal(RolloutPartial, await NextAsync(provider.FirstLines));
        // New words discard it, but its generation ignores cancellation and keeps running, so their pause starts nothing yet.
        await PauseOnPartialAsync(socket, provider, clock, new("q", 2, longer, false));
        provider.Emit(new("q", 3, longer, false));
        Assert.Equal("transcript.partial", TypeOf(await Receive(socket)));
        Assert.Equal(1, provider.Answers);

        // Once the discarded generation has ended, the pause that waited behind it starts the next speculative reply.
        hold.SetResult();
        Assert.Equal(RolloutPartial, await NextAsync(provider.Finished));
        Assert.Equal(longer, await NextAsync(provider.FirstLines));
        Assert.Equal(2, provider.Answers);
        Assert.Equal(1, provider.MaxActive);
        await Send(socket, """{"type":"session.stop"}""");
        Assert.DoesNotContain(await ReceiveUntilCloseAsync(socket), message => TypeOf(message).StartsWith("response.", StringComparison.Ordinal));
        Assert.Equal(2, Speculations(capture, "started").Count());
    }

    [Fact]
    public async Task ADiscardedSpeculationsFailureIsNeitherSentNorLogged()
    {
        var logs = new CollectingLogs();
        var clock = new ManualClock();
        var provider = new SpeculationProvider { FailingAnswers = 1 };
        await using var host = await Host.StartAsync(provider: provider, clock: clock, configuration: SpeculationOn(), logs: logs);
        using var socket = await host.ConnectAsync();
        await StartSpeculativeSessionAsync(socket);
        const string final = "What is the rollout plan for Q3?";

        await PauseOnPartialAsync(socket, provider, clock, new("q", 1, RolloutPartial, false));
        Assert.Equal(RolloutPartial, await NextAsync(provider.Finished));
        provider.Emit(new("q", 2, final, true));

        var messages = await ReceiveUntilAsync(socket, Completed);
        Assert.DoesNotContain(messages, message => TypeOf(message) == "error");
        Assert.Equal([SpeculationProvider.ReplyTo(final), ""], DeltaTexts(messages));
        await Send(socket, """{"type":"session.stop"}""");
        await ReceiveUntilCloseAsync(socket);
        Assert.DoesNotContain(logs.Messages, message => message.Contains("Meeting response failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ADiscardedSpeculationsRetrievalFailureIsNeitherSentNorLogged()
    {
        var logs = new CollectingLogs();
        var clock = new ManualClock();
        var provider = new SpeculationProvider { FailingRetrievals = 1 };
        await using var host = await Host.StartAsync(provider: provider, clock: clock, configuration: SpeculationOn(), logs: logs);
        using var socket = await host.ConnectAsync();
        await StartSpeculativeSessionAsync(socket, mode: "grounded");
        const string final = "What is the rollout plan for Q3?";

        // The speculative reply's search fails; it records that (held back) before it calls the model.
        await PauseOnPartialAsync(socket, provider, clock, new("q", 1, RolloutPartial, false));
        Assert.Equal(RolloutPartial, await NextAsync(provider.FirstLines));
        provider.Emit(new("q", 2, final, true));

        var messages = await ReceiveUntilAsync(socket, Completed);
        Assert.DoesNotContain(messages, message => TypeOf(message) == "error");
        Assert.Equal("no_matches", messages.Single(message => TypeOf(message) == "response.completed").GetProperty("grounding").GetString());
        Assert.Equal(2, provider.Retrievals);
        await Send(socket, """{"type":"session.stop"}""");
        await ReceiveUntilCloseAsync(socket);
        Assert.DoesNotContain(logs.Messages, message => message.Contains("Meeting retrieval unavailable", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ADiscardedSpeculationsClassifierFailureIsNotLogged()
    {
        var logs = new CollectingLogs();
        var clock = new ManualClock();
        var provider = new SpeculationProvider { ClassifierFailure = new InvalidOperationException("Synthetic classifier failure.") };
        await using var host = await Host.StartAsync(provider: provider, clock: clock, configuration: SpeculationOn(), logs: logs);
        using var socket = await host.ConnectAsync();
        await StartSpeculativeSessionAsync(socket);

        await PauseOnPartialAsync(socket, provider, clock, new("q", 1, RolloutPartial, false));
        Assert.Equal(RolloutPartial, await NextAsync(provider.Finished));
        provider.Emit(new("q", 2, "What is the rollout plan for Q3?", true));
        await ReceiveUntilAsync(socket, Completed);
        await Send(socket, """{"type":"session.stop"}""");
        await ReceiveUntilCloseAsync(socket);

        // Only the reply that was sent logs its classifier failure.
        Assert.Single(logs.Messages, message => message.Contains("Turn classifier failed", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AFilteredSpeculativeReplyIsWithdrawnLikeAnyReplyWhenAdoptedAndLeaksNothingWhenDiscarded(bool adopted)
    {
        if (adopted)
        {
            // Adopted, it is withdrawn exactly like a reply started at its final: the same events, the same log line, no Korean.
            var atFinal = await RunAsync(speculative: false, RolloutQuestion);
            var speculated = await RunAsync(speculative: true, RolloutQuestion);
            Assert.Equal(atFinal.Messages, speculated.Messages);
            Assert.Equal(atFinal.Withdrawn, speculated.Withdrawn);
            Assert.Equal(["transcript.partial", "transcript.final", "response.started", "response.delta", "error", "response.cancelled"],
                speculated.Types);
            Assert.Contains("\"code\":\"response_filtered\"", speculated.Messages[4], StringComparison.Ordinal);
            Assert.Single(speculated.Withdrawn);
            Assert.Empty(speculated.Provider.Translations);
            Assert.Equal(1, speculated.Provider.Answers);
        }
        else
        {
            // Discarded, nothing of it is sent, translated or logged; the final's own reply completes as usual.
            var run = await RunAsync(speculative: true, "What is the rollout plan for Q3?");
            Assert.DoesNotContain(run.Types, type => type is "error" or "response.cancelled");
            Assert.Single(run.Types, type => type == "response.started");
            Assert.Contains("response.completed", run.Types);
            Assert.Empty(run.Withdrawn);
            Assert.DoesNotContain(SpeculationProvider.ReplyTo(RolloutPartial), run.Provider.Translations);
            Assert.Equal(2, run.Provider.Answers);
        }

        static async Task<(List<string> Messages, List<string> Types, string[] Withdrawn, SpeculationProvider Provider)> RunAsync(
            bool speculative, string final)
        {
            var logs = new CollectingLogs();
            var clock = new ManualClock();
            var provider = new SpeculationProvider { FilteredAnswers = 1 };
            await using var host = await Host.StartAsync(provider: provider, clock: clock,
                configuration: speculative ? SpeculationOn() : EarlyStartOn(), logs: logs);
            using var socket = await host.ConnectAsync();
            await StartSpeculativeSessionAsync(socket, korean: true);
            var timers = clock.TimersCreated;
            var messages = new List<JsonElement>();

            provider.Emit(new("q", 1, RolloutPartial, false));
            messages.Add(await Receive(socket));
            if (speculative)
            {
                await clock.WaitForTimersAsync(timers + 1);
                clock.Advance(MeetingSession.SpeculationPause);
                // The speculative reply streamed its first line and was then stopped by the filter, all before its final.
                Assert.Equal(RolloutPartial, await NextAsync(provider.Finished));
            }
            else clock.Advance(MeetingSession.SpeculationPause);
            provider.Emit(new("q", 2, final, true));
            messages.AddRange(await ReceiveUntilAsync(socket, found => found.Any(message => TypeOf(message) == "response.cancelled") ||
                (Completed(found) && found.Count(message => TypeOf(message) == "response.korean") == 2)));
            await Send(socket, """{"type":"session.stop"}""");
            messages.AddRange(await ReceiveUntilCloseAsync(socket));
            var withdrawn = logs.Messages.Where(message => message.Contains("Meeting response withdrawn", StringComparison.Ordinal)).ToArray();
            // Response IDs are random; everything else must be the same.
            return (messages.Select(message => JsonSerializer.Serialize(message.EnumerateObject()
                    .Where(property => property.Name != "responseId").ToDictionary(property => property.Name, property => property.Value))).ToList(),
                messages.Select(TypeOf).ToList(), withdrawn, provider);
        }
    }

    [Fact]
    public async Task ASpeculativeReplyTakesOverAStartedSearchAndItsAdoptedReplyDoesNotSearchAgain()
    {
        using var capture = new MetricCapture();
        var clock = new ManualClock();
        var provider = new SpeculationProvider();
        await using var host = await Host.StartAsync(provider: provider, clock: clock, configuration: SpeculationOn());
        using var socket = await host.ConnectAsync();
        await StartSpeculativeSessionAsync(socket, mode: "grounded");
        var timers = clock.TimersCreated;

        provider.Emit(new("q", 1, RolloutPartial, false));
        Assert.Equal("transcript.partial", TypeOf(await Receive(socket)));
        // The partial's prefetched search has started (its debounce runs in real time) before the speculation pause ends.
        await NextAsync(provider.RetrievalCalls);
        await clock.WaitForTimersAsync(timers + 1);
        clock.Advance(MeetingSession.SpeculationPause);
        Assert.Equal(RolloutPartial, await NextAsync(provider.Finished));
        provider.Emit(new("q", 2, RolloutQuestion, true));

        var completed = (await ReceiveUntilAsync(socket, Completed)).Single(message => TypeOf(message) == "response.completed");
        Assert.Equal("knowledge", completed.GetProperty("responseRoute").GetString());
        Assert.Equal("no_matches", completed.GetProperty("grounding").GetString());
        Assert.True(completed.GetProperty("retrievalPrefetched").GetBoolean());
        Assert.Equal(1, provider.Retrievals);
        await Send(socket, """{"type":"session.stop"}""");
        await ReceiveUntilCloseAsync(socket);
        Assert.Single(capture.Named("voiceassistant.reply.prefetch"), measurement => measurement.Tags.Contains(new("result", "hit")));
        Assert.Single(Speculations(capture, "adopted"), tags => tags.Contains(new("route", "knowledge")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NewWordsCancelASearchNobodyWaitsForAndFreeTheWorkerForTheNextOne(bool speculative)
    {
        using var capture = new MetricCapture();
        var clock = new ManualClock();
        var provider = new SpeculationProvider { BlockRetrievals = true };
        await using var host = await Host.StartAsync(provider: provider, clock: clock,
            configuration: speculative ? SpeculationOn() : EarlyStartOn());
        using var socket = await host.ConnectAsync();
        await StartSpeculativeSessionAsync(socket, mode: "grounded");
        var timers = clock.TimersCreated;
        const string longer = RolloutPartial + " for the launch";

        provider.Emit(new("q", 1, RolloutPartial, false));
        Assert.Equal("transcript.partial", TypeOf(await Receive(socket)));
        var first = await NextAsync(provider.RetrievalCalls);
        if (speculative)
        {
            // A speculative reply takes over the search that is running.
            await clock.WaitForTimersAsync(timers + 1);
            clock.Advance(MeetingSession.SpeculationPause);
        }
        provider.Emit(new("q", 2, longer, false));
        Assert.Equal("transcript.partial", TypeOf(await Receive(socket)));

        // The new words discard that reply; nobody waits for the search any more, so it is cancelled and the single
        // prefetch worker moves on to the newer words.
        await first.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = await NextAsync(provider.RetrievalCalls);
        Assert.EndsWith(ResponseRouting.Normalize(longer), second.Query, StringComparison.Ordinal);
        second.Release.SetResult(new("no_matches", []));
        Assert.Equal(speculative ? 1 : 0, Speculations(capture, "started").Count());
        Assert.Equal(speculative ? 1 : 0, Speculations(capture, "discarded").Count());
        await Send(socket, """{"type":"session.stop"}""");
        Assert.DoesNotContain(await ReceiveUntilCloseAsync(socket), message => TypeOf(message).StartsWith("response.", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AReplyKeepsItsGroundingWhenTheNextUtteranceAndAModeChangeArriveWhileItSearches(bool speculative)
    {
        var clock = new ManualClock();
        var provider = new SpeculationProvider { BlockRetrievals = true, StubbornRetrievals = 1 };
        await using var host = await Host.StartAsync(provider: provider, clock: clock,
            configuration: speculative ? SpeculationOn() : EarlyStartOn());
        using var socket = await host.ConnectAsync();
        await StartSpeculativeSessionAsync(socket, mode: "grounded");

        // A statement's prefetched search hangs and ignores cancellation, so it keeps the single prefetch worker busy.
        provider.Emit(new("z", 1, "we discussed the timeline", false));
        Assert.Equal("transcript.partial", TypeOf(await Receive(socket)));
        var stuck = await NextAsync(provider.RetrievalCalls);
        provider.Emit(new("z", 2, "We discussed the timeline.", true));
        Assert.Equal("transcript.final", TypeOf(await Receive(socket)));

        // A question follows. Its search cannot start on the busy worker, so the reply (speculative, then adopted, or started
        // at the final) searches on its own at once.
        await PauseOnPartialAsync(socket, provider, clock, new("a", 1, "what is the budget", false));
        provider.Emit(new("a", 2, "What is the budget?", true));
        var messages = await ReceiveUntilAsync(socket, found => found.Any(message => TypeOf(message) == "response.started"));
        var own = await NextAsync(provider.RetrievalCalls);
        Assert.Contains("what is the budget", own.Query, StringComparison.Ordinal);

        // The next utterance starts and the document-search mode changes while that search is still running.
        provider.Emit(new("n", 1, "and one more thing", false));
        messages.AddRange(await ReceiveUntilAsync(socket, found => found.Any(message => TypeOf(message) == "transcript.partial")));
        await Send(socket, """{"type":"session.update","options":{"responseMode":"balanced"}}""");
        messages.AddRange(await ReceiveUntilAsync(socket, found => found.Any(message => TypeOf(message) == "session.updated")));
        own.Release.SetResult(new("no_matches", []));

        messages.AddRange(await ReceiveUntilAsync(socket, Completed));
        Assert.DoesNotContain(messages, message => TypeOf(message) == "error");
        var completed = messages.Single(message => TypeOf(message) == "response.completed");
        Assert.Equal("a", completed.GetProperty("turnId").GetString());
        Assert.Equal("no_matches", completed.GetProperty("grounding").GetString());
        Assert.False(completed.GetProperty("retrievalPrefetched").GetBoolean());
        stuck.Release.SetResult(new("no_matches", []));
        await Send(socket, """{"type":"session.stop"}""");
        await ReceiveUntilCloseAsync(socket);
    }

    [Fact]
    public async Task ASpeculativeReplyIsAdoptedWhenItsFinalPushesOldSpeechOutOfTheHistory()
    {
        var clock = new ManualClock();
        var provider = new SpeculationProvider();
        await using var host = await Host.StartAsync(provider: provider, clock: clock, configuration: SpeculationOn());
        using var socket = await host.ConnectAsync();
        await StartSpeculativeSessionAsync(socket);
        // Twelve finals fill the bounded history; the long first one leaves it as a shortened excerpt at the next final.
        string[] earlier =
        [
            string.Join(' ', Enumerable.Repeat("yeah", 70)) + ".", "Okay.", "Right.", "Sure.", "Yes.", "Great.", "Cool.", "Nice.",
            "Exactly.", "Got it.", "I see.", "Thanks."
        ];
        var timers = clock.TimersCreated;
        for (var index = 0; index < earlier.Length; index++)
        {
            provider.Emit(new($"t{index}", 1, earlier[index], true));
            Assert.Equal("transcript.final", TypeOf(await Receive(socket)));
        }
        // Their settle finds only acknowledgements and drops them, so the question below is the only target.
        await clock.WaitForTimersAsync(timers + earlier.Length);
        clock.Advance(TimeSpan.FromMilliseconds(1000));

        await PauseOnPartialAsync(socket, provider, clock, new("q", 1, RolloutPartial, false));
        Assert.Equal(RolloutPartial, await NextAsync(provider.Finished));
        provider.Emit(new("q", 2, RolloutQuestion, true));

        var messages = await ReceiveUntilAsync(socket, Completed);
        Assert.Equal("q", messages.Single(message => TypeOf(message) == "response.started").GetProperty("turnId").GetString());
        // Adopted: the speculative reply had worked out the same shortened history the final produced.
        Assert.Equal(1, provider.Answers);
        var history = Assert.Single(provider.Histories);
        Assert.Equal(13, history.Length);
        Assert.StartsWith("yeah yeah", history[0].Text, StringComparison.Ordinal);
        Assert.Contains("…", history[0].Text, StringComparison.Ordinal);
        Assert.Equal([RolloutPartial], history.Where(turn => turn.CurrentResponseTarget).Select(turn => turn.Text));
        await Send(socket, """{"type":"session.stop"}""");
        await ReceiveUntilCloseAsync(socket);
    }

    [Fact]
    public async Task SpeculativeStartRequiresEarlyStartAndIsLoggedAtStartup()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Host.StartAsync(configuration: new() { ["Session:SpeculativeStart"] = "true" }));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Host.StartAsync(configuration: new() { ["Session:EarlyStartAtFinal"] = "true", ["Session:SpeculativeStart"] = "yes" }));

        var logs = new CollectingLogs();
        await using var host = await Host.StartAsync(configuration: SpeculationOn(), logs: logs);
        Assert.Contains(logs.Messages, message =>
            message.Contains("early start at final True, speculative start True", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("what is the rollout plan", "what is the rollout plan.")]
    [InlineData("What is the rollout plan?", "What is the rollout plan?")]
    [InlineData("  ", "")]
    public void APausedPartialIsJudgedAsAClosedSentence(string partial, string closed) =>
        Assert.Equal(closed, ReplyTrigger.Closed(partial));

    private static async Task<T> NextAsync<T>(Channel<T> signals)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        return await signals.Reader.ReadAsync(timeout.Token);
    }

    /// <summary>One search call of the test provider: released by the test, and noted when it is cancelled.</summary>
    private sealed record RetrievalCall(string Query, CancellationToken Cancellation)
    {
        public TaskCompletionSource<Grounding> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>
    /// Replies name their target (case and punctuation folded), so a test can tell which utterance a sent line answers. An
    /// answer can be held after its first line and made to ignore cancellation; answers or the classifier can fail.
    /// </summary>
    private sealed class SpeculationProvider : IMeetingProvider
    {
        private int answers;
        private int active;
        private int maxActive;
        public int Retrievals;
        public int Answers => Volatile.Read(ref answers);
        public int Active => Volatile.Read(ref active);
        public int MaxActive => Volatile.Read(ref maxActive);
        public TaskCompletionSource? Hold { get; set; }
        public bool IgnoreCancellation { get; init; }
        public int FailingAnswers { get; init; }
        /// <summary>The first answers stream their first line, then the content filter stops them, as the Azure provider reports it.</summary>
        public int FilteredAnswers { get; init; }
        public System.Collections.Concurrent.ConcurrentQueue<string> Translations { get; } = new();
        public int FailingRetrievals { get; init; }
        /// <summary>Every search blocks until the test releases it.</summary>
        public bool BlockRetrievals { get; init; }
        /// <summary>The first blocked searches also ignore cancellation, as a slow search call might, and keep the worker busy.</summary>
        public int StubbornRetrievals { get; init; }
        /// <summary>Every search, in call order.</summary>
        public Channel<RetrievalCall> RetrievalCalls { get; } = Channel.CreateUnbounded<RetrievalCall>();
        public Exception? ClassifierFailure { get; init; }
        /// <summary>Each answer's target, once the session has taken that answer's first line.</summary>
        public Channel<string> FirstLines { get; } = Channel.CreateUnbounded<string>();
        /// <summary>Each answer's target, once that answer ended, completed or not.</summary>
        public Channel<string> Finished { get; } = Channel.CreateUnbounded<string>();
        public System.Collections.Concurrent.ConcurrentQueue<ConversationTurn[]> Histories { get; } = new();
        private SpeechStub? speech;

        public static string ReplyTo(string target) => "Reply to: " + ResponseRouting.Identity(target);

        public void Emit(Transcript transcript) => speech!.Emit(transcript);

        public Task<ISpeechStream> StartSpeechAsync(Action<Transcript> transcript, Action<ProviderException> error, CancellationToken cancellation)
        {
            speech = new SpeechStub(transcript, error);
            return Task.FromResult<ISpeechStream>(speech);
        }

        public Task<Grounding> RetrieveAsync(string query, string objectId, CancellationToken cancellation)
        {
            var number = Interlocked.Increment(ref Retrievals);
            var call = new RetrievalCall(query, cancellation);
            cancellation.Register(() => call.Cancelled.TrySetResult());
            RetrievalCalls.Writer.TryWrite(call);
            // Like the Azure provider, a failed search is reported as grounding_unavailable.
            if (number <= FailingRetrievals)
                return Task.FromException<Grounding>(new ProviderException("grounding_unavailable", "Synthetic search failure."));
            if (!BlockRetrievals) return Task.FromResult(new Grounding("no_matches", []));
            return number <= StubbornRetrievals ? call.Release.Task : call.Release.Task.WaitAsync(cancellation);
        }

        public IAsyncEnumerable<string> AnswerAsync(IReadOnlyList<ConversationTurn> conversation, Grounding grounding, CancellationToken cancellation) =>
            throw new InvalidOperationException("Replies use AnswerWithSuggestionsAsync.");

        public async IAsyncEnumerable<ReplyUpdate> AnswerWithSuggestionsAsync(IReadOnlyList<ConversationTurn> conversation, Grounding grounding,
            SessionOptions options, string responseRoute, [EnumeratorCancellation] CancellationToken cancellation)
        {
            var number = Interlocked.Increment(ref answers);
            var running = Interlocked.Increment(ref active);
            for (var seen = Volatile.Read(ref maxActive); seen < running; seen = Volatile.Read(ref maxActive))
                Interlocked.CompareExchange(ref maxActive, running, seen);
            var target = string.Join(' ', conversation.Where(turn => turn.CurrentResponseTarget).Select(turn => turn.Text));
            try
            {
                Histories.Enqueue(conversation.ToArray());
                await Task.Yield();
                if (number <= FailingAnswers) throw new InvalidOperationException("Synthetic answer failure.");
                yield return new(ReplyTo(target));
                if (number <= FilteredAnswers) throw new ProviderException("response_filtered", AzureMeetingProvider.FilteredMessage);
                yield return new("", PrimaryDone: true);
                FirstLines.Writer.TryWrite(target);
                if (Hold is { } hold) await (IgnoreCancellation ? hold.Task : hold.Task.WaitAsync(cancellation));
                yield return new("", "Second line.");
            }
            finally
            {
                Interlocked.Decrement(ref active);
                Finished.Writer.TryWrite(target);
            }
        }

        public Task<bool> ShouldRespondAsync(IReadOnlyList<ConversationTurn> conversation, CancellationToken cancellation) =>
            ClassifierFailure is { } failure ? Task.FromException<bool>(failure) : Task.FromResult(true);

        public Task<string?> TranslateReplyAsync(string english, TranslationContext context, CancellationToken cancellation)
        {
            Translations.Enqueue(english);
            return Task.FromResult<string?>("KO " + english);
        }
    }
}
