using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

namespace VoiceAssistant.Api.Knowledge;

public sealed class KnowledgeRequestReader(MaterialExtractor extractor)
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public async Task<KnowledgeInput> ReadAsync(HttpRequest request, CancellationToken cancellation)
    {
        if (request.ContentLength > KnowledgeLimits.MaxRequestBytes) throw KnowledgeException.TooLarge();
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var contentType)) throw KnowledgeException.Unsupported();
        if (contentType.MediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase))
        {
            // JSON escaping can expand 400k UTF-16 characters to 2.4MB, still below the request bound.
            var bytes = await ReadBytesAsync(request.Body, KnowledgeLimits.MaxRequestBytes, cancellation);
            using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 });
            if (json.RootElement.ValueKind != JsonValueKind.Object) throw KnowledgeException.Invalid();
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in json.RootElement.EnumerateObject())
                if (property.Name is not ("title" or "text") || !names.Add(property.Name)) throw KnowledgeException.Invalid();
            if (!json.RootElement.TryGetProperty("title", out var title) || title.ValueKind != JsonValueKind.String ||
                !json.RootElement.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String)
                throw KnowledgeException.Invalid();
            var value = text.GetString()!;
            if (value.Length > KnowledgeLimits.MaxCharactersPerDocument) throw KnowledgeException.TooLarge();
            if (string.IsNullOrWhiteSpace(value)) throw KnowledgeException.NoText();
            return new(Title(title.GetString()!), value);
        }
        if (!contentType.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase))
            throw KnowledgeException.Unsupported();
        var boundary = HeaderUtilities.RemoveQuotes(contentType.Boundary).Value;
        if (string.IsNullOrEmpty(boundary) || boundary.Length > 128) throw KnowledgeException.Invalid();
        using var capped = new CappedReadStream(request.Body, KnowledgeLimits.MaxRequestBytes);
        var multipart = new MultipartReader(boundary, capped)
        {
            HeadersCountLimit = 16, HeadersLengthLimit = 8192,
            BodyLengthLimit = KnowledgeLimits.MaxRequestBytes
        };
        byte[]? file = null;
        string? filename = null, overrideTitle = null;
        var titleSeen = false;
        MultipartSection? section;
        while ((section = await multipart.ReadNextSectionAsync(cancellation)) is not null)
        {
            if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition) ||
                !disposition.DispositionType.Equals("form-data", StringComparison.OrdinalIgnoreCase))
                throw KnowledgeException.Invalid();
            var name = HeaderUtilities.RemoveQuotes(disposition.Name).Value;
            if (name == "file" && (disposition.FileName.HasValue || disposition.FileNameStar.HasValue))
            {
                if (file is not null) throw KnowledgeException.Invalid();
                filename = HeaderUtilities.RemoveQuotes(disposition.FileNameStar.HasValue ? disposition.FileNameStar : disposition.FileName).Value;
                if (string.IsNullOrWhiteSpace(filename)) throw KnowledgeException.Invalid();
                filename = filename.Replace('\\', '/').Split('/')[^1];
                file = await ReadBytesAsync(section.Body, KnowledgeLimits.MaxFileBytes, cancellation);
            }
            else if (name == "title" && !disposition.FileName.HasValue && !disposition.FileNameStar.HasValue)
            {
                if (titleSeen) throw KnowledgeException.Invalid();
                titleSeen = true;
                overrideTitle = Title(Utf8.GetString(await ReadBytesAsync(section.Body, 800, cancellation)));
            }
            else throw KnowledgeException.Invalid();
        }
        if (file is null || filename is null) throw KnowledgeException.Invalid();
        var documentTitle = Title(overrideTitle ?? filename);
        var extracted = await extractor.ExtractAsync(filename, file, cancellation);
        return new(documentTitle, extracted);
    }

    internal static string Title(string title)
    {
        if (title.Length is < 1 or > 200 || string.IsNullOrWhiteSpace(title) || title.Any(char.IsControl))
            throw KnowledgeException.Invalid();
        return title.Trim();
    }

    internal static async Task<byte[]> ReadBytesAsync(Stream stream, int limit, CancellationToken cancellation)
    {
        using var output = new MemoryStream();
        var buffer = new byte[16384];
        int count;
        while ((count = await stream.ReadAsync(buffer, cancellation)) > 0)
        {
            if (output.Length + count > limit) throw KnowledgeException.TooLarge();
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }
}
