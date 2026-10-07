namespace VoiceAssistant.Api;

internal sealed class EarlierTranscriptContext
{
    private readonly List<ConversationTurn> excerpts = [];

    internal void Remember(ConversationTurn recognized)
    {
        excerpts.Add(new(RetrievalQuery.Bound(recognized.Text, 300)));
        // Retain the opening topic plus the three most recently evicted recognition segments.
        if (excerpts.Count > 4) excerpts.RemoveAt(1);
    }

    internal ConversationTurn[] With(IReadOnlyList<ConversationTurn> recent) =>
        excerpts.Concat(recent).ToArray();
}
