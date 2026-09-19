using System.Text.Json;
using VoiceAssistant.LiveProbe;
using Xunit;

namespace VoiceAssistant.LiveProbe.Tests;

public sealed class RecognitionQualityTests
{
    [Theory]
    [InlineData("One two three four", "one two three four", 0, 4, true)]
    [InlineData("One two three four", "one extra two three four", 1, 5, true)]
    [InlineData("One two three four", "one two four", 1, 3, true)]
    [InlineData("One two three four", "one two different four", 1, 4, true)]
    [InlineData("One two three four", "one different different four", 2, 4, false)]
    [InlineData("One two", "one two three four five", 3, 5, false)]
    [InlineData("One two", "", 2, 0, false)]
    [InlineData("Project Lumen needs a brief update by Friday.", "PROJECT lumen needs a brief update, by Friday!", 0, 8, true)]
    [InlineData("Don't re-test.", "DON’T re test!", 0, 3, true)]
    public void ComputesWordEditsAndInclusiveThreshold(string reference, string recognized, int edits, int actualWords, bool passed)
    {
        var result = WordErrorRate.Measure(reference, recognized);
        Assert.True(result.ReferencePresent);
        Assert.False(result.QualityNotMeasured);
        Assert.Equal(edits, result.WordEditCount);
        Assert.Equal(actualWords, result.RecognizedWordCount);
        Assert.Equal((double)edits / result.ReferenceWordCount!.Value, result.WordErrorRate);
        Assert.Equal(passed, result.Passed);
        Assert.Equal(0.25, result.MaximumWordErrorRate);
    }

    [Fact]
    public void MissingReferenceIsExplicitlyUnmeasured()
    {
        var result = WordErrorRate.Measure(null, "private recognized content");
        Assert.False(result.ReferencePresent);
        Assert.True(result.QualityNotMeasured);
        Assert.Null(result.Passed);
        Assert.Null(result.WordEditCount);
        Assert.Null(result.WordErrorRate);
        Assert.Null(result.ReferenceWordCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("...!?")]
    public void EmptySuppliedReferenceIsInvalidNotVerified(string reference) =>
        Assert.Throws<InvalidDataException>(() => WordErrorRate.Measure(reference, "anything"));

    [Fact]
    public void BoundsInputAndNeverIncludesContentInResultsOrErrors()
    {
        Assert.Throws<InvalidDataException>(() => WordErrorRate.Measure(new string('x', 2049), "text"));
        Assert.Throws<InvalidDataException>(() => WordErrorRate.Measure("text", new string('x', 8001)));
        var result = WordErrorRate.Measure("private reference sentence", "private recognized sentence");
        var json = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("private", json);
        Assert.DoesNotContain("reference sentence", json);
        Assert.DoesNotContain("recognized sentence", json);
    }
}
