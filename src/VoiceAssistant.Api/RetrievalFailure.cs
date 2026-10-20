using System.ClientModel;
using System.Text.Json;
using Azure;
using Azure.Identity;

namespace VoiceAssistant.Api;

/// <summary>
/// Content-free description of why document retrieval failed. Every field is safe to log: no query, transcript,
/// document text, identity, URL or service error message is ever stored here.
/// </summary>
/// <param name="Kind">Fixed category such as <c>throttled</c>, <c>server_error</c>, <c>timeout</c>, <c>network</c>, <c>quota</c>,
/// <c>auth</c>, <c>not_found</c>, <c>bad_request</c>, <c>partial_results</c>, <c>semantic_failed</c>, <c>unscored</c>,
/// <c>invalid_result</c> or <c>unexpected</c>.</param>
/// <param name="Transient">Whether one quick retry can reasonably succeed.</param>
/// <param name="ExceptionType">Exception type name, or <c>SearchResult</c> for a response that failed validation.</param>
/// <param name="HttpStatus">HTTP status when the service answered; null for timeouts and network failures.</param>
/// <param name="ErrorCode">Service error code (for example a Search code) when it is a plain identifier.</param>
/// <param name="Retried">Whether the failure is final after one retry.</param>
public sealed record RetrievalFailure(string Kind, bool Transient, string ExceptionType, int? HttpStatus, string? ErrorCode, bool Retried)
{
    internal const string ResultValidation = "SearchResult";

    internal static RetrievalFailure Result(string kind, bool transient = false) => new(kind, transient, ResultValidation, null, null, false);

    /// <summary>Classifies a retrieval exception; null when it is not a recognised retrieval failure.</summary>
    internal static RetrievalFailure? Classify(Exception exception) => exception switch
    {
        ProviderException { Retrieval: { } known } => known,
        RequestFailedException failed => FromStatus(failed.Status, nameof(RequestFailedException), SafeCode(failed.ErrorCode)),
        ClientResultException failed => FromStatus(failed.Status, nameof(ClientResultException), null),
        AuthenticationFailedException => new("auth", false, exception.GetType().Name, null, null, false),
        OperationCanceledException => new("timeout", true, exception.GetType().Name, null, null, false),
        HttpRequestException or IOException => new("network", true, exception.GetType().Name, null, null, false),
        JsonException or FormatException or ProviderException => new("invalid_result", false, exception.GetType().Name, null, null, false),
        AggregateException { InnerExceptions.Count: > 0 } aggregate => Classify(aggregate.InnerExceptions[^1]),
        _ => null
    };

    /// <summary>Like <see cref="Classify"/>, but an unrecognised exception becomes a permanent <c>unexpected</c> failure.</summary>
    internal static RetrievalFailure From(Exception exception) =>
        Classify(exception) ?? new("unexpected", false, exception.GetType().Name, null, null, false);

    private static RetrievalFailure FromStatus(int status, string type, string? code) => status switch
    {
        // Azure SDK transports report a request that never got an HTTP answer as status 0.
        0 => new("network", true, type, null, code, false),
        408 => new("timeout", true, type, status, code, false),
        429 => new("throttled", true, type, status, code, false),
        >= 500 => new("server_error", true, type, status, code, false),
        401 or 403 => new("auth", false, type, status, code, false),
        402 => new("quota", false, type, status, code, false),
        404 => new("not_found", false, type, status, code, false),
        _ => new("bad_request", false, type, status, code, false)
    };

    // Service error codes are identifiers such as "OperationNotAllowed"; anything else is dropped rather than logged.
    private static string? SafeCode(string? code) =>
        code is { Length: > 0 and <= 64 } && code.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '.' or '-')
            ? code : null;
}
