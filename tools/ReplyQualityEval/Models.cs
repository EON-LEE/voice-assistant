using System.Diagnostics;
using System.Text.Json;
using Azure.AI.OpenAI;
using Azure.AI.OpenAI.Chat;
using Azure.Core;
using OpenAI.Chat;
using VoiceAssistant.Api;

namespace ReplyQualityEval;

/// <summary>A moment where the app would offer a suggestion (a settled, non-backchannel unit of speech).</summary>
public sealed record Candidate(double At, int LastIndex, string[] Pending, bool Question, bool OwnSpeech)
{
    public string Primary { get; set; } = "";
    public string Alternative { get; set; } = "";
    public double LatencySeconds { get; set; }
    public bool RespondNow { get; set; }
    public Judgement? Judge { get; set; }
}

public sealed record Judgement(int Relevance, int Usefulness, int? Alignment, string Issue);

public sealed class Models(TokenCredential credential, string endpoint, string deployment)
{
    private readonly AzureMeetingProvider provider = new(new ServiceSettings
    { OpenAIEndpoint = endpoint, ChatDeployment = deployment, ChatMaxOutputTokens = 2048 }, credential);
    private readonly ChatClient chat = new AzureOpenAIClient(new Uri(endpoint), credential).GetChatClient(deployment);

    /// <summary>Production prompt and parser, exactly as the deployed service uses them.</summary>
    public async Task Suggest(Candidate candidate, IReadOnlyList<Utterance> history)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { await SuggestOnce(candidate, history); return; }
            catch (System.ClientModel.ClientResultException ex) when (ex.Status == 429 && attempt < 6)
            { await Task.Delay(3000 * attempt); }
        }
    }

    private async Task SuggestOnce(Candidate candidate, IReadOnlyList<Utterance> history)
    {
        var watch = Stopwatch.StartNew();
        string primary = "", alternative = "";
        await foreach (var update in provider.AnswerWithSuggestionsAsync(
            history.TakeLast(12).Select(u => new ConversationTurn(u.Text)).ToArray(), new Grounding("no_matches", []),
            SessionOptions.Legacy, "knowledge", CancellationToken.None))
        {
            primary += update.Text;
            if (update.Alternative is not null) alternative = update.Alternative;
        }
        candidate.Primary = primary; candidate.Alternative = alternative;
        candidate.LatencySeconds = watch.Elapsed.TotalSeconds;
    }

    /// <summary>Turn classifier: does the latest speech invite the listening participants to respond now?</summary>
    public async Task ClassifyTurn(Candidate candidate, IReadOnlyList<Utterance> history)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                candidate.RespondNow = await provider.ShouldRespondAsync(
                    history.TakeLast(12).Select(u => new ConversationTurn(u.Text)).ToArray(), CancellationToken.None);
                return;
            }
            catch (System.ClientModel.ClientResultException ex) when (ex.Status == 429 && attempt < 6)
            { await Task.Delay(3000 * attempt); }
        }
    }

    public async Task JudgeAsync(Candidate candidate, IReadOnlyList<Utterance> history, string target, Utterance? actual)
    {
        var context = string.Join("\n", history.TakeLast(10).Select(u => $"{(u.Speaker == target ? "ME" : "Speaker " + u.Speaker)}: {u.Text}"));
        var input = $"""
            Meeting context (oldest first):
            {context}

            Suggestion 1: {candidate.Primary}
            Suggestion 2: {candidate.Alternative}
            What ME actually said next: {(actual is null ? "(ME did not speak next)" : actual.Text)}
            """;
        var json = await Ask(JudgePrompt, input, 400, jsonObject: true);
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            int Score(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? Math.Clamp(v.GetInt32(), 1, 5) : 1;
            candidate.Judge = new(Score("relevance"), Score("usefulness"),
                actual is null ? null : Score("alignment"),
                root.TryGetProperty("issue", out var issue) ? issue.GetString() ?? "" : "");
        }
        catch (JsonException) { candidate.Judge = new(1, 1, null, "judge returned invalid JSON"); }
    }

    private async Task<string> Ask(string system, string user, int maxTokens, bool jsonObject = false)
    {
        // Same option setup as the API: GPT-5 deployments need max_completion_tokens instead of legacy max_tokens.
        var options = System.ClientModel.Primitives.ModelReaderWriter.Read<ChatCompletionOptions>(BinaryData.FromString("{}"))!;
        options.MaxOutputTokenCount = Math.Max(maxTokens, 1024);
#pragma warning disable AOAI001
        options.SetNewMaxCompletionTokensPropertyEnabled();
#pragma warning restore AOAI001
        if (jsonObject) options.ResponseFormat = ChatResponseFormat.CreateJsonObjectFormat();
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var response = await chat.CompleteChatAsync([new SystemChatMessage(system), new UserChatMessage(user)], options);
                return string.Concat(response.Value.Content.Select(part => part.Text));
            }
            catch (System.ClientModel.ClientResultException ex) when (ex.Status == 429 && attempt < 6)
            { await Task.Delay(3000 * attempt); }
        }
    }

    private const string JudgePrompt = """
        You evaluate a live meeting assistant for a Korean IT engineer who is not fluent in English.
        The assistant shows two short English lines the participant ("ME") could glance at as a hint for what to say next.
        Score strictly (1 = bad, 5 = excellent) and return ONLY a JSON object:
        {"relevance": n, "usefulness": n, "alignment": n, "issue": "<= 15 words, main problem or empty"}
        relevance: do the suggestions fit what the others were just discussing (not an older or misheard topic)?
        usefulness: could ME naturally say this (or adapt it) right now to take part well? Penalize empty fillers,
          "I'm not sure", repeat requests, wrong facts, claims about ME's own work or promises, and awkward English.
        alignment: does at least one suggestion go in a similar direction (same topic, intent or kind of answer) as
          what ME actually said next? If ME did not speak next, set alignment to 3.
        Transcript lines may contain speech-recognition-style errors; judge the intended meaning.
        """;
}
