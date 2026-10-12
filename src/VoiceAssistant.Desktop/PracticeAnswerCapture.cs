namespace VoiceAssistant.Desktop;

public enum PracticeTranscriptResult { Ignored, Partial, Final, Duplicate, TooLong, Invalid }

public sealed class PracticeAnswerCapture
{
    private readonly HashSet<string> finalTurnIds = [];
    public bool IsActive { get; private set; }
    public bool HasFinal { get; private set; }
    public bool CanSubmit => IsActive && HasFinal && !string.IsNullOrWhiteSpace(Answer);
    public string Answer { get; private set; } = "";
    public string Partial { get; private set; } = "";

    public void Begin()
    {
        Reset();
        IsActive = true;
    }

    public PracticeTranscriptResult Add(string turnId, string text, bool isFinal)
    {
        if (!IsActive) return PracticeTranscriptResult.Ignored;
        if (!isFinal)
        {
            Partial = text.Length <= 800 ? text : text[..800];
            return PracticeTranscriptResult.Partial;
        }
        if (!finalTurnIds.Add(turnId)) return PracticeTranscriptResult.Duplicate;
        string combined = string.Join(' ', new[] { Answer, text }.Where(value => !string.IsNullOrWhiteSpace(value)));
        if (combined.Length > 800) return PracticeTranscriptResult.TooLong;
        if (combined.Any(char.IsControl)) return PracticeTranscriptResult.Invalid;
        Answer = combined;
        Partial = "";
        HasFinal = true;
        return PracticeTranscriptResult.Final;
    }

    public string Finish()
    {
        if (!CanSubmit) throw new InvalidOperationException("A finalized recognized answer is required.");
        IsActive = false;
        Partial = "";
        return Answer;
    }

    public void End() { IsActive = false; Partial = ""; }

    public void Reset()
    {
        IsActive = false;
        HasFinal = false;
        Answer = "";
        Partial = "";
        finalTurnIds.Clear();
    }
}
