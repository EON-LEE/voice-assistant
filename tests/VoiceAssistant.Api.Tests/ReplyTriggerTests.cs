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

    [Theory]
    [InlineData("Tell me about yourself.")]
    [InlineData("Walk us through the project timeline.")]
    [InlineData("Please describe your role on the Lighthouse project.")]
    [InlineData("Give us a quick update on the risks.")]
    [InlineData("Let's talk about the budget.")]
    [InlineData("I'd like you to explain the next steps.")]
    [InlineData("Explain how you would handle a missed deadline.")]
    [InlineData("Share your thoughts on the launch plan.")]
    [InlineData("I was wondering when the review is happening.")]
    [InlineData("So, tell me about yourself.")]
    [InlineData("Go ahead.")]
    [InlineData("Over to you.")]
    [InlineData("If you could walk us through the plan.")]
    [InlineData("I want you to describe the rollout.")]
    [InlineData("We’re curious how the migration went.")]
    [InlineData("Maybe you could tell us about the budget.")]
    [InlineData("Thoughts on the launch plan.")]
    [InlineData("Help me understand why the date moved.")]
    [InlineData("Please confirm the delivery date.")]
    [InlineData("Just walk us through the plan.")]
    [InlineData("Now, Eon, walk us through the rollout.")]
    [InlineData("Back to you, Eon.")]
    public void DirectRequestsWithoutQuestionMarksGetReplies(string speech)
    {
        Assert.Equal(ReplyDecision.Reply, ReplyTrigger.Evaluate([speech]));
        Assert.True(ReplyTrigger.ContainsRequest([speech]));
        Assert.True(ReplyTrigger.InvitesReply([speech]));
        Assert.False(ReplyTrigger.ContainsQuestion([speech]));
    }

    [Theory]
    [InlineData("Thanks for joining everyone.")]
    [InlineData("Let's get started.")]
    [InlineData("Give me a second.")]
    [InlineData("Give us a few minutes.")]
    [InlineData("Share your screen.")]
    [InlineData("We can talk about it later.")]
    [InlineData("I want you to know that we appreciate your time.")]
    [InlineData("I'd like to introduce our new colleague.")]
    [InlineData("I'd like you to meet John.")]
    [InlineData("Explaining the process takes a while.")]
    [InlineData("I can't tell us about that yet.")]
    [InlineData("I'll get back to you tomorrow.")]
    [InlineData("He will get right back to you on that.")]
    [InlineData("John will walk us through the plan next week.")]
    [InlineData("She'll walk us through the numbers.")]
    [InlineData("We want Maria to walk us through the budget.")]
    public void StatementsAndLogisticsAreNotRequests(string speech)
    {
        Assert.Equal(ReplyDecision.Wait, ReplyTrigger.Evaluate([speech]));
        Assert.False(ReplyTrigger.ContainsRequest([speech]));
        Assert.False(ReplyTrigger.InvitesReply([speech]));
    }

    [Fact]
    public void RequestsAreFoundInAnySentenceOfTheGatheredSpeech() =>
        Assert.True(ReplyTrigger.InvitesReply(["The numbers look fine.", "OK, walk us through the plan."]));

    [Theory]
    [InlineData(true, "Which vendor did we pick for the data migration?")]
    [InlineData(true, "What are you talking about?")]
    [InlineData(true, "Can you tell me what the deadline is?")]
    [InlineData(true, "Do you know who we're meeting with?")]
    [InlineData(true, "Let's talk about the budget.")]
    [InlineData(true, "Tell me about yourself.")]
    [InlineData(true, "“Where?”")]
    [InlineData(true, "Could you do that? Yeah.")]
    [InlineData(false, "Could you walk us through?")]
    [InlineData(false, "Could you tell me about the")]
    [InlineData(false, "Could you walk us through…")]
    [InlineData(false, "Could you walk us through...")]
    [InlineData(false, "Could you tell us, um.")]
    [InlineData(false, "And when does the.")]
    [InlineData(false, "So I was wondering if.")]
    [InlineData(false, "What do we?")]
    [InlineData(false, "Do you want to?")]
    [InlineData(false, "")]
    public void CompletenessIgnoresFragmentsThatEndMidThought(bool expected, string last) =>
        Assert.Equal(expected, ReplyTrigger.LooksComplete(["Earlier sentence.", last]));

    [Fact]
    public void CompletenessOnlyLooksAtTheLatestFinal()
    {
        Assert.False(ReplyTrigger.LooksComplete([]));
        Assert.False(ReplyTrigger.LooksComplete(["Which vendor did we pick?", "And when does the"]));
        Assert.True(ReplyTrigger.LooksComplete(["Could you walk us through", "the timeline?"]));
    }
}
