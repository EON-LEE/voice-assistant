using System.ClientModel;
using Azure;

namespace VoiceAssistant.Api.Practice;

internal static class PracticeDiagnostics
{
    internal static void Failure(ILogger logger, string operation, Exception error, int attempt)
    {
        var endpoint = operation is "enrich" or "speak" or "turn" or "suggest" or "feedback" or "summary" ? operation : "unknown";
        var status = error switch { ClientResultException client => client.Status, RequestFailedException azure => azure.Status, _ => 0 };
        var category = error switch
        {
            PracticeException { DiagnosticCategory: { } code } => code,
            System.Text.Json.JsonException => "json_parse",
            OperationCanceledException => "timeout",
            ClientResultException or RequestFailedException => "http_error",
            _ => "provider_error"
        };
        logger.LogWarning("Practice endpoint {Endpoint} failure {Category}, HTTP status {Status}, attempt {Attempt}.",
            endpoint, category, status, attempt);
    }
}
