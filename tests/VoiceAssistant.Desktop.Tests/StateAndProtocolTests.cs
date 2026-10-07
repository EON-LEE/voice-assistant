using System.Text;
using VoiceAssistant.Desktop.Protocol;

namespace VoiceAssistant.Desktop.Tests;

public sealed class StateAndProtocolTests
{
    [Fact]
    public void RevisionsDoNotRegressAndFinalCannotBecomePartial()
    {
        var state = new ReplyState();
        state.Apply(new("transcript.partial", "a", 3, "new"));
        state.Apply(new("transcript.partial", "a", 2, "old"));
        Assert.Equal("new", state.Turns.Single().Text);
        state.Apply(new("transcript.final", "a", 3, "final"));
        state.Apply(new("transcript.partial", "a", 4, "late"));
        Assert.Equal("final", state.Turns.Single().Text);
        Assert.True(state.Turns.Single().IsFinal);
    }

    [Fact]
    public void StreamsIgnoreObsoleteIdsAndKeepPinnedSnapshotUnchanged()
    {
        var state = new ReplyState();
        state.Apply(new("transcript.final", "t", 1, "Question"));
        state.Apply(new("response.started", "t", ResponseId: "one"));
        state.Apply(new("response.delta", "t", Text: "Pinned", ResponseId: "one"));
        state.Pin();
        state.Apply(new("response.delta", "t", Text: " more", ResponseId: "one"));
        state.Apply(new("response.started", "t", ResponseId: "two"));
        state.Apply(new("response.delta", "t", Text: "stale", ResponseId: "one"));
        state.Apply(new("response.completed", "t", Text: "New answer", ResponseId: "two",
            Sources: [new("Source", "https://example.org", "2026-09-17")]));
        state.Apply(new("response.delta", "t", Text: "late", ResponseId: "two"));
        Assert.Equal("Pinned", state.Pinned!.Text);
        Assert.Equal("New answer", state.Current!.Text);
        Assert.Single(state.Current.Sources);
        state.Apply(new("response.started", "t", ResponseId: "one"));
        Assert.Equal("two", state.Current.ResponseId);
    }

    [Fact]
    public void NewTurnPauseAndLocalCancelSuppressLateAnswers()
    {
        var state = new ReplyState();
        state.Apply(new("transcript.final", "old", 1, "old"));
        state.Apply(new("response.started", "old", ResponseId: "old-r"));
        state.Apply(new("transcript.partial", "new", 1, "new"));
        Assert.Equal("old-r", state.Current!.ResponseId);
        state.Apply(new("transcript.final", "new", 2, "new"));
        state.Apply(new("response.started", "old", ResponseId: "late"));
        Assert.Equal("old-r", state.Current.ResponseId);
        state.Pause(true);
        state.Apply(new("response.started", "new", ResponseId: "paused"));
        Assert.Null(state.Current);
        state.Pause(false);
        state.Apply(new("response.started", "new", ResponseId: "current"));
        state.CancelCurrent();
        state.Apply(new("response.started", "new", ResponseId: "late-cancel"));
        Assert.Null(state.Current);
        state.BeginRequest();
        state.Apply(new("response.started", "new", ResponseId: "requested"));
        Assert.Equal("requested", state.Current!.ResponseId);
    }

    [Fact]
    public void ContinuousSpeechDoesNotDiscardAnAlreadyStartedReply()
    {
        var state = new ReplyState();
        state.Apply(new("transcript.final", "one", 1, "Launch?"));
        state.Apply(new("response.started", "one", ResponseId: "r1"));
        state.Apply(new("transcript.partial", "two", 1, "And"));
        state.Apply(new("response.delta", "one", Text: "Review first.", ResponseId: "r1"));
        state.Apply(new("transcript.final", "two", 2, "And who owns launch?"));
        state.Apply(new("response.completed", "one", Text: "Review first.", ResponseId: "r1",
            Sources: [], Suggestions: ["Review first.", "Could we review the findings together?"]));
        Assert.Equal(2, state.Display!.Answers.Count);
        state.Apply(new("response.started", "two", ResponseId: "r2"));
        Assert.Equal("r1", state.Display.ResponseId);
        state.Apply(new("response.delta", "one", Text: "stale", ResponseId: "r1"));
        Assert.Equal("", state.Current!.Text);
    }

    [Fact]
    public void BoundedTranscriptHistoryAndPinnedLifetime()
    {
        var state = new ReplyState();
        for (int i = 0; i < 100; i++) state.Apply(new("transcript.final", $"t{i}", 1, "text"));
        Assert.Equal(64, state.Turns.Count);
        state.Apply(new("response.started", "t99", ResponseId: "r"));
        state.Apply(new("response.delta", "t99", Text: "keep", ResponseId: "r"));
        state.Pin();
        state.ResetSession();
        Assert.Empty(state.Turns);
        Assert.Null(state.Current);
        Assert.Equal("keep", state.Pinned!.Text);
    }

    [Fact]
    public void ParsesContractSourcesAndRetryableErrors()
    {
        var completed = Parse("""{"type":"response.completed","responseId":"r","turnId":"t","text":"yes","sources":[{"title":"guide","url":"https://example.org","updatedAt":null}]}""");
        Assert.Equal("yes", completed.Text);
        Assert.Null(completed.Sources![0].UpdatedAt);
        Assert.True(Parse("""{"type":"error","code":"busy","message":"Try later","retryable":true}""").Retryable);
    }

