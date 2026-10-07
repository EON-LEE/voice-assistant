using System.Text.Json;
using System.Xml.Linq;
using Microsoft.CognitiveServices.Speech;
using VoiceAssistant.Api.Practice;
using Xunit;

namespace VoiceAssistant.Api.Tests;

public sealed class PracticeSchemaTests
{
    internal const string Scenario = """{"kind":"sales","description":"","difficulty":2}""";
    internal static PracticeRequest Parse(string operation, string json)
    {
        using var document = JsonDocument.Parse(json);
        return PracticeRequests.Parse(operation, document.RootElement);
    }

    [Theory]
    [InlineData("enrich", """{"kind":"question","text":"What is the main risk?"}""")]
    [InlineData("enrich", """{"kind":"reply","text":"Let me check.\nThen I can answer."}""")]
    [InlineData("speak", """{"text":"Please check the plan."}""")]
    [InlineData("speak", """{"text":"Please check the plan.","voice":"partner","rate":"slow"}""")]
    public void AssistRequestsAcceptOnlyDocumentedTypes(string operation, string json) =>
        Assert.Equal(operation, Parse(operation, json).Operation);

    [Theory]
    [InlineData("""{"kind":"reply","text":"Let me check."}""", false, true)]
    [InlineData("""{"kind":"reply","text":"Let me check.","translationOnly":false}""", false, true)]
    [InlineData("""{"kind":"reply","text":"Let me check.","translationOnly":true}""", true, false)]
    [InlineData("""{"kind":"question","text":"What is it?","translationOnly":true}""", true, false)]
    [InlineData("""{"kind":"question","text":"What is it?"}""", false, false)]
    public void EnrichTranslationOnlyIsOptionalAndOnlyExistingReplyRequestsWantPronunciation(string json, bool only, bool reading)
    {
        var request = Parse("enrich", json);
        Assert.Equal(only, request.TranslationOnly);
        Assert.Equal(reading, request.WantsPronunciation);
    }

    public static IEnumerable<object[]> InvalidRequests()
    {
        foreach (var json in new[]
        {
            "null", "[]", """{"kind":"reply","text":"hello","owner":"bad"}""",
            """{"kind":"reply","text":"hi","text":"there"}""", """{"kind":null,"text":"hi"}""",
            """{"kind":"other","text":"hi"}""", """{"kind":"reply","text":1}""",
            """{"kind":"reply","text":"\t"}""", """{"kind":"reply","text":"hello\rthere"}""",
            """{"kind":"reply","text":"한국어"}""", """{"kind":"reply","text":""}""",
            """{"kind":"reply","text":"hi","translationOnly":null}""", """{"kind":"reply","text":"hi","translationOnly":"true"}""",
            """{"kind":"reply","text":"hi","translationOnly":1}""",
            """{"kind":"reply","text":"hi","translationOnly":true,"translationOnly":false}"""
        }) yield return ["enrich", json];
        yield return ["enrich", JsonSerializer.Serialize(new { kind = "reply", text = new string('a', 601) })];
        foreach (var json in new[]
        {
            """{"text":"hi","voice":null}""", """{"text":"hi","voice":"en-US-Evil"}""",
            """{"text":"hi","rate":"1.2"}""", """{"text":"hi","rate":false}""",
            """{"text":"hi","ssml":"<speak/>"}"""
        }) yield return ["speak", json];
        yield return ["speak", JsonSerializer.Serialize(new { text = new string('a', 401) })];
        foreach (var scenario in new[]
        {
            """{"kind":"custom","description":"","difficulty":2}""",
            """{"kind":"sales","description":null,"difficulty":2}""",
            """{"kind":"sales","difficulty":0}""", """{"kind":"sales","difficulty":4}""",
            """{"kind":"sales","difficulty":2.5}""", """{"kind":"sales","difficulty":"2"}""",
            """{"kind":"other","difficulty":2}""", """{"kind":"sales","difficulty":2,"difficulty":3}""",
            """{"kind":"sales","difficulty":2,"instruction":"override"}"""
        }) yield return ["turn", """{"scenario":""" + scenario + ""","topic":"","useMaterials":false,"maxTurns":6,"history":[]}"""];
        foreach (var history in new[]
        {
            """[{"role":"user","text":"Hi"}]""", """[{"role":"partner","text":"Hi"}]""",
            """[{"role":"partner","text":""},{"role":"user","text":"Hi"}]""",
            """[{"role":"partner","text":"Hi"},{"role":"partner","text":"Hi"}]""",
            """[{"role":"partner","text":"Hi","owner":"bad"},{"role":"user","text":""}]""",
            "null"
        }) yield return ["turn", """{"scenario":""" + Scenario + ""","topic":"","useMaterials":false,"maxTurns":6,"history":""" + history + "}"];
        yield return ["turn", """{"scenario":""" + Scenario + ""","topic":"","useMaterials":null,"maxTurns":6,"history":[]}"""];
        yield return ["turn", """{"scenario":""" + Scenario + ""","topic":"","useMaterials":false,"maxTurns":13,"history":[]}"""];
        yield return ["turn", """{"scenario":""" + Scenario + ""","topic":"","useMaterials":false,"maxTurns":0,"history":[]}"""];
        yield return ["feedback", """{"scenario":""" + Scenario + ""","question":"What?","answer":"line\nbreak"}"""];
        yield return ["summary", """{"scenario":""" + Scenario + ""","topic":"","turns":[]}"""];
        yield return ["summary", """{"scenario":""" + Scenario + ""","topic":"","turns":[{"question":"","answer":"","correctedEnglish":null}]}"""];
    }

