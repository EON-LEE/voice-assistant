using System.Globalization;
using System.Text.Json;

namespace VoiceAssistant.Api.Practice;

public static class PracticeOutputs
{
    public static JsonElement Validate(string json, PracticeRequest request, bool fake = false)
    {
        if (json.Length > 32768) throw PracticeException.Unavailable();
        using var parsed = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
        var value = parsed.RootElement;
        switch (request.Operation)
        {
            case "enrich":
                PracticeJson.Object(value, ["korean", "pronunciation"]);
                if (fake)
                {
                    var korean = PracticeJson.Text(value.GetProperty("korean"), 1, 400, true);
                    if (!korean.StartsWith("[fake-ko] ", StringComparison.Ordinal)) throw PracticeException.Invalid();
                }
                else Korean(value.GetProperty("korean"), 400);
                if (request.Kind == "question")
                {
                    if (value.GetProperty("pronunciation").ValueKind != JsonValueKind.Null) throw PracticeException.Invalid();
                }
                else
                {
                    var chunks = PracticeJson.Array(value.GetProperty("pronunciation"), 1, 40);
                    var english = new List<string>();
                    foreach (var chunk in chunks)
                    {
                        PracticeJson.Object(chunk, ["en", "ko"]);
                        var en = PracticeJson.Text(chunk.GetProperty("en"), 1, 600);
                        if (en != PracticeJson.Collapse(en) || en.Split(' ').Length is < 1 or > 4) throw PracticeException.Invalid();
                        var ko = PracticeJson.Text(chunk.GetProperty("ko"), 1, 80);
                        if (!ko.Any(Hangul) || ko.Any(c => !Hangul(c) && c != ' ' && !",.?!'-".Contains(c) &&
                            !(char.IsAsciiDigit(c) && en.Any(char.IsAsciiDigit)))) throw PracticeException.Invalid();
                        english.Add(en);
                    }
                    if (string.Join(' ', english) != PracticeJson.Collapse(request.Text)) throw PracticeException.Invalid();
                }
                break;
            case "turn":
            case "suggest":
                PracticeJson.Object(value, ["text"]);
                var reply = English(value.GetProperty("text"), request.Operation == "turn" ? 30 : 25, 2);
                if (request.Operation == "turn" && !reply.EndsWith('?')) throw PracticeException.Invalid();
                break;
            case "feedback":
                PracticeJson.Object(value, ["correctedEnglish", "easierEnglish", "feedbackKo", "points", "clarity"]);
                English(value.GetProperty("correctedEnglish"), 40, 2);
                English(value.GetProperty("easierEnglish"), 40, 2);
                var feedback = Korean(value.GetProperty("feedbackKo"), 400);
                if (SentenceCount(feedback) > 2) throw PracticeException.Invalid();
                foreach (var point in PracticeJson.Array(value.GetProperty("points"), 0, 3))
                {
                    PracticeJson.Object(point, ["tag", "ko"]);
                    PracticeJson.Choice(point.GetProperty("tag"), "grammar", "vocabulary", "clarity", "length", "tone");
                    Korean(point.GetProperty("ko"), 120);
                }
                PracticeJson.Integer(value.GetProperty("clarity"), 1, 5);
                break;
            case "summary":
                PracticeJson.Object(value, ["headlineKo", "strengthsKo", "improveKo", "phrases"]);
                Korean(value.GetProperty("headlineKo"), 400);
                foreach (var key in new[] { "strengthsKo", "improveKo" })
                    foreach (var text in PracticeJson.Array(value.GetProperty(key), 0, 3)) Korean(text, 120);
                foreach (var phrase in PracticeJson.Array(value.GetProperty("phrases"), 0, 8))
                {
                    PracticeJson.Object(phrase, ["en", "ko"]);
                    English(phrase.GetProperty("en"), 40, 2);
                    Korean(phrase.GetProperty("ko"), 120);
                }
                break;
            default: throw PracticeException.Invalid();
        }
        return value.Clone();
    }

    internal static string English(JsonElement element, int maxWords, int maxSentences)
    {
        var text = PracticeJson.Text(element, 1, 800);
        if (text != PracticeJson.Collapse(text) || !text.Any(char.IsAsciiLetter) ||
            text.Split(' ').Length > maxWords || SentenceCount(text) > maxSentences ||
            text.Any(c => !(char.IsAsciiLetterOrDigit(c) || " .,?!';:-()".Contains(c))) ||
            text.IndexOf("You could say", StringComparison.OrdinalIgnoreCase) >= 0 || text.Contains(';') ||
            text.StartsWith('-') || text.Contains(" - ", StringComparison.Ordinal) ||
            (char.IsAsciiDigit(text[0]) && text.Contains(". ", StringComparison.Ordinal)) ||
            text.Contains("http", StringComparison.OrdinalIgnoreCase)) throw PracticeException.Invalid();
        return text;
    }

    private static string Korean(JsonElement value, int max)
    {
        var text = PracticeJson.Text(value, 1, max);
        var hangul = text.Count(Hangul);
        if (hangul == 0 || hangul < text.Count(char.IsAsciiLetter) ||
            text.Any(c => CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.Surrogate or UnicodeCategory.OtherSymbol) ||
            text.IndexOfAny(['<', '>', '*', '`', '#', '"']) >= 0) throw PracticeException.Invalid();
        return text;
    }
    private static bool Hangul(char c) => c is >= '\uAC00' and <= '\uD7A3';
    private static int SentenceCount(string text)
    {
        var count = 0;
        var inEnding = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '.' && i > 0 && i + 1 < text.Length && char.IsAsciiDigit(text[i - 1]) && char.IsAsciiDigit(text[i + 1])) continue;
            if (".?!".Contains(c)) { if (!inEnding) count++; inEnding = true; }
            else if (!char.IsWhiteSpace(c)) inEnding = false;
        }
        return count + (inEnding ? 0 : 1);
    }
}
