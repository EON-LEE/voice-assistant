using VoiceAssistant.Api;
using VoiceAssistant.LiveProbe;
using Xunit;

namespace VoiceAssistant.LiveProbe.Tests;

public sealed class ProbeSpeechInputTests
{
    [Theory]
    [InlineData("write")]
    [InlineData("complete")]
    [InlineData("dispose")]
    public async Task SynchronousNativeStallDoesNotPreventDeadlineAndDoesNotRaceDisposal(string operation)
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var stream = new BlockingStream(operation, entered, release);
        var input = new ProbeSpeechInput(stream);
        using var cancellation = new CancellationTokenSource();
        Task pending = operation switch
        {
            "write" => input.WriteAsync([1, 0], cancellation.Token),
            "complete" => input.CompleteAsync(cancellation.Token),
            _ => input.DisposeAsync(cancellation.Token)
        };
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            using var cleanupCancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => input.DisposeAsync(cleanupCancellation.Token));
            Assert.Equal(operation == "dispose" ? 1 : 0, stream.DisposeCalls);
        }
        finally { release.Set(); }
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await input.DisposeAsync(cleanup.Token);
        Assert.Equal(1, stream.DisposeCalls);
    }

    private sealed class BlockingStream(string operation, ManualResetEventSlim entered, ManualResetEventSlim release) : ISpeechStream
    {
        private int disposals;
        public int DisposeCalls => Volatile.Read(ref disposals);
        private void Block(string name)
        {
            if (operation != name) return;
            entered.Set();
            release.Wait();
        }
        public void Write(byte[] audio) => Block("write");
        public Task CompleteInputAsync(CancellationToken cancellation)
        {
            Block("complete");
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref disposals);
            Block("dispose");
            return ValueTask.CompletedTask;
        }
    }
}
