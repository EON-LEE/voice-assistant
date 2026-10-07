using VoiceAssistant.Api;
using Xunit;

namespace VoiceAssistant.Api.Tests;

public sealed class RetrievalQueryTests
{
    [Fact]
    public void FollowUpIncludesRecentRecognizedTopicWithoutModelRewriting()
    {
        ConversationTurn[] history = [new("Our Lighthouse migration uses JMAP."), new("The client supports push updates.")];
        Assert.Equal("our lighthouse migration uses jmap. the client supports push updates. when can we ship it?",
            RetrievalQuery.Build("When can we ship it?", history));
    }

    [Fact]
    public void BoundsCurrentAndRecentContextPreservingBothEndsAndNegations()
    {
        var history = Enumerable.Range(0, 12).Select(index => new ConversationTurn(
            $"topic{index} " + new string('x', 8000) + " cannot ship")).ToArray();
        var query = RetrievalQuery.Build("current " + new string('y', 8000) + " not tomorrow", history);
        Assert.True(query.Length <= RetrievalQuery.MaximumLength);
        Assert.DoesNotContain("topic6", query);
        Assert.Contains("topic0", query);
        Assert.Contains("topic4", query);
        Assert.Contains("topic8", query);
        Assert.Contains("cannot ship", query);
        Assert.Contains("current", query);
        Assert.EndsWith("not tomorrow", query);
    }

    [Fact]
    public void OlderExcerptsAreBoundedActualSpeechAndOpeningTopicSurvivesLongMeeting()
    {
        var older = new EarlierTranscriptContext();
        older.Remember(new("Our Lighthouse migration uses JMAP. " + new string('x', 8000) + " We cannot promise a date."));
        for (var index = 1; index <= 100; index++) older.Remember(new($"Earlier recognized segment {index}."));
        var context = older.With([new("Can we deliver it tomorrow?")]);
        Assert.Equal(5, context.Length);
        Assert.True(context[0].Text.Length <= 300);
        Assert.StartsWith("Our Lighthouse migration uses JMAP.", context[0].Text);
        Assert.EndsWith("We cannot promise a date.", context[0].Text);
        Assert.Contains("…", context[0].Text);
        Assert.Equal("Earlier recognized segment 98.", context[1].Text);
        Assert.Contains("lighthouse", RetrievalQuery.Build(context[^1].Text, context[..^1]));
    }
}
