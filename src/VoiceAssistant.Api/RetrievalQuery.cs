namespace VoiceAssistant.Api;

internal static class RetrievalQuery
{
    internal const int MaximumLength = 4096;
    private const int CurrentLimit = 1536;
    private const int ContextLimit = 350;

    // History contains recognition segments only, never generated suggestions.
    internal static string Build(string text, IReadOnlyList<ConversationTurn>? history = null)
    {
        var current = Bound(ResponseRouting.Normalize(text), CurrentLimit);
        var turns = history?.ToArray() ?? [];
        var older = turns.Take(Math.Max(0, turns.Length - 4)).ToArray();
        var anchors = older.Length == 0 ? Array.Empty<ConversationTurn>() :
            new[] { 0, older.Length / 2, older.Length - 1 }.Distinct().Select(index => older[index]);
        var context = anchors.Concat(turns.TakeLast(4))
            .Select(turn => Bound(ResponseRouting.Normalize(turn.Text), ContextLimit))
            .Where(turn => turn.Length > 0).ToArray() ?? [];
        return string.Join(' ', context.Append(current)).Trim();
    }

    internal static string Bound(string text, int limit) => text.Length <= limit
        ? text : text[..(limit / 2)] + " … " + text[^(limit - limit / 2 - 3)..];
}
