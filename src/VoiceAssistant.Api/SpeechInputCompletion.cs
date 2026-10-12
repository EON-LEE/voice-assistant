using Microsoft.CognitiveServices.Speech;

namespace VoiceAssistant.Api;

internal sealed class SpeechInputCompletion(Action closeInput)
{
    private readonly TaskCompletionSource<ProviderException?> stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int inputEnded;

    internal bool InputEnded => Volatile.Read(ref inputEnded) != 0;

    internal void CloseInput()
    {
        if (Interlocked.Exchange(ref inputEnded, 1) == 0) closeInput();
    }

    internal async Task CompleteAsync(CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        CloseInput();
        var failure = await stopped.Task.WaitAsync(cancellation);
        if (failure is not null) throw failure;
    }

    internal bool IsExpectedEnd(CancellationReason reason, CancellationErrorCode code) =>
        InputEnded && reason == CancellationReason.EndOfStream && code == CancellationErrorCode.NoError;

    internal void Stopped(ProviderException? failure = null) => stopped.TrySetResult(failure);
}
