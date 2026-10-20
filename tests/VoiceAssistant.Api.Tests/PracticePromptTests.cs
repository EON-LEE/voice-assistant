using VoiceAssistant.Api.Practice;
using Xunit;

namespace VoiceAssistant.Api.Tests;

public sealed class PracticePromptTests
{
    private static PracticeRequest Request(string operation) => operation switch
    {
        "turn" => new("turn", Scenario: new("interview", "", 2), MaxTurns: 6, History: []),
        "suggest" => new("suggest", Scenario: new("sales", "", 2), Question: "What is the goal?"),
        "feedback" => new("feedback", Scenario: new("sales", "", 2), Question: "What is the goal?", Answer: "We start early."),
        "summary" => new("summary", Scenario: new("sales", "", 2), Topic: "Rollout",
            Turns: [new("What is the goal?", "We start early.", null)]),
        "enrich" => new("enrich", "The main risk is slow migration.", "reply"),
        _ => throw new ArgumentOutOfRangeException(nameof(operation))
    };

    [Theory]
    [InlineData("turn")]
    [InlineData("suggest")]
    [InlineData("feedback")]
    [InlineData("summary")]
    [InlineData("enrich")]
    public void NoOperationCarriesSentencesTheModelCopiedAsItsOwnAnswer(string operation)
    {
        var prompt = AzurePracticeModel.Instructions(Request(operation));
        Assert.DoesNotContain("clear goals and small, safe steps", prompt);
        Assert.DoesNotContain("check that detail", prompt);
        Assert.Contains("untrusted data", prompt);
        Assert.Contains("ONLY the words the speaker would actually say", prompt);
        Assert.Contains("never pronunciation/accent", prompt);
    }

    [Fact]
    public void PartnerTurnIsToldItIsThePartnerAndNeverReceivesLearnerWording()
    {
        var prompt = AzurePracticeModel.Instructions(Request("turn"));
        Assert.Contains("PARTNER", prompt);
        Assert.Contains("never the learner and never a coach", prompt);
        Assert.Contains("different point", prompt);
        Assert.Contains("never describe your own work", prompt);
        Assert.DoesNotContain("first-person speaker wording", prompt);
        Assert.DoesNotContain("With no materials, offer", prompt);
    }

    [Theory]
    [InlineData("suggest")]
    [InlineData("feedback")]
    [InlineData("summary")]
    public void LearnerOperationsKeepFirstPersonWordingAndHedgeOnlyAboutMissingDetails(string operation)
    {
        var prompt = AzurePracticeModel.Instructions(Request(operation));
        Assert.Contains("first-person speaker wording", prompt);
        Assert.Contains("not a made-up fact", prompt);
        Assert.Contains("answers the question, give that answer directly", prompt);
        Assert.Contains("Hedge only about a specific detail that is truly missing", prompt);
        Assert.DoesNotContain("PARTNER", prompt);
    }

    [Fact]
    public void EnrichmentGetsNeitherPartnerNorLearnerBlocks()
    {
        var prompt = AzurePracticeModel.Instructions(Request("enrich"));
        Assert.DoesNotContain("PARTNER", prompt);
        Assert.DoesNotContain("first-person speaker wording", prompt);
        Assert.Contains("Pronunciation ko chunks", prompt);
    }

    [Fact]
    public void TranslationOnlyKeepsItsOwnRulesAndNoPracticeCoachPolicy()
    {
        var prompt = AzurePracticeModel.Instructions(new("enrich", "Is it ready?", "question", TranslationOnly: true));
        Assert.Contains("Translate the supplied English into natural Korean", prompt);
        Assert.Contains("do not explain, coach, or speculate", prompt);
        Assert.DoesNotContain("PARTNER", prompt);
        Assert.DoesNotContain("English meeting practice coach", prompt);
    }
}