    [Fact]
    public void CompletedSuggestionsRemainVisibleWhileNextTurnIsPreparingOrPaused()
    {
        var state = new ReplyState();
        state.Apply(new("transcript.final", "one", 1, "Launch?"));
        state.Apply(new("response.started", "one", ResponseId: "r1"));
        state.Apply(new("response.completed", "one", Text: "Review first.", ResponseId: "r1",
            Sources: [], Suggestions: ["Review first.", "We need a review before launch."]));
        state.Apply(new("transcript.partial", "two", 1, "When?"));
        Assert.Equal("r1", state.Display!.ResponseId);
        Assert.Equal(2, state.Display.Answers.Count);
        state.Apply(new("response.started", "two", ResponseId: "r2"));
        Assert.Equal("r1", state.Display.ResponseId);
        state.Apply(new("response.delta", "two", Text: "After", ResponseId: "r2"));
        Assert.Equal("After", state.Display.Text);
        Assert.Single(state.Display.Answers);
        state.Pause(true);
        Assert.Equal("r1", state.Display.ResponseId);
        state.ResetSession();
        Assert.Null(state.LastCompleted);
    }

    [Fact]
    public void ParsesTwoSuggestionsAndLegacySingleAnswer()
    {
        var message = Parse("""{"type":"response.completed","responseId":"r","turnId":"t","text":"Review first.","sources":[],"suggestions":["Review first.","Let's review first."]}""");
        Assert.Equal(2, message.Suggestions!.Count);
        var legacy = Parse("""{"type":"response.completed","responseId":"r","turnId":"t","text":"yes","sources":[]}""");
        Assert.Null(legacy.Suggestions);
    }

    [Fact]
    public void ParsesGroundingAndRouteMetadataForVisibleSearchState()
    {
        var completed = Parse("""{"type":"response.completed","responseId":"r","turnId":"t","text":"yes","sources":[],"grounding":"no_matches","responseRoute":"knowledge","retrievalPrefetched":false}""");
        var state = new ReplyState();
        state.Apply(new("transcript.final", "t", 1, "Question?"));
        state.Apply(new("response.started", "t", ResponseId: "r"));
        state.Apply(completed);
        Assert.Equal("no_matches", state.Current!.Grounding);
        Assert.Equal("knowledge", state.Current.ResponseRoute);
        Assert.False(state.Current.RetrievalPrefetched);
    }

    [Fact]
    public void ProductionSettingsRequireCanonicalAllowedOriginAndConfirmedProfile()
    {
        var settings = new ClientSettings { Mode = ConnectionMode.Production };
        Assert.Equal("https://voice-web.gentlesky-d6ba12c8.koreacentral.azurecontainerapps.io", settings.Origin);
        Assert.Equal("https://voice-web.gentlesky-d6ba12c8.koreacentral.azurecontainerapps.io/", settings.ApiBase.AbsoluteUri);
        Assert.Throws<InvalidOperationException>(() =>
            (settings with { Origin = "https://voice-web.gentlesky-d6ba12c8.koreacentral.azurecontainerapps.io/" }).Validate());
        Assert.Throws<InvalidOperationException>(() =>
            (settings with { ProfileName = "Ari", ProfileConfirmed = false }).Validate());
    }

    [Theory]
    [InlineData("""{"type":"transcript.partial","turnId":"t","text":"x","revision":-1}""")]
    [InlineData("""{"type":"response.delta","turnId":"t","text":"x"}""")]
    [InlineData("""{"type":"error","code":"bad","message":"bad"}""")]
    [InlineData("""{"type":"response.completed","turnId":"t","responseId":"r","text":"x"}""")]
    [InlineData("""{"type":"response.completed","turnId":"t","responseId":"r","text":"x","sources":[],"suggestions":[]}""")]
    [InlineData("""{"type":"response.completed","turnId":"t","responseId":"r","text":"x","sources":[],"suggestions":["wrong"]}""")]
    [InlineData("""{"type":"response.completed","turnId":"t","responseId":"r","text":"x","sources":[],"suggestions":["x","a","b"]}""")]
    [InlineData("""{"type":"unknown"}""")]
    public void RejectsMalformedContractEvents(string json) => Assert.Throws<InvalidDataException>(() => Parse(json));

    [Theory]
    [InlineData("ws://example.org/api/meeting", ConnectionMode.Development)]
    [InlineData("ws://localhost:8080/api/meeting", ConnectionMode.Production)]
    [InlineData("wss://example.org/api/meeting?token=secret", ConnectionMode.Production)]
    [InlineData("ws://user:pass@localhost/api/meeting", ConnectionMode.Development)]
    [InlineData("ws://localhost/other", ConnectionMode.Development)]
    public void RejectsUnsafeEndpoints(string endpoint, ConnectionMode mode) =>
        Assert.Throws<InvalidOperationException>(() => (new ClientSettings { Mode = mode, Endpoint = endpoint }).Validate());

    [Theory]
    [InlineData("ws://localhost:8080/api/meeting")]
    [InlineData("ws://127.0.0.1:8080/api/meeting")]
    [InlineData("ws://[::1]:8080/api/meeting")]
    public void AllowsExplicitLoopbackDevelopment(string endpoint) =>
        Assert.Equal(endpoint, (new ClientSettings { Mode = ConnectionMode.Development, Endpoint = endpoint }).Validate().AbsoluteUri);

    private static ServerEvent Parse(string json) => ServerEvent.Parse(Encoding.UTF8.GetBytes(json));
}
