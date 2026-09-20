using Microsoft.CognitiveServices.Speech;
using VoiceAssistant.Api;
using Xunit;

namespace VoiceAssistant.Api.Tests;

public sealed class SpeechInputCompletionTests
{
    [Fact]
    public async Task EofClosesExactlyOnceAndWaitsForRecognitionToStop()
    {
        var closes = 0;
        var completion = new SpeechInputCompletion(() => closes++);
        var first = completion.CompleteAsync(CancellationToken.None);
        var second = completion.CompleteAsync(CancellationToken.None);
        Assert.Equal(1, closes);
        Assert.True(completion.InputEnded);
        Assert.False(first.IsCompleted);
        completion.Stopped();
        await Task.WhenAll(first, second);
        completion.CloseInput();
        Assert.Equal(1, closes);
    }

    [Fact]
    public async Task ExpectedEndOfStreamRequiresExplicitEofAndNoError()
    {
        var completion = new SpeechInputCompletion(() => { });
        Assert.False(completion.IsExpectedEnd(CancellationReason.EndOfStream, CancellationErrorCode.NoError));
        var drain = completion.CompleteAsync(CancellationToken.None);
        Assert.True(completion.IsExpectedEnd(CancellationReason.EndOfStream, CancellationErrorCode.NoError));
        Assert.False(completion.IsExpectedEnd(CancellationReason.Error, CancellationErrorCode.NoError));
        Assert.False(completion.IsExpectedEnd(CancellationReason.EndOfStream, CancellationErrorCode.AuthenticationFailure));
        completion.Stopped();
        await drain;
    }

    [Fact]
    public async Task FailureAfterEofDoesNotBecomeSuccessfulCompletion()
    {
        var completion = new SpeechInputCompletion(() => { });
        var drain = completion.CompleteAsync(CancellationToken.None);
        var failure = new ProviderException("speech_unavailable", "Safe error.");
        completion.Stopped(failure);
        Assert.Same(failure, await Assert.ThrowsAsync<ProviderException>(() => drain));
    }

    [Fact]
    public async Task CancelledDrainDoesNotPretendRecognitionStopped()
    {
        using var cancellation = new CancellationTokenSource();
        var completion = new SpeechInputCompletion(() => { });
        var drain = completion.CompleteAsync(cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => drain);
        var next = completion.CompleteAsync(CancellationToken.None);
        Assert.False(next.IsCompleted);
        completion.Stopped();
        await next;
    }

    [Fact]
    public async Task AlreadyCancelledCompletionDoesNotCloseLiveInput()
    {
        var closes = 0;
        var completion = new SpeechInputCompletion(() => closes++);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => completion.CompleteAsync(cancellation.Token));
        Assert.Equal(0, closes);
        Assert.False(completion.InputEnded);
    }

    [Fact]
    public async Task FakeFiniteCompletionIsIdempotentAndRejectsFurtherAudio()
    {
        await using var stream = await new FakeMeetingProvider().StartSpeechAsync(_ => { }, _ => Assert.Fail(), CancellationToken.None);
        await stream.CompleteInputAsync(CancellationToken.None);
        await stream.CompleteInputAsync(CancellationToken.None);
        Assert.Throws<InvalidOperationException>(() => stream.Write([1, 0]));
    }
}
