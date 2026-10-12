using System.Text.Json;

namespace VoiceAssistant.Desktop.Protocol;

public sealed record ReplySource(string Title, string Url, string? UpdatedAt);
public sealed record ServerEvent(
    string Type, string? TurnId = null, int Revision = 0, string? Text = null,
    string? ResponseId = null, IReadOnlyList<ReplySource>? Sources = null,
    string? Code = null, bool Retryable = false, string? Grounding = null,
    string? ResponseRoute = null, bool? RetrievalPrefetched = null, IReadOnlyList<string>? Suggestions = null)
{
    public static ServerEvent Parse(ReadOnlySpan<byte> json)
    {
        using var document = JsonDocument.Parse(json.ToArray());
        var root = document.RootElement;
        string Required(string name)
        {
            if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String ||
                value.GetString() is not { } text || text.Length > 32768)
                throw new InvalidDataException($"Server event has missing or invalid '{name}'.");
            return text;
        }
        string type = Required("type");
        switch (type)
        {
            case "session.ready": return new(type);
            case "transcript.partial":
            case "transcript.final":
                if (!root.TryGetProperty("revision", out var revision) || !revision.TryGetInt32(out int number) || number < 0)
                    throw new InvalidDataException("Transcript revision must be a nonnegative integer.");
                return new(type, Required("turnId"), number, Required("text"));
            case "response.started":
            case "response.cancelled":
                return new(type, Required("turnId"), ResponseId: Required("responseId"));
            case "response.delta":
                return new(type, Required("turnId"), Text: Required("text"), ResponseId: Required("responseId"));
            case "response.completed":
                var sources = new List<ReplySource>();
                if (!root.TryGetProperty("sources", out var sourceArray) || sourceArray.ValueKind != JsonValueKind.Array ||
                    sourceArray.GetArrayLength() > 20)
                    throw new InvalidDataException("Response sources must be an array of at most 20 entries.");
                foreach (var source in sourceArray.EnumerateArray())
                {
                    var title = source.GetProperty("title").GetString() ?? throw new InvalidDataException("Source title is missing.");
                    var url = source.GetProperty("url").GetString() ?? throw new InvalidDataException("Source URL is missing.");
                    var updatedAt = source.TryGetProperty("updatedAt", out var updated) && updated.ValueKind != JsonValueKind.Null
                        ? updated.GetString() : null;
                    sources.Add(new(title, url, updatedAt));
                }
                string? grounding = root.TryGetProperty("grounding", out var groundingValue) ? groundingValue.GetString() : null;
                string? route = root.TryGetProperty("responseRoute", out var routeValue) ? routeValue.GetString() : null;
                bool? prefetched = root.TryGetProperty("retrievalPrefetched", out var prefetchedValue)
                    ? prefetchedValue.GetBoolean() : null;
                if (grounding is not null && grounding is not ("disabled" or "grounded" or "unavailable" or "no_matches") ||
                    route is not null && route is not ("transcript" or "profile" or "knowledge"))
                    throw new InvalidDataException("Response grounding metadata is invalid.");
                IReadOnlyList<string>? suggestions = null;
                if (root.TryGetProperty("suggestions", out var suggestionArray))
                {
                    if (suggestionArray.ValueKind != JsonValueKind.Array || suggestionArray.GetArrayLength() is < 1 or > 2)
                        throw new InvalidDataException("Response suggestions must contain one or two English answers.");
                    var values = new List<string>();
                    foreach (var item in suggestionArray.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.String || item.GetString() is not { } value ||
                            string.IsNullOrWhiteSpace(value) || value.Length > 8000)
                            throw new InvalidDataException("Response suggestion is invalid.");
                        values.Add(value);
                    }
                    if (values[0] != Required("text"))
                        throw new InvalidDataException("The primary suggestion must match response text.");
                    suggestions = values;
                }
                return new(type, Required("turnId"), Text: Required("text"), ResponseId: Required("responseId"),
                    Sources: sources, Grounding: grounding, ResponseRoute: route, RetrievalPrefetched: prefetched,
                    Suggestions: suggestions);
            case "error":
                if (!root.TryGetProperty("retryable", out var retryable) ||
                    retryable.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new InvalidDataException("Error retryable must be a boolean.");
                return new(type, Text: Required("message"), Code: Required("code"), Retryable: retryable.GetBoolean());
            default: throw new InvalidDataException($"Unsupported server event '{type}'.");
        }
    }
}
