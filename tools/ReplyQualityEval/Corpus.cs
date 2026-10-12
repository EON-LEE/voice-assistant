using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ReplyQualityEval;

public sealed record Utterance(string Speaker, double Start, double End, string Text)
{
    /// <summary>Time the live recognizer would emit this as a final segment.</summary>
    public double FinalAt => End + 0.7;
    public int Words => Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
}

/// <summary>Reads human word-level transcripts (NXT XML) from the AMI and ICSI corpus annotation archives.</summary>
public static partial class Corpus
{
    public static List<Utterance> Load(string zipPath, string meeting)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var pattern = new Regex($@"(^|/){Regex.Escape(meeting)}\.([A-Z])\.words\.xml$");
        var utterances = new List<Utterance>();
        foreach (var entry in zip.Entries)
        {
            var match = pattern.Match(entry.FullName);
            if (!match.Success) continue;
            using var stream = entry.Open();
            var words = XDocument.Load(stream).Root!.Elements().Where(e => e.Name.LocalName == "w")
                .Select(e => (Start: Time(e, "starttime"), End: Time(e, "endtime"), Text: e.Value.Trim(),
                    Punct: (string?)e.Attribute("punc") == "true" || (string?)e.Attribute("c") is "." or "CM" ||
                           e.Value.Trim() is "." or "," or "?" or "!"))
                .Where(w => w.Text.Length > 0 && !double.IsNaN(w.Start)).OrderBy(w => w.Start).ToList();
            var text = new StringBuilder(); double start = 0, end = 0;
            void Flush()
            {
                var value = Tidy(text.ToString());
                if (value.Length > 0) utterances.Add(new(match.Groups[2].Value, start, end, value));
                text.Clear();
            }
            foreach (var word in words)
            {
                var sentenceEnded = text.Length > 0 && ".?!".Contains(text[^1]);
                if (text.Length > 0 && (word.Start - end > 0.8 || sentenceEnded && word.Start - end > 0.25)) Flush();
                if (text.Length == 0) start = word.Start;
                if (word.Punct) text.Append(word.Text); else text.Append(' ').Append(word.Text);
                end = Math.Max(end, double.IsNaN(word.End) ? word.Start : word.End);
            }
            Flush();
        }
        return utterances.OrderBy(u => u.End).ToList();
    }

    private static double Time(XElement element, string name) =>
        double.TryParse((string?)element.Attribute(name), System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : double.NaN;

    private static string Tidy(string value) => Spaces().Replace(value, " ").Trim();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    private static readonly HashSet<string> Backchannel = new(StringComparer.OrdinalIgnoreCase)
    { "yeah", "yes", "yep", "mm", "mm-hmm", "uh-huh", "hmm", "okay", "ok", "right", "sure", "uh", "um", "mm-hmm", "mhm", "oh", "ah", "so", "and", "well", "good", "great", "cool", "alright", "exactly", "true" };

    public static bool IsBackchannel(string text) =>
        text.ToLowerInvariant().Split([' ', '.', ',', '?', '!'], StringSplitOptions.RemoveEmptyEntries).All(Backchannel.Contains);
}
