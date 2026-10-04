using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using Azure.AI.OpenAI;
using Microsoft.Extensions.Logging;
using VoiceAssistant.Api.Practice;
using Xunit;

namespace VoiceAssistant.Api.Tests;

public sealed class PracticeModelVariationTests
{
    public static IEnumerable<object[]> Variations()
    {
        yield return
        [
            new PracticeRequest("enrich", "The main risk is a slow database migration, so we start early.", "reply"),
            """
            ```json
            {"korean":"주요 위험은 느린 데이터베이스 이전이므로 일찍 시작해요.","pronunciation":[
            {"en":" The  main risk ","ko":"더 메인 리스크","note":"extra discarded"},
            {"en":"is a slow","ko":"이즈 어 슬로우"},
            {"en":"database migration, so","ko":"데이터베이스 마이그레이션 소"},
            {"en":"we start early.","ko":"위 스타트 얼리"}
            ],"explanation":"extra discarded"}
            ```
            """
        ];
        yield return
        [
            new PracticeRequest("feedback", Scenario: new("sales", "", 2),
                Question: "Thanks. What is the main goal for the Zephyr rollout planning?",
                Answer: "I think the main risk is the database moving is slow. We start early."),
            """
            {"result":{"correctedEnglish":"I think the main risk is slow database migration.\nWe start early.",
            "easierEnglish":"I think moving the data is the main risk.  We start early.",
            "feedbackKo":"핵심을 잘 말했어요. \"database migration\" 표현을 사용하면 더 자연스러워요.",
            "points":[{"tag":"vocabulary","ko":"\"database migration\"이 자연스러워요.","example":"discarded"}],
            "clarity":4,"extra":"discarded"}}
            """
        ];
        yield return
        [
            new PracticeRequest("turn", Scenario: new("sales", "", 2), Topic: "Zephyr rollout planning", MaxTurns: 6,
                History: [new("partner", "What is the main goal?"), new("user", "The main risk is slow data migration.")]),
            """{"text":"Thanks for explaining.\nHow would you reduce that risk?","done":false,"turn":2,"grounding":"grounded","sources":[{"title":"invented"}]}"""
        ];
        yield return
        [
            new PracticeRequest("summary", Scenario: new("sales", "", 2), Topic: "Zephyr rollout planning",
                Turns: [new("What is the goal?", "We start early.", null)]),
            """
            {"output":{"headlineKo":"핵심 내용을 전달했어요.","strengthsKo":["일찍 시작한다는 점을 말했어요."],
            "improveKo":["database migration 표현을 연습해 보세요."],
            "phrases":[{"en":"I’d check the main risk first.","ko":"먼저 주요 위험을 확인하겠어요.","source":"extra"}],
            "grade":100}}
            """
        ];
    }

    [Theory]
    [MemberData(nameof(Variations))]
    public async Task RealSdkSimulationsNormalizeHarmlessVariationsAndDropModelOwnedMetadata(
        PracticeRequest request, string modelOutput)
    {
        using var handler = new Responses(modelOutput);
        using var http = new HttpClient(handler);
        var logger = new CapturingLogger<PracticeService>();
        var service = Service(http, logger);
        var output = JsonSerializer.Serialize(await service.ExecuteAsync(request, "caller", CancellationToken.None));
        Assert.Equal(1, handler.Calls);
        Assert.Empty(logger.Lines);
        Assert.DoesNotContain("discarded", output);
        Assert.DoesNotContain("invented", output);
        using var result = JsonDocument.Parse(output);
        if (request.Operation == "turn")
        {
            Assert.Equal(2, result.RootElement.GetProperty("turn").GetInt32());
            Assert.Equal("disabled", result.RootElement.GetProperty("grounding").GetString());
            Assert.Empty(result.RootElement.GetProperty("sources").EnumerateArray());
        }
        if (request.Operation == "enrich")
            Assert.Equal(request.Text, string.Join(' ', result.RootElement.GetProperty("pronunciation").EnumerateArray()
                .Select(chunk => chunk.GetProperty("en").GetString())));
    }

