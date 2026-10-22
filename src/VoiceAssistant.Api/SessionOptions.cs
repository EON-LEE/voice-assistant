using System.Text.Json;

namespace VoiceAssistant.Api;

public sealed record ConfirmedProfile(string Name = "", string Role = "", string Project = "")
{
    public bool IsEmpty => Name.Length == 0 && Role.Length == 0 && Project.Length == 0;
}

/// <summary>The settings a running session may change; a null member keeps the current value.</summary>
internal sealed record SessionUpdate(string? ResponseMode, bool? ReplyKorean, string? ReplyOn = null);

public sealed record SessionOptions
{
    public string ResponseMode { get; init; } = "grounded";
    public ConfirmedProfile Profile { get; init; } = new();
    public bool ProfileConfirmed { get; init; }
    public string Topic { get; init; } = "";
    public IReadOnlyList<string> Phrases { get; init; } = Array.Empty<string>();
    /// <summary>Speech silence timeout. It is still validated, but it has no effect while Semantic segmentation is active (<see cref="SemanticSegmentation"/> without <see cref="TranscribeOnly"/>): Speech then chooses the boundaries and the server does not send the timeout.</summary>
    public int EndSilenceMs { get; init; } = 700;
    public bool TranscribeOnly { get; init; }
    public bool SemanticSegmentation { get; init; }
    /// <summary>0 = reply to every final utterance (legacy). Otherwise wait for this pause and reply to the gathered meaning.</summary>
    public int ReplySettleMs { get; init; }
    /// <summary>Opt-in: the server also sends the Korean meaning of each reply line as <c>response.korean</c> events.</summary>
    public bool ReplyKorean { get; init; }
    /// <summary>
    /// Which speech starts an automatic reply: <c>all</c> (default) or <c>questions</c>, only speech that invites a reply
    /// (<see cref="ReplyTrigger.InvitesReply"/>: a question or a direct request). A manual <c>response.request</c> answers either way.
    /// </summary>
    public string ReplyOn { get; init; } = "all";
    /// <summary><see cref="ReplyOn"/> is <c>questions</c>: a statement never starts an automatic reply, however long it is.</summary>
    public bool QuestionsOnly => ReplyOn == "questions";
    /// <summary>
    /// Start only: continue the conversation of this user's previous meeting session, which a reconnect or a rollover of the
    /// same live session asks for (see <see cref="ResumableConversations"/>). Without it, a start discards that conversation.
    /// </summary>
    public bool ResumeConversation { get; init; }
    public static SessionOptions Legacy { get; } = new();

    internal static SessionOptions Parse(JsonElement value)
    {
        CheckObject(value, "responseMode", "profile", "profileConfirmed", "topic", "phrases", "endSilenceMs", "transcribeOnly", "semanticSegmentation", "replySettleMs", "replyKorean",
            "resumeConversation", "replyOn");
        var transcribeOnly = value.TryGetProperty("transcribeOnly", out var only) && only.GetBoolean();
        var mode = Text(value, "responseMode", 20, "grounded");
        if (mode is not ("balanced" or "grounded" or "conversation")) throw Invalid();
        var replyOn = Text(value, "replyOn", 20, "all");
        if (replyOn is not ("all" or "questions")) throw Invalid();
        var profile = new ConfirmedProfile();
        if (value.TryGetProperty("profile", out var p))
        {
            CheckObject(p, "name", "role", "project");
            profile = new(Text(p, "name", 100), Text(p, "role", 160), Text(p, "project", 300));
        }
        var confirmed = value.TryGetProperty("profileConfirmed", out var c) && c.GetBoolean();
        if (!profile.IsEmpty && !confirmed) throw Invalid();
        var silence = value.TryGetProperty("endSilenceMs", out var s) ? s.GetInt32() : 700;
        if (silence is < 350 or > 1500) throw Invalid();
        var settle = value.TryGetProperty("replySettleMs", out var r) ? r.GetInt32() : 0;
        if (settle != 0 && settle is < 300 or > 5000) throw Invalid();
        var phrases = new List<string>();
        if (value.TryGetProperty("phrases", out var list))
        {
            if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() > 40) throw Invalid();
            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String) throw Invalid();
                var phrase = item.GetString()!;
                if (phrase.Length is < 1 or > 64 || string.IsNullOrWhiteSpace(phrase) || phrase.Any(char.IsControl)) throw Invalid();
                phrases.Add(phrase.Trim());
            }
            if (phrases.Sum(phrase => phrase.Length) > 2048) throw Invalid();
        }
        return new()
        {
            ResponseMode = mode, Profile = profile, ProfileConfirmed = confirmed,
            Topic = Text(value, "topic", 300), Phrases = phrases.AsReadOnly(), EndSilenceMs = silence,
            TranscribeOnly = transcribeOnly,
            SemanticSegmentation = value.TryGetProperty("semanticSegmentation", out var semantic) && semantic.GetBoolean(),
            ReplySettleMs = settle,
            ReplyKorean = value.TryGetProperty("replyKorean", out var korean) && korean.GetBoolean(),
            ReplyOn = replyOn,
            ResumeConversation = value.TryGetProperty("resumeConversation", out var resume) && resume.GetBoolean()
        };
    }

    /// <summary>
    /// Only the document-search mode, the Korean push and which speech gets automatic replies may change after the handshake;
    /// at least one must be present.
    /// </summary>
    internal static SessionUpdate ParseUpdate(JsonElement value)
    {
        CheckObject(value, "responseMode", "replyKorean", "replyOn");
        var mode = value.TryGetProperty("responseMode", out _) ? Text(value, "responseMode", 20) : null;
        if (mode is not null and not ("balanced" or "grounded" or "conversation")) throw Invalid();
        bool? korean = null;
        if (value.TryGetProperty("replyKorean", out var flag))
        {
            if (flag.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Invalid();
            korean = flag.GetBoolean();
        }
        var replyOn = value.TryGetProperty("replyOn", out _) ? Text(value, "replyOn", 20) : null;
        if (replyOn is not null and not ("all" or "questions")) throw Invalid();
        if (mode is null && korean is null && replyOn is null) throw Invalid();
        return new(mode, korean, replyOn);
    }

    internal SessionOptions Apply(SessionUpdate update) => this with
    {
        ResponseMode = update.ResponseMode ?? ResponseMode,
        ReplyKorean = update.ReplyKorean ?? ReplyKorean,
        ReplyOn = update.ReplyOn ?? ReplyOn
    };

    private static string Text(JsonElement element, string name, int limit, string fallback = "")
    {
        if (!element.TryGetProperty(name, out var value)) return fallback;
        if (value.ValueKind != JsonValueKind.String) throw Invalid();
        var text = value.GetString()!;
        if (text.Length > limit || text.Any(char.IsControl)) throw Invalid();
        return text.Trim();
    }

    private static void CheckObject(JsonElement value, params string[] allowed)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Invalid();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!allowed.Contains(property.Name, StringComparer.Ordinal) || !names.Add(property.Name)) throw Invalid();
    }

    private static JsonException Invalid() => new("Invalid session options.");
}
