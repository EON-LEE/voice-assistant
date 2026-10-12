using System.Text.RegularExpressions;

namespace VoiceAssistant.Api;

public enum ReplyDecision { Reply, Wait, Discard }

/// <summary>
/// Decides whether the speech gathered since the last suggestion forms a meaningful unit worth a new reply.
/// Backchannels ("Yeah.", "OK, good.") are discarded; questions reply; short statements wait for more context.
/// </summary>
public static partial class ReplyTrigger
{
    public const int SubstantiveWords = 12;

    public static ReplyDecision Evaluate(IReadOnlyList<string> pending)
    {
        var text = string.Join(' ', pending).Trim();
        if (text.Length == 0) return ReplyDecision.Discard;
        var sentences = Sentences().Split(text).Select(Normalize).Where(sentence => sentence.Length > 0).ToArray();
        if (sentences.Length == 0 || sentences.All(IsBackchannel)) return ReplyDecision.Discard;
        if (IsQuestion(text, sentences)) return ReplyDecision.Reply;
        var words = sentences.Where(sentence => !IsBackchannel(sentence))
            .Sum(sentence => sentence.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
        return words >= SubstantiveWords ? ReplyDecision.Reply : ReplyDecision.Wait;
    }

    public static bool ContainsQuestion(IReadOnlyList<string> pending)
    {
        var text = string.Join(' ', pending);
        return IsQuestion(text, Sentences().Split(text).Select(Normalize).Where(sentence => sentence.Length > 0).ToArray());
    }

    private static bool IsQuestion(string text, string[] sentences) =>
        text.Contains('?') || sentences.Any(sentence => QuestionStart().IsMatch(LeadingFiller().Replace(sentence, "")));

    private static bool IsBackchannel(string sentence) =>
        sentence.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(Backchannels.Contains);

    private static string Normalize(string sentence) =>
        NonWord().Replace(sentence.ToLowerInvariant().Replace('’', '\''), " ").Trim();

    private static readonly HashSet<string> Backchannels = new(StringComparer.Ordinal)
    {
        "yeah", "yes", "yep", "ok", "okay", "right", "sure", "exactly", "good", "great", "nice", "cool", "uh", "huh",
        "mm", "hmm", "um", "umm", "uhm", "er", "erm", "mhm", "oh", "ah", "i", "see", "got", "it", "thanks", "thank", "you",
        "so", "and", "well", "alright",
        "true", "totally", "absolutely", "indeed", "perfect", "wow", "really", "agreed", "fine"
    };

    [GeneratedRegex(@"(?<=[.!?])\s+")]
    private static partial Regex Sentences();
    [GeneratedRegex(@"[^a-z0-9' ]+")]
    private static partial Regex NonWord();
    [GeneratedRegex(@"^(?:(?:so|and|but|well|ok|okay|um|umm|uh|right|then)\s+)+")]
    private static partial Regex LeadingFiller();
    [GeneratedRegex(@"^(what|how|why|when|where|who|which|whose|can|could|would|will|do|does|did|is|are|was|were|should|shall|have|has|may|might|any) ")]
    private static partial Regex QuestionStart();
}
