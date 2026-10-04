using System.Globalization;
using System.Text.Json;

namespace VoiceAssistant.Api.Practice;

public static class PracticeOutputs
{
    public static JsonElement Validate(string json, PracticeRequest request, bool fake = false)
    {
        json = PracticeOutputNormalization.Normalize(json, request.Operation);
        if (request.Operation == "enrich" && request.Kind == "reply")
            json = PronunciationAlignment.Rebuild(json, request.Text);
        using var parsed = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
        var value = parsed.RootElement;
        var rule = "root";
        try
        {
        switch (request.Operation)
        {
            case "enrich":
                PracticeJson.Object(value, ["korean", "pronunciation"]);
                rule = "korean";
                if (fake)
                {
                    var korean = PracticeJson.Text(value.GetProperty("korean"), 1, 400, true);
                    if (!korean.StartsWith("[fake-ko] ", StringComparison.Ordinal)) throw PracticeException.Invalid();
                }
                else Korean(value.GetProperty("korean"), 400, hangulFirst: true);
                if (request.Kind == "question")
                {
                    rule = "pronunciation:null";
                    if (value.GetProperty("pronunciation").ValueKind != JsonValueKind.Null) throw PracticeException.Invalid();
                }
                else
                {
                    rule = "pronunciation:chunks";
                    var chunks = PracticeJson.Array(value.GetProperty("pronunciation"), 1, 40);
                    var english = new List<string>();
                    foreach (var chunk in chunks)
                    {
                        PracticeJson.Object(chunk, ["en", "ko"]);
                        rule = "pronunciation:english_chunk";
                        var en = PracticeJson.Text(chunk.GetProperty("en"), 1, 600);
                        if (en != PracticeJson.Collapse(en) || en.Split(' ').Length is < 1 or > 4) throw PracticeException.Invalid();
                        rule = "pronunciation:hangul_chunk";
                        var ko = PracticeJson.Text(chunk.GetProperty("ko"), 1, 80);
                        if (!ko.Any(Hangul) || ko.Any(c => !Hangul(c) && c != ' ' && !",.?!'-".Contains(c) &&
                            !(char.IsAsciiDigit(c) && en.Any(char.IsAsciiDigit)))) throw PracticeException.Invalid();
                        english.Add(en);
                    }
                    rule = "pronunciation:rejoin";
                    if (string.Join(' ', english) != PracticeJson.Collapse(request.Text.Normalize(System.Text.NormalizationForm.FormC))) throw PracticeException.Invalid();
                }
                break;
            case "turn":
            case "suggest":
                PracticeJson.Object(value, ["text"]);
                rule = "text:english_style";
                var reply = English(value.GetProperty("text"), request.Operation == "turn" ? 30 : 25, 2, "text:english_style");
                rule = "text:question";
                if (request.Operation == "turn" && !reply.EndsWith('?')) throw PracticeException.Invalid();
                break;
            case "feedback":
                PracticeJson.Object(value, ["correctedEnglish", "easierEnglish", "feedbackKo", "points", "clarity"]);
                rule = "correctedEnglish:style";
                English(value.GetProperty("correctedEnglish"), 40, 2, "correctedEnglish:style");
                rule = "easierEnglish:style";
                English(value.GetProperty("easierEnglish"), 40, 2, "easierEnglish:style");
                rule = "feedbackKo";
                var feedback = Korean(value.GetProperty("feedbackKo"), 800, rule: "feedbackKo");
                if (SentenceCount(feedback) > 3) throw PracticeOutputNormalization.Invalid("schema:feedbackKo:max_sentences");
                rule = "points";
                foreach (var point in PracticeJson.Array(value.GetProperty("points"), 0, 3))
                {
                    rule = "points";
                    PracticeJson.Object(point, ["tag", "ko"]);
                    PracticeJson.Choice(point.GetProperty("tag"), "grammar", "vocabulary", "clarity", "length", "tone");
                    Korean(point.GetProperty("ko"), 120);
                }
                rule = "clarity";
                PracticeJson.Integer(value.GetProperty("clarity"), 1, 5);
                break;
            case "summary":
                PracticeJson.Object(value, ["headlineKo", "strengthsKo", "improveKo", "phrases"]);
                rule = "headlineKo";
                Korean(value.GetProperty("headlineKo"), 800);
                foreach (var key in new[] { "strengthsKo", "improveKo" })
                {
                    rule = key;
                    foreach (var text in PracticeJson.Array(value.GetProperty(key), 0, 3)) Korean(text, 120);
                }
                rule = "phrases";
                foreach (var phrase in PracticeJson.Array(value.GetProperty("phrases"), 0, 8))
                {
                    PracticeJson.Object(phrase, ["en", "ko"]);
                    English(phrase.GetProperty("en"), 40, 2);
                    Korean(phrase.GetProperty("ko"), 400);
                }
                break;
            default: throw PracticeException.Invalid();
        }
        return value.Clone();
        }
        catch (PracticeException error) when (error.DiagnosticCategory is null)
        { throw PracticeOutputNormalization.Invalid("schema:" + rule); }
    }

