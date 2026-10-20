using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace VoiceAssistant.Api;

/// <summary>
/// Content-free meeting metrics. Every tag value comes from a fixed vocabulary (route, response mode, grounding status,
/// error code, failure class, reason); none ever carries transcript, answer, query, identity or URL text.
/// </summary>
public static class MeetingMetrics
{
    public const string MeterName = "VoiceAssistant.Api";
    private static readonly Meter Meter = new(MeterName, "1.1.0");
    private static int activeSessions;

    // Per reply, measured from that reply's own final transcript unless stated otherwise.
    private static readonly Histogram<double> FirstDelta = Milliseconds(
        "voiceassistant.stt_final_to_first_delta", "Final STT callback to first emitted response delta; NOT speech-end latency.");
    private static readonly Histogram<double> Completed = Milliseconds(
        "voiceassistant.stt_final_to_completed", "Final STT callback to emitted response completion; NOT speech-end latency.");
    private static readonly Histogram<double> SettleWait = Milliseconds(
        "voiceassistant.reply.settle_wait", "Final transcript to the reply starting: settle window plus any wait behind an active reply.");
    private static readonly Histogram<double> RetrievalWait = Milliseconds(
        "voiceassistant.reply.retrieval_wait", "How long a started reply still waited for its documents.");
    private static readonly Histogram<double> FirstToken = Milliseconds(
        "voiceassistant.reply.first_token", "Answer request sent to the first model text.");
    private static readonly Histogram<double> PrimaryDone = Milliseconds(
        "voiceassistant.reply.final_to_primary_done", "Final transcript to the delta that completes the primary line.");
    private static readonly Histogram<double> ClassifierWait = Milliseconds(
        "voiceassistant.reply.classifier_wait", "How long the completion waited for the turn classifier.");
    private static readonly Histogram<double> KoreanReady = Milliseconds(
        "voiceassistant.reply.korean_ready", "Final transcript to a pushed Korean line.");
    private static readonly Histogram<double> Retrieval = Milliseconds(
        "voiceassistant.retrieval_duration", "Provider retrieval duration, including disabled retrieval.");

    private static readonly Counter<long> Prefetch = Count("voiceassistant.reply.prefetch", "{reply}",
        "Knowledge replies that reused a prefetched retrieval (hit) or started their own (miss).");
    private static readonly Counter<long> Grounding = Count("voiceassistant.reply.grounding", "{reply}", "Completed replies by grounding status.");
    private static readonly Counter<long> RetrievalFailures = Count("voiceassistant.retrieval.failures", "{failure}",
        "Retrievals that failed, by transient or permanent class, failure kind and HTTP status class.");
    private static readonly Counter<long> ChatThrottled = Count("voiceassistant.chat.throttled", "{response}", "HTTP 429 answers to live chat calls.");
    private static readonly Counter<long> ChatRetries = Count("voiceassistant.chat.retries", "{retry}", "Live chat calls that were retried.");
    private static readonly Counter<long> Errors = Count("voiceassistant.session.errors", "{event}", "Error events sent to clients, by code.");
    private static readonly Counter<long> Ended = Count("voiceassistant.session.ended", "{session}", "Ended meeting sessions, by reason.");
    private static readonly Counter<long> SpeechRestarts = Count("voiceassistant.speech.restarts", "{restart}",
        "In-process speech recognizer restarts after a retryable cancellation.");
    private static readonly Counter<long> WarmupFailures = Count("voiceassistant.warmup.failures", "{attempt}",
        "Failed startup warm-up attempts, by stage and whether readiness requires it.");
    private static readonly Counter<long> Speculations = Count("voiceassistant.reply.speculation", "{reply}",
        "Speculative replies started on a paused partial, adopted by their final, or discarded (by reason).");
    private static readonly Histogram<double> SpeculativeHeadStart = Milliseconds(
        "voiceassistant.reply.speculative_head_start", "Adopted speculative replies: final transcript time minus the speculative start.");
    static MeetingMetrics() => Meter.CreateObservableGauge(
        "voiceassistant.sessions.active", () => Volatile.Read(ref activeSessions), "{session}", "Open meeting sessions.");

