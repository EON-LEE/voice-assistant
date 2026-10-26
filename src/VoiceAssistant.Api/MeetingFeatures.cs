namespace VoiceAssistant.Api;

/// <summary>The experiment switches a meeting session runs with. All are off by default, which keeps the documented behavior.</summary>
public sealed record MeetingFeatures
{
    public static MeetingFeatures Off { get; } = new();

    /// <summary>
    /// Start the reply at a final that is a complete question or request (the rule that picks the short settle), instead of
    /// after the settle window. The settle still runs and replaces that reply only when later speech changed its target.
    /// </summary>
    public bool EarlyStartAtFinal { get; init; }

    /// <summary>
    /// On top of <see cref="EarlyStartAtFinal"/>: when a partial pauses on a complete question or request, generate its reply
    /// already, held back server-side. The final adopts it when it is exactly the reply early start would begin then; otherwise
    /// it is discarded unseen.
    /// </summary>
    public bool SpeculativeStart { get; init; }

    /// <summary>
    /// With a settle window: gathered speech that waits for more context (a short statement, not a question or request) is
    /// answered once the room stays quiet this many milliseconds after the settle. 0 (off) keeps waiting for more speech, so a
    /// short answer to the user's own question ("Sure. The pricing first, then the timeline.") never got a reply.
    /// </summary>
    public int QuietReplyMs { get; init; }

    /// <summary>
    /// Speech phrase hints from the caller's own materials, merged after the client's phrases into the recognizer's phrase list
    /// (not into prompts or translation). Null when switched off.
    /// </summary>
    public Knowledge.MaterialPhraseHints? PhraseHints { get; init; }
}
