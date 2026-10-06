using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace VoiceAssistant.Desktop.Protocol;

public sealed class AuthenticatedApiClient : IDisposable
{
    private readonly HttpClient http;
    private readonly ClientSettings settings;
    private readonly IAccessTokenProvider identity;
    private readonly JsonSerializerOptions json = new(JsonSerializerDefaults.Web);

    public AuthenticatedApiClient(ClientSettings settings, IAccessTokenProvider identity, HttpMessageHandler? handler = null)
    {
        this.settings = settings;
        this.identity = identity;
        http = handler is null
            ? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            : new HttpClient(handler, disposeHandler: true);
    }

    public async Task<Uri> GetMeetingSocketAsync(CancellationToken cancellationToken)
    {
        var ticketUri = new Uri(settings.ApiBase, "/api/session/ticket");
        using var request = await CreateRequestAsync(HttpMethod.Post, ticketUri, cancellationToken);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        RequireJson(response);
        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = JsonDocument.Parse(await ReadBoundedAsync(responseStream, 65536, cancellationToken));
        var root = document.RootElement;
        string ticket = root.GetProperty("ticket").GetString() ?? "";
        DateTimeOffset expiry = root.GetProperty("expiresAt").GetDateTimeOffset();
        if (ticket.Length is < 32 or > 128 || expiry <= DateTimeOffset.UtcNow || expiry > DateTimeOffset.UtcNow.AddMinutes(1))
            throw new InvalidDataException("The server returned an invalid or expired meeting ticket.");
        var endpoint = new UriBuilder(settings.Validate());
        endpoint.Query = "ticket=" + Uri.EscapeDataString(ticket);
        return endpoint.Uri;
    }

    public async Task<JsonDocument> PostJsonAsync(string path, object payload, CancellationToken cancellationToken)
    {
        if (!AllowedApiPath(path)) throw new InvalidOperationException("Unsupported authenticated API path.");
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(payload, json);
        if (body.Length > 16384) throw new InvalidDataException("The request exceeds the API's 16 KiB limit.");
        using var request = await CreateRequestAsync(HttpMethod.Post, new Uri(settings.ApiBase, path), cancellationToken);
        request.Content = new ByteArrayContent(body);
        request.Content.Headers.ContentType = new("application/json") { CharSet = "utf-8" };
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        RequireJson(response);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        byte[] responseBytes = await ReadBoundedAsync(stream, 65536, cancellationToken);
        return JsonDocument.Parse(responseBytes, new JsonDocumentOptions { MaxDepth = 16 });
    }

    public async Task<byte[]> PostAudioAsync(string path, object payload, CancellationToken cancellationToken)
    {
        if (path != "/api/assist/speak") throw new InvalidOperationException("Unsupported audio endpoint.");
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(payload, json);
        using var request = await CreateRequestAsync(HttpMethod.Post, new Uri(settings.ApiBase, path), cancellationToken);
        request.Content = new ByteArrayContent(body);
        request.Content.Headers.ContentType = new("application/json") { CharSet = "utf-8" };
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        if (response.Content.Headers.ContentType?.MediaType is not ("audio/mpeg" or "audio/wav"))
            throw new InvalidDataException("The speech service returned an unsupported audio format.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) != 0)
        {
            if (output.Length + read > 4 * 1024 * 1024)
                throw new InvalidDataException("The speech response exceeded the 4 MiB limit.");
            output.Write(buffer, 0, read);
        }
        if (output.Length == 0) throw new InvalidDataException("The speech service returned empty audio.");
        return output.ToArray();
    }

    public async Task<HttpResponseMessage> SendKnowledgeAsync(HttpMethod method, string path, HttpContent? content,
        CancellationToken cancellationToken)
    {
        if (path != "/api/knowledge" && !System.Text.RegularExpressions.Regex.IsMatch(path, @"\A/api/knowledge/[0-9a-f]{32}\z"))
            throw new InvalidOperationException("Unsupported materials endpoint.");
        var request = await CreateRequestAsync(method, new Uri(settings.ApiBase, path), cancellationToken);
        request.Content = content;
        try
        {
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            request.Dispose();
            return response;
        }
        catch { request.Dispose(); throw; }
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream input, int maximum, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await input.ReadAsync(buffer, cancellationToken)) != 0)
        {
            if (output.Length + count > maximum) throw new InvalidDataException("The service response exceeded the 64 KiB limit.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    private async Task<HttpRequestMessage> CreateRequestAsync(HttpMethod method, Uri uri, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await identity.AcquireTokenAsync(false, cancellationToken));
        request.Headers.Add("Origin", settings.Origin);
        request.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
        return request;
    }

    private static bool AllowedApiPath(string path) => path is
        "/api/assist/enrich" or "/api/practice/turn" or "/api/practice/suggest" or
        "/api/practice/feedback" or "/api/practice/summary";

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        string code = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "unauthorized",
            HttpStatusCode.Forbidden => "forbidden",
            (HttpStatusCode)429 => "busy",
            _ => "provider_unavailable"
        };
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = JsonDocument.Parse(await ReadBoundedAsync(stream, 8192, cancellationToken));
            if (document.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
                code = error.GetString() ?? code;
        }
        catch (JsonException) { }
        catch (InvalidDataException) { }
        throw new ApiRequestException(code, response.Headers.RetryAfter?.Delta);
    }

    private static void RequireJson(HttpResponseMessage response)
    {
        if (response.Content.Headers.ContentType?.MediaType != "application/json")
            throw new InvalidDataException("The service returned an invalid JSON response.");
    }

    public void Dispose() => http.Dispose();
}

public sealed class ApiRequestException(string code, TimeSpan? retryAfter = null) : IOException(SafeMessage(code))
{
    public string Code { get; } = code;
    public TimeSpan? RetryAfter { get; } = retryAfter;
    private static string SafeMessage(string code) => code switch
    {
        "unauthorized" => "Sign-in expired. Sign in again.",
        "forbidden" => "This account is not authorized for the configured service.",
        "busy" => "The coach is busy. Wait briefly, then retry.",
        "invalid_request" => "Check the practice inputs and try again.",
        "too_large" => "The request is too long.",
        "provider_timeout" => "The service timed out. Retry when ready.",
        "knowledge_unavailable" => "Personal materials are unavailable. Retry later.",
        "quota_exceeded" => "Your personal materials quota is full.",
        "unsupported_type" => "This material type is not supported.",
        "no_text" => "No readable text was found in this material.",
        _ => "The service request failed. Check connectivity and retry."
    };
}