    internal static string English(JsonElement element, int maxWords, int maxSentences, string rule = "english_style")
    {
        var text = OutputText(element, 800, rule);
        if (text != PracticeJson.Collapse(text)) throw Invalid("whitespace");
        if (!text.Any(char.IsAsciiLetter)) throw Invalid("no_english");
        if (text.Split(' ').Length > maxWords) throw Invalid("max_words");
        if (SentenceCount(text) > maxSentences) throw Invalid("max_sentences");
        if (text.Any(c => !(char.IsAsciiLetterOrDigit(c) || " .,?!';:-()".Contains(c)))) throw Invalid("characters");
        if (HasMetaCommentary(text)) throw Invalid("meta_commentary");
        if (text.StartsWith('-') || (char.IsAsciiDigit(text[0]) && text.Contains(". ", StringComparison.Ordinal))) throw Invalid("list_prefix");
        if (text.Contains("http://", StringComparison.OrdinalIgnoreCase) || text.Contains("https://", StringComparison.OrdinalIgnoreCase))
            throw Invalid("url");
        return text;
        PracticeException Invalid(string reason) => PracticeOutputNormalization.Invalid("schema:" + rule + ":" + reason);
    }

    private static string Korean(JsonElement value, int max, bool hangulFirst = false, string rule = "korean")
    {
        var text = OutputText(value, max, rule);
        var hangul = text.Count(Hangul);
        if (hangul == 0) throw Invalid("no_hangul");
        if (hangulFirst && !Hangul(text.FirstOrDefault(char.IsLetter))) throw Invalid("hangul_first");
        if (text.Any(c => CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.Surrogate or UnicodeCategory.OtherSymbol))
            throw Invalid("symbols");
        if (text.IndexOfAny(['<', '>', '*', '`', '#']) >= 0) throw Invalid("markup");
        return text;
        PracticeException Invalid(string reason) => PracticeOutputNormalization.Invalid("schema:" + rule + ":" + reason);
    }
    private static string OutputText(JsonElement value, int maximum, string rule)
    {
        if (value.ValueKind != JsonValueKind.String) throw Invalid("type");
        var text = value.GetString()!;
        if (string.IsNullOrWhiteSpace(text)) throw Invalid("empty");
        if (text.Length > maximum) throw Invalid("max_characters");
        if (text.Any(char.IsControl)) throw Invalid("control_character");
        return text;
        PracticeException Invalid(string reason) => PracticeOutputNormalization.Invalid("schema:" + rule + ":" + reason);
    }
    private static bool HasMetaCommentary(string text) => new[]
    {
        "you could say", "a safe answer", "safe response", "provided information", "provided context",
        "suggested answer", "suggested response", "sample answer", "here is an answer", "here's an answer",
        "as an ai", "based on the information", "the user could", "you can say"
    }.Any(phrase => text.Contains(phrase, StringComparison.OrdinalIgnoreCase));
    private static bool Hangul(char c) => c is >= '\uAC00' and <= '\uD7A3';
    private static int SentenceCount(string text)
    {
        var count = 0;
        var inEnding = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '.' && i > 0 && i + 1 < text.Length && char.IsAsciiDigit(text[i - 1]) && char.IsAsciiDigit(text[i + 1])) continue;
            if (".?!。？！".Contains(c)) { if (!inEnding) count++; inEnding = true; }
            else if (!char.IsWhiteSpace(c) && !"\"'”’)]".Contains(c)) inEnding = false;
        }
        return count + (inEnding ? 0 : 1);
    }
}
