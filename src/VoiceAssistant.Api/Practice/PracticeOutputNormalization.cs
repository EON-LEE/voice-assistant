using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VoiceAssistant.Api.Practice;

internal static class PracticeOutputNormalization
{
    internal static string Normalize(string json, string operation, string? kind = null, bool translationOnly = false)
    {
        if (json.Length > 32768) throw Invalid("schema:output_size");
        json = json.Trim().TrimStart('\uFEFF').Replace("\r\n", "\n");
        if (json.Length == 0) throw Invalid("empty");
        if (json.StartsWith("```json\n", StringComparison.OrdinalIgnoreCase) && json.EndsWith("\n```", StringComparison.Ordinal))
            json = json[8..^4];
        else if (json.StartsWith("```\n", StringComparison.Ordinal) && json.EndsWith("\n```", StringComparison.Ordinal))
            json = json[4..^4];
        using var parsed = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
        RejectDuplicates(parsed.RootElement);
        var root = JsonNode.Parse(json) as JsonObject ?? throw Invalid("schema:root");
        if (root.Count == 1 && root.First().Key is "result" or "data" or "output" && root.First().Value is JsonObject wrapper)
            root = (JsonObject)wrapper.DeepClone();
        var fields = operation switch
        {
            "enrich" => new[] { "korean", "pronunciation" },
            "turn" or "suggest" => ["text"],
            "feedback" => ["correctedEnglish", "easierEnglish", "feedbackKo", "points", "clarity"],
            "summary" => ["headlineKo", "strengthsKo", "improveKo", "phrases"],
            _ => throw Invalid("schema:root")
        };
        Project(root, fields);
        if (operation == "enrich" && (kind == "question" || translationOnly))
            root["pronunciation"] = null; // Server-owned when no reading was requested, irrespective of model output.
        foreach (var pair in root.ToArray())
        {
            if (pair.Value is JsonValue value && value.TryGetValue<string>(out var text))
                root[pair.Key] = Text(text, pair.Key is "text" or "correctedEnglish" or "easierEnglish");
            else if (pair.Value is JsonArray array)
            {
                for (var i = 0; i < array.Count; i++)
                {
                    if (array[i] is JsonValue item && item.TryGetValue<string>(out var label))
                        array[i] = Text(label, false);
                    else if (array[i] is JsonObject entry)
                    {
                        Project(entry, pair.Key == "points" ? ["tag", "ko"] : ["en", "ko"]);
                        foreach (var field in entry.ToArray())
                            if (field.Value is JsonValue part && part.TryGetValue<string>(out var partText))
                                entry[field.Key] = field.Key == "tag" ? partText :
                                    Text(partText, pair.Key == "phrases" && field.Key == "en");
                    }
                }
            }
        }
        return root.ToJsonString();
    }

    private static string Text(string value, bool english)
    {
        if (value.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t')))
            throw Invalid("schema:control_character");
        string text;
        try { text = PracticeJson.Collapse(value.Normalize(NormalizationForm.FormC)); }
        catch (ArgumentException) { throw Invalid("schema:unicode"); }
        return english ? text.Replace('\u2019', '\'').Replace('\u2018', '\'') : text;
    }
    private static void Project(JsonObject value, string[] allowed)
    {
        foreach (var key in value.Select(pair => pair.Key).ToArray())
            if (!allowed.Contains(key, StringComparer.Ordinal)) value.Remove(key);
    }
    private static void RejectDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw Invalid("schema:duplicate_property");
                RejectDuplicates(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicates(item);
    }
    internal static PracticeException Invalid(string category) => new(502, "provider_unavailable", "The assistant provider returned an invalid response.")
    { DiagnosticCategory = category };
}
