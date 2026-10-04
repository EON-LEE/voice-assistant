using System.Text.Json;

namespace VoiceAssistant.Api.Practice;

public sealed class PracticeException(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public static PracticeException Invalid() => new(400, "invalid_request", "The practice request is invalid.");
    public static PracticeException TooLarge() => new(413, "too_large", "The request exceeds 16 KiB.");
    public static PracticeException Unavailable() => new(502, "provider_unavailable", "The assistant provider is unavailable. Please retry.");
    public static PracticeException Timeout() => new(504, "provider_timeout", "The assistant provider timed out. Please retry.");
    public static PracticeException Busy() => new(429, "busy", "Too many requests. Please retry shortly.");
}

public sealed record PracticeScenario(string Kind, string Description, int Difficulty);
public sealed record PracticeHistory(string Role, string Text);
public sealed record PracticeTurn(string Question, string Answer, string? CorrectedEnglish);
public sealed record PracticeRequest(string Operation, string Text = "", string Kind = "", string Voice = "coach",
    string Rate = "normal", PracticeScenario? Scenario = null, string Topic = "", bool UseMaterials = false,
    int MaxTurns = 0, IReadOnlyList<PracticeHistory>? History = null, string Question = "", string Answer = "",
    IReadOnlyList<PracticeTurn>? Turns = null);

internal static class PracticeJson
{
    internal static void Object(JsonElement value, string[] required, params string[] optional)
    {
        if (value.ValueKind != JsonValueKind.Object) throw PracticeException.Invalid();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in value.EnumerateObject())
            if (!names.Add(item.Name) || (!required.Contains(item.Name) && !optional.Contains(item.Name)))
                throw PracticeException.Invalid();
        if (required.Any(name => !names.Contains(name))) throw PracticeException.Invalid();
    }

    internal static string Text(JsonElement value, int min, int max, bool newlines = false)
    {
        if (value.ValueKind != JsonValueKind.String) throw PracticeException.Invalid();
        var text = value.GetString()!;
        if (text.Length < min || text.Length > max || (min > 0 && string.IsNullOrWhiteSpace(text)) ||
            text.Any(c => char.IsControl(c) && !(newlines && c == '\n'))) throw PracticeException.Invalid();
        for (var i = 0; i < text.Length; i++)
            if (char.IsSurrogate(text[i]))
            {
                if (!char.IsHighSurrogate(text[i]) || i + 1 >= text.Length || !char.IsLowSurrogate(text[++i]))
                    throw PracticeException.Invalid();
            }
        return text;
    }
    internal static string Choice(JsonElement value, params string[] choices)
    {
        var text = Text(value, 1, 40);
        return choices.Contains(text, StringComparer.Ordinal) ? text : throw PracticeException.Invalid();
    }
    internal static int Integer(JsonElement value, int min, int max) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number >= min && number <= max
            ? number : throw PracticeException.Invalid();
    internal static bool Boolean(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => true, JsonValueKind.False => false, _ => throw PracticeException.Invalid()
    };
    internal static JsonElement[] Array(JsonElement value, int min, int max)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() < min || value.GetArrayLength() > max)
            throw PracticeException.Invalid();
        return value.EnumerateArray().ToArray();
    }
    internal static string Collapse(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}

public static class PracticeRequests
{
    public static PracticeRequest Parse(string operation, JsonElement root)
    {
        if (operation == "enrich")
        {
            PracticeJson.Object(root, ["kind", "text"]);
            var text = PracticeJson.Text(root.GetProperty("text"), 1, 600, true);
            EnglishInput(text);
            return new(operation, text, PracticeJson.Choice(root.GetProperty("kind"), "question", "reply"));
        }
        if (operation == "speak")
        {
            PracticeJson.Object(root, ["text"], "voice", "rate");
            var text = PracticeJson.Text(root.GetProperty("text"), 1, 400, true);
            EnglishInput(text);
            return new(operation, Text: text,
                Voice: root.TryGetProperty("voice", out var voice) ? PracticeJson.Choice(voice, "coach", "partner") : "coach",
                Rate: root.TryGetProperty("rate", out var rate) ? PracticeJson.Choice(rate, "normal", "slow") : "normal");
        }
        string[] required = operation switch
        {
            "turn" => ["scenario", "topic", "useMaterials", "maxTurns", "history"],
            "suggest" => ["scenario", "topic", "useMaterials", "question"],
            "feedback" => ["scenario", "question", "answer"],
            "summary" => ["scenario", "topic", "turns"],
            _ => throw PracticeException.Invalid()
        };
        PracticeJson.Object(root, required);
        var scenario = Scenario(root.GetProperty("scenario"));
        var topic = root.TryGetProperty("topic", out var topicValue) ? PracticeJson.Text(topicValue, 0, 300) : "";
        var materials = root.TryGetProperty("useMaterials", out var use) && PracticeJson.Boolean(use);
        if (operation == "turn")
        {
            var history = PracticeJson.Array(root.GetProperty("history"), 0, 24).Select((entry, index) =>
            {
                PracticeJson.Object(entry, ["role", "text"]);
                var role = PracticeJson.Choice(entry.GetProperty("role"), "partner", "user");
                if (role != (index % 2 == 0 ? "partner" : "user")) throw PracticeException.Invalid();
                return new PracticeHistory(role, PracticeJson.Text(entry.GetProperty("text"), role == "partner" ? 1 : 0, 800, true));
            }).ToArray();
            if (history.Length % 2 != 0) throw PracticeException.Invalid();
            return new(operation, Scenario: scenario, Topic: topic, UseMaterials: materials,
                MaxTurns: PracticeJson.Integer(root.GetProperty("maxTurns"), 1, 12), History: history);
        }
        if (operation == "suggest") return new(operation, Scenario: scenario, Topic: topic, UseMaterials: materials,
            Question: PracticeJson.Text(root.GetProperty("question"), 1, 800));
        if (operation == "feedback") return new(operation, Scenario: scenario,
            Question: PracticeJson.Text(root.GetProperty("question"), 1, 800),
            Answer: PracticeJson.Text(root.GetProperty("answer"), 0, 800));
        var turns = PracticeJson.Array(root.GetProperty("turns"), 1, 12).Select(turn =>
        {
            PracticeJson.Object(turn, ["question", "answer"], "correctedEnglish");
            return new PracticeTurn(PracticeJson.Text(turn.GetProperty("question"), 0, 800),
                PracticeJson.Text(turn.GetProperty("answer"), 0, 800),
                turn.TryGetProperty("correctedEnglish", out var corrected) ? PracticeJson.Text(corrected, 0, 800) : null);
        }).ToArray();
        return new(operation, Scenario: scenario, Topic: topic, Turns: turns);
    }

    private static PracticeScenario Scenario(JsonElement value)
    {
        PracticeJson.Object(value, ["kind", "difficulty"], "description");
        var kind = PracticeJson.Choice(value.GetProperty("kind"), "sales", "interview", "presentation", "custom");
        var description = value.TryGetProperty("description", out var text) ? PracticeJson.Text(text, 0, 400) : "";
        if (kind == "custom" && string.IsNullOrWhiteSpace(description)) throw PracticeException.Invalid();
        return new(kind, description, PracticeJson.Integer(value.GetProperty("difficulty"), 1, 3));
    }

    private static void EnglishInput(string text)
    {
        if (!text.Any(char.IsAsciiLetter) || text.Any(c => char.IsLetter(c) && !char.IsAsciiLetter(c)))
            throw PracticeException.Invalid();
    }
}
