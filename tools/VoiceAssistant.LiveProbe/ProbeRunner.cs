using VoiceAssistant.Api;

namespace VoiceAssistant.LiveProbe;

internal sealed class ProbeRunner(IMeetingProvider provider, IProbeClock? probeClock = null)
{
    internal async Task<ProbeEvidence> RunAsync(AudioFixture fixture, TimeSpan deadline, CancellationToken cancellation)
    {
        var clock = probeClock ?? new ProbeClock();
        var startedAt = DateTimeOffset.UtcNow;
        var providerName = provider is AzureMeetingProvider ? "Azure" : "TestDouble";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(deadline);
        using var execution = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var gate = new object();
        var partials = 0;
        var finals = 0;
        var deltas = 0;
        var firstFinal = new TaskCompletionSource<Transcript>(TaskCreationOptions.RunContinuationsAsynchronously);
        double? audioStart = null, speechEnd = null, finalStt = null, firstDelta = null, completed = null;
        var lateness = 0d;
        var providerFailed = false;
        var acceptingTranscript = true;
        var status = "FAILED";
        var reason = "provider_error";
        var stage = "speech";
        ServiceFailure? failure = null;
        var quality = new RecognitionQuality(fixture.ReferenceText is not null, true);
        ISpeechStream? stream = null;
        try
        {
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(execution.Token);
            startup.CancelAfter(TimeSpan.FromSeconds(15));
            stream = await provider.StartSpeechAsync(transcript =>
            {
                lock (gate)
                {
                    if (!acceptingTranscript) return;
                    if (!transcript.Final) { partials++; return; }
                    finals++;
                    if (finals == 1) finalStt = clock.ElapsedMs;
                    if (transcript.Text.Length > 8000)
                    {
                        providerFailed = true;
                        execution.Cancel();
                    }
                    else firstFinal.TrySetResult(transcript);
                }
            }, error =>
            {
                lock (gate)
                {
                    if (!acceptingTranscript) return;
                    providerFailed = true;
                    failure ??= ServiceFailure.From(error, "speech");
                    execution.Cancel();
                }
            }, startup.Token).WaitAsync(startup.Token);
            await FramePacer.PlayAsync(fixture, stream.Write, clock,
                start => audioStart = start, end => speechEnd = end,
                late => lateness = Math.Max(lateness, late), execution.Token);
            Transcript recognized;
            try { recognized = await firstFinal.Task.WaitAsync(TimeSpan.FromSeconds(15), execution.Token); }
            catch (TimeoutException) { throw new ProbeFailure("no_final_transcript"); }
            await stream.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), execution.Token);
            stream = null;
            lock (gate)
            {
                acceptingTranscript = false;
                if (finals != 1) throw new ProbeFailure("fixture_not_single_utterance");
            }
            execution.Token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(recognized.Text)) throw new ProbeFailure("empty_transcript");
            quality = WordErrorRate.Measure(fixture.ReferenceText, recognized.Text);
            if (quality.Passed == false) throw new ProbeFailure("recognition_quality_failed");
            // The probe never retrieves corporate documents: only the approved synthetic utterance reaches OpenAI.
            stage = "openai";
            var outputLength = 0;
            await foreach (var delta in provider.AnswerAsync([new(recognized.Text)], new("disabled", []), execution.Token)
                .WithCancellation(execution.Token))
            {
                execution.Token.ThrowIfCancellationRequested();
                if (string.IsNullOrEmpty(delta)) continue;
                firstDelta ??= clock.ElapsedMs;
                deltas++;
                outputLength += delta.Length;
                if (outputLength > 8000) throw new ProbeFailure("response_limit");
            }
            if (deltas == 0) throw new ProbeFailure("empty_response");
            completed = clock.ElapsedMs;
            execution.Token.ThrowIfCancellationRequested();
            status = "SUCCESS";
            reason = "completed";
        }
        catch (OperationCanceledException)
        {
            status = cancellation.IsCancellationRequested ? "CANCELLED" : "FAILED";
            reason = cancellation.IsCancellationRequested ? "cancelled" : providerFailed ? "speech_error" : "deadline_exceeded";
        }
        catch (ProbeFailure exception) { reason = exception.Reason; }
        catch (Exception exception)
        {
            failure = ServiceFailure.From(exception, stage);
            reason = failure.HttpStatus.HasValue ? "service_error" : "provider_error";
        }
        finally
        {
            lock (gate) acceptingTranscript = false;
            execution.Cancel();
            if (stream is not null)
            {
                try { await stream.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (Exception)
                {
                    status = "FAILED";
                    reason = "cleanup_failed";
                }
            }
        }
        lock (gate)
        {
            return new(status, reason, providerName, startedAt, clock.ElapsedMs, partials, finals, deltas,
                new(audioStart, speechEnd, finalStt,
                    speechEnd.HasValue && finalStt.HasValue ? finalStt - speechEnd : null,
                    firstDelta - finalStt, completed - finalStt, lateness,
                    fixture.SpeechEndSample * 1000d / AudioFixture.SampleRate), failure, quality);
        }
    }

    private sealed class ProbeFailure(string reason) : Exception
    {
        public string Reason { get; } = reason;
    }
}