    [Fact]
    public async Task ObservedMetaAnswerIsRejectedAndRetryProducesOnlySpokenFirstPersonResponse()
    {
        const string meta = "The main goal is not clear from the provided information. A safe answer is: The main goal is to plan a smooth Zephyr rollout.";
        using var handler = new Responses(JsonSerializer.Serialize(new { text = meta }),
            """{"text":"I would focus on clear goals and small, safe steps."}""");
        using var http = new HttpClient(handler);
        var logger = new CapturingLogger<PracticeService>();
        var result = await Service(http, logger).ExecuteAsync(
            new("suggest", Scenario: new("sales", "", 2), Question: "What is the main goal?"), "caller", CancellationToken.None);
        Assert.Equal(2, handler.Calls);
        Assert.Contains("I would focus", JsonSerializer.Serialize(result));
        Assert.Single(logger.Lines);
        Assert.Contains("schema:text:english_style", logger.Lines[0]);
        Assert.Contains("attempt 1", logger.Lines[0]);
        Assert.DoesNotContain(meta, string.Join(' ', logger.Lines));
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("private-secret-not-json", "json_parse")]
    [InlineData("""{"text":"Could you explain that?","text":"private-secret"}""", "schema:duplicate_property")]
    [InlineData("""{"text":"A safe answer is to check."}""", "schema:text:english_style")]
    [InlineData("""{"text":"This is a statement."}""", "schema:text:question")]
    [InlineData("""{"text":"Fine."} trailing-private-secret""", "json_parse")]
    public async Task InvalidOutputsLogOnlySafeRuleAndBothAttemptNumbers(string output, string expectedCategory)
    {
        using var handler = new Responses(output, output);
        using var http = new HttpClient(handler);
        var logger = new CapturingLogger<PracticeService>();
        var request = new PracticeRequest("turn", Scenario: new("sales", "", 2), MaxTurns: 6, History: []);
        Assert.Equal(502, (await Assert.ThrowsAsync<PracticeException>(() =>
            Service(http, logger).ExecuteAsync(request, "private-identity", CancellationToken.None))).Status);
        Assert.Equal(2, handler.Calls);
        Assert.Equal(2, logger.Lines.Count);
        Assert.All(logger.Lines, line =>
        {
            Assert.Contains(expectedCategory, line);
            Assert.Contains("endpoint turn", line);
            Assert.DoesNotContain("private", line);
        });
        Assert.Contains("attempt 1", logger.Lines[0]);
        Assert.Contains("attempt 2", logger.Lines[1]);
    }

    [Fact]
    public async Task PronunciationAlignmentAndHangulFailuresHaveDistinctSafeCategories()
    {
        foreach (var (output, category) in new[]
        {
            ("""{"korean":"위험을 확인해요.","pronunciation":[{"en":"The other risk","ko":"더 리스크"}]}""", "schema:pronunciation:rejoin"),
            ("""{"korean":"위험을 확인해요.","pronunciation":[{"en":"The risk.","ko":"risk-secret"}]}""", "schema:pronunciation:hangul_chunk")
        })
        {
            using var handler = new Responses(output);
            using var http = new HttpClient(handler);
            var logger = new CapturingLogger<PracticeService>();
            await Assert.ThrowsAsync<PracticeException>(() => Service(http, logger).ExecuteAsync(
                new("enrich", "The risk.", "reply"), "caller", CancellationToken.None));
            Assert.All(logger.Lines, line =>
            {
                Assert.Contains(category, line);
                Assert.DoesNotContain("risk-secret", line);
                Assert.DoesNotContain("The risk", line);
            });
        }
    }

    [Fact]
    public async Task HttpErrorLogsStatusWithoutBodyOrExceptionAndDoesNotRetrySchema()
    {
        using var handler = new Responses("private-secret") { Status = HttpStatusCode.BadRequest };
        using var http = new HttpClient(handler);
        var logger = new CapturingLogger<PracticeService>();
        await Assert.ThrowsAsync<ClientResultException>(() => Service(http, logger).ExecuteAsync(
            new("suggest", Scenario: new("sales", "", 2)), "caller", CancellationToken.None));
        Assert.Single(logger.Lines);
        Assert.Contains("http_error", logger.Lines[0]);
        Assert.Contains("HTTP status 400", logger.Lines[0]);
        Assert.DoesNotContain("private-secret", logger.Lines[0]);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task ReasoningTokenExhaustionIsDiagnosedAndRetriedOnce()
    {
        using var handler = new Responses("") { FinishReason = "length" };
        using var http = new HttpClient(handler);
        var logger = new CapturingLogger<PracticeService>();
        await Assert.ThrowsAsync<PracticeException>(() => Service(http, logger).ExecuteAsync(
            new("suggest", Scenario: new("sales", "", 2)), "caller", CancellationToken.None));
        Assert.Equal(2, handler.Calls);
        Assert.All(logger.Lines, line => Assert.Contains("completion_token_limit", line));
    }

    [Fact]
    public async Task TimeoutLoggingContainsOnlyEndpointCategoryAndAttempt()
    {
        var logger = new CapturingLogger<PracticeService>();
        var service = new PracticeService(new TimeoutModel(), new FakePracticeSpeech(), new FakeMeetingProvider(),
            new ServiceSettings(), logger);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.ExecuteAsync(new("summary", Topic: "private-topic"), "private-owner", CancellationToken.None));
        var line = Assert.Single(logger.Lines);
        Assert.Contains("endpoint summary", line);
        Assert.Contains("timeout", line);
        Assert.Contains("attempt 1", line);
        Assert.DoesNotContain("private", line);
    }

