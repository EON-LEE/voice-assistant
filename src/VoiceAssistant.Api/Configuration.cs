namespace VoiceAssistant.Api;

public sealed class ServiceSettings
{
    public string Mode { get; init; } = "Azure";
    public int MaxSessionMinutes { get; init; } = 30;
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
    /// <summary>Reasoning effort sent with the live answer request (minimal, low or medium); null sends none.</summary>
    public string? ChatReasoningEffort { get; init; }
    /// <summary>Reasoning effort sent with the turn classifier and Korean translation requests; null sends none.</summary>
    public string? ChatAuxiliaryReasoningEffort { get; init; }
    /// <summary>An api-version for the live chat requests instead of the pinned SDK's default; null keeps the SDK default.</summary>
    public string? ChatApiVersion { get; init; }
    public string EmbeddingDeployment { get; init; } = "";
    public string SearchEndpoint { get; init; } = "";
    public string SearchIndex { get; init; } = "";
    public string SearchSemanticConfiguration { get; init; } = "meeting-semantic";
    public double SearchMinimumRerankerScore { get; init; } = 2.0;
    public string[] AllowedOrigins { get; init; } = [];
    /// <summary>Experiment: start the reply at a final that is a complete question or request, instead of after the settle.</summary>
    public bool EarlyStartAtFinal { get; init; }
    /// <summary>
    /// Experiment, on top of <see cref="EarlyStartAtFinal"/>: generate that reply already on a paused partial, held back until
    /// the final adopts it.
    /// </summary>
    public bool SpeculativeStart { get; init; }
    /// <summary>
    /// Experiment: a short statement that waits for more context is answered after this much more quiet (milliseconds after
    /// the settle window); 0 keeps waiting for more speech.
    /// </summary>
    public int QuietReplyMs { get; init; }
    /// <summary>The most search chunks one reply may use as evidence.</summary>
    public int SearchMaxEvidence { get; init; } = 5;
    /// <summary>Experiment: add phrase hints from the caller's own materials to the speech recognizer.</summary>
    public bool MaterialPhraseHints { get; init; }
    /// <summary>Experiment: tell the answer model to decline guarantees and promises and state the documented policy instead.</summary>
    public bool CommitmentPolicy { get; init; }
    public bool Fake => Mode == "Fake";
    public bool SearchEnabled => !string.IsNullOrEmpty(SearchEndpoint);

    public static ServiceSettings Read(IConfiguration config, IHostEnvironment environment)
    {
        string Get(string key) => config[key] ?? "";
        var settings = new ServiceSettings
        {
            Mode = config["Provider:Mode"] ?? "Azure",
            MaxSessionMinutes = ReadMaxSessionMinutes(config),
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
            ChatReasoningEffort = ReadReasoningEffort(config, "Azure:ChatReasoningEffort"),
            ChatAuxiliaryReasoningEffort = ReadReasoningEffort(config, "Azure:ChatAuxiliaryReasoningEffort"),
            ChatApiVersion = ReadApiVersion(config),
            EmbeddingDeployment = Get("Azure:EmbeddingDeployment"),
            SearchEndpoint = Get("Azure:SearchEndpoint"),
            SearchIndex = Get("Azure:SearchIndex"),
            SearchSemanticConfiguration = config["Azure:SearchSemanticConfiguration"] ?? "meeting-semantic",
            SearchMinimumRerankerScore = ReadMinimumRerankerScore(config),
            SearchMaxEvidence = ReadSearchMaxEvidence(config),
            AllowedOrigins = config.GetSection("Security:AllowedOrigins").Get<string[]>() ?? [],
            EarlyStartAtFinal = ReadSwitch(config, "Session:EarlyStartAtFinal"),
            SpeculativeStart = ReadSwitch(config, "Session:SpeculativeStart"),
            QuietReplyMs = ReadQuietReplyMs(config),
            MaterialPhraseHints = ReadSwitch(config, "Speech:MaterialPhraseHints"),
            CommitmentPolicy = ReadSwitch(config, "Reply:CommitmentPolicy")
        };
        // A speculative reply is adopted as its final's early reply, so it means nothing without early start.
        if (settings.SpeculativeStart && !settings.EarlyStartAtFinal)
            throw new InvalidOperationException("Session:SpeculativeStart requires Session:EarlyStartAtFinal to be true.");
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

    /// <summary>
    /// An optional reasoning effort for chat requests. Unset or empty sends nothing (the deployment default); any value
    /// other than minimal, low or medium prevents startup rather than being ignored.
    /// </summary>
    public static string? ReadReasoningEffort(IConfiguration config, string key)
    {
        var value = config[key];
        if (string.IsNullOrWhiteSpace(value)) return null;
        var effort = value.Trim().ToLowerInvariant();
        return effort is "minimal" or "low" or "medium"
            ? effort
            : throw new InvalidOperationException($"{key} must be minimal, low or medium when set.");
    }

    /// <summary>An optional experiment switch: unset is off; <c>true</c> or <c>false</c> in any case; anything else prevents startup.</summary>
    public static bool ReadSwitch(IConfiguration config, string key)
    {
        var value = config[key];
        if (string.IsNullOrWhiteSpace(value)) return false;
        return bool.TryParse(value.Trim(), out var enabled)
            ? enabled
            : throw new InvalidOperationException($"{key} must be true or false when set.");
    }

    /// <summary>An optional dated Azure OpenAI api-version (yyyy-MM-dd, optionally -preview) for the live chat requests.</summary>
    public static string? ReadApiVersion(IConfiguration config)
    {
        var value = config["Azure:ChatApiVersion"];
        if (string.IsNullOrWhiteSpace(value)) return null;
        var version = value.Trim();
        return System.Text.RegularExpressions.Regex.IsMatch(version, @"\A20\d\d-[01]\d-[0-3]\d(-preview)?\z",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant)
            ? version
            : throw new InvalidOperationException("Azure:ChatApiVersion must be a dated api-version such as 2025-04-01-preview when set.");
    }

    /// <summary>Optional <c>Session:QuietReplyMs</c>: unset or 0 is off; otherwise an integer from 500 to 10000.</summary>
    public static int ReadQuietReplyMs(IConfiguration config)
    {
        var value = config["Session:QuietReplyMs"];
        if (string.IsNullOrWhiteSpace(value)) return 0;
        if (int.TryParse(value.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture,
            out var milliseconds) && (milliseconds == 0 || milliseconds is >= 500 and <= 10000)) return milliseconds;
        throw new InvalidOperationException("Session:QuietReplyMs must be 0 or an integer from 500 to 10000 when set.");
    }

    /// <summary>Optional <c>Azure:SearchMaxEvidence</c>: unset is 5; otherwise an integer from 1 to 10.</summary>
    public static int ReadSearchMaxEvidence(IConfiguration config)
    {
        var value = config["Azure:SearchMaxEvidence"];
        if (string.IsNullOrWhiteSpace(value)) return 5;
        if (int.TryParse(value.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture,
            out var count) && count is >= 1 and <= 10) return count;
        throw new InvalidOperationException("Azure:SearchMaxEvidence must be an integer from 1 to 10 when set.");
    }

    public static int ReadMaxSessionMinutes(IConfiguration config)
    {
        var value = config["Session:MaxMinutes"];
        if (value is null) return 30;
        if (int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture,
            out var minutes) && minutes is >= 5 and <= 180) return minutes;
        throw new InvalidOperationException("Session:MaxMinutes must be an integer from 5 to 180.");
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
