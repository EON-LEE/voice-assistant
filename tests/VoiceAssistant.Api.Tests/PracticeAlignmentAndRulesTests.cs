using System.Text.Json;
using VoiceAssistant.Api.Practice;
using Xunit;

namespace VoiceAssistant.Api.Tests;

public sealed class PracticeAlignmentAndRulesTests
{
    private const string Input = "The main risk is a slow database migration, so we start early.";
    private static string Output(params (string En, string Ko)[] chunks) => JsonSerializer.Serialize(new
    {
        korean = "주요 위험은 느린 데이터베이스 이전이므로 일찍 시작해요.",
        pronunciation = chunks.Select(chunk => new { en = chunk.En, ko = chunk.Ko })
    });

    [Fact]
    public void RealisticReplyAlignmentRestoresExactOriginalWordsCaseAndComma()
    {
        var result = PracticeOutputs.Validate(Output(
            ("the  main risk", "더 메인 리스크"), ("IS a slow", "이즈 어 슬로우"),
            ("database migration so", "데이터베이스 마이그레이션 소"), ("we start early", "위 스타트 얼리")),
            new("enrich", Input, "reply"));
        var chunks = result.GetProperty("pronunciation").EnumerateArray().ToArray();
        Assert.Equal(Input, string.Join(' ', chunks.Select(chunk => chunk.GetProperty("en").GetString())));
        Assert.Equal("database migration, so", chunks[2].GetProperty("en").GetString());
        Assert.Equal("The main risk", chunks[0].GetProperty("en").GetString());
    }

    [Theory]
    [InlineData("The main risk is a database migration, so we start early.")]
    [InlineData("The main risk is a very slow database migration, so we start early.")]
    [InlineData("The main risk is a slow migration database, so we start early.")]
    [InlineData("The main risk is not a slow database migration, so we start early.")]
    public void RealWordChangesAreNotRepaired(string changed)
    {
        var output = Output(changed.Split(' ').Chunk(3).Select(words => (string.Join(' ', words), "가나다")).ToArray());
        var error = Assert.Throws<PracticeException>(() => PracticeOutputs.Validate(output, new("enrich", Input, "reply")));
        Assert.Equal("schema:pronunciation:rejoin:word_sequence", error.DiagnosticCategory);
    }

    [Fact]
    public void InvalidChunkSizesAreRechunkedOnlyWhenHangulCountMatches()
    {
        const string original = "One two three four five six seven eight nine.";
        var repaired = PracticeOutputs.Validate(Output(
            ("", "원 투 쓰리"), ("One two three four five", "포 파이브 식스"),
            ("six seven eight nine", "세븐 에이트 나인")), new("enrich", original, "reply"));
        Assert.Equal(new[] { "One two three", "four five six", "seven eight nine." },
            repaired.GetProperty("pronunciation").EnumerateArray().Select(chunk => chunk.GetProperty("en").GetString()));
        var mismatch = Assert.Throws<PracticeException>(() => PracticeOutputs.Validate(
            Output((original, "가나다")), new("enrich", original, "reply")));
        Assert.Equal("schema:pronunciation:rechunk:ko_count", mismatch.DiagnosticCategory);
    }

    [Fact]
    public void RechunkingStillRequiresValidHangulAndNeverAddsMissingKorean()
    {
        var output = Output(("One two three four five", "one two"), ("six", "가나다"));
        var failure = Assert.Throws<PracticeException>(() => PracticeOutputs.Validate(output,
            new("enrich", "One two three four five six.", "reply")));
        Assert.Equal("schema:pronunciation:hangul_chunk", failure.DiagnosticCategory);
        var missing = """{"korean":"안녕하세요.","pronunciation":[{"en":"Hello."}]}""";
        Assert.Throws<PracticeException>(() => PracticeOutputs.Validate(missing, new("enrich", "Hello.", "reply")));
    }

    [Theory]
    [InlineData("좋은 답변이에요. 주요 위험을 잘 말했어요. 두 문장으로 나누면 더 또렷해요.")]
    [InlineData("좋은 답변이에요。 주요 위험을 잘 말했어요！ 두 문장으로 나누면 더 또렷해요？")]
    [InlineData("좋은 답변이에요. \"We start early.\"처럼 짧게 말하면 더 또렷해요.")]
    public void RealisticKoreanFeedbackAcceptsThreeSentencesAndPunctuationVariants(string ko) =>
        PracticeOutputs.Validate(Feedback(ko), new("feedback"));

    [Theory]
    [InlineData("좋아요. 좋아요. 좋아요. 좋아요.", "schema:feedbackKo:max_sentences")]
    [InlineData("Only English feedback.", "schema:feedbackKo:no_hangul")]
    [InlineData("좋아요 <script>bad</script>", "schema:feedbackKo:markup")]
    public void FeedbackDiagnosticsNameTheSpecificRuleWithoutContent(string ko, string category)
    {
        var failure = Assert.Throws<PracticeException>(() => PracticeOutputs.Validate(Feedback(ko), new("feedback")));
        Assert.Equal(category, failure.DiagnosticCategory);
        Assert.DoesNotContain(ko, failure.Message);
    }

    [Theory]
    [InlineData("Thanks. How would you reduce that risk?")]
    [InlineData("Let's look at the next step: how would you prepare?")]
    [InlineData("Thanks; what would you check first?")]
    [InlineData("Thanks - how would you reduce that risk?")]
    [InlineData("Would a 1.5 second delay affect the plan?")]
    public void PartnerAllowsHarmlessAcknowledgementContractionColonAndDecimal(string text) =>
        PracticeOutputs.Validate(JsonSerializer.Serialize(new { text }), new("turn"));

    [Theory]
    [InlineData("Thanks. That makes sense. What is next?", "max_sentences")]
    [InlineData("**What is next?**", "characters")]
    [InlineData("A safe answer is to check first?", "meta_commentary")]
    [InlineData("- What is next?", "list_prefix")]
    public void PartnerDiagnosticsExposeActualStyleRule(string text, string rule)
    {
        var error = Assert.Throws<PracticeException>(() =>
            PracticeOutputs.Validate(JsonSerializer.Serialize(new { text }), new("turn")));
        Assert.Equal("schema:text:english_style:" + rule, error.DiagnosticCategory);
    }

    [Fact]
    public void PartnerKeepsThirtyWordLimitIncludingAcknowledgement()
    {
        var text = "Thanks. " + string.Join(' ', Enumerable.Repeat("word", 30)) + "?";
        var error = Assert.Throws<PracticeException>(() =>
            PracticeOutputs.Validate(JsonSerializer.Serialize(new { text }), new("turn")));
        Assert.Equal("schema:text:english_style:max_words", error.DiagnosticCategory);
    }

    private static string Feedback(string korean) => JsonSerializer.Serialize(new
    {
        correctedEnglish = "I think the main risk is slow database migration. We start early.",
        easierEnglish = "I think moving the data is the main risk. We start early.",
        feedbackKo = korean, points = Array.Empty<object>(), clarity = 4
    });
}