    private static Histogram<double> Milliseconds(string name, string description) => Meter.CreateHistogram<double>(name, "ms", description);
    private static Counter<long> Count(string name, string unit, string description) => Meter.CreateCounter<long>(name, unit, description);

    public static string ProviderMode(IMeetingProvider provider) => provider switch
    {
        AzureMeetingProvider => "Azure",
        FakeMeetingProvider => "Fake",
        _ => "TestDouble"
    };

    /// <summary>A tag value from a fixed vocabulary; anything that does not look like one is reported as "other".</summary>
    internal static string Bounded(string? value) =>
        value is { Length: > 0 and <= 40 } && value.All(character => character is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_')
            ? value : "other";

    internal static string StatusClass(int? status) => status switch
    {
        >= 500 and < 600 => "5xx",
        >= 400 => "4xx",
        >= 300 => "3xx",
        >= 200 => "2xx",
        _ => "none"
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

    public static void Prefetched(bool hit) => Prefetch.Add(1, new KeyValuePair<string, object?>("result", hit ? "hit" : "miss"));

    public static void RetrievalFailed(RetrievalFailure failure) => RetrievalFailures.Add(1,
        new("class", failure.Transient ? "transient" : "permanent"), new("kind", Bounded(failure.Kind)),
        new("status_class", StatusClass(failure.HttpStatus)), new("retried", failure.Retried ? "true" : "false"));

    public static void ChatThrottledResponse(string operation) => ChatThrottled.Add(1, new KeyValuePair<string, object?>("operation", Bounded(operation)));

    public static void ChatRetried(string operation) => ChatRetries.Add(1, new KeyValuePair<string, object?>("operation", Bounded(operation)));

    public static void ErrorSent(string code) => Errors.Add(1, new KeyValuePair<string, object?>("code", Bounded(code)));

    public static void SpeechRestarted(string code, bool restarted) => SpeechRestarts.Add(1,
        new KeyValuePair<string, object?>("code", Bounded(code)), new KeyValuePair<string, object?>("outcome", restarted ? "restarted" : "failed"));

    public static void WarmupFailed(string stage, bool required) => WarmupFailures.Add(1,
        new KeyValuePair<string, object?>("stage", Bounded(stage)), new KeyValuePair<string, object?>("required", required ? "true" : "false"));

    /// <summary>
    /// One speculative reply event: <c>started</c>, <c>adopted</c> or <c>discarded</c> (with <paramref name="reason"/>
    /// <c>mismatch</c>, <c>target_changed</c> or <c>cancelled</c>). An adopted one also records its head start.
    /// </summary>
    public static void Speculation(string outcome, string provider, string route, string responseMode, string? reason = null,
        TimeSpan? headStart = null)
    {
        var tags = new TagList
        {
            { "provider", provider is "Azure" or "Fake" ? provider : "TestDouble" }, { "route", Bounded(route) },
            { "response_mode", Bounded(responseMode) }
        };
        if (headStart is { } ahead) SpeculativeHeadStart.Record(ahead.TotalMilliseconds, tags);
        tags.Add("outcome", Bounded(outcome));
        if (reason is not null) tags.Add("reason", Bounded(reason));
        Speculations.Add(1, tags);
    }

    public static void SessionStarted() => Interlocked.Increment(ref activeSessions);

    public static void SessionEnded(string reason)
    {
        Interlocked.Decrement(ref activeSessions);
        Ended.Add(1, new KeyValuePair<string, object?>("reason", Bounded(reason)));
    }

    /// <summary>
    /// Timings of one reply, all measured from that reply's own final transcript (or request, for first token). A speculative
    /// reply's measurement holds every record until its final adopts it, and drops them all when it is discarded, so a
    /// discarded speculation never counts as a reply.
    /// </summary>
    public sealed class ResponseMeasurement(long finalSttTimestamp, string provider, bool manual, TimeProvider? timeProvider = null)
    {
        private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
        private readonly string mode = provider is "Azure" or "Fake" ? provider : "TestDouble";
        private readonly object gate = new();
        private readonly bool speculative;
        private long finalTimestamp = finalSttTimestamp;
        private List<Action>? held;
        private bool discarded;
        private int firstRecorded;
        private int completedRecorded;
        private int primaryRecorded;
        private int tokenRecorded;
        private long requestSent;
        private volatile string grounding = "pending";
        public string Route { get; init; } = "knowledge";
        public string ResponseMode { get; init; } = "grounded";

        /// <summary>A speculative reply, started before its final: records wait for <see cref="Adopt"/> or are dropped by <see cref="Discard"/>.</summary>
        public bool Speculative
        {
            get => speculative;
            init
            {
                speculative = value;
                if (value) held = [];
            }
        }

        private TagList Tags(bool withGrounding = true)
        {
            var tags = new TagList
            {
                { "provider", mode }, { "trigger", manual ? "manual" : "automatic" },
                { "route", Bounded(Route) }, { "response_mode", Bounded(ResponseMode) }
            };
            if (withGrounding) tags.Add("grounding", grounding);
            return tags;
        }

        private double SinceFinal() => clock.GetElapsedTime(Volatile.Read(ref finalTimestamp)).TotalMilliseconds;

        private static bool Once(ref int flag) => Interlocked.Exchange(ref flag, 1) == 0;

        private void Record(Histogram<double> histogram, double value, TagList tags) => Record(() => histogram.Record(value, tags));

        private void Record(Action record)
        {
            lock (gate)
            {
                if (discarded) return;
                if (held is not null)
                {
                    held.Add(record);
                    return;
                }
            }
            record();
        }

        /// <summary>The speculative reply became the reply to this final: what it measured so far is recorded now.</summary>
        public void Adopt(long adoptedFinal)
        {
            List<Action> release;
            lock (gate)
            {
                Volatile.Write(ref finalTimestamp, adoptedFinal);
                release = held ?? [];
                held = null;
            }
            foreach (var record in release) record();
        }

        /// <summary>The speculative reply was discarded: nothing it measured is ever recorded.</summary>
        public void Discard()
        {
            lock (gate)
            {
                discarded = true;
                held = null;
            }
        }

        public void Started() => Record(SettleWait, SinceFinal(), Tags(withGrounding: false));

        /// <summary>Documents (or their absence) are known; a knowledge route also reports how long the reply waited for them.</summary>
        public void GroundingReady(string status, TimeSpan? waited = null, bool prefetched = false)
        {
            grounding = Bounded(status);
            if (waited is not { } wait) return;
            var tags = Tags();
            tags.Add("prefetched", prefetched ? "true" : "false");
            Record(RetrievalWait, wait.TotalMilliseconds, tags);
        }

        public void RequestSent() => Volatile.Write(ref requestSent, clock.GetTimestamp());

        public void FirstTokenReceived()
        {
            var sent = Volatile.Read(ref requestSent);
            if (sent == 0 || !Once(ref tokenRecorded)) return;
            Record(FirstToken, clock.GetElapsedTime(sent).TotalMilliseconds, Tags());
        }

        public void FirstDeltaSent()
        {
            if (Once(ref firstRecorded)) Record(FirstDelta, SinceFinal(), Tags());
        }

        public void PrimaryDoneSent()
        {
            if (Once(ref primaryRecorded)) Record(PrimaryDone, SinceFinal(), Tags());
        }

        public void CompletedSent()
        {
            if (Once(ref completedRecorded)) Record(Completed, SinceFinal(), Tags());
        }

        public void ClassifierWaited(TimeSpan waited, string outcome)
        {
            var tags = Tags();
            tags.Add("outcome", Bounded(outcome));
            Record(ClassifierWait, waited.TotalMilliseconds, tags);
        }

        public void KoreanSent(int index, bool translated)
        {
            var tags = Tags();
            tags.Add("line", index == 0 ? "primary" : "alternative");
            tags.Add("outcome", translated ? "translated" : "failed");
            Record(KoreanReady, SinceFinal(), tags);
        }

        /// <summary>Counts every completed reply, deterministic ones included, by its final grounding status.</summary>
        public void ReplyCompleted(string status)
        {
            var tags = Tags(withGrounding: false);
            tags.Add("grounding", Bounded(status));
            Record(() => Grounding.Add(1, tags));
        }
    }
}