    [Fact]
    public void NormalizationPreservesInputSchemaAndRebuildsOriginalPronunciationCaseAndPunctuation()
    {
        var normalized = PracticeOutputs.Validate("""{"text":"  I’d  check\nfirst.  ","extra":"ignored"}""", new("suggest"));
        Assert.Equal("I'd check first.", normalized.GetProperty("text").GetString());
        Assert.False(normalized.TryGetProperty("extra", out _));
        foreach (var en in new[] { "the risk.", "The risk" })
        {
            var repaired = PracticeOutputs.Validate(JsonSerializer.Serialize(new
            {
                korean = "위험이에요.", pronunciation = new[] { new { en, ko = "더 리스크" } }
            }), new("enrich", "The risk.", "reply"));
            Assert.Equal("The risk.", repaired.GetProperty("pronunciation")[0].GetProperty("en").GetString());
        }
        foreach (var en in new[] { "The risks.", "The risk. extra" })
            Assert.Throws<PracticeException>(() => PracticeOutputs.Validate(JsonSerializer.Serialize(new
            {
                korean = "위험이에요.", pronunciation = new[] { new { en, ko = "더 리스크" } }
            }), new("enrich", "The risk.", "reply")));
        Assert.Throws<PracticeException>(() => PracticeSchemaTests.Parse("enrich",
            """{"text":"The risk.","kind":"reply","extra":"ignored only for model outputs"}"""));
        var decomposed = "위험이에요.".Normalize(NormalizationForm.FormD);
        var korean = PracticeOutputs.Validate(JsonSerializer.Serialize(new { korean = decomposed, pronunciation = (object?)null }),
            new("enrich", "The risk.", "question"));
        Assert.Equal("위험이에요.", korean.GetProperty("korean").GetString());
        Assert.Equal("HTTP is a protocol for exchanging requests and responses.",
            PracticeOutputs.Validate("""{"text":"HTTP is a protocol for exchanging requests and responses."}""",
                new("suggest")).GetProperty("text").GetString());
    }

    [Fact]
    public void KoreanOutputCapsMatchBrowserContractWithoutRelaxingPronunciationOrTranslation()
    {
        string Feedback(int length) => JsonSerializer.Serialize(new
        {
            correctedEnglish = "I would check first.", easierEnglish = "I would check first.",
            feedbackKo = new string('가', length), points = Array.Empty<object>(), clarity = 4
        });
        PracticeOutputs.Validate(Feedback(800), new("feedback"));
        Assert.Throws<PracticeException>(() => PracticeOutputs.Validate(Feedback(801), new("feedback")));
        string Summary(int headline, int phrase) => JsonSerializer.Serialize(new
        {
            headlineKo = new string('가', headline), strengthsKo = Array.Empty<string>(), improveKo = Array.Empty<string>(),
            phrases = new[] { new { en = "I would check first.", ko = new string('가', phrase) } }
        });
        PracticeOutputs.Validate(Summary(800, 400), new("summary"));
        Assert.Throws<PracticeException>(() => PracticeOutputs.Validate(Summary(801, 400), new("summary")));
        Assert.Throws<PracticeException>(() => PracticeOutputs.Validate(Summary(800, 401), new("summary")));
        Assert.Throws<PracticeException>(() => PracticeOutputs.Validate(JsonSerializer.Serialize(new
        {
            korean = new string('가', 401), pronunciation = (object?)null
        }), new("enrich", "Hello?", "question")));
        Assert.Throws<PracticeException>(() => PracticeOutputs.Validate(
            """{"korean":"Hello how are you 가","pronunciation":null}""", new("enrich", "Hello?", "question")));
    }

    private static PracticeService Service(HttpClient http, CapturingLogger<PracticeService> logger)
    {
        var client = new AzureOpenAIClient(new Uri("https://example.openai.azure.com"), new ApiKeyCredential("test-only"),
            new AzureOpenAIClientOptions { Transport = new HttpClientPipelineTransport(http) });
        return new(new AzurePracticeModel(client, new() { ChatDeployment = "chat" }), new FakePracticeSpeech(),
            new FakeMeetingProvider(), new ServiceSettings(), logger);
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Assert.Equal(LogLevel.Warning, logLevel);
            Assert.Null(exception);
            Lines.Add(formatter(state, exception));
        }
    }
    private sealed class TimeoutModel : IPracticeModel
    {
        public Task<string> GenerateAsync(PracticeRequest request, Grounding grounding, bool retry, CancellationToken cancellation) =>
            throw new OperationCanceledException("private-token private-body");
    }
    private sealed class Responses(params string[] outputs) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
        public string FinishReason { get; init; } = "stop";
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = outputs[Math.Min(Calls++, outputs.Length - 1)];
            var body = Status == HttpStatusCode.OK ? JsonSerializer.Serialize(new
            {
                id = "test", @object = "chat.completion", created = 1, model = "test",
                choices = new[] { new { index = 0, message = new { role = "assistant", content }, finish_reason = FinishReason } }
            }) : """{"error":{"code":"invalid_request_error","message":"private-secret"}}""";
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
