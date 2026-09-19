using System.Diagnostics.Metrics;

namespace VoiceAssistant.Api;

public static class MeetingMetrics
{
    public const string MeterName = "VoiceAssistant.Api";
    private static readonly Meter Meter = new(MeterName, "1.0.0");
    private static readonly Histogram<double> FirstDelta = Meter.CreateHistogram<double>(
        "voiceassistant.stt_final_to_first_delta", "ms", "Final STT callback to first emitted response delta; NOT speech-end latency.");
    private static readonly Histogram<double> Completed = Meter.CreateHistogram<double>(
        "voiceassistant.stt_final_to_completed", "ms", "Final STT callback to emitted response completion; NOT speech-end latency.");
    private static readonly Histogram<double> Retrieval = Meter.CreateHistogram<double>(
        "voiceassistant.retrieval_duration", "ms", "Provider retrieval duration, including disabled retrieval.");

    public static string ProviderMode(IMeetingProvider provider) => provider switch
    {
        AzureMeetingProvider => "Azure",
        FakeMeetingProvider => "Fake",
        _ => "TestDouble"
    };

    public static async Task<Grounding> MeasureRetrievalAsync(IMeetingProvider provider, string query,
        string objectId, CancellationToken cancellation)
    {
        var started = TimeProvider.System.GetTimestamp();
        var outcome = "failed";
        try
        {
            var result = await provider.RetrieveAsync(query, objectId, cancellation);
            outcome = result.Status is "grounded" or "disabled" or "no_matches" or "unavailable" ? result.Status : "unknown";
            return result;
        }
        catch (OperationCanceledException) { outcome = "cancelled"; throw; }
        finally
        {
            Retrieval.Record(TimeProvider.System.GetElapsedTime(started).TotalMilliseconds,
                new("provider", ProviderMode(provider)), new("outcome", outcome));
        }
    }

    public sealed class ResponseMeasurement(long finalSttTimestamp, string provider, bool manual, TimeProvider? timeProvider = null)
    {
        private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
        private readonly string mode = provider is "Azure" or "Fake" ? provider : "TestDouble";
        private bool firstRecorded;
        private bool completedRecorded;

        public void FirstDeltaSent()
        {
            if (firstRecorded) return;
            firstRecorded = true;
            FirstDelta.Record(clock.GetElapsedTime(finalSttTimestamp).TotalMilliseconds,
                new("provider", mode), new("trigger", manual ? "manual" : "automatic"));
        }

        public void CompletedSent()
        {
            if (completedRecorded) return;
            completedRecorded = true;
            Completed.Record(clock.GetElapsedTime(finalSttTimestamp).TotalMilliseconds,
                new("provider", mode), new("trigger", manual ? "manual" : "automatic"));
        }
    }
}
