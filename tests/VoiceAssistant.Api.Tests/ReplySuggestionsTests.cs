using VoiceAssistant.Api;
using Xunit;

namespace VoiceAssistant.Api.Tests;

public sealed class ReplySuggestionsTests
{
    [Theory]
    [InlineData("First answer.\nSecond answer.")]
    [InlineData("First answer.\r\nSecond answer.")]
    public void SeparatorNeverLeaksEvenWhenEveryCharacterIsAChunk(string output)
    {
        var parser = new ReplySuggestions();
        var primary = string.Concat(output.Select(character => parser.Append(character.ToString())));
        Assert.Equal("First answer.", primary);
        Assert.Equal(["First answer.", "Second answer."], ReplySuggestions.Complete(primary, parser.Alternative));
    }

    [Fact]
    public void MissingAlternativeKeepsReadableSinglePrimary()
    {
        var parser = new ReplySuggestions();
        Assert.Equal("Let me check.", parser.Append("Let me check."));
        Assert.Equal(["Let me check."], ReplySuggestions.Complete("Let me check.", parser.Alternative));
    }

    [Theory]
    [InlineData("First answer.")]
    [InlineData("first answer.")]
    [InlineData(" ")]
    [InlineData("Second answer.\nUnexpected third answer.")]
    public void DuplicatesEmptyAndMalformedAlternativesFallBackSafely(string alternative) =>
        Assert.Single(ReplySuggestions.Complete("First answer.", alternative));

    [Fact]
    public void OversizedAlternativeAndCombinedOutputAreBounded()
    {
        Assert.Single(ReplySuggestions.Complete("First answer.", string.Join(' ', Enumerable.Repeat("word", 26))));
        var parser = new ReplySuggestions();
        parser.Append("First answer.\n");
        Assert.Equal("response_limit", Assert.Throws<ProviderException>(() => parser.Append(new string('x', 8000))).Code);
    }
}
