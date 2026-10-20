using VoiceAssistant.Api;
using Xunit;

namespace VoiceAssistant.Api.Tests;

public sealed partial class ApiTests
{
    private static readonly string DeckUrl = "https://my-materials.invalid/" + new string('a', 32);

    [Fact]
    public void SourcesAreDeduplicatedByUrlThenTitleInEvidenceOrder()
    {
        static Evidence Chunk(string title, string url) => new("Fictional chunk text.", new(title, url, null));
        var sources = MeetingSession.DistinctSources(
        [
            Chunk("Rollout deck", DeckUrl), Chunk("Release notes", "https://example.test/notes"), Chunk("Rollout deck", DeckUrl),
            Chunk("Untitled", ""), Chunk("untitled ", ""), Chunk("Rollout deck", "https://my-materials.invalid/" + new string('b', 32))
        ]);
        Assert.Equal(
            [DeckUrl, "https://example.test/notes", "", "https://my-materials.invalid/" + new string('b', 32)],
            sources.Select(source => source.Url));
        Assert.Equal("Untitled", sources[2].Title);
    }

    [Fact]
    public async Task CompletedSourcesListEachDocumentOnceWhileTheModelKeepsEveryChunk()
    {
        var provider = new ControlledProvider();
        await using var host = await Host.StartAsync(provider: provider);
        using var socket = await host.ConnectAsync();
        await Send(socket, WithOptions(new { responseMode = "grounded" }));
        await Receive(socket);
        provider.Emit(new("q", 1, "What does the rollout deck say about Friday?", true));
        await provider.RetrievalStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        provider.Release.SetResult(new("grounded",
        [
            new("Slide 1: the fictional rollout review is on Friday.", new("Rollout deck", DeckUrl, null)),
            new("Notes for the fictional build.", new("Release notes", "https://example.test/notes", null)),
            new("Slide 2: the fictional owner list.", new("Rollout deck", DeckUrl, null))
        ]));
        var completed = await Until(socket, "response.completed");
        Assert.Equal(["Rollout deck", "Release notes"],
            completed.GetProperty("sources").EnumerateArray().Select(source => source.GetProperty("title").GetString()));
        Assert.Equal(3, provider.LastGrounding?.Documents.Count);
        await Send(socket, """{"type":"session.stop"}""");
    }
}
