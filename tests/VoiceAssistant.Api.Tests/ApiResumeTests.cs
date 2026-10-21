using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using VoiceAssistant.Api;
using Xunit;

namespace VoiceAssistant.Api.Tests;

public sealed partial class ApiTests
{
    private const string EarlierTurn = "We agreed on the fictional Lumen pricing tiers.";
    private const string FollowUp = "What about the price?";
    private const string OtherObjectId = "6b7f1d52-3c4e-4f80-9a1b-2c3d4e5f6a7b";

    [Theory]
    [InlineData("closed")]
    [InlineData("dropped")]
    [InlineData("superseded")]
    public async Task AReconnectThatAsksToResumeContinuesTheConversationTheModelSees(string end)
    {
        using var capture = new MetricCapture();
        var logs = new CollectingLogs();
        var provider = new RestartingSpeechProvider();
        await using var host = await Host.StartAsync(provider: provider, logs: logs);
        using var first = await host.ConnectAsync();
        Assert.False((await OpenConversationAsync(first, resume: false)).TryGetProperty("resumedTurns", out _));
        await SayAsync(first, provider.Stream(0), "earlier", EarlierTurn);

        Task<List<JsonElement>>? closing = null;
        if (end == "closed") await first.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
        else if (end == "dropped") first.Abort();
        // The reconnect arrives while the old socket is still open, as after a network drop the server has not noticed yet.
        else closing = AnswerCloseAsync(first);
        using var second = await host.ConnectAsync();
        var ready = await OpenConversationAsync(second, resume: true);
        Assert.Equal(1, ready.GetProperty("resumedTurns").GetInt32());
        if (closing is not null)
            Assert.Contains(await closing, message => TypeOf(message) == "error" &&
                message.GetProperty("code").GetString() == "session_superseded");

        await SayAsync(second, provider.Stream(1), "follow-up", FollowUp);
        // The follow-up is answered with the earlier turn as its context; only the follow-up is the reply target.
        Assert.Equal([$"context: {EarlierTurn}", $"target: {FollowUp}"], History(provider));
        Assert.Contains(capture.Named("voiceassistant.session.resume"), measurement => measurement.Tags.Contains(new("outcome", "restored")));
        Assert.Equal(1, Assert.Single(capture.Named("voiceassistant.session.resumed_turns")).Value);
        Assert.Contains(logs.Messages, message => message.EndsWith("Meeting conversation resumed; finalized turns restored: 1.",
            StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Messages, message => message.Contains("Lumen", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("price", StringComparison.OrdinalIgnoreCase));
        await Send(second, """{"type":"session.stop"}""");
    }

    [Fact]
    public async Task ARolloverAtTheSessionTimeLimitContinuesTheConversation()
    {
        var clock = new ManualClock();
        var provider = new RestartingSpeechProvider();
        await using var host = await Host.StartAsync(provider: provider, clock: clock,
            configuration: new() { ["Session:MaxMinutes"] = "5" });
        using (var first = await host.ConnectAsync())
        {
            await OpenConversationAsync(first, resume: false);
            await SayAsync(first, provider.Stream(0), "earlier", EarlierTurn);
            var closing = AnswerCloseAsync(first);
            clock.Advance(TimeSpan.FromMinutes(5));
            Assert.Contains(await closing, message => TypeOf(message) == "error" &&
                message.GetProperty("code").GetString() == "session_time_limit");
        }
        using var second = await host.ConnectAsync();
        Assert.Equal(1, (await OpenConversationAsync(second, resume: true)).GetProperty("resumedTurns").GetInt32());
        await SayAsync(second, provider.Stream(1), "follow-up", FollowUp);
        Assert.Equal([$"context: {EarlierTurn}", $"target: {FollowUp}"], History(provider));
        await Send(second, """{"type":"session.stop"}""");
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, false)]
    public async Task AConversationIsResumedOnlyWithinItsLifetime(int secondsAfterLifetime, bool restored)
    {
        using var capture = new MetricCapture();
        var clock = new ManualClock();
        var provider = new RestartingSpeechProvider();
        await using var host = await Host.StartAsync(provider: provider, clock: clock);
        await EndConversationAsync(host, provider.Stream, 0, EarlierTurn);
        clock.Advance(ResumableConversations.Lifetime + TimeSpan.FromSeconds(secondsAfterLifetime));

        using var second = await host.ConnectAsync();
        Assert.Equal(restored, (await OpenConversationAsync(second, resume: true)).TryGetProperty("resumedTurns", out _));
        await SayAsync(second, provider.Stream(1), "follow-up", FollowUp);
        string[] expected = restored ? [$"context: {EarlierTurn}", $"target: {FollowUp}"] : [$"target: {FollowUp}"];
        Assert.Equal(expected, History(provider));
        Assert.Contains(capture.Named("voiceassistant.session.resume"), measurement =>
            measurement.Tags.Contains(new("outcome", restored ? "restored" : "expired")));
        await Send(second, """{"type":"session.stop"}""");
    }

    [Fact]
    public async Task AnotherUsersConversationIsNeverResumed()
    {
        var provider = new RestartingSpeechProvider();
        await using var host = await Host.StartAsync(azure: true, provider: provider);
        using (var first = await ConnectAsAsync(host, ObjectId))
        {
            await OpenConversationAsync(first, resume: false);
            await SayAsync(first, provider.Stream(0), "earlier", EarlierTurn);
            await first.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
        }
        using (var stranger = await ConnectAsAsync(host, OtherObjectId))
        {
            Assert.False((await OpenConversationAsync(stranger, resume: true)).TryGetProperty("resumedTurns", out _));
            await SayAsync(stranger, provider.Stream(1), "stranger", FollowUp);
            Assert.Equal([$"target: {FollowUp}"], History(provider));
            await stranger.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
        }
        // The other user's start left the owner's conversation alone, and the owner never gets the other user's.
        using var owner = await ConnectAsAsync(host, ObjectId);
        Assert.Equal(1, (await OpenConversationAsync(owner, resume: true)).GetProperty("resumedTurns").GetInt32());
        await SayAsync(owner, provider.Stream(2), "owner", "Shall we start?");
        Assert.Equal([$"context: {EarlierTurn}", "target: Shall we start?"], History(provider));
        await Send(owner, """{"type":"session.stop"}""");
    }

    [Fact]
    public async Task AStartWithoutTheFlagIsFreshAndDiscardsTheKeptConversation()
    {
        using var capture = new MetricCapture();
        var provider = new RestartingSpeechProvider();
        await using var host = await Host.StartAsync(provider: provider);
        var kept = host.Services.GetRequiredService<ResumableConversations>();
        await EndConversationAsync(host, provider.Stream, 0, EarlierTurn);
        Assert.Equal(1, kept.Count);

        using (var fresh = await host.ConnectAsync())
        {
            Assert.False((await OpenConversationAsync(fresh, resume: false)).TryGetProperty("resumedTurns", out _));
            Assert.Equal(0, kept.Count);
            fresh.Abort();
        }
        // Nothing was said in the fresh session, so nothing is left for a later reconnect either.
        using var reconnect = await host.ConnectAsync();
        Assert.False((await OpenConversationAsync(reconnect, resume: true)).TryGetProperty("resumedTurns", out _));
        await SayAsync(reconnect, provider.Stream(2), "follow-up", FollowUp);
        Assert.Equal([$"target: {FollowUp}"], History(provider));
        Assert.Contains(capture.Named("voiceassistant.session.resume"), measurement => measurement.Tags.Contains(new("outcome", "none")));
        Assert.Empty(capture.Named("voiceassistant.session.resumed_turns"));
        await Send(reconnect, """{"type":"session.stop"}""");
    }

    [Fact]
    public async Task AKeptConversationIsResumedOnceCarriedOnAndDroppedWhenTheUserStops()
    {
        var provider = new RestartingSpeechProvider();
        await using var host = await Host.StartAsync(provider: provider);
        var kept = host.Services.GetRequiredService<ResumableConversations>();
        await EndConversationAsync(host, provider.Stream, 0, EarlierTurn);
        using (var second = await host.ConnectAsync())
        {
            Assert.Equal(1, (await OpenConversationAsync(second, resume: true)).GetProperty("resumedTurns").GetInt32());
            // Taken out: no other start can resume it while this session runs.
            Assert.Equal(0, kept.Count);
            await SayAsync(second, provider.Stream(1), "follow-up", FollowUp);
            second.Abort();
        }
        using (var third = await host.ConnectAsync())
        {
            // The resumed session kept what it continued with plus its own turn.
            Assert.Equal(2, (await OpenConversationAsync(third, resume: true)).GetProperty("resumedTurns").GetInt32());
            await Send(third, """{"type":"session.stop"}""");
            await ReceiveUntilCloseAsync(third);
        }
        // The user stopped: only a fresh start follows, so nothing is kept.
        Assert.Equal(0, kept.Count);
        using var fourth = await host.ConnectAsync();
        Assert.False((await OpenConversationAsync(fourth, resume: true)).TryGetProperty("resumedTurns", out _));
        await Send(fourth, """{"type":"session.stop"}""");
    }

    [Fact]
    public async Task RecognitionOnlySessionsNeitherKeepNorResumeAConversation()
    {
        var provider = new RestartingSpeechProvider();
        await using var host = await Host.StartAsync(provider: provider);
        var kept = host.Services.GetRequiredService<ResumableConversations>();
        using (var practice = await host.ConnectAsync())
        {
            await Send(practice, WithOptions(new { transcribeOnly = true, endSilenceMs = 500 }));
            Assert.Equal("session.ready", TypeOf(await Receive(practice)));
            provider.Stream(0).Emit(new("practice", 1, EarlierTurn, true));
            Assert.Equal("transcript.final", TypeOf(await Receive(practice)));
            await practice.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
        }
        Assert.Equal(0, kept.Count);

        await EndConversationAsync(host, provider.Stream, 1, EarlierTurn);
        Assert.Equal(1, kept.Count);
        using (var practice = await host.ConnectAsync())
        {
            await Send(practice, WithOptions(new { transcribeOnly = true, endSilenceMs = 500, resumeConversation = true }));
            var ready = await Receive(practice);
            Assert.Equal("session.ready", TypeOf(ready));
            Assert.False(ready.TryGetProperty("resumedTurns", out _));
            // Like any start, it takes the kept conversation out, so it is not resumed after it either.
            Assert.Equal(0, kept.Count);
            await practice.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
        }
        Assert.Equal(0, kept.Count);
    }

    [Fact]
    public async Task AResumedConversationKeepsTheBoundedHistoryWithTheExcerptsOfOlderTurns()
    {
        // The settle window never passes on this clock, so the first session gathers its finals without replying.
        var clock = new ManualClock();
        var provider = new RestartingSpeechProvider();
        await using var host = await Host.StartAsync(provider: provider, clock: clock);
        var turns = Enumerable.Range(1, 14).Select(turn => $"Synthetic statement number {turn}.").ToArray();
        using (var first = await host.ConnectAsync())
        {
            await Send(first, WithOptions(new { responseMode = "conversation", replySettleMs = 1000 }));
            Assert.Equal("session.ready", TypeOf(await Receive(first)));
            for (var turn = 0; turn < turns.Length; turn++)
            {
                provider.Stream(0).Emit(new($"t{turn}", 1, turns[turn], true));
                Assert.Equal("transcript.final", TypeOf(await Receive(first)));
            }
            await first.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
        }
        var kept = host.Services.GetRequiredService<ResumableConversations>();
        Assert.Equal(1, kept.Count);

        using var second = await host.ConnectAsync();
        Assert.Equal(12, (await OpenConversationAsync(second, resume: true)).GetProperty("resumedTurns").GetInt32());
        await SayAsync(second, provider.Stream(1), "follow-up", FollowUp);
        // Exactly the history an uninterrupted session would give: excerpts of the three oldest turns, then the twelve latest.
        Assert.Equal(turns.Select(turn => $"context: {turn}").Append($"target: {FollowUp}"), History(provider));
        await Send(second, """{"type":"session.stop"}""");
    }

    /// <summary>The history of the latest model call, each turn marked as context or as the reply target.</summary>
    private static string[] History(RestartingSpeechProvider provider) =>
        provider.LastHistory!.Select(turn => $"{(turn.CurrentResponseTarget ? "target" : "context")}: {turn.Text}").ToArray();

    /// <summary>Starts a conversation-mode meeting session (no Search), asking to resume or not; returns its session.ready.</summary>
    private static async Task<JsonElement> OpenConversationAsync(ClientWebSocket socket, bool resume)
    {
        await Send(socket, resume ? WithOptions(new { responseMode = "conversation", resumeConversation = true })
            : WithOptions(new { responseMode = "conversation" }));
        var ready = await Receive(socket);
        Assert.Equal("session.ready", TypeOf(ready));
        return ready;
    }

    /// <summary>A final utterance, answered.</summary>
    private static async Task SayAsync(ClientWebSocket socket, SpeechStub stream, string turnId, string text)
    {
        stream.Emit(new(turnId, 1, text, true));
        await ReceiveUntilAsync(socket, Completed);
    }

    /// <summary>A fresh meeting session that says one answered utterance, then ends because the client closes the socket.</summary>
    private static async Task EndConversationAsync(Host host, Func<int, SpeechStub> stream, int index, string text)
    {
        using var socket = await host.ConnectAsync();
        await OpenConversationAsync(socket, resume: false);
        await SayAsync(socket, stream(index), $"turn-{index}", text);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
    }

    private static async Task<ClientWebSocket> ConnectAsAsync(Host host, string objectId)
    {
        using var client = host.Client;
        client.DefaultRequestHeaders.Authorization = new("Bearer", host.Token(oid: objectId));
        using var response = await client.PostAsync("/api/session/ticket", null);
        response.EnsureSuccessStatusCode();
        var ticket = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ticket").GetString()!;
        return await host.ConnectAsync(ticket, "https://meeting.example");
    }
}

public sealed class ResumableConversationsTests
{
    private static ConversationSnapshot Conversation(params string[] turns) => new(turns.Select(text => new ConversationTurn(text)).ToArray(), []);