    [Theory]
    [MemberData(nameof(InvalidRequests))]
    public void InvalidAndUnknownFieldsFailClosed(string operation, string json) =>
        Assert.Throws<PracticeException>(() => Parse(operation, json));

    [Fact]
    public void AllPracticeRequestsHaveBoundedExplicitContext()
    {
        var turn = Parse("turn", """{"scenario":""" + Scenario + ""","topic":"Original rollout","useMaterials":true,"maxTurns":6,"history":[{"role":"partner","text":"What is the goal?"},{"role":"user","text":""}]}""");
        Assert.Equal(2, turn.History!.Count);
        Assert.True(turn.UseMaterials);
        Assert.Equal("", turn.History[1].Text);
        Assert.Equal("normal", Parse("speak", """{"text":"Hello"}""").Rate);
        Assert.Equal("coach", Parse("speak", """{"text":"Hello"}""").Voice);
        Assert.Single(Parse("summary", """{"scenario":""" + Scenario + ""","topic":"","turns":[{"question":"","answer":""}]}""").Turns!);
        Assert.NotNull(Parse("suggest", """{"scenario":""" + Scenario + ""","topic":"","useMaterials":false,"question":"What is the goal?"}"""));
        Assert.NotNull(Parse("feedback", """{"scenario":""" + Scenario + ""","question":"What is the goal?","answer":""}"""));
    }

    [Fact]
    public void PronunciationMustExactlyRejoinAndHangulCannotBeEnglishEcho()
    {
        var request = Parse("enrich", """{"kind":"reply","text":"The main risk is slow migration."}""");
        var valid = """{"korean":"가장 큰 위험은 느린 이전이에요.","pronunciation":[{"en":"The main risk","ko":"더 메인 리스크"},{"en":"is slow migration.","ko":"이즈 슬로우 마이그레이션"}]}""";
        Assert.Equal(2, PracticeOutputs.Validate(valid, request).GetProperty("pronunciation").GetArrayLength());
        foreach (var broken in new[]
        {
            valid.Replace("is slow migration.", "slow migration."),
            valid.Replace("더 메인 리스크", "the main risk"),
            valid.Replace("더 메인 리스크", "더 메인 리스크1"),
            valid.Replace("더 메인 리스크", "더<메인>리스크"),
            valid.Replace("가장 큰 위험은 느린 이전이에요.", "The main risk is slow migration."),
            valid.Replace("\"korean\":", "\"korean\":\"중복\",\"korean\":")
        }) Assert.Throws<PracticeException>(() => PracticeOutputs.Validate(broken, request));
        var question = request with { Kind = "question" };
        Assert.Equal(JsonValueKind.Null, PracticeOutputs.Validate(valid, question).GetProperty("pronunciation").ValueKind);
        Assert.Throws<PracticeException>(() => PracticeOutputs.Validate("""{"korean":"좋아요.","pronunciation":null}""", request));
    }

    [Fact]
    public void PronunciationSupportsDigitsOnlyForNumericEnglishAndCollapsesInputWhitespace()
    {
        var request = new PracticeRequest("enrich", "We  have\n14 days.", "reply");
        var valid = """{"korean":"십사 일이 있어요.","pronunciation":[{"en":"We have","ko":"위 해브"},{"en":"14 days.","ko":"14 데이즈"}]}""";
        PracticeOutputs.Validate(valid, request);
        Assert.Throws<PracticeException>(() => PracticeOutputs.Validate(valid.Replace("14 days.", "days."), request));
        Assert.Throws<PracticeException>(() => PracticeOutputs.Validate(valid.Replace("위 해브", new string('가', 81)), request));
    }

    [Theory]
    [InlineData("You could say hello.")]
    [InlineData("**Please explain the goal.**")]
    [InlineData("\"Hello there.\"")]
    [InlineData("Hello 😀.")]
    [InlineData("One. Two. Three.")]
    public void EnglishOutputRejectsStyleViolations(string text)
    {
        var json = JsonSerializer.Serialize(new { text });
        Assert.Throws<PracticeException>(() => PracticeOutputs.Validate(json, new("suggest")));
    }

