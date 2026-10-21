namespace VoiceAssistant.Api;

internal sealed class EarlierTranscriptContext
{
    private const int MaxExcerpts = 4;
    private readonly List<ConversationTurn> excerpts = [];

    internal void Remember(ConversationTurn recognized)
    {
        excerpts.Add(new(RetrievalQuery.Bound(recognized.Text, 300)));
        // Retain the opening topic plus the three most recently evicted recognition segments.
        if (excerpts.Count > MaxExcerpts) excerpts.RemoveAt(1);
    }

    internal ConversationTurn[] With(IReadOnlyList<ConversationTurn> recent) =>
        excerpts.Concat(recent).ToArray();

    /// <summary>A copy of the excerpts, which a resumed session continues from (see <see cref="ResumableConversations"/>).</summary>
    internal ConversationTurn[] Excerpts() => excerpts.ToArray();

    /// <summary>Continues from the excerpts of the previous session of the same conversation.</summary>
    internal void Restore(IReadOnlyList<ConversationTurn> earlier)
    {
        excerpts.Clear();
        excerpts.AddRange(earlier.Take(MaxExcerpts));
    }

    /// <summary>An independent copy, to work out the history a final would produce without changing this one.</summary>
    internal EarlierTranscriptContext Copy()
    {
        var copy = new EarlierTranscriptContext();
        copy.excerpts.AddRange(excerpts);
        return copy;
    }
}
