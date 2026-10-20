using System.ClientModel.Primitives;
using System.Globalization;

namespace VoiceAssistant.Api;

/// <summary>
/// Sends a configured api-version on the live chat requests instead of the pinned SDK's default, for example when a
/// deployment needs a newer version to accept <c>reasoning_effort</c>. A per-call policy, so the single retry reuses it.
/// </summary>
internal sealed class ApiVersionPolicy(string version) : PipelinePolicy
{
    public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        Apply(message);
        ProcessNext(message, pipeline, currentIndex);
    }

    public override async ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        Apply(message);
        await ProcessNextAsync(message, pipeline, currentIndex).ConfigureAwait(false);
    }

    private void Apply(PipelineMessage message)
    {
        if (message.Request.Uri is not { } uri) return;
        var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(part => !part.StartsWith("api-version=", StringComparison.OrdinalIgnoreCase))
            .Append("api-version=" + Uri.EscapeDataString(version));
        message.Request.Uri = new UriBuilder(uri) { Query = string.Join('&', query) }.Uri;
    }
}

/// <summary>
/// The live reply chat call running on this async flow (answer, classifier or translation) and its deadline, read by
/// <see cref="LiveChatRetryPolicy"/>. The provider opens one around each live call; disposing it restores the previous one.
/// A call that serves the reply being generated (answer, classifier) is also bounded by that reply's absolute deadline,
/// which the session sets with <see cref="WithinReplyDeadline"/> where it creates the reply's cancellation.
/// </summary>
internal sealed class LiveChatCall : IDisposable
{
    private static readonly AsyncLocal<LiveChatCall?> Active = new();
    private static readonly AsyncLocal<ReplyDeadline?> Reply = new();
    private readonly LiveChatCall? previous;
    private readonly TimeProvider clock;
    private readonly long started;
    private readonly TimeSpan budget;
    private readonly ReplyDeadline? reply;

    private LiveChatCall(string operation, TimeSpan budget, TimeProvider clock, bool withinReply)
    {
        Operation = operation;
        this.budget = budget;
        this.clock = clock;
        started = clock.GetTimestamp();
        reply = withinReply ? Reply.Value : null;
        previous = Active.Value;
        Active.Value = this;
    }

    public static LiveChatCall? Current => Active.Value;
    public string Operation { get; }

    /// <summary>The call's own budget left, or the time until the reply's deadline when that comes first.</summary>
    public TimeSpan Remaining
    {
        get
        {
            var own = budget - clock.GetElapsedTime(started);
            if (reply is null) return own;
            var untilDeadline = reply.Clock.GetElapsedTime(reply.Clock.GetTimestamp(), reply.Timestamp);
            return untilDeadline < own ? untilDeadline : own;
        }
    }

    /// <param name="withinReply">True when the call is part of the reply being generated, so the reply's deadline bounds it too.</param>
    public static LiveChatCall Begin(string operation, TimeSpan budget, TimeProvider? clock = null, bool withinReply = false) =>
        new(operation, budget, clock ?? TimeProvider.System, withinReply);

    /// <summary>Sets, for this async flow, the absolute deadline of the reply being generated as a timestamp of <paramref name="clock"/>.</summary>
    public static IDisposable WithinReplyDeadline(long deadlineTimestamp, TimeProvider clock)
    {
        var outer = Reply.Value;
        Reply.Value = new(deadlineTimestamp, clock);
        return new RestoreReply(outer);
    }

    public void Dispose() => Active.Value = previous;

    private sealed record ReplyDeadline(long Timestamp, TimeProvider Clock);

    private sealed class RestoreReply(ReplyDeadline? outer) : IDisposable
    {
        public void Dispose() => Reply.Value = outer;
    }
}

