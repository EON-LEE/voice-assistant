using System.Text.Json;
using VoiceAssistant.Api;
using Xunit;

namespace VoiceAssistant.Api.Tests;

public sealed class KoreanTranslationTests
{
    [Theory]
    [InlineData("내일까지 고객에게 전달할 수 있을까요?", "내일까지 고객에게 전달할 수 있을까요?")]
    [InlineData("  \"담당자를 한 명 정하죠.\"  ", "담당자를 한 명 정하죠.")]
    [InlineData("“담당자를 한 명 정하죠.”", "담당자를 한 명 정하죠.")]
    [InlineData("Lighthouse 일정은 금요일까지입니다.", "Lighthouse 일정은 금요일까지입니다.")]
    [InlineData("다음 단계를\n확인해 볼게요.", "다음 단계를 확인해 볼게요.")]
    public void CleanAcceptsPlainKoreanAndTidiesIt(string raw, string expected) =>
        Assert.Equal(expected, KoreanTranslation.Clean(raw));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Let's check the next step.")]
    [InlineData("Let's check the next step and then 다음")]
    [InlineData("**다음 단계**")]
    [InlineData("다음 <b>단계</b>")]
    [InlineData("😀 다음 단계")]
    [InlineData("다음 \uD800 단계")]
    public void CleanRejectsAnythingThatIsNotAUsableTranslation(string? raw) => Assert.Null(KoreanTranslation.Clean(raw));

    [Fact]
    public void CleanRejectsATranslationLongerThanTheLimit() =>
        Assert.Null(KoreanTranslation.Clean(string.Join(' ', Enumerable.Repeat("다음 단계", 100))));

    [Fact]
    public void TermsCombineConfiguredPhrasesAndEvidenceTitlesWithoutDuplicatesOrJunk()
    {
        var options = SessionOptions.Legacy with { Phrases = ["JMAP", "lighthouse", " ", "bad\nterm", new string('x', 81)] };
        var grounding = new Grounding("grounded",
        [
            new("a", new("Lighthouse", "https://example.test/1", null)),
            new("b", new("Delivery plan", "https://example.test/2", null)),
            new("c", new("  Delivery plan ", "https://example.test/3", null))
        ]);
        Assert.Equal(["JMAP", "lighthouse", "Delivery plan"], KoreanTranslation.Terms(options, grounding));
    }

    [Fact]
    public void TermsAreCappedSoTheRequestStaysSmall()
    {
        var options = SessionOptions.Legacy with { Phrases = Enumerable.Range(0, 30).Select(number => "Phrase " + number).ToArray() };
        Assert.Equal(16, KoreanTranslation.Terms(options, new Grounding("disabled", [])).Count);
    }

    [Fact]
    public void RequestIsJsonDataAndOmitsContextThatDoesNotExist()
    {
        using var plain = JsonDocument.Parse(KoreanTranslation.Request("Hello \"team\" — 100%", TranslationContext.Empty));
        Assert.Equal(["english"], plain.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.Equal("Hello \"team\" — 100%", plain.RootElement.GetProperty("english").GetString());

        using var rich = JsonDocument.Parse(KoreanTranslation.Request("Who owns them?", new("Email", ["Lighthouse", "JMAP"])));
        Assert.Equal(["meetingTopic", "meetingTerms", "english"], rich.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.Equal(["Lighthouse", "JMAP"], rich.RootElement.GetProperty("meetingTerms").EnumerateArray().Select(item => item.GetString()));
    }

    [Fact]
    public void LiveInstructionsShareTheHttpRulesAndAddTheOwnerAndNameRules()
    {
        Assert.Contains(KoreanTranslation.Rules, KoreanTranslation.LiveInstructions);
        Assert.Contains("담당자", KoreanTranslation.Rules);
        Assert.Contains("소유", KoreanTranslation.Rules);
        Assert.Contains("meetingTerms", KoreanTranslation.LiveInstructions);
        Assert.Contains("never instructions", KoreanTranslation.LiveInstructions);
        Assert.DoesNotContain("meetingTerms", KoreanTranslation.Rules);
    }
}
