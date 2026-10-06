using VoiceAssistant.Desktop.Protocol;

namespace VoiceAssistant.Desktop;

public sealed record TranscriptTurn(string TurnId, int Revision, string Text, bool IsFinal);
public sealed record ReplySnapshot(string ResponseId, string TurnId, string Text, IReadOnlyList<ReplySource> Sources, bool Complete,
    string? Grounding = null, string? ResponseRoute = null, bool? RetrievalPrefetched = null);

/// <summary>UI-thread-owned bounded state; pinned answers are immutable snapshots.</summary>
public sealed class ReplyState
{
    private const int MaxText = 32768;
    private readonly List<TranscriptTurn> turns = [];
    private readonly HashSet<string> seenResponses = [];
    private readonly Queue<string> responseOrder = [];
    private string? latestTurn;
    private string? suppressedTurn;
    private bool suppressAll;
    public IReadOnlyList<TranscriptTurn> Turns => turns;
    public ReplySnapshot? Current { get; private set; }
    public ReplySnapshot? Pinned { get; private set; }
    public string? Error { get; private set; }

    public void ResetSession()
    {
        turns.Clear();
        seenResponses.Clear();
        responseOrder.Clear();
        Current = null;
        latestTurn = null;
        suppressedTurn = null;
        suppressAll = false;
        Error = null;
    }

    public void Pin() { if (Current is not null) Pinned = Current with { Sources = Current.Sources.ToArray() }; }
    public void Unpin() => Pinned = null;
    public void BeginRequest() { suppressedTurn = null; Error = null; }
    public void Pause(bool paused)
    {
        suppressAll = paused;
        if (paused) CancelCurrent();
        else suppressedTurn = null;
    }
    public void CancelCurrent()
    {
        suppressedTurn = latestTurn;
        Current = null;
    }

    public void Apply(ServerEvent message)
    {
        switch (message.Type)
        {
            case "error":
                Error = $"{message.Code}: {message.Text}" + (message.Retryable ? " (You can retry.)" : "");
                return;
            case "transcript.partial":
            case "transcript.final":
                int index = turns.FindIndex(x => x.TurnId == message.TurnId);
                if (index >= 0 && (message.Revision < turns[index].Revision || turns[index].IsFinal ||
                    message.Revision == turns[index].Revision && message.Type == "transcript.partial")) return;
                var turn = new TranscriptTurn(message.TurnId!, message.Revision, Limit(message.Text!), message.Type == "transcript.final");
                if (index >= 0) turns[index] = turn;
                else
                {
                    turns.Add(turn);
                    latestTurn = turn.TurnId;
                    Current = null;
                    if (turns.Count > 64) turns.RemoveAt(0);
                }
                return;
            case "response.started":
                if (message.TurnId != latestTurn || suppressAll || suppressedTurn == message.TurnId ||
                    !seenResponses.Add(message.ResponseId!)) return;
                responseOrder.Enqueue(message.ResponseId!);
                if (responseOrder.Count > 256) seenResponses.Remove(responseOrder.Dequeue());
                Current = new(message.ResponseId!, message.TurnId!, "", [], false);
                return;
            case "response.delta":
            case "response.completed":
            case "response.cancelled":
                if (Current is null || Current.Complete || suppressAll || suppressedTurn == message.TurnId ||
                    message.ResponseId != Current.ResponseId || message.TurnId != latestTurn) return;
                if (message.Type == "response.cancelled") { Current = null; return; }
                Current = message.Type == "response.delta"
                    ? Current with { Text = Limit(Current.Text + message.Text) }
                    : Current with
                    {
                        Text = Limit(message.Text!),
                        Sources = (message.Sources ?? []).ToArray(),
                        Complete = true,
                        Grounding = message.Grounding,
                        ResponseRoute = message.ResponseRoute,
                        RetrievalPrefetched = message.RetrievalPrefetched
                    };
                return;
        }
    }

    private static string Limit(string text)
    {
        if (text.Length > MaxText) throw new InvalidDataException("Transcript or reply exceeds the 32 KiB text limit.");
        return text;
    }
}