/// <summary>
/// Retry policy of the live reply chat calls (answer, classifier, Korean translation). The SDK default waits out
/// Retry-After intervals of many seconds up to three times while the overlay stays silent. Here a call is retried at most
/// once, and only when the service asks for a short wait (<see cref="MaxRetryDelay"/> or less) and the call's deadline still
/// leaves <see cref="MinRemaining"/> after that wait. Otherwise the failure surfaces at once through the existing error
/// path (<c>model_busy</c> for HTTP 429, <c>response_unavailable</c> otherwise). Practice and materials keep the SDK default.
/// </summary>
internal sealed class LiveChatRetryPolicy(TimeProvider? clock = null) : ClientRetryPolicy(maxRetries: 1)
{
    internal static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan MinRemaining = TimeSpan.FromSeconds(2);
    /// <summary>The pause before the retry when the service gives no Retry-After hint (a server error or a network failure).</summary>
    internal static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromMilliseconds(250);
    private readonly TimeProvider clock = clock ?? TimeProvider.System;

    /// <summary>
    /// The decision taken for one failed try, cached on the message: the SDK's async check may also call the sync one, so
    /// the same failure must not be decided (and counted) twice.
    /// </summary>
    private sealed class Decision(object cause, bool retry, TimeSpan delay)
    {
        public object Cause { get; } = cause;
        public bool Retry { get; } = retry;
        public TimeSpan Delay { get; } = delay;
    }

    protected override void OnRequestSent(PipelineMessage message)
    {
        CountThrottling(message);
        base.OnRequestSent(message);
    }

    protected override ValueTask OnRequestSentAsync(PipelineMessage message)
    {
        CountThrottling(message);
        return base.OnRequestSentAsync(message);
    }

    protected override bool ShouldRetry(PipelineMessage message, Exception? exception) =>
        base.ShouldRetry(message, exception) && Allow(message, exception);

    protected override async ValueTask<bool> ShouldRetryAsync(PipelineMessage message, Exception? exception) =>
        await base.ShouldRetryAsync(message, exception) && Allow(message, exception);

    protected override TimeSpan GetNextDelay(PipelineMessage message, int tryCount) =>
        message.TryGetProperty(typeof(Decision), out var decided) && decided is Decision { Retry: true } decision
            ? decision.Delay : DefaultRetryDelay;

    protected override Task WaitAsync(TimeSpan time, CancellationToken cancellationToken) => Task.Delay(time, clock, cancellationToken);

    protected override void Wait(TimeSpan time, CancellationToken cancellationToken) =>
        WaitAsync(time, cancellationToken).GetAwaiter().GetResult();

    private static void CountThrottling(PipelineMessage message)
    {
        if (message.Response?.Status == 429) MeetingMetrics.ChatThrottledResponse(LiveChatCall.Current?.Operation ?? "other");
    }

    /// <summary>Called only for a retriable failure while the single retry is still available.</summary>
    private bool Allow(PipelineMessage message, Exception? exception)
    {
        object cause = (object?)message.Response ?? (object?)exception ?? message;
        if (message.TryGetProperty(typeof(Decision), out var decided) && decided is Decision previous && ReferenceEquals(previous.Cause, cause))
            return previous.Retry;
        var call = LiveChatCall.Current;
        var delay = ServerDelay(message.Response) ?? DefaultRetryDelay;
        var retry = delay <= MaxRetryDelay && (call is null || call.Remaining - delay >= MinRemaining);
        message.SetProperty(typeof(Decision), new Decision(cause, retry, delay));
        if (retry) MeetingMetrics.ChatRetried(call?.Operation ?? "other");
        return retry;
    }

    /// <summary>The wait the service asked for: <c>retry-after-ms</c>, <c>x-ms-retry-after-ms</c> or <c>Retry-After</c> (seconds or date).</summary>
    internal TimeSpan? ServerDelay(PipelineResponse? response)
    {
        if (response is null) return null;
        foreach (var name in new[] { "retry-after-ms", "x-ms-retry-after-ms" })
            if (response.Headers.TryGetValue(name, out var milliseconds) &&
                double.TryParse(milliseconds, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value >= 0)
                return TimeSpan.FromMilliseconds(value);
        if (!response.Headers.TryGetValue("Retry-After", out var retryAfter) || string.IsNullOrWhiteSpace(retryAfter)) return null;
        if (int.TryParse(retryAfter, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)) return TimeSpan.FromSeconds(seconds);
        if (!DateTimeOffset.TryParse(retryAfter, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)) return null;
        var wait = at - clock.GetUtcNow();
        return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
    }
}
