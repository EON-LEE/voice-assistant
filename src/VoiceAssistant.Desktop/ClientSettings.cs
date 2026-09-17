using System.Text.Json;
using System.Text.Json.Serialization;

namespace VoiceAssistant.Desktop;

public enum ConnectionMode { Demo, Development, Production }

public sealed record ClientSettings
{
    public ConnectionMode Mode { get; init; } = ConnectionMode.Demo;
    public string Endpoint { get; init; } = "ws://localhost:8080/api/meeting";
    public string Authority { get; init; } = "https://login.microsoftonline.com";
    public string TenantId { get; init; } = "YOUR-TENANT-ID";
    public string ClientId { get; init; } = "YOUR-PUBLIC-CLIENT-ID";
    public string Scope { get; init; } = "api://YOUR-API-CLIENT-ID/Meeting.Access";

    public static ClientSettings Load(string path) =>
        JsonSerializer.Deserialize<ClientSettings>(File.ReadAllText(path), new JsonSerializerOptions
        {
            Converters = { new JsonStringEnumConverter() },
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        }) ?? throw new InvalidDataException("Settings file is empty.");

    public Uri Validate()
    {
        if (!Enum.IsDefined(Mode))
            throw new InvalidOperationException("Unknown connection mode.");
        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme is not ("ws" or "wss") || endpoint.AbsolutePath != "/api/meeting" ||
            !string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Fragment) ||
            !string.IsNullOrEmpty(endpoint.Query))
            throw new InvalidOperationException("Endpoint must be ws(s)://host[:port]/api/meeting without credentials, query, or fragment.");
        if (Mode == ConnectionMode.Development && !endpoint.IsLoopback)
            throw new InvalidOperationException("Unauthenticated Development mode is restricted to loopback endpoints.");
        if (Mode == ConnectionMode.Production)
        {
            if (endpoint.Scheme != "wss")
                throw new InvalidOperationException("Production requires a secure wss:// endpoint.");
            if (!Uri.TryCreate(Authority, UriKind.Absolute, out var authority) || authority.Scheme != "https" ||
                !string.IsNullOrEmpty(authority.UserInfo) || !string.IsNullOrEmpty(authority.Query) ||
                !string.IsNullOrEmpty(authority.Fragment) || authority.AbsolutePath != "/")
                throw new InvalidOperationException("Authority must be an HTTPS Entra authority host (without a tenant path).");
            if (!Guid.TryParse(TenantId, out _) || !Guid.TryParse(ClientId, out _) ||
                string.IsNullOrWhiteSpace(Scope) || Scope.Contains("YOUR-", StringComparison.Ordinal))
                throw new InvalidOperationException("Configure TenantId, public ClientId, and delegated API Scope before using Production.");
        }
        return endpoint;
    }
}
