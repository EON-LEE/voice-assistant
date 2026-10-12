using System.Text.Json.Nodes;

namespace VoiceAssistant.Api.Practice;

internal static class PronunciationAlignment
{
    internal static string Rebuild(string json, string original)
    {
        var root = JsonNode.Parse(json) as JsonObject ?? throw Invalid("root");
        if (root["pronunciation"] is not JsonArray chunks || chunks.Count is < 1 or > 40) throw Invalid("chunks");
        var originalWords = PracticeJson.Collapse(original).Split(' ');
        var expected = originalWords.Select(Word).ToArray();
        if (expected.Any(word => word.Length == 0)) throw Invalid("input_word");
        var actual = new List<string>();
        var counts = new List<int>();
        foreach (var chunk in chunks)
        {
            if (chunk is not JsonObject entry || entry["en"] is not JsonValue enValue ||
                !enValue.TryGetValue<string>(out var en) || en.Length > 600 ||
                entry["ko"] is not JsonValue koValue || !koValue.TryGetValue<string>(out _))
                throw Invalid("english_chunk:type_or_length");
            var words = PracticeJson.Collapse(en).Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(Word).Where(word => word.Length > 0).ToArray();
            counts.Add(words.Length);
            actual.AddRange(words);
        }
        if (!expected.SequenceEqual(actual, StringComparer.Ordinal)) throw Invalid("rejoin:word_sequence");
        if (counts.Any(count => count is < 1 or > 4))
        {
            var regrouped = originalWords.Chunk(3).ToArray();
            if (regrouped.Length != chunks.Count) throw Invalid("rechunk:ko_count");
            counts = regrouped.Select(words => words.Length).ToList();
        }
        var offset = 0;
        for (var i = 0; i < counts.Count; i++)
        {
            chunks[i]!["en"] = string.Join(' ', originalWords.Skip(offset).Take(counts[i]));
            offset += counts[i];
        }
        return root.ToJsonString();
    }

    // The original words remain authoritative. Only the comparison ignores letter case and punctuation.
    private static string Word(string text) => string.Concat(text.Where(c => !char.IsPunctuation(c))).ToLowerInvariant();
    private static PracticeException Invalid(string rule) => PracticeOutputNormalization.Invalid("schema:pronunciation:" + rule);
}
