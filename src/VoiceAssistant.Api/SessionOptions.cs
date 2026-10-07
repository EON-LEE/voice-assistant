using System.Text.Json;

namespace VoiceAssistant.Api;

public sealed record ConfirmedProfile(string Name = "", string Role = "", string Project = "")
{
    public bool IsEmpty => Name.Length == 0 && Role.Length == 0 && Project.Length == 0;
}

public sealed record SessionOptions
{
    public string ResponseMode { get; init; } = "grounded";
    public ConfirmedProfile Profile { get; init; } = new();
    public bool ProfileConfirmed { get; init; }
    public string Topic { get; init; } = "";
    public IReadOnlyList<string> Phrases { get; init; } = Array.Empty<string>();
    public int EndSilenceMs { get; init; } = 700;
    public bool TranscribeOnly { get; init; }
    public bool SemanticSegmentation { get; init; }
    public static SessionOptions Legacy { get; } = new();

    internal static SessionOptions Parse(JsonElement value)
    {
        CheckObject(value, "responseMode", "profile", "profileConfirmed", "topic", "phrases", "endSilenceMs", "transcribeOnly", "semanticSegmentation");
        var transcribeOnly = value.TryGetProperty("transcribeOnly", out var only) && only.GetBoolean();
        var mode = Text(value, "responseMode", 20, "grounded");
        if (mode is not ("balanced" or "grounded" or "conversation")) throw Invalid();
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
            SemanticSegmentation = value.TryGetProperty("semanticSegmentation", out var semantic) && semantic.GetBoolean()
        };
    }

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
