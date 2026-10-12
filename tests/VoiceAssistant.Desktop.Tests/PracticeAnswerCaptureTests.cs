using VoiceAssistant.Desktop;
using VoiceAssistant.Desktop.Audio;

namespace VoiceAssistant.Desktop.Tests;

public sealed class PracticeAnswerCaptureTests
{
    [Fact]
    public void PartialRecognitionNeverEnablesFinalSubmission()
    {
        var answer = new PracticeAnswerCapture();
        answer.Begin();

        Assert.Equal(PracticeTranscriptResult.Partial, answer.Add("turn-1", "I can help", false));
        Assert.Equal("I can help", answer.Partial);
        Assert.False(answer.CanSubmit);
        Assert.Throws<InvalidOperationException>(() => answer.Finish());
    }

    [Fact]
    public void FinalAnswersAreDeduplicatedAndCombinedUntilExplicitDone()
    {
        var answer = new PracticeAnswerCapture();
        answer.Begin();

        Assert.Equal(PracticeTranscriptResult.Final, answer.Add("turn-1", "I can help.", true));
        Assert.True(answer.CanSubmit);
        Assert.Equal(PracticeTranscriptResult.Duplicate, answer.Add("turn-1", "duplicate", true));
        Assert.Equal(PracticeTranscriptResult.Final, answer.Add("turn-2", "by Friday.", true));
        Assert.Equal("I can help. by Friday.", answer.Finish());
        Assert.False(answer.IsActive);
        Assert.Equal(PracticeTranscriptResult.Ignored, answer.Add("turn-3", "late", true));
    }

    [Fact]
    public void RejectsOverlongOrControlCharacterFinalsAndResetClearsRoundData()
    {
        var answer = new PracticeAnswerCapture();
        answer.Begin();

        Assert.Equal(PracticeTranscriptResult.TooLong, answer.Add("long", new string('a', 801), true));
        Assert.Equal(PracticeTranscriptResult.Invalid, answer.Add("control", "no\n", true));
        Assert.Empty(answer.Answer);
        answer.Add("valid", "Try again.", true);
        answer.Reset();
        Assert.Empty(answer.Answer);
        Assert.Empty(answer.Partial);
        Assert.False(answer.IsActive);
        Assert.False(answer.HasFinal);
    }

    [Fact]
    public void MeetingAndPracticeCannotAcquireMicrophoneAtTheSameTime()
    {
        using var meeting = AudioSessionCoordinator.TryAcquire();
        Assert.NotNull(meeting);
        Assert.Null(AudioSessionCoordinator.TryAcquire());
        meeting.Dispose();
        using var practice = AudioSessionCoordinator.TryAcquire();
        Assert.NotNull(practice);
    }
}
