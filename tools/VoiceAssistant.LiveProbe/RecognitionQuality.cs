using System.Text;

namespace VoiceAssistant.LiveProbe;

public sealed record RecognitionQuality(bool ReferencePresent, bool QualityNotMeasured,
    int? ReferenceWordCount = null, int? RecognizedWordCount = null, int? WordEditCount = null,
    double? WordErrorRate = null, bool? Passed = null)
{
    public double MaximumWordErrorRate => 0.25;
}

internal static class WordErrorRate
{
    internal const int MaximumReferenceCharacters = 2048;
    internal const int MaximumRecognizedCharacters = 8000;

    internal static void ValidateReference(string? reference)
    {
        if (reference is null) return;
        if (reference.Length > MaximumReferenceCharacters || Words(reference).Length == 0)
            throw new InvalidDataException("invalid_reference_text");
    }

    internal static RecognitionQuality Measure(string? reference, string recognized)
    {
        ValidateReference(reference);
        if (recognized.Length > MaximumRecognizedCharacters)
            throw new InvalidDataException("recognized_text_limit");
        if (reference is null) return new(false, true);
        var expected = Words(reference);
        var actual = Words(recognized);
        var previous = Enumerable.Range(0, actual.Length + 1).ToArray();
        var current = new int[previous.Length];
        for (var i = 1; i <= expected.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= actual.Length; j++)
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + (expected[i - 1] == actual[j - 1] ? 0 : 1));
            (previous, current) = (current, previous);
        }
        var edits = previous[actual.Length];
        var rate = (double)edits / expected.Length;
        return new(true, false, expected.Length, actual.Length, edits, rate, rate <= 0.25);
    }

    private static string[] Words(string text)
    {
        var normalized = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            if (char.IsLetterOrDigit(character)) normalized.Append(char.ToLowerInvariant(character));
            else if (character is not ('\'' or '\u2019')) normalized.Append(' ');
        }
        return normalized.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }
}
