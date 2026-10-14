using System.Text.Json;
using VoiceAssistant.Api;

namespace ReplyQualityEval;

public static class ScenarioRunner
{
    public static async Task RunAsync(string suitePath, string outputPath, Models models)
    {
        if (File.Exists(outputPath)) throw new InvalidOperationException("Choose a new output path; results are not overwritten.");
        using var document = JsonDocument.Parse(File.ReadAllText(suitePath));
        var suite = document.RootElement;
        var results = new List<object>();
        foreach (var scenario in suite.GetProperty("scenarios").EnumerateArray())
        {
            var history = scenario.GetProperty("script").EnumerateArray()
                .Select((turn, index) => new Utterance(
                    turn.GetProperty("speaker").GetString() ?? $"speaker-{index}",
                    index, index + 0.5, turn.GetProperty("text").GetString() ?? ""))
                .ToList();
            var evidence = scenario.GetProperty("expectedFacts").EnumerateArray()
                .Select(fact => new Evidence(
                    fact.GetProperty("text").GetString() ?? "",
                    new Source(
                        $"Slide {fact.GetProperty("sourceSlide").GetInt32()}",
                        $"https://meeting-eval.invalid/{scenario.GetProperty("id").GetString()}/{fact.GetProperty("id").GetString()}",
                        null)))
                .ToArray();
            var pending = history.TakeLast(2).Select(turn => turn.Text).ToArray();
            var candidate = new Candidate(
                history[^1].End,
                history.Count - 1,
                pending,
                ReplyTrigger.ContainsQuestion(pending),
                false);
            await models.Suggest(candidate, history, new("grounded", evidence));
            results.Add(new
            {
                scenarioId = scenario.GetProperty("id").GetString(),
                response = candidate.Primary,
                alternative = candidate.Alternative,
                firstResponseMs = (int)Math.Round(candidate.LatencySeconds * 1000)
            });
            Console.WriteLine($"{scenario.GetProperty("id").GetString()}: {candidate.LatencySeconds:F2}s {candidate.Primary}");
        }
        var payload = new
        {
            schemaVersion = 1,
            suite = suite.GetProperty("name").GetString(),
            generatedAt = DateTimeOffset.UtcNow,
            results
        };
        File.WriteAllText(outputPath, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
    }
}
