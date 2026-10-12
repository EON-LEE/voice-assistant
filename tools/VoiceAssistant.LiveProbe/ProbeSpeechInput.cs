using VoiceAssistant.Api;

namespace VoiceAssistant.LiveProbe;

internal sealed class ProbeSpeechInput(ISpeechStream stream)
{
    private Task pending = Task.CompletedTask;
    private bool disposalStarted;

    internal Task WriteAsync(byte[] bytes, CancellationToken cancellation) =>
        RunAsync(() => { stream.Write(bytes); return Task.CompletedTask; }, cancellation);

    internal Task CompleteAsync(CancellationToken cancellation) =>
        RunAsync(() => stream.CompleteInputAsync(cancellation), cancellation);

    private Task RunAsync(Func<Task> operation, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!pending.IsCompleted || disposalStarted) throw new InvalidOperationException("Speech operation is still active or input is disposed.");
        // Native SDK calls may block before returning a Task. Keep the probe's deadline observable.
        pending = Task.Run(operation, CancellationToken.None);
        return pending.WaitAsync(cancellation);
    }

    internal async Task DisposeAsync(CancellationToken cancellation)
    {
        try { await pending.WaitAsync(cancellation); }
        catch (Exception) when (pending.IsCompleted)
        {
            // The calling phase already reports operation failures; disposal still owns native resources.
        }
        cancellation.ThrowIfCancellationRequested();
        if (!disposalStarted)
        {
            disposalStarted = true;
            pending = Task.Run(async () => await stream.DisposeAsync(), CancellationToken.None);
        }
        await pending.WaitAsync(cancellation);
    }
}
