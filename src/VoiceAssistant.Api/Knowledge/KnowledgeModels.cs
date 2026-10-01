using System.Text.RegularExpressions;

namespace VoiceAssistant.Api.Knowledge;

public static class KnowledgeLimits
{
    public const int MaxDocuments = 300;
    public const int MaxChunks = 5000;
    public const int MaxFileBytes = 5 * 1024 * 1024;
    public const int MaxCharactersPerDocument = 400000;
    public const int MaxRequestBytes = MaxFileBytes + 65536;
    public static object Public => new
    {
        maxDocuments = MaxDocuments, maxChunks = MaxChunks,
        maxFileBytes = MaxFileBytes, maxCharactersPerDocument = MaxCharactersPerDocument
    };
}

public sealed record KnowledgeDocument(string Id, string Title, int Chunks, DateTimeOffset UpdatedAt);
public sealed record KnowledgeUpload(string Id, string Title, int Chunks, int Characters, DateTimeOffset UpdatedAt);
public sealed record KnowledgeInput(string Title, string Text);
public sealed record KnowledgeChunk(string Id, string Content);

public sealed class KnowledgeException(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public static KnowledgeException Invalid() => new(400, "invalid_request", "The materials request is invalid.");
    public static KnowledgeException TooLarge() => new(413, "too_large", "The document exceeds the supported size limit.");
    public static KnowledgeException Unsupported() => new(415, "unsupported_type", "Unsupported input. Save as UTF-8 text, docx, pptx or a text-layer PDF.");
    public static KnowledgeException NoText() => new(422, "no_text", "No readable text was found. Scanned PDFs and images require text extraction first.");
    public static KnowledgeException Unavailable() => new(503, "knowledge_unavailable", "Meeting materials are unavailable. Please retry.");
    public static KnowledgeException Busy() => new(429, "busy", "Too many materials uploads are in progress. Please retry.");
}

public static partial class KnowledgeIds
{
    [GeneratedRegex("\\A[0-9a-f]{32}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex DocumentPattern();
    [GeneratedRegex("\\Akb-([0-9a-f]{32})-([0-9]{4})\\z", RegexOptions.CultureInvariant)]
    private static partial Regex ChunkPattern();
    public static bool ValidDocument(string value) => DocumentPattern().IsMatch(value);
    public static string? DocumentOf(string value)
    {
        var match = ChunkPattern().Match(value);
        return match.Success ? match.Groups[1].Value : null;
    }
    public static string Owner(string value) => Guid.TryParseExact(value, "D", out var id)
        ? id.ToString("D") : throw KnowledgeException.Invalid();
}

public interface IKnowledgeStore
{
    Task<IReadOnlyList<KnowledgeDocument>> ListAsync(string owner, CancellationToken cancellation);
    Task UploadAsync(string owner, KnowledgeDocument document, IReadOnlyList<KnowledgeChunk> chunks, CancellationToken cancellation);
    Task DeleteAsync(string owner, string documentId, CancellationToken cancellation);
}
