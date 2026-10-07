using System.Text.Json;
using System.Text.Json.Serialization;

namespace VoiceAssistant.Desktop;

public enum ConnectionMode { Demo, Development, Production }

public sealed record ClientSettings
{
    public ConnectionMode Mode { get; init; } = ConnectionMode.Demo;
    public string Endpoint { get; init; } = "wss://voice-web.gentlesky-d6ba12c8.koreacentral.azurecontainerapps.io/api/meeting";
    public string Origin { get; init; } = "https://voice-web.gentlesky-d6ba12c8.koreacentral.azurecontainerapps.io";
    public string Authority { get; init; } = "https://login.microsoftonline.com";
    public string TenantId { get; init; } = "2573db8c-dfe5-4805-9e28-a0859692e705";
    public string ClientId { get; init; } = "6c67aee7-0c67-48aa-9dce-40db347c5a7f";
    public string Scope { get; init; } = "api://4546bd70-1872-4a1d-bdcd-d09377e365e4/Meeting.Access";
    public string ResponseMode { get; init; } = "grounded";
    public int EndSilenceMs { get; init; } = 500;
    public string Topic { get; init; } = "";
    public string ProfileName { get; init; } = "";
    public string ProfileRole { get; init; } = "";
    public string ProfileProject { get; init; } = "";
    public bool ProfileConfirmed { get; init; }
    public bool TranscribeOnly { get; init; }

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
                string.IsNullOrWhiteSpace(Scope) || Scope.Contains("YOUR-", StringComparison.Ordinal) ||
                !Scope.EndsWith("/Meeting.Access", StringComparison.Ordinal))
                throw new InvalidOperationException("Configure TenantId, public ClientId, and the delegated Meeting.Access scope before using Production.");
            if (!Uri.TryCreate(Origin, UriKind.Absolute, out var origin) || origin.Scheme != "https" ||
                Origin != origin.GetLeftPart(UriPartial.Authority) || origin.UserInfo.Length != 0)
                throw new InvalidOperationException("Origin must exactly match the API's configured canonical HTTPS application origin.");
            if (!origin.Host.Equals(endpoint.Host, StringComparison.OrdinalIgnoreCase) || origin.Port != 443 || endpoint.Port != 443)
                throw new InvalidOperationException("Production Origin and WSS endpoint must use the same HTTPS host on port 443.");
            if (ResponseMode is not ("balanced" or "grounded" or "conversation"))
                throw new InvalidOperationException("ResponseMode must be balanced, grounded, or conversation.");
            if (EndSilenceMs is < 350 or > 1500)
                throw new InvalidOperationException("EndSilenceMs must be between 350 and 1500.");
            if (Topic.Length > 300 || ProfileName.Length > 100 || ProfileRole.Length > 160 || ProfileProject.Length > 300 ||
                (!ProfileConfirmed && (ProfileName.Length != 0 || ProfileRole.Length != 0 || ProfileProject.Length != 0)))
                throw new InvalidOperationException("Profile values must be within the documented limits and explicitly confirmed.");
        }
        return endpoint;
    }

    public Uri ApiBase
    {
        get
        {
            var endpoint = Validate();
            return new UriBuilder(endpoint.Scheme == "wss" ? Uri.UriSchemeHttps : Uri.UriSchemeHttp,
                endpoint.Host, endpoint.IsDefaultPort ? -1 : endpoint.Port).Uri;
        }
    }
}
