using System.Text.Json;

namespace VoiceAssistant.Api.Practice;

public sealed class PracticeService(IPracticeModel model, IPracticeSpeech speech, IMeetingProvider meeting, ServiceSettings settings,
    ILogger<PracticeService>? logger = null)
{
    public Task<PracticeAudio> SpeakAsync(PracticeRequest request, CancellationToken cancellation) => speech.SpeakAsync(request, cancellation);

    // A search call that hangs or fails once should not leave the answer ungrounded: each attempt is bounded and
    // retried once. Worst case stays near the provider's own 10 s limit, inside the 20 s endpoint deadline.
    internal TimeSpan RetrievalAttemptLimit { get; init; } = TimeSpan.FromSeconds(5);
    private const int RetrievalAttempts = 2;

    private async Task<Grounding> RetrieveAsync(string operation, string query, string owner, CancellationToken cancellation)
    {
        var log = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<PracticeService>.Instance;
        for (var attempt = 1; ; attempt++)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            limit.CancelAfter(RetrievalAttemptLimit);
            try { return await meeting.RetrieveAsync(query, owner, limit.Token); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
            catch (Exception)
            {
                var last = attempt >= RetrievalAttempts;
                PracticeDiagnostics.Failure(log, operation,
                    PracticeOutputNormalization.Invalid(last ? "retrieval_unavailable" : "retrieval_retry"), attempt);
                if (last) return new("unavailable", []);
            }
            await Task.Delay(TimeSpan.FromMilliseconds(200), cancellation);
        }
    }

    public async Task<object> ExecuteAsync(PracticeRequest request, string owner, CancellationToken cancellation)
    {
        var turn = request.History?.Count / 2 + 1 ?? 1;
        if (request.Operation == "turn" && turn > request.MaxTurns)
            return new { text = "Thanks for practicing with me. See you next time.", done = true, turn, grounding = "disabled", sources = Array.Empty<object>() };
        var grounding = new Grounding("disabled", []);
        if (request.UseMaterials)
        {
            var query = string.Join(' ', new[] { request.Topic, request.Scenario!.Kind, request.Scenario.Description,
                request.Operation == "turn" ? request.History!.LastOrDefault()?.Text ?? "" : request.Question });
            grounding = await RetrieveAsync(request.Operation, query, owner, cancellation);
            if (grounding.Status is not ("disabled" or "grounded" or "no_matches" or "unavailable"))
                grounding = new("unavailable", []);
        }
        string? failedRule = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            cancellation.ThrowIfCancellationRequested();
            JsonElement output;
            try
            {
                var response = await model.GenerateAsync(request, grounding, attempt > 0, failedRule, cancellation);
                cancellation.ThrowIfCancellationRequested();
                output = PracticeOutputs.Validate(response, request, settings.Fake);
            }
            catch (Exception exception)
            {
                PracticeDiagnostics.Failure(logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<PracticeService>.Instance,
                    request.Operation, exception, attempt + 1);
                var invalidOutput = exception is JsonException ||
                    exception is PracticeException { DiagnosticCategory: not null };
                if (invalidOutput && attempt == 0)
                {
                    failedRule = PracticeDiagnostics.SafeRule(exception is JsonException ? "json_parse" :
                        (exception as PracticeException)?.DiagnosticCategory);
                    continue;
                }
                if (exception is OperationCanceledException) throw;
                if (!invalidOutput) throw;
                throw PracticeException.Unavailable();
            }
            var sources = grounding.Status == "grounded" ? grounding.Documents.Select(item => item.Source.Title)
                .Distinct(StringComparer.Ordinal).Take(5).Select(title => new { title }).ToArray() : [];
            return request.Operation switch
            {
                "turn" => new { text = output.GetProperty("text").GetString(), done = false, turn, grounding = grounding.Status, sources },
                "suggest" => new { text = output.GetProperty("text").GetString(), grounding = grounding.Status, sources },
                _ => output
            };
        }
        throw PracticeException.Unavailable();
    }
}
