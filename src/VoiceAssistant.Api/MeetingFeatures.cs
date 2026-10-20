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
}
