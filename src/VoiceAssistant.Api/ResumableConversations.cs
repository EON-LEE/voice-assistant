namespace VoiceAssistant.Api;

/// <summary>
/// The finalized recognition history of an ended meeting session: its recent turns, bounded like every reply history (at most
/// 12 turns and 24,000 characters), and the short excerpts of older ones. Never partial, pending or generated text.
/// </summary>
public sealed record ConversationSnapshot(IReadOnlyList<ConversationTurn> Recent, IReadOnlyList<ConversationTurn> Earlier);

/// <summary>
/// Keeps the conversation of each user's last ended meeting session for a short while, so that the next session of the same
/// user can continue it after a reconnect or a rollover (<see cref="SessionOptions.ResumeConversation"/>). In memory only and
/// keyed by the authenticated object ID, one entry per user: it is never persisted, logged or sent anywhere, and a restart of
/// the process loses it. Every session start takes its user's entry out, so an entry is used at most once and a fresh
/// meeting never inherits one. Like the session slots and tickets, this needs a single replica.
/// </summary>
public sealed class ResumableConversations(TimeProvider clock)
{
    /// <summary>
    /// How long an ended session's conversation can be resumed. The live page rolls over at once and reconnects after a drop
    /// within about a minute (20 attempts, at most 3 s apart); ten minutes also covers a network outage or a sign-in of a few
    /// minutes. A longer gap is more likely a new conversation, and meeting text should not stay in memory any longer.
    /// </summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    /// <summary>At most this many users' conversations are kept, as many as there can be sessions; the oldest gives way first.</summary>
    public const int Capacity = 100;

    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);

    /// <summary>Keeps this user's conversation, replacing an earlier one. An empty conversation only removes it.</summary>
    public void Save(string objectId, ConversationSnapshot conversation)
    {
        lock (entries)
        {
            var now = clock.GetTimestamp();
            Prune(now);
            entries.Remove(objectId);
            if (conversation.Recent.Count == 0) return;
            if (entries.Count >= Capacity) entries.Remove(entries.MinBy(entry => entry.Value.SavedAt).Key);
            entries[objectId] = new(conversation, now);
        }
    }

    /// <summary>
    /// Takes this user's conversation out of the store. It is returned only while it is younger than <see cref="Lifetime"/>;
    /// <paramref name="expired"/> tells that one was found too late.
    /// </summary>
    public ConversationSnapshot? Take(string objectId, out bool expired)
    {
        lock (entries)
        {
            var now = clock.GetTimestamp();
            var found = entries.Remove(objectId, out var entry);
            Prune(now);
            expired = entry is not null && Expired(entry, now);
            return found && !expired ? entry!.Conversation : null;
        }
    }

    /// <summary>How many users' conversations are kept now, expired ones included until the next save or take.</summary>
    public int Count
    {
        get { lock (entries) return entries.Count; }
    }

    private bool Expired(Entry entry, long now) => clock.GetElapsedTime(entry.SavedAt, now) >= Lifetime;

    private void Prune(long now)
    {
        foreach (var key in entries.Where(pair => Expired(pair.Value, now)).Select(pair => pair.Key).ToArray()) entries.Remove(key);
    }

    private sealed record Entry(ConversationSnapshot Conversation, long SavedAt);
}
