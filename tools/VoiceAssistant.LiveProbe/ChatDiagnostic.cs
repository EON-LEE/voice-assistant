using System.Text.Json;
using VoiceAssistant.Api;

namespace VoiceAssistant.LiveProbe;

internal static class ChatDiagnostic
{
    internal static async Task<int> RunAsync(AzureMeetingProvider provider, TimeSpan deadline, TextWriter output, CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(deadline);
        var clock = new ProbeClock();
        var status = "FAILED";
        var reason = "empty_response";
        var deltas = 0;
        var characters = 0;
        double? firstDeltaMs = null;
        ServiceFailure? failure = null;
        try
        {
            await foreach (var delta in provider.AnswerAsync(
                [new("Please suggest one concise English sentence asking for a project update.")],
                new("disabled", []), timeout.Token))
            {
                timeout.Token.ThrowIfCancellationRequested();
                if (!string.IsNullOrEmpty(delta))
                {
                    firstDeltaMs ??= clock.ElapsedMs;
                    deltas++;
                }
                characters += delta.Length;
                if (characters > 8000) { reason = "response_limit"; break; }
            }
            if (deltas > 0 && reason != "response_limit") { status = "SUCCESS"; reason = "completed"; }
        }
        catch (OperationCanceledException)
        {
            status = cancellation.IsCancellationRequested ? "CANCELLED" : "FAILED";
            reason = cancellation.IsCancellationRequested ? "cancelled" : "deadline_exceeded";
        }
        catch (Exception exception)
        {
            failure = ServiceFailure.From(exception, "openai");
            reason = failure.HttpStatus.HasValue ? "service_error" : "provider_error";
        }
        await output.WriteLineAsync(JsonSerializer.Serialize(new
        {
            schemaVersion = 1, scope = "openai_only", provider = "Azure", status, reason,
            deltaEvents = deltas, elapsedMs = clock.ElapsedMs, firstDeltaMs,
            failure, fullPipelineVerified = false
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        return status == "SUCCESS" ? 0 : 1;
    }
}
