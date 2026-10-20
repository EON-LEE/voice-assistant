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

    /// <summary>An independent copy, to work out the history a final would produce without changing this one.</summary>
    internal EarlierTranscriptContext Copy()
    {
        var copy = new EarlierTranscriptContext();
        copy.excerpts.AddRange(excerpts);
        return copy;
    }
}
