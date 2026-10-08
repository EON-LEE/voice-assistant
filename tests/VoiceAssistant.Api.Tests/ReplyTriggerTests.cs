using VoiceAssistant.Api;
using Xunit;

namespace VoiceAssistant.Api.Tests;

public sealed class ReplyTriggerTests
{
    [Theory]
    [InlineData(ReplyDecision.Discard, "Yeah.")]
    [InlineData(ReplyDecision.Discard, "OK, good.", "Yeah, yeah.")]
    [InlineData(ReplyDecision.Discard, "Umm.", "Right.", "I see.")]
    [InlineData(ReplyDecision.Reply, "Did you catch the news?")]
    [InlineData(ReplyDecision.Reply, "So what do you think about the price")]
    [InlineData(ReplyDecision.Reply, "Yeah.", "Is that whale?")]
    [InlineData(ReplyDecision.Wait, "So busy today I.")]
    [InlineData(ReplyDecision.Wait, "Module.", "Now.")]
    [InlineData(ReplyDecision.Reply, "So according to the group, we're going to be selling this remote control for 25 euros.")]
    [InlineData(ReplyDecision.Reply, "And then the small cut and break.", "I'm planning to come real soon.", "The weather was bad.")]
    public void GathersSpeechIntoMeaningfulUnits(ReplyDecision expected, params string[] pending) =>
        Assert.Equal(expected, ReplyTrigger.Evaluate(pending));

    [Fact]
    public void QuestionDetectionCoversQuestionMarksAndQuestionWords()
    {
        Assert.True(ReplyTrigger.ContainsQuestion(["We start Monday.", "How about the budget"]));
        Assert.False(ReplyTrigger.ContainsQuestion(["We start Monday.", "OK."]));
    }
}
