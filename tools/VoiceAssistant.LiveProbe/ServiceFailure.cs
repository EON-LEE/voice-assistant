using System.ClientModel;
using System.Text.Json;
using Azure;
using VoiceAssistant.Api;

namespace VoiceAssistant.LiveProbe;

public sealed record ServiceFailure(string Stage, int? HttpStatus, string Code, string? Parameter,
    string? SpeechCancellationReason = null, string? SpeechCancellationErrorCode = null)
{
    internal static ServiceFailure From(Exception exception, string stage) => exception switch
    {
        ProviderException { SpeechCancellation: { } speech } => new("speech", null, "speech_cancelled", null,
            Enum.IsDefined(speech.Reason) ? speech.Reason.ToString() : "Unknown",
            Enum.IsDefined(speech.ErrorCode) ? speech.ErrorCode.ToString() : "Unknown"),
        ClientResultException client => Parse(client.Status, client.GetRawResponse()?.Content, stage),
        RequestFailedException azure => new(stage, Status(azure.Status), AllowedCode(azure.ErrorCode), null),
        _ => new(stage, null, "unknown", null)
    };

    internal static ServiceFailure Parse(int status, BinaryData? body, string stage)
    {
        var code = "unknown";
        string? parameter = null;
        if (body is not null && body.ToMemory().Length <= 65536)
        {
            try
            {
                using var json = JsonDocument.Parse(body);
                if (json.RootElement.ValueKind == JsonValueKind.Object &&
                    json.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
                {
                    if (error.TryGetProperty("code", out var value) && value.ValueKind == JsonValueKind.String)
                        code = AllowedCode(value.GetString());
                    if (error.TryGetProperty("param", out var param) && param.ValueKind == JsonValueKind.String)
                        parameter = param.GetString() is "max_tokens" or "max_completion_tokens" or "temperature" or
                            "messages" or "messages[0].role" or "reasoning_effort" ? param.GetString() : null;
                }
            }
            catch (JsonException) { code = "unparseable_error"; }
        }
        return new(stage, Status(status), code, parameter);
    }

    private static int? Status(int value) => value is >= 100 and <= 599 ? value : null;
    private static string AllowedCode(string? value) => value switch
    {
        "unsupported_parameter" or "unsupported_value" or "invalid_request_error" or "DeploymentNotFound" or
        "AuthorizationFailed" or "PermissionDenied" or "permission_denied" or "content_filter" or
        "rate_limit_exceeded" or "invalid_api_key" or "model_not_found" => value,
        _ => "unknown"
    };
}
