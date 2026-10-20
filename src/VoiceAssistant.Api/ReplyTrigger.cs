using System.Text.RegularExpressions;

namespace VoiceAssistant.Api;

public enum ReplyDecision { Reply, Wait, Discard }

/// <summary>
/// Decides whether the speech gathered since the last suggestion forms a meaningful unit worth a new reply.
/// Backchannels ("Yeah.", "OK, good.") are discarded; questions and direct requests ("Tell me about yourself.")
/// reply; short statements wait for more context.
/// </summary>
public static partial class ReplyTrigger
{
    public const int SubstantiveWords = 12;

    public static ReplyDecision Evaluate(IReadOnlyList<string> pending)
    {
        var text = string.Join(' ', pending).Trim();
        if (text.Length == 0) return ReplyDecision.Discard;
        var sentences = Split(text);
        if (sentences.Length == 0 || sentences.All(IsBackchannel)) return ReplyDecision.Discard;
        if (IsQuestion(text, sentences) || IsRequest(sentences)) return ReplyDecision.Reply;
        var words = sentences.Where(sentence => !IsBackchannel(sentence))
            .Sum(sentence => sentence.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
        return words >= SubstantiveWords ? ReplyDecision.Reply : ReplyDecision.Wait;
    }

    public static bool ContainsQuestion(IReadOnlyList<string> pending)
    {
        var text = string.Join(' ', pending);
        return IsQuestion(text, Split(text));
    }

    public static bool ContainsRequest(IReadOnlyList<string> pending) =>
        IsRequest(Split(string.Join(' ', pending)));

    /// <summary>True when the speech is addressed to the listener: a question or a direct request.</summary>
    public static bool InvitesReply(IReadOnlyList<string> pending)
    {
        var text = string.Join(' ', pending);
        var sentences = Split(text);
        return IsQuestion(text, sentences) || IsRequest(sentences);
    }

    /// <summary>
    /// True when the latest final ends like a finished sentence. A trailing preposition, auxiliary, article, conjunction
    /// or filler ("Could you walk us through?") means the speaker is still mid-thought, so the caller should keep waiting.
    /// </summary>
    public static bool LooksComplete(IReadOnlyList<string> pending)
    {
        var last = pending.Count == 0 ? "" : pending[^1].Trim();
        if (last.Length == 0 || last.EndsWith("...", StringComparison.Ordinal) || !SentenceEnd().IsMatch(last)) return false;
        var words = (Split(last).LastOrDefault() ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return false;
        var ending = words[^1];
        if (DanglingAlways.Contains(ending)) return false;
        return !DanglingUnlessWh.Contains(ending) || words.Any(WhWords.Contains);
    }

    /// <summary>
    /// A speech partial as if the recognizer had closed the sentence where the speaker paused. Partials carry no punctuation,
    /// so after a pause the dangling-word rules of <see cref="LooksComplete"/> alone decide whether it looks finished.
    /// </summary>
    public static string Closed(string partial)
    {
        var text = partial.TrimEnd();
        return text.Length == 0 || SentenceEnd().IsMatch(text) ? text : text + ".";
    }

    private static string[] Split(string text) =>
        Sentences().Split(text).Select(Normalize).Where(sentence => sentence.Length > 0).ToArray();

    private static bool IsQuestion(string text, string[] sentences) =>
        text.Contains('?') || sentences.Any(sentence => QuestionStart().IsMatch(LeadingFiller().Replace(sentence, "")));

    private static bool IsRequest(string[] sentences) =>
        sentences.Any(sentence => Request().IsMatch(LeadingFiller().Replace(sentence, "")));

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

    private static readonly HashSet<string> DanglingAlways = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "and", "or", "but", "because", "if", "my", "your", "our", "their", "its", "each", "every",
        "another", "whether", "unless", "although", "though", "while", "than", "as",
        "um", "umm", "uh", "uhm", "er", "erm", "hmm", "i", "we", "they", "he", "she"
    };

    private static readonly HashSet<string> DanglingUnlessWh = new(StringComparer.Ordinal)
    {
        "to", "of", "for", "with", "about", "on", "in", "at", "by", "from", "into", "over", "under", "around", "between",
        "within", "without", "across", "after", "before", "during", "since", "until", "via", "per", "through", "like",
        "onto", "upon", "among", "against", "toward", "towards", "along", "behind", "beyond",
        "is", "are", "was", "were", "be", "been", "am", "do", "does", "did", "have", "has", "had", "will", "would",
        "can", "could", "should", "may", "might", "must", "shall"
    };

    private static readonly HashSet<string> WhWords = new(StringComparer.Ordinal)
    {
        "what", "who", "whom", "whose", "which", "where", "how"
    };

    [GeneratedRegex(@"(?<=[.!?])\s+")]
    private static partial Regex Sentences();
    [GeneratedRegex(@"[.!?][""'”’)\]]*$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex SentenceEnd();
    // Direct requests without a question mark. Matched against a normalized sentence after leading fillers are removed.
    [GeneratedRegex(@"
        ^(?:please\s+)?(?:tell|give|show)\s+(?:me|us)\b(?!\s+(?:a\s+)?(?:sec|second|moment|minute|minutes|chance|break|day|few|bit|little)\b)
      | (?<!\b(?:he|she|they|it|who|that|will|would|can|could|should|shall|may|might|must|to|gonna|going)\s)(?<!'ll\s)\b(?:walk|take|run|talk|step|guide)\s+(?:me|us)\s+through\b
      | ^(?:please\s+)?walk\s+through\b
      | ^(?:please\s+)?(?:explain|describe|outline|summari[sz]e|clarify|elaborate)\b
      | \bintroduce\s+yourself\b
      | ^(?:please\s+)?share\s+(?:your\s+(?!(?:screen|slides?|deck|window|presentation|document|file|link|desktop)\b)|with\s+(?:me|us)\b|what\b|how\b|any\b)
      | ^(?:please\s+)?talk\s+(?:to\s+(?:me|us)\s+)?about\b
      | ^(?:please\s+)?help\s+(?:me|us)\s+(?:to\s+)?understand\b
      | ^(?:please\s+)?go\s+(?:ahead|on)\b
      | ^please\s+(?:say|answer|respond|comment|confirm|provide|update|highlight|compare|recap|detail|justify|go\s+over)\b
      | ^let(?:'s|\s+us)\s+(?:talk|discuss|go\s+over|walk|hear|cover|revisit|review|focus|dive|start\s+with)\b
      | (?<!\b(?:get|got|getting|come|coming|circle|circling|revert|reverting|loop|looping|reach|reaching|report|reporting)\s(?:(?:right|straight|just|also|shortly|soon|promptly)\s)?)\b(?:over\s+to\s+you|back\s+to\s+you|your\s+turn|the\s+floor\s+is\s+yours|take\s+it\s+away)\b
      | ^(?:(?:i|we)(?:\s+(?:was|were|am|are)|'m|'re)?\s+)?(?:just\s+)?wonder(?:ing)?\b
      | ^(?:i|we)(?:'d|\s+would)\s+(?:like|love)\s+(?:you\s+to\s+(?!(?:ask|meet|join)\b)|to\s+(?:hear|know|understand|learn|see|get\s+your|find\s+out)\b|(?:your|some)\s+(?:thoughts|input|opinion|take|view|feedback|perspective)\b)
      | ^(?:i|we)(?:'d|\s+would)\s+appreciate\b
      | ^(?:i|we)\s+(?:want|need|expect)\s+you\s+to\b(?!\s+(?:know|understand|be\s+aware|remember)\b)
      | ^(?:i|we)\s+(?:wanted|want)\s+to\s+(?:hear|know|get\s+your|understand|find\s+out|learn)\b
      | ^(?:i|we)(?:'m|'re|\s+am|\s+are)\s+(?:(?:very|really|just)\s+)?(?:curious|(?:interested|keen)\s+in\s+(?:hearing|your|how|what|knowing|understanding))\b
      | ^(?:maybe|perhaps)\s+you\s+(?:could|can|should|would)\b
      | ^if\s+you\s+(?:could|can|would)\s
      | ^(?:thoughts|ideas|opinions|views)\s+(?:on|about)\b
    ", RegexOptions.IgnorePatternWhitespace | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex Request();
    [GeneratedRegex(@"[^a-z0-9' ]+")]
    private static partial Regex NonWord();
    [GeneratedRegex(@"^(?:(?:so|and|but|well|ok|okay|um|umm|uh|right|then)\s+)+")]
    private static partial Regex LeadingFiller();
    [GeneratedRegex(@"^(what|how|why|when|where|who|which|whose|can|could|would|will|do|does|did|is|are|was|were|should|shall|have|has|may|might|any) ")]
    private static partial Regex QuestionStart();
}