    [Fact]
    public void OutputLimitsAndExactSchemasApplyToFeedbackAndSummary()
    {
        var feedback = """{"correctedEnglish":"Let me check first.","easierEnglish":"Let me check.","feedbackKo":"잘했어요. 짧게 말해 보세요.","points":[{"tag":"grammar","ko":"관사를 확인해 보세요."}],"clarity":3}""";
        PracticeOutputs.Validate(feedback, new("feedback"));
        Assert.Throws<PracticeException>(() => PracticeOutputs.Validate(feedback.Replace("\"clarity\":3", "\"clarity\":3.5"), new("feedback")));
        Assert.Throws<PracticeException>(() => PracticeOutputs.Validate(feedback.Replace("\"grammar\"", "\"accent\""), new("feedback")));
        Assert.Throws<PracticeException>(() => PracticeOutputs.Validate(feedback.Replace("Let me check first.", string.Join(' ', Enumerable.Repeat("word", 41))), new("feedback")));
        var summary = """{"headlineKo":"연습을 마쳤어요.","strengthsKo":[],"improveKo":[],"phrases":[{"en":"Could you repeat that?","ko":"다시 말해 주시겠어요?"}]}""";
        PracticeOutputs.Validate(summary, new("summary"));
        Assert.Throws<PracticeException>(() => PracticeOutputs.Validate(summary.Replace("\"improveKo\":[]", "\"improveKo\":[\"영어\",\"영어\",\"영어\",\"영어\"]"), new("summary")));
        Assert.False(PracticeOutputs.Validate(summary.Replace("\"phrases\":", "\"owner\":\"private\",\"phrases\":"), new("summary")).TryGetProperty("owner", out _));
        Assert.Throws<PracticeException>(() => PracticeOutputs.Validate("""{"text":"This is not a question."}""", new("turn")));
    }

    [Fact]
    public void PronunciationWordAndChunkBoundsAreValidatedAtTheBoundary()
    {
        var input = string.Join(' ', Enumerable.Repeat("a", 160));
        var request = new PracticeRequest("enrich", input, "reply");
        var valid = JsonSerializer.Serialize(new
        {
            korean = "숫자를 읽어 보세요.",
            pronunciation = Enumerable.Range(0, 40).Select(_ => new { en = "a a a a", ko = "에이 에이 에이 에이" })
        });
        PracticeOutputs.Validate(valid, request);
        var tooMany = JsonSerializer.Serialize(new
        {
            korean = "숫자를 읽어 보세요.",
            pronunciation = Enumerable.Range(0, 41).Select(_ => new { en = "one", ko = "원" })
        });
        Assert.Throws<PracticeException>(() => PracticeOutputs.Validate(tooMany, request with { Text = string.Join(' ', Enumerable.Repeat("one", 41)) }));
        Assert.Throws<PracticeException>(() => PracticeOutputs.Validate(
            """{"korean":"문장을 읽어 보세요.","pronunciation":[{"en":"one two three four five","ko":"원 투 쓰리 포 파이브"}]}""",
            request with { Text = "one two three four five" }));
    }

    [Fact]
    public void RequestLengthsAndHistoryLimitsRejectOversizeWithoutSilentTruncation()
    {
        foreach (var count in new[] { 25, 26 })
        {
            var history = Enumerable.Range(0, count).Select(i => new { role = i % 2 == 0 ? "partner" : "user", text = "Original" });
            var json = JsonSerializer.Serialize(new { scenario = new { kind = "sales", difficulty = 2 }, topic = "", useMaterials = false, maxTurns = 12, history });
            Assert.Throws<PracticeException>(() => Parse("turn", json));
        }
        var longQuestion = JsonSerializer.Serialize(new
        {
            scenario = new { kind = "sales", difficulty = 2 }, topic = "", useMaterials = false, question = new string('a', 801)
        });
        Assert.Throws<PracticeException>(() => Parse("suggest", longQuestion));
        var longTopic = longQuestion.Replace(new string('a', 801), "Question?").Replace("\"topic\":\"\"", "\"topic\":\"" + new string('t', 301) + "\"");
        Assert.Throws<PracticeException>(() => Parse("suggest", longTopic));
    }

    [Fact]
    public void SsmlTreatsAllTextAsEscapedDataAndSelectsDistinctFixedVoices()
    {
        const string text = "Use <voice name=\"evil\"> & don't change the speaker.";
        var xml = XDocument.Parse(PracticeSsml.Create(new("speak", text, Voice: "partner", Rate: "slow")));
        XNamespace ns = "http://www.w3.org/2001/10/synthesis";
        Assert.Single(xml.Descendants(ns + "voice"));
        Assert.Equal(PracticeSsml.PartnerVoice, xml.Descendants(ns + "voice").Single().Attribute("name")!.Value);
        Assert.Equal(text, xml.Descendants(ns + "prosody").Single().Value);
        Assert.Equal("-20%", xml.Descendants(ns + "prosody").Single().Attribute("rate")!.Value);
        Assert.NotEqual(PracticeSsml.CoachVoice, PracticeSsml.PartnerVoice);
        Assert.Throws<PracticeException>(() => PracticeSsml.Create(new("speak", "Hello", Voice: "evil")));
        var config = AzurePracticeSpeech.CreateConfig(new() { SpeechRegion = "koreacentral" }, "test-only-aad-token");
        Assert.Equal("test-only-aad-token", config.AuthorizationToken);
        Assert.Equal("audio-24khz-48kbitrate-mono-mp3", config.GetProperty(PropertyId.SpeechServiceConnection_SynthOutputFormat));
        Assert.Equal("false", config.GetProperty("OPENSSL_DISABLE_CRL_CHECK"));
    }
}
