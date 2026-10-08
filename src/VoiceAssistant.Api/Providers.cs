namespace VoiceAssistant.Api;

public sealed record Transcript(string TurnId, int Revision, string Text, bool Final);
public sealed record Source(string Title, string Url, DateTimeOffset? UpdatedAt);
public sealed record Evidence(string Content, Source Source);
public sealed record Grounding(string Status, IReadOnlyList<Evidence> Documents);
public sealed record ConversationTurn(string Text);
public sealed record SpeechCancellation(
    Microsoft.CognitiveServices.Speech.CancellationReason Reason,
    Microsoft.CognitiveServices.Speech.CancellationErrorCode ErrorCode);
public sealed class ProviderException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
    public SpeechCancellation? SpeechCancellation { get; init; }
}

public interface ISpeechStream : IAsyncDisposable
{
    void Write(byte[] audio);
    // Finite input only: signal EOF and drain recognition callbacks. Live capture does not call this.
    Task CompleteInputAsync(CancellationToken cancellation);
}

public interface IMeetingProvider
{
    Task<ISpeechStream> StartSpeechAsync(Action<Transcript> transcript, Action<ProviderException> error, CancellationToken cancellation);
    Task<Grounding> RetrieveAsync(string query, string objectId, CancellationToken cancellation);
    IAsyncEnumerable<string> AnswerAsync(IReadOnlyList<ConversationTurn> conversation, Grounding grounding, CancellationToken cancellation);
    Task<ISpeechStream> StartSpeechAsync(SessionOptions options, Action<Transcript> transcript,
        Action<ProviderException> error, CancellationToken cancellation) => StartSpeechAsync(transcript, error, cancellation);
    IAsyncEnumerable<string> AnswerAsync(IReadOnlyList<ConversationTurn> conversation, Grounding grounding,
        SessionOptions options, string responseRoute, CancellationToken cancellation) => AnswerAsync(conversation, grounding, cancellation);
    async IAsyncEnumerable<ReplyUpdate> AnswerWithSuggestionsAsync(IReadOnlyList<ConversationTurn> conversation, Grounding grounding,
        SessionOptions options, string responseRoute,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellation)
    {
        await foreach (var text in AnswerAsync(conversation, grounding, options, responseRoute, cancellation).WithCancellation(cancellation))
            yield return new(text);
    }
    /// <summary>Does the latest speech invite the listening participant to respond now? Used for emphasis only.</summary>
    Task<bool> ShouldRespondAsync(IReadOnlyList<ConversationTurn> conversation, CancellationToken cancellation) =>
        Task.FromResult(conversation.Count > 0 && ReplyTrigger.ContainsQuestion([conversation[^1].Text]));
}

public sealed class FakeMeetingProvider : IMeetingProvider
{
    public const string TranscriptText = "Could you briefly explain the next steps?";
    public const string AnswerText = "Let's confirm the goal, agree on the next action, and assign an owner.";
    public Task<ISpeechStream> StartSpeechAsync(Action<Transcript> transcript, Action<ProviderException> error, CancellationToken cancellation) =>
        Task.FromResult<ISpeechStream>(new FakeSpeechStream(transcript));
    public Task<Grounding> RetrieveAsync(string query, string objectId, CancellationToken cancellation) =>
        Task.FromResult(new Grounding("disabled", []));

    public async IAsyncEnumerable<string> AnswerAsync(IReadOnlyList<ConversationTurn> conversation, Grounding grounding,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellation)
    {
        foreach (var text in new[] { "Let's confirm the goal, ", "agree on the next action, ", "and assign an owner." })
        {
            await Task.Delay(50, cancellation);
            yield return text;
        }
    }

    public async IAsyncEnumerable<ReplyUpdate> AnswerWithSuggestionsAsync(IReadOnlyList<ConversationTurn> conversation, Grounding grounding,
        SessionOptions options, string responseRoute,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellation)
    {
        await foreach (var text in AnswerAsync(conversation, grounding, cancellation))
            yield return new(text);
        yield return new("", "Let's agree on the goal and next step, then choose an owner.");
    }

    private sealed class FakeSpeechStream(Action<Transcript> transcript) : ISpeechStream
    {
        private int bytes;
        private int silence;
        private bool emitted;
        private bool inputCompleted;
        public void Write(byte[] audio)
        {
            if (inputCompleted) throw new InvalidOperationException("Speech input is complete.");
            var nonzero = audio.Any(value => value != 0);
            if (!nonzero)
            {
                silence += audio.Length;
                if (silence >= 16000) { emitted = false; bytes = 0; }
                return;
            }
            silence = 0;
            if (emitted) return;
            bytes += audio.Length;
            if (bytes < 640) return;
            emitted = true;
            var turnId = Guid.NewGuid().ToString("N");
            transcript(new(turnId, 1, "Could you briefly explain", false));
            transcript(new(turnId, 2, TranscriptText, true));
        }
        public Task CompleteInputAsync(CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            inputCompleted = true;
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