    [Fact]
    public void AConversationIsTakenOnceOnlyByItsOwnUserAndOnlyWithinItsLifetime()
    {
        var clock = new ManualClock();
        var store = new ResumableConversations(clock);
        store.Save("user-a", Conversation("First"));
        Assert.Null(store.Take("user-b", out var expired));
        Assert.False(expired);
        clock.Advance(ResumableConversations.Lifetime - TimeSpan.FromSeconds(1));
        Assert.Equal(["First"], store.Take("user-a", out expired)!.Recent.Select(turn => turn.Text));
        Assert.False(expired);
        Assert.Null(store.Take("user-a", out expired));
        Assert.False(expired);

        store.Save("user-a", Conversation("Second"));
        clock.Advance(ResumableConversations.Lifetime);
        Assert.Null(store.Take("user-a", out expired));
        Assert.True(expired);
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void EachUserKeepsOnlyTheLatestConversationAndTheOldestGivesWayAtCapacity()
    {
        var clock = new ManualClock();
        var store = new ResumableConversations(clock);
        store.Save("user-0", Conversation("Older"));
        store.Save("user-0", Conversation("Newer"));
        Assert.Equal(1, store.Count);
        for (var user = 1; user < ResumableConversations.Capacity; user++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            store.Save($"user-{user}", Conversation($"Turn {user}"));
        }
        Assert.Equal(ResumableConversations.Capacity, store.Count);

        clock.Advance(TimeSpan.FromSeconds(1));
        store.Save("user-new", Conversation("Latest"));
        Assert.Equal(ResumableConversations.Capacity, store.Count);
        Assert.Null(store.Take("user-0", out _));
        Assert.Equal(["Turn 1"], store.Take("user-1", out _)!.Recent.Select(turn => turn.Text));
        Assert.Equal(["Latest"], store.Take("user-new", out _)!.Recent.Select(turn => turn.Text));
    }

    [Fact]
    public void ExpiredConversationsArePrunedOnAccessAndAnEmptyOneIsNotKept()
    {
        var clock = new ManualClock();
        var store = new ResumableConversations(clock);
        store.Save("user-a", Conversation("A"));
        store.Save("user-b", Conversation("B"));
        clock.Advance(ResumableConversations.Lifetime);
        store.Save("user-c", Conversation("C"));
        Assert.Equal(1, store.Count);
        store.Save("user-c", Conversation());
        Assert.Equal(0, store.Count);
    }
}
