using NAudio.Wave;

namespace VoiceAssistant.Desktop.Audio;

public sealed class MemoryAudioPlayback : IDisposable
{
    private readonly MemoryStream memory;
    private readonly WaveStream stream;
    private readonly WaveOutEvent output = new();

    public MemoryAudioPlayback(byte[] audio)
    {
        if (audio.Length < 4) throw new InvalidDataException("Speech audio is incomplete.");
        memory = new MemoryStream(audio, writable: false);
        stream = audio.AsSpan(0, 4).SequenceEqual("RIFF"u8)
            ? new WaveFileReader(memory) : new Mp3FileReader(memory);
        output.Init(stream);
    }

    public async Task PlayAsync(CancellationToken cancellationToken)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        output.PlaybackStopped += (_, args) =>
        {
            if (args.Exception is not null) completed.TrySetException(args.Exception);
            else completed.TrySetResult();
        };
        using var registration = cancellationToken.Register(output.Stop);
        output.Play();
        await completed.Task.WaitAsync(cancellationToken);
    }

    public void Dispose()
    {
        output.Dispose();
        stream.Dispose();
        memory.Dispose();
    }
}
