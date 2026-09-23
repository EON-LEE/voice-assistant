using System.Text.RegularExpressions;

namespace VoiceAssistant.Api;

public static class ResponseRouting
{
    // These patterns are intentionally a small anchored allowlist, not a keyword-based relevance classifier.
    private static readonly Regex PrivateOrSpecific = new(
        @"\b(company|customer|client|contract|deadline|date|today|tomorrow|yesterday|monday|tuesday|wednesday|thursday|friday|saturday|sunday|january|february|march|april|may|june|july|august|september|october|november|december|schedule|commit|commitment|promise|revenue|budget|price|pricing|cost|release|launch|deliver|delivery|policy|roadmap|lighthouse|our|we|my|mine|your|you|their|his|her|not|never|no|isn't|isn’t|don't|don’t|didn't|didn’t|won't|won’t|can't|can’t|cannot)\b|[\d]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex Introduction = new(
        @"^(?:(?:please|could you|can you|would you) )?(?:introduce yourself(?: and your (?:role|project))?|give a brief introduction|tell us (?:your name|about yourself)|what is your name|what is your role|what project do you work on)[?.!]*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex Conversational = new(
        @"^(?:hello|hi|good morning|good afternoon|thanks|thank you|thank you for the explanation|could you repeat that|could you say that again|please repeat that|please speak more slowly|could you speak more slowly|can you hear me|let's begin|let us begin|that makes sense|i agree|i see|sounds good)[?.!]*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex GeneralTechnical = new(
        @"^(?:(?:please )?(?:explain|define)|what is|what are|how does|how do) (?:a |an |the )?(?:api|apis|rest|rest api|jmap|json|http|https|websocket|websockets|oauth|oauth authentication|pkce|unit testing|dependency injection|caching|a cache|vector search|semantic search|encryption|asymmetric encryption|symmetric encryption|a database index|database indexing|a hash table|hash tables|binary search|recursion|streaming|speech recognition|rate limiting)(?: work| works| mean| in general| briefly| at a high level)?[?.!]*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public static string Select(string text, SessionOptions options, IReadOnlyList<ConversationTurn>? history = null)
    {
        if (options.ResponseMode == "grounded") return "knowledge";
        var normalized = Normalize(text);
        if (options.ProfileConfirmed && !options.Profile.IsEmpty && Introduction.IsMatch(normalized))
        {
            var covered = normalized.StartsWith("what is your name", StringComparison.Ordinal) || normalized.StartsWith("tell us your name", StringComparison.Ordinal) ? options.Profile.Name.Length > 0 :
                normalized.StartsWith("what is your role", StringComparison.Ordinal) ? options.Profile.Role.Length > 0 :
                normalized.StartsWith("what project", StringComparison.Ordinal) ? options.Profile.Project.Length > 0 :
                options.Profile.Name.Length > 0 && options.Profile.Role.Length > 0 &&
                (!normalized.Contains("project", StringComparison.Ordinal) || options.Profile.Project.Length > 0);
            if (covered) return "profile";
        }
        if (options.ResponseMode == "conversation") return "transcript";
        // History can make an otherwise generic question private; it never turns an ambiguous fragment into a cache hit.
        if (history is not null && history.TakeLast(12).Any(turn => PrivateOrSpecific.IsMatch(turn.Text)))
            return "knowledge";
        if (Conversational.IsMatch(normalized)) return "transcript";
        if (!PrivateOrSpecific.IsMatch(normalized) && GeneralTechnical.IsMatch(normalized)) return "transcript";
        return "knowledge";
    }

    // Preserve punctuation, apostrophes, digits and negations for exact prefetch identity.
    public static string Normalize(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    internal static bool Substantive(string text) =>
        text.Length >= 20 && text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 4;
}
