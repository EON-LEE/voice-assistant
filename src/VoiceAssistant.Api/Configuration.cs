namespace VoiceAssistant.Api;

public sealed class ServiceSettings
{
    public string Mode { get; init; } = "Azure";
    public string TenantId { get; init; } = "";
    public string Audience { get; init; } = "";
    public string ClientId { get; init; } = "";
    public string Scope { get; init; } = "";
    public string SpeechRegion { get; init; } = "";
    public string SpeechEndpoint { get; init; } = "";
    public string SpeechResourceId { get; init; } = "";
    public string OpenAIEndpoint { get; init; } = "";
    public string ChatDeployment { get; init; } = "";
    public int ChatMaxOutputTokens { get; init; } = 2048;
    public string EmbeddingDeployment { get; init; } = "";
    public string SearchEndpoint { get; init; } = "";
    public string SearchIndex { get; init; } = "";
    public string SearchSemanticConfiguration { get; init; } = "meeting-semantic";
    public double SearchMinimumRerankerScore { get; init; } = 2.0;
    public string[] AllowedOrigins { get; init; } = [];
    public bool Fake => Mode == "Fake";
    public bool SearchEnabled => !string.IsNullOrEmpty(SearchEndpoint);

    public static ServiceSettings Read(IConfiguration config, IHostEnvironment environment)
    {
        string Get(string key) => config[key] ?? "";
        var settings = new ServiceSettings
        {
            Mode = config["Provider:Mode"] ?? "Azure",
            TenantId = Get("Authentication:TenantId"),
            Audience = Get("Authentication:Audience"),
            ClientId = Get("Authentication:ClientId"),
            Scope = Get("Authentication:Scope"),
            SpeechRegion = Get("Azure:SpeechRegion"),
            SpeechEndpoint = Get("Azure:SpeechEndpoint"),
            SpeechResourceId = Get("Azure:SpeechResourceId"),
            OpenAIEndpoint = Get("Azure:OpenAIEndpoint"),
            ChatDeployment = Get("Azure:ChatDeployment"),
            ChatMaxOutputTokens = ReadChatTokenLimit(config),
            EmbeddingDeployment = Get("Azure:EmbeddingDeployment"),
            SearchEndpoint = Get("Azure:SearchEndpoint"),
            SearchIndex = Get("Azure:SearchIndex"),
            SearchSemanticConfiguration = config["Azure:SearchSemanticConfiguration"] ?? "meeting-semantic",
            SearchMinimumRerankerScore = ReadMinimumRerankerScore(config),
            AllowedOrigins = config.GetSection("Security:AllowedOrigins").Get<string[]>() ?? []
        };
        if (settings.Mode is not ("Azure" or "Fake") || (settings.Fake && !environment.IsDevelopment()))
            throw new InvalidOperationException("Fake requires Development; Provider:Mode must be Azure or Fake.");
        if (!settings.Fake)
        {
            if (!Guid.TryParse(settings.TenantId, out _) || !Guid.TryParse(settings.ClientId, out _) ||
                string.IsNullOrWhiteSpace(settings.Audience) || !settings.Scope.EndsWith("/Meeting.Access", StringComparison.Ordinal) ||
                settings.AllowedOrigins.Length == 0 || settings.AllowedOrigins.Any(origin => !OriginPolicy.IsCanonicalHttpsOrigin(origin)))
                throw new InvalidOperationException("Azure mode requires valid Authentication, Speech, OpenAI and Security:AllowedOrigins configuration.");
            settings.ValidateAzureProvider();
            settings.ValidateSearchRelevance();
            if ((settings.SearchEnabled || settings.SearchIndex.Length > 0) &&
                (!Https(settings.SearchEndpoint) || settings.SearchIndex.Length == 0 || settings.EmbeddingDeployment.Length == 0))
                throw new InvalidOperationException("Search requires endpoint, index and embedding deployment together.");
        }
        return settings;
    }

    public void ValidateAzureProvider()
    {
        if (Mode != "Azure" || string.IsNullOrWhiteSpace(SpeechRegion) || string.IsNullOrWhiteSpace(SpeechResourceId) ||
            !Https(OpenAIEndpoint) || string.IsNullOrWhiteSpace(ChatDeployment))
            throw new InvalidOperationException("Azure provider requires Speech region/resource ID and OpenAI endpoint/deployment.");
        if (SpeechEndpoint.Length > 0 && !Https(SpeechEndpoint))
            throw new InvalidOperationException("Azure:SpeechEndpoint must be HTTPS.");
        if (ChatMaxOutputTokens is < 64 or > 4096)
            throw new InvalidOperationException("Azure:ChatMaxOutputTokens must be an integer from 64 to 4096.");
    }

    public static int ReadChatTokenLimit(IConfiguration config)
    {
        var value = config["Azure:ChatMaxOutputTokens"];
        if (value is null) return 2048;
        if (int.TryParse(value, out var limit) && limit is >= 64 and <= 4096) return limit;
        throw new InvalidOperationException("Azure:ChatMaxOutputTokens must be an integer from 64 to 4096.");
    }

    public void ValidateSearchRelevance()
    {
        if (string.IsNullOrWhiteSpace(SearchSemanticConfiguration) ||
            !double.IsFinite(SearchMinimumRerankerScore) || SearchMinimumRerankerScore is < 0 or > 4)
            throw new InvalidOperationException("Search requires a semantic configuration and a finite minimum reranker score from 0 to 4.");
    }

    public static double ReadMinimumRerankerScore(IConfiguration config)
    {
        var value = config["Azure:SearchMinimumRerankerScore"];
        if (value is null) return 2.0;
        if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture,
            out var score) && double.IsFinite(score) && score is >= 0 and <= 4) return score;
        throw new InvalidOperationException("Azure:SearchMinimumRerankerScore must be a finite number from 0 to 4.");
    }

    private static bool Https(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.UserInfo.Length == 0;
}
