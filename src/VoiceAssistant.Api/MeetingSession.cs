using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace VoiceAssistant.Api;

public sealed class SessionSlots
{
    private readonly Dictionary<string, Slot> active = new(StringComparer.Ordinal);

    /// <summary>Acquires the identity's single meeting slot, superseding a stale session of the same identity.</summary>
    public async Task<SessionLease?> AcquireAsync(string objectId, CancellationToken cancellation)
    {
        Slot? previous;
        lock (active)
        {
            if (!active.TryGetValue(objectId, out previous) && active.Count >= 100) return null;
            if (previous is null) return Register(objectId);
        }
        // A reconnect after a network drop can arrive before the old socket notices the drop.
        previous.Supersede.Cancel();
        try { await previous.Released.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellation); }
        catch (TimeoutException) { return null; }
        lock (active)
            return active.ContainsKey(objectId) ? null : Register(objectId);
    }

    public IDisposable? TryAcquire(string objectId)
    {
        lock (active)
            return active.Count >= 100 || active.ContainsKey(objectId) ? null : Register(objectId);
    }

    private SessionLease Register(string objectId)
    {
        var slot = new Slot();
        active.Add(objectId, slot);
        return new SessionLease(slot.Supersede.Token, () =>
        {
            lock (active) if (active.TryGetValue(objectId, out var current) && current == slot) active.Remove(objectId);
            slot.Released.TrySetResult();
            slot.Supersede.Dispose();
        });
    }

    private sealed class Slot
    {
        public CancellationTokenSource Supersede { get; } = new();
        public TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

public sealed class SessionLease(CancellationToken superseded, Action release) : IDisposable
{
    private Action? release = release;
    public CancellationToken Superseded { get; } = superseded;
    public void Dispose() => Interlocked.Exchange(ref this.release, null)?.Invoke();
}

public sealed class MeetingSession(WebSocket socket, IMeetingProvider provider, string objectId, ILogger logger,
    int maxSessionMinutes = 30, TimeProvider? timeProvider = null, MeetingFeatures? meetingFeatures = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly MeetingFeatures features = meetingFeatures ?? MeetingFeatures.Off;
    // Session duration, settle timers and reply timing all use this clock, so tests can drive them without sleeping.
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly Channel<SessionEvent> events = Channel.CreateBounded<SessionEvent>(
        new BoundedChannelOptions(64) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly List<ConversationTurn> conversation = [];
    private readonly EarlierTranscriptContext earlierTranscripts = new();
    private CancellationTokenSource? generation;
    private Task generationTask = Task.CompletedTask;
    private string? responseId;
    private string? responseTurn;
    private string? lastTurn;
    private long lastFinalTimestamp;
    private MeetingMetrics.ResponseMeasurement? responseMeasurement;
    private int generationNumber;
    private bool responseActive;
    private bool responsePending;
    private ConversationTurn[]? pendingResponseHistory;
    private string? pendingResponseTurn;
    private long pendingResponseTimestamp;
    private readonly List<string> pendingSpeech = [];
    private long pendingSince;
    /// <summary>A reply started early at a final (experiment): its generation and how many gathered segments it covered.</summary>
    private (int Generation, int Segments)? earlyReply;
    /// <summary>Experiment: a reply generated on a paused partial before its final, held back until that final adopts or discards it.</summary>
    private Speculation? speculation;
    /// <summary>The latest speculative generation; the next starts only after it finished, so at most one runs at a time.</summary>
    private Task speculationTask = Task.CompletedTask;
    /// <summary>The latest partial of the utterance being spoken, and the text identity its pause is timed for.</summary>
    private (Transcript Partial, string Identity)? pausedPartial;
    private int pauseNumber;
    /// <summary>
    /// How long the latest partial must stay unchanged before a speculative reply starts (experiment). The Speech service sends
    /// a new partial as each word is processed, and partials carry no punctuation, so until the final (about 1 s after speech
    /// ends on staging) a pause in partials is the only sign that a question is finished; a repeated partial is not, because
    /// silence produces no new partials. 250 ms is the pause PartialRetrieval already waits before it retrieves for a partial:
    /// the speculative reply starts from the same text that retrieval is for, and a gap between words shorter than that never
    /// starts one. A speculation that the final does not confirm costs one cancelled model call and is never seen.
    /// </summary>
    internal static readonly TimeSpan SpeculationPause = TimeSpan.FromMilliseconds(250);
    private int settleNumber;
    private int overflow;
    private SessionOptions options = SessionOptions.Legacy;
    /// <summary>The recognizer's options: the client's, plus material phrase hints when that experiment is on.</summary>
    private SessionOptions speechOptions = SessionOptions.Legacy;
    /// <summary>How long a session start waits for material phrase hints that are not cached yet.</summary>
    internal static readonly TimeSpan PhraseHintWait = TimeSpan.FromSeconds(1);
    private PartialRetrieval? prefetch;
    private string responseRoute = "knowledge";
    private bool retrievalPrefetched;
    private readonly List<Task> retired = [];
    private readonly List<Task> translations = [];
    private KoreanScope? koreanScope;
    /// <summary>How long one reply may take, retrieval and model included (the protocol's response deadline).</summary>
    internal static readonly TimeSpan ResponseDeadline = TimeSpan.FromSeconds(30);
    /// <summary>Recognizer restarts allowed per session after retryable cancellations; a failed restart attempt counts too.</summary>
    internal const int MaxSpeechRestarts = 3;
    private const string SpeechUnavailableMessage = "Speech recognition ended unexpectedly. Reconnect to continue.";
    /// <summary>Number of the most recently started recognizer; errors of an earlier (replaced) one are ignored.</summary>
    private int speechNumber;
    private int speechRestarts;
    private Task speechRestart = Task.CompletedTask;
    private sealed record RestartedSpeech(ISpeechStream Stream, SpeechAttempt Attempt);

    /// <summary>
    /// Holds the errors a restarted recognizer reports before the event loop has taken it over, so none is lost and none
    /// is mistaken for a failure of a recognizer that is not running yet.
    /// </summary>
    private sealed class SpeechAttempt(bool delivered = false)
    {
        private readonly object gate = new();
        private bool handedOver = delivered;
        private ProviderException? early;

        public bool HoldIfNotHandedOver(ProviderException failure)
        {
            lock (gate)
            {
                if (handedOver) return false;
                early ??= failure;
                return true;
            }
        }

        public ProviderException? HandOver()
        {
            lock (gate)
            {
                handedOver = true;
                return early;
            }
        }
    }
    private sealed record SessionEvent(string Kind, object? Value = null, int Generation = 0, long Timestamp = 0, bool ModelResponse = false,
        bool PrimaryDone = false);
    private sealed record AudioMessage(byte[] Bytes);
    private sealed record WireMessage(byte[] Bytes, WebSocketMessageType Type);
    private sealed record Completion(string Text, string[] Suggestions, Source[] Sources, string Grounding, string ResponseRoute, bool RetrievalPrefetched, bool RespondNow = false);
    private sealed record KoreanLine(KoreanScope Scope, int Index, string? Korean);

    /// <summary>A reply generated ahead of its final (experiment). Nothing of it is sent, logged or measured until the final adopts it.</summary>
    private sealed record Speculation(string TurnId, string Identity, ConversationTurn[] History, string Route, SessionOptions Settings,
        string ResponseId, CancellationTokenSource Cancellation, ReplyOutput Output, MeetingMetrics.ResponseMeasurement Measurement,
        KoreanScope? Scope, Task<RetrievalOutcome>? Prefetched, long StartedAt)
    {
        public Task Task { get; set; } = Task.CompletedTask;
    }

    /// <summary>
    /// What one reply's generation produces for the outside: events for the client (through the event loop) and failure log
    /// lines. A reply started at its final writes them straight through. A speculative reply holds them until its final adopts
    /// it, then hands them over in order as if they were produced just then; a discarded one drops them, so nothing of it is
    /// ever sent or logged.
    /// </summary>
    private sealed class ReplyOutput
    {
        private readonly ChannelWriter<SessionEvent> writer;
        private readonly object gate = new();
        private List<SessionEvent>? heldEvents;
        private List<Action>? heldLogs;
        private bool discarded;
        private int generation;

        /// <summary>Straight through, as the events of reply <paramref name="number"/>.</summary>
        public ReplyOutput(ChannelWriter<SessionEvent> writer, int number)
        {
            this.writer = writer;
            generation = number;
        }

        private ReplyOutput(ChannelWriter<SessionEvent> writer)
        {
            this.writer = writer;
            heldEvents = [];
            heldLogs = [];
        }

        /// <summary>For a speculative reply: everything is held until <see cref="Adopt"/>, or dropped by <see cref="Discard"/>.</summary>
        public static ReplyOutput Held(ChannelWriter<SessionEvent> writer) => new(writer);

        /// <summary>The reply's generation number; a held log line reads it when written, so after adoption it is the adopted one.</summary>
        public int Generation
        {
            get { lock (gate) return generation; }
        }

        public ValueTask WriteAsync(SessionEvent message, CancellationToken cancellation)
        {
            lock (gate)
            {
                if (discarded) return ValueTask.CompletedTask;
                if (heldEvents is not null)
                {
                    heldEvents.Add(message);
                    return ValueTask.CompletedTask;
                }
                message = message with { Generation = generation };
            }
            return writer.WriteAsync(message, cancellation);
        }

        /// <summary>A failure log line of this reply: written now, or only once a speculative reply is adopted.</summary>
        public void Log(Action write)
        {
            lock (gate)
            {
                if (discarded) return;
                if (heldLogs is not null)
                {
                    heldLogs.Add(write);
                    return;
                }
            }
            write();
        }

        /// <summary>
        /// The speculative reply became reply <paramref name="number"/>: its held log lines are written, and its held events
        /// are returned in order for the event loop to send now. Later events go straight through.
        /// </summary>
        public SessionEvent[] Adopt(int number)
        {
            List<SessionEvent> events;
            List<Action> logs;
            lock (gate)
            {
                generation = number;
                events = heldEvents ?? [];
                logs = heldLogs ?? [];
                heldEvents = null;
                heldLogs = null;
            }
            foreach (var write in logs) write();
            return events.Select(message => message with { Generation = number }).ToArray();
        }

        public void Discard()
        {
            lock (gate)
            {
                discarded = true;
                heldEvents = null;
                heldLogs = null;
            }
        }
    }

    /// <summary>Tracks the Korean lines of one response; a response cancelled before it completed never publishes any.</summary>
    private sealed class KoreanScope(string responseId, string? turnId, ReplyOutput output, MeetingMetrics.ResponseMeasurement? measurement = null)
    {
        private readonly string?[] lines = new string?[2];
        public string ResponseId { get; } = responseId;
        public string? TurnId { get; } = turnId;
        /// <summary>Where the translated lines go: with the reply's other events, so a speculative reply holds them too.</summary>
        public ReplyOutput Output { get; } = output;
        public MeetingMetrics.ResponseMeasurement? Measurement { get; } = measurement;
        public volatile bool Abandoned;
        /// <summary>False when this exact text is already being translated for the line.</summary>
        public bool Begin(int index, string english) { lock (lines) { if (lines[index] == english) return false; lines[index] = english; return true; } }
        public bool IsCurrent(int index, string english) { lock (lines) return lines[index] == english; }
    }

    public async Task RunAsync(CancellationToken requestAborted, CancellationToken superseded = default)
    {
        using var duration = new CancellationTokenSource(TimeSpan.FromMinutes(maxSessionMinutes), clock);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(requestAborted, duration.Token, superseded);
        using var receiving = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        ISpeechStream? speech = null;
        Task receiver = Task.CompletedTask;
        // A fixed vocabulary of end reasons (stopped, client_closed, time_limit, an error code...) for the session metric.
        var endReason = "ended";
        MeetingMetrics.SessionStarted();
        try
        {
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            startup.CancelAfter(TimeSpan.FromSeconds(15));
            var start = await ReceiveAsync(startup.Token, 32768);
            if (start is null || start.Type != WebSocketMessageType.Text || !IsValidStart(start.Bytes))
                throw new ProviderException("invalid_start", "First message must be protocol v1 session.start with PCM16LE 16 kHz mono audio.");
            using (var json = JsonDocument.Parse(start.Bytes))
                options = json.RootElement.TryGetProperty("options", out var input) ? SessionOptions.Parse(input) : SessionOptions.Legacy;
            if (!options.TranscribeOnly) prefetch = new(provider, objectId, options, lifetime.Token);
            speechOptions = await SpeechOptionsAsync(startup.Token);
            speech = await StartSpeechAsync(speechOptions, new SpeechAttempt(delivered: true), lifetime, startup.Token);
            // The client learns which optional features this socket has: pushed Korean lines, and settings changes while running.
            var ready = new Dictionary<string, object> { ["type"] = "session.ready", ["protocolVersion"] = 1 };
            if (options.ReplyKorean) ready["replyKorean"] = true;
            if (!options.TranscribeOnly) ready["sessionUpdate"] = true;
            await SendAsync(ready, lifetime.Token);
            // Cancelling a pending ReceiveAsync aborts the socket; retain it until the fatal time-limit event is sent.
            receiver = ReceiveLoopAsync(receiving.Token);
            await foreach (var message in events.Reader.ReadAllAsync(lifetime.Token))
            {
                switch (message.Kind)
                {
                    case "audio":
                        // Dropped while a failed recognizer is being replaced.
                        speech?.Write(((AudioMessage)message.Value!).Bytes);
                        break;
                    case "transcript":
                        var transcript = (Transcript)message.Value!;
                        if (transcript.Text.Length > 8000)
                            throw new ProviderException("transcript_limit", "Utterance is too long. Reconnect and use shorter utterances.");
                        await SendAsync(new
                        {
                            type = transcript.Final ? "transcript.final" : "transcript.partial",
                            turnId = transcript.TurnId,
                            revision = transcript.Revision,
                            text = transcript.Text
                        }, lifetime.Token);
                        if (options.TranscribeOnly) break;
                        if (transcript.Final)
                        {
                            conversation.Add(new(transcript.Text));
                            Bound(conversation, earlierTranscripts);
                            lastTurn = transcript.TurnId;
                            lastFinalTimestamp = message.Timestamp;
                            // The utterance ended, so a pause timer of its partials is void.
                            pausedPartial = null;
                            pauseNumber++;
                            if (options.ReplySettleMs > 0)
                            {
                                if (pendingSpeech.Count == 0) pendingSince = message.Timestamp;
                                pendingSpeech.Add(transcript.Text);
                                // Gathered speech that already left the bounded history can no longer be a reply target.
                                if (pendingSpeech.Count > conversation.Count)
                                {
                                    var dropped = pendingSpeech.Count - conversation.Count;
                                    pendingSpeech.RemoveRange(0, dropped);
                                    if (earlyReply is { } covered) earlyReply = (covered.Generation, Math.Max(0, covered.Segments - dropped));
                                }
                                // Experiment: this final decides a speculative reply, adopting it only when it is exactly the reply
                                // early start would begin now; that reply already has its retrieval.
                                var adopted = AdoptableSpeculation(transcript);
                                // The reply waits out the settle window anyway, so retrieve for this final in the meantime.
                                if (adopted is null)
                                    prefetch!.Update(transcript, earlierTranscripts.With(conversation.GetRange(0, conversation.Count - 1)));
                                ScheduleSettle(lifetime, afterFinal: true);
                                if (adopted is not null) await AdoptSpeculationAsync(adopted, lifetime.Token);
                                // Experiment: a complete question or request is answered now; the settle above only checks it later.
                                else if (features.EarlyStartAtFinal && CanStartEarly()) await StartEarlyReplyAsync(lifetime.Token);
                            }
                            else if (responseActive)
                            {
                                PreparePendingResponse(1);
                            }
                            else await StartResponseAsync(lifetime.Token, manual: false, currentSegments: 1);
                        }
                        else
                        {
                            // A speculative reply waiting for its final keeps the retrieval it took (experiment).
                            if (!FollowPartial(transcript, lifetime)) prefetch!.Update(transcript, earlierTranscripts.With(conversation));
                            // Ongoing speech postpones the reply, except a question or request already waiting too long.
                            if (options.ReplySettleMs > 0 && pendingSpeech.Count > 0 &&
                                !(clock.GetElapsedTime(pendingSince) > MaxGatherTime && ReplyTrigger.InvitesReply(pendingSpeech)))
                                ScheduleSettle(lifetime);
                        }
                        break;
                    case "speculate" when message.Generation == pauseNumber && pausedPartial is { } paused:
                        if (speculationTask.IsCompleted) StartSpeculation(paused.Partial, paused.Identity, lifetime.Token);
                        else
                        {
                            // One speculative generation at a time: this pause counts again once the previous one has unwound.
                            var pause = message.Generation;
                            _ = speculationTask.ContinueWith(_ => EnqueueCallback(new("speculate", Generation: pause), lifetime),
                                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                        }
                        break;
                    case "settle" when message.Generation == settleNumber && pendingSpeech.Count > 0:
                        if (earlyReply is { } started)
                        {
                            // The reply started at the final stands unless later speech in the window changed its target; then
                            // the whole gathered speech is answered, superseding it like a newer request.
                            earlyReply = null;
                            var gathered = pendingSpeech.Count;
                            var changed = TargetChangedSince(started);
                            pendingSpeech.Clear();
                            if (!changed) break;
                            if (responseActive && started.Generation != generationNumber) PreparePendingResponse(gathered);
                            else await StartResponseAsync(lifetime.Token, manual: false, gathered);
                            break;
                        }
                        var decision = ReplyTrigger.Evaluate(pendingSpeech);
                        if (decision == ReplyDecision.Wait) break;
                        var currentSegments = pendingSpeech.Count;
                        pendingSpeech.Clear();
                        if (decision == ReplyDecision.Discard) break;
                        if (responseActive)
                        {
                            PreparePendingResponse(currentSegments);
                        }
                        else await StartResponseAsync(lifetime.Token, manual: false, currentSegments);
                        break;
                    case "response.request":
                        if (options.TranscribeOnly) await ErrorAsync("transcribe_only", "This session only transcribes speech.", false, lifetime.Token);
                        else if (lastTurn is null) await ErrorAsync("no_transcript", "Wait for a finalized utterance.", true, lifetime.Token);
                        else
                        {
                            pendingSpeech.Clear(); settleNumber++; earlyReply = null;
                            DiscardSpeculation("cancelled");
                            await StartResponseAsync(lifetime.Token, manual: true, currentSegments: 1);
                        }
                        break;
                    case "response.cancel":
                        DiscardSpeculation("cancelled");
                        prefetch?.Cancel();
                        pendingSpeech.Clear(); settleNumber++; earlyReply = null;
                        await CancelResponseAsync(lifetime.Token);
                        break;
                    case "session.update":
                        if (options.TranscribeOnly)
                        {
                            await ErrorAsync("transcribe_only", "This session only transcribes speech.", false, lifetime.Token);
                            break;
                        }
                        // A speculative reply was prepared with the earlier settings, so it is dropped (experiment).
                        DiscardSpeculation("cancelled");
                        // A reply already generating keeps the settings it started with; the change applies from the next one.
                        options = options.Apply((SessionUpdate)message.Value!);
                        prefetch?.UpdateOptions(options);
                        await SendAsync(new { type = "session.updated", responseMode = options.ResponseMode, replyKorean = options.ReplyKorean }, lifetime.Token);
                        break;
                    case "session.stop":
                        endReason = "stopped";
                        DiscardSpeculation("cancelled");
                        await CancelResponseAsync(lifetime.Token);
                        return;
                    case "closed":
                        endReason = "client_closed";
                        return;
                    case "fatal":
                        var fatal = (ProviderException)message.Value!;
                        // An error of a recognizer that was already replaced says nothing about the current one.
                        if (message.Generation != 0 && message.Generation != Volatile.Read(ref speechNumber)) break;
                        if (speech is null || RestartableSpeechFailure(fatal) is not { } failedWith) throw fatal;
                        // The utterance being recognized is lost with the recognizer, and so is a reply speculated on it.
                        DiscardSpeculation("cancelled");
                        pausedPartial = null;
                        pauseNumber++;
                        speechRestart = RestartSpeechAsync(speech, failedWith, speechOptions, lifetime);
                        speech = null;
                        break;
                    case "speech.restarted":
                        var restarted = (RestartedSpeech)message.Value!;
                        speech = restarted.Stream;
                        // A failure the new recognizer reported before this hand-over is handled like any other.
                        if (restarted.Attempt.HandOver() is not { } early) break;
                        if (RestartableSpeechFailure(early) is not { } earlyCode) throw early;
                        speechRestart = RestartSpeechAsync(speech, earlyCode, speechOptions, lifetime);
                        speech = null;
                        break;
                    case "korean" or "delta" or "complete" or "grounding.unavailable" or "grounding.fallback" or "generation.error":
                        await HandleReplyEventAsync(message, lifetime.Token);
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            if (superseded.IsCancellationRequested)
            {
                endReason = "superseded";
                logger.LogWarning("Meeting session superseded by a newer session of the same user.");
                // Not retryable: otherwise two clients of the same user would keep replacing each other.
                await TryErrorAsync("session_superseded",
                    "Live started in another window or device, so this session was closed.", retryable: false);
            }
            else
            {
                endReason = overflow != 0 ? "overloaded" : duration.IsCancellationRequested ? "time_limit" :
                    requestAborted.IsCancellationRequested ? "aborted" : "ended";
                if (requestAborted.IsCancellationRequested)
                    logger.LogWarning("Meeting session ended: connection aborted.");
                await TryErrorAsync(overflow != 0 ? "session_overloaded" : duration.IsCancellationRequested ? "session_time_limit" : "session_ended",
                    overflow != 0 ? "Session could not keep up. Reconnect." :
                    duration.IsCancellationRequested ? "Session reached its time limit. Start a new session to continue." : "Session ended.",
                    retryable: !duration.IsCancellationRequested);
            }
        }
        catch (OperationCanceledException)
        {
            endReason = "startup_timeout";
            await TryErrorAsync("startup_timeout", "Session startup timed out. Reconnect.");
        }
        catch (ProviderException exception)
        {
            endReason = exception.Code;
            await TryErrorAsync(exception.Code, exception.Message);
        }
        catch (WebSocketException)
        {
            endReason = "transport_error";
            logger.LogWarning("Meeting transport disconnected by the client or network.");
        }
        catch (Exception exception)
        {
            endReason = "provider_unavailable";
            // The type name says what failed; the message could contain meeting content, so it is never logged.
            logger.LogWarning("Meeting provider failed with {ExceptionType}; sensitive error detail suppressed.", exception.GetType().Name);
            await TryErrorAsync("provider_unavailable", "The meeting provider is unavailable. Reconnect.");
        }
        finally
        {
            lifetime.Cancel();
            // A speculative reply still waiting for its final ends with the session, unseen (experiment).
            DiscardSpeculation("cancelled");
            // Send the close frame, then give the client's close (a browser answers at once) the rest of the budget to end the
            // receive loop before cancelling it. Cancelling a pending receive aborts the socket, and since ASP.NET Core 9 that
            // aborts the whole connection, which can discard the close frame and show the client a reset instead.
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var close = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Session ended", close.Token);
                    await Task.WhenAny(receiver, Task.Delay(Timeout.InfiniteTimeSpan, close.Token));
                }
                catch (Exception exception) when (exception is WebSocketException or OperationCanceledException)
                { logger.LogInformation("Meeting transport closed before shutdown completed."); }
            }
            receiving.Cancel();
            generation?.Cancel();
            if (prefetch is not null) await prefetch.DisposeAsync();
            events.Writer.TryComplete();
            Task[] pendingTranslations;
            lock (translations) pendingTranslations = translations.ToArray();
            await ObserveCleanupAsync(Task.WhenAll(retired.Concat(pendingTranslations).Append(receiver).Append(generationTask)
                .Append(speechRestart)));
            generation?.Dispose();
            // A recognizer restarted just as the session ended was never taken over by the loop.
            while (events.Reader.TryRead(out var leftover))
                if (leftover.Value is RestartedSpeech orphan) await DisposeSpeechAsync(orphan.Stream);
            if (speech is not null) await DisposeSpeechAsync(speech);
            MeetingMetrics.SessionEnded(endReason);
        }
    }

    /// <summary>
    /// The recognizer's options: the client's, plus phrase hints from the caller's own materials when that experiment is on
    /// (live sessions only, not practice). The hints reach only the speech phrase list, never prompts or translation, and
    /// only their count is logged.
    /// </summary>
    private async Task<SessionOptions> SpeechOptionsAsync(CancellationToken cancellation)
    {
        if (features.PhraseHints is not { } hints || options.TranscribeOnly) return options;
        IReadOnlyList<string> materials;
        try { materials = await hints.GetAsync(objectId).WaitAsync(PhraseHintWait, cancellation); }
        catch (TimeoutException)
        {
            materials = [];
            logger.LogInformation("Material phrase hints were not ready within {WaitMs} ms; speech starts with the client phrases.",
                (long)PhraseHintWait.TotalMilliseconds);
        }
        var merged = Knowledge.MaterialPhrases.Merge(options.Phrases, materials);
        logger.LogInformation("Speech phrase list: {ClientPhrases} from the client and {MaterialPhrases} from materials.",
            options.Phrases.Count, merged.Count - options.Phrases.Count);
        return options with { Phrases = merged };
    }

    /// <summary>Starts a recognizer whose errors carry its number, so a late error of a replaced recognizer can be ignored.</summary>
    private Task<ISpeechStream> StartSpeechAsync(SessionOptions settings, SpeechAttempt attempt, CancellationTokenSource lifetime,
        CancellationToken cancellation)
    {
        var number = Interlocked.Increment(ref speechNumber);
        return provider.StartSpeechAsync(settings,
            transcript => EnqueueCallback(new("transcript", transcript, Timestamp: clock.GetTimestamp()), lifetime),
            error =>
            {
                if (!attempt.HoldIfNotHandedOver(error)) EnqueueCallback(new("fatal", error, number), lifetime);
            }, cancellation);
    }

    /// <summary>
    /// The metric code of a recognizer cancellation worth an in-process restart (connection failure, service time-out or
    /// service unavailable) while restarts remain; null when the failure must end the session.
    /// </summary>
    private string? RestartableSpeechFailure(ProviderException failure)
    {
        if (Volatile.Read(ref speechRestarts) >= MaxSpeechRestarts) return null;
        return failure.SpeechCancellation is { Reason: Microsoft.CognitiveServices.Speech.CancellationReason.Error } cancellation
            ? cancellation.ErrorCode switch
            {
                Microsoft.CognitiveServices.Speech.CancellationErrorCode.ConnectionFailure => "connection_failure",
                Microsoft.CognitiveServices.Speech.CancellationErrorCode.ServiceTimeout => "service_timeout",
                Microsoft.CognitiveServices.Speech.CancellationErrorCode.ServiceUnavailable => "service_unavailable",
                _ => null
            }
            : null;
    }

    /// <summary>Backoff before restart attempt 1, 2, 3...: 0.5 s, 1 s, 2 s.</summary>
    internal static TimeSpan SpeechRestartDelay(int restart) => TimeSpan.FromMilliseconds(500 * (1 << Math.Clamp(restart - 1, 0, 4)));

    /// <summary>
    /// Replaces a recognizer that stopped on a retryable cancellation. It runs beside the event loop, which keeps draining
    /// (and dropping) audio meanwhile and keeps the transcript history. Each attempt waits its backoff on the session clock;
    /// when the attempts allowed per session are used up without a working recognizer, the session ends as before.
    /// </summary>
    private async Task RestartSpeechAsync(ISpeechStream failed, string code, SessionOptions settings, CancellationTokenSource lifetime)
    {
        logger.LogWarning("Speech recognition stopped with {Code}; restarting the recognizer in-process.", code);
        await DisposeSpeechAsync(failed);
        while (true)
        {
            var restart = Interlocked.Increment(ref speechRestarts);
            try
            {
                await Task.Delay(SpeechRestartDelay(restart), clock, lifetime.Token);
                var attempt = new SpeechAttempt();
                var stream = await StartSpeechAsync(settings, attempt, lifetime, lifetime.Token);
                try { await events.Writer.WriteAsync(new("speech.restarted", new RestartedSpeech(stream, attempt)), lifetime.Token); }
                catch
                {
                    await DisposeSpeechAsync(stream);
                    throw;
                }
                MeetingMetrics.SpeechRestarted(code, restarted: true);
                logger.LogInformation("Speech recognizer restarted (restart {Restart} of {Max}).", restart, MaxSpeechRestarts);
                return;
            }
            catch (Exception) when (lifetime.IsCancellationRequested) { return; }
            catch (Exception exception)
            {
                MeetingMetrics.SpeechRestarted(code, restarted: false);
                logger.LogWarning("Speech restart {Restart} of {Max} failed with {ExceptionType}.", restart, MaxSpeechRestarts,
                    exception.GetType().Name);
                if (restart >= MaxSpeechRestarts)
                {
                    EnqueueCallback(new("fatal", new ProviderException("speech_unavailable", SpeechUnavailableMessage)), lifetime);
                    return;
                }
            }
        }
    }

    private async Task DisposeSpeechAsync(ISpeechStream stream)
    {
        try { await stream.DisposeAsync(); }
        catch (Exception) { logger.LogWarning("Speech cleanup failed; sensitive error detail suppressed."); }
    }

    /// <summary>Statements wait for more context, but never longer than this before a reply is offered.</summary>
    private static readonly TimeSpan MaxGatherTime = TimeSpan.FromSeconds(12);

    /// <summary>A question or request that ends like a finished sentence is answered after this short pause, not the full settle window.</summary>
    private const int CompleteSettleMs = 300;

    /// <summary>
    /// The turn signal is optional: a slow or failed classifier yields "no emphasis", never an error. A failure is returned
    /// so it can be logged, for example a request the deployment rejects.
    /// </summary>
    private static async Task<(bool RespondNow, string Outcome, Exception? Failure)> TurnSignalAsync(Task<bool> signal)
    {
        try
        {
            var respond = await signal.WaitAsync(TimeSpan.FromMilliseconds(800));
            return (respond, respond ? "respond" : "listen", null);
        }
        catch (TimeoutException) { return (false, "timeout", null); }
        catch (Exception exception) { return (false, "failed", exception); }
    }

    private void ScheduleSettle(CancellationTokenSource lifetime, bool afterFinal = false)
    {
        var number = ++settleNumber;
        var delay = TimeSpan.FromMilliseconds(options.ReplySettleMs);
        if (pendingSpeech.Count > 0 && clock.GetElapsedTime(pendingSince) > MaxGatherTime)
            // A long-running gathered statement is answered as soon as the speaker pauses briefly.
            delay = TimeSpan.FromMilliseconds(CompleteSettleMs);
        else if (afterFinal && CompleteQuestionOrRequest())
            // New speech re-arms the full window, so a continuation still merges into one reply.
            delay = TimeSpan.FromMilliseconds(Math.Min(options.ReplySettleMs, CompleteSettleMs));
        // Enqueued inline when the timer fires (a non-blocking TryWrite), so the settle is ordered before any later speech.
        _ = Task.Delay(delay, clock, lifetime.Token).ContinueWith(task =>
        {
            if (task.IsCompletedSuccessfully) EnqueueCallback(new("settle", Generation: number), lifetime);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private void EnqueueCallback(SessionEvent message, CancellationTokenSource lifetime)
    {
        if (lifetime.IsCancellationRequested) return;
        if (!events.Writer.TryWrite(message))
        {
            Interlocked.Exchange(ref overflow, 1);
            lifetime.Cancel();
        }
    }

    /// <summary>The gathered speech ends with a question or request that looks finished: the rule that picks the short settle.</summary>
    private bool CompleteQuestionOrRequest() => CompleteQuestionOrRequest(pendingSpeech);

    private static bool CompleteQuestionOrRequest(IReadOnlyList<string> gathered) =>
        ReplyTrigger.InvitesReply(gathered) && ReplyTrigger.LooksComplete(gathered);

    /// <summary>Speech gathered after an early reply started, unless it is only backchannels, changes what it should answer.</summary>
    private bool TargetChangedSince((int Generation, int Segments) early) => TargetChangedSince(early, pendingSpeech);

    private static bool TargetChangedSince((int Generation, int Segments) early, IReadOnlyList<string> gathered) =>
        gathered.Count > early.Segments &&
        ReplyTrigger.Evaluate(gathered.Skip(early.Segments).ToArray()) != ReplyDecision.Discard;

    private bool CanStartEarly() => CanStartEarly(pendingSpeech);

    private bool CanStartEarly(IReadOnlyList<string> gathered)
    {
        if (!CompleteQuestionOrRequest(gathered) || ReplyTrigger.Evaluate(gathered) != ReplyDecision.Reply) return false;
        // Within one window a new early start needs a changed target (a backchannel never is) and replaces only its own reply.
        if (earlyReply is { } early) return TargetChangedSince(early, gathered) && (!responseActive || early.Generation == generationNumber);
        // Otherwise only when nothing else is answering: an active reply keeps today's hold-and-queue through the settle.
        return !responseActive;
    }

    private async Task StartEarlyReplyAsync(CancellationToken lifetime)
    {
        var segments = pendingSpeech.Count;
        // Like a superseding request, this cancels the window's earlier early reply if it is still running.
        await StartResponseAsync(lifetime, manual: false, segments);
        earlyReply = (generationNumber, segments);
    }

    /// <summary>Experiment (Session:SpeculativeStart, on top of early start): replies may start on a paused partial.</summary>
    private bool Speculative => features.SpeculativeStart && options.ReplySettleMs > 0 && !options.TranscribeOnly && prefetch is not null;

    /// <summary>
    /// Follows the partials of the utterance being spoken (experiment). A partial that changes what the speculative reply
    /// answers discards it; new words restart the pause timer. True while a speculative reply is still waiting for its final.
    /// </summary>
    private bool FollowPartial(Transcript partial, CancellationTokenSource lifetime)
    {
        if (!Speculative) return false;
        var identity = ResponseRouting.Identity(partial.Text);
        if (speculation is { } running && ChangesTarget(running, partial.TurnId, identity)) DiscardSpeculation("target_changed");
        var unchanged = pausedPartial is { } last && last.Partial.TurnId == partial.TurnId && last.Identity == identity;
        pausedPartial = (partial, identity);
        // The same words again keep the pause counting from their first partial.
        if (!unchanged) ArmPause(lifetime);
        return speculation is not null;
    }

    private void ArmPause(CancellationTokenSource lifetime)
    {
        var number = ++pauseNumber;
        _ = Task.Delay(SpeculationPause, clock, lifetime.Token).ContinueWith(task =>
        {
            if (task.IsCompletedSuccessfully) EnqueueCallback(new("speculate", Generation: number), lifetime);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>
    /// Whether newer speech changes what a speculative reply answers. As for an early reply (TargetChangedSince), words that
    /// only add backchannels do not. Another utterance always does: only this utterance's final can adopt the reply.
    /// </summary>
    private static bool ChangesTarget(Speculation running, string turnId, string identity)
    {
        if (turnId != running.TurnId) return true;
        if (identity == running.Identity) return false;
        return !identity.StartsWith(running.Identity + " ", StringComparison.Ordinal) ||
            ReplyTrigger.Evaluate([identity[(running.Identity.Length + 1)..]]) != ReplyDecision.Discard;
    }

    /// <summary>
    /// The latest partial stayed unchanged for <see cref="SpeculationPause"/> (experiment). When the gathered speech ending with
    /// it passes the early-start rule and nothing else is answering, its reply is generated now, exactly as early start would
    /// begin it at this text's final, and held back until that final adopts or discards it.
    /// </summary>
    private void StartSpeculation(Transcript partial, string identity, CancellationToken lifetime)
    {
        // One speculative generation at a time (the caller waits for the previous one to unwind), and none beside a reply.
        if (!Speculative || speculation is not null || !speculationTask.IsCompleted || responseActive) return;
        // Partials carry no punctuation, so the pause stands in for the end of the sentence.
        var gathered = pendingSpeech.Append(ReplyTrigger.Closed(partial.Text)).ToArray();
        if (!CanStartEarly(gathered)) return;
        var history = HistoryWith(partial.Text, gathered.Length);
        var prior = history[..^1];
        var settings = options;
        var route = ResponseRouting.Select(partial.Text, settings, prior);
        // A confirmed introduction is composed at once at its final, so starting it early gains nothing.
        if (route == "profile") return;
        Task<RetrievalOutcome>? prefetched = null;
        // Exactly as for a reply started at the final: it reuses this text's search only when that search has already started,
        // and otherwise searches on its own at once, so it never waits behind the single prefetch worker.
        if (route == "knowledge") prefetched = prefetch!.Take(partial.TurnId, partial.Text, route, prior);
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        cancellation.CancelAfter(ResponseDeadline);
        var deadline = clock.GetTimestamp() + (long)(ResponseDeadline.TotalSeconds * clock.TimestampFrequency);
        var responseId = Guid.NewGuid().ToString("N");
        var output = ReplyOutput.Held(events.Writer);
        var measurement = new MeetingMetrics.ResponseMeasurement(0, MeetingMetrics.ProviderMode(provider), manual: false, clock)
        {
            Route = route, ResponseMode = settings.ResponseMode, Speculative = true
        };
        var scope = settings.ReplyKorean ? new KoreanScope(responseId, partial.TurnId, output, measurement) : null;
        var started = new Speculation(partial.TurnId, identity, history, route, settings, responseId, cancellation, output, measurement,
            scope, prefetched, clock.GetTimestamp());
        var token = cancellation.Token;
        started.Task = Task.Run(() => GenerateAsync(output, history, route, prefetched, scope, settings, measurement, deadline, token, lifetime));
        speculation = started;
        speculationTask = started.Task;
        MeetingMetrics.Speculation("started", MeetingMetrics.ProviderMode(provider), route, settings.ResponseMode);
    }

    /// <summary>
    /// This final ends the utterance a speculative reply was generated for (experiment). The reply is adopted only when it is
    /// exactly the one early start would begin now: same utterance and words (case, punctuation and spacing aside), same
    /// history, route and settings. Otherwise it is discarded, unseen.
    /// </summary>
    private Speculation? AdoptableSpeculation(Transcript final)
    {
        if (speculation is not { } candidate) return null;
        if (candidate.TurnId == final.TurnId && candidate.Identity == ResponseRouting.Identity(final.Text) && candidate.Settings == options &&
            CanStartEarly())
        {
            var history = CreateFocusedHistory(pendingSpeech.Count);
            if (SameHistory(history, candidate.History) && ResponseRouting.Select(final.Text, options, history[..^1]) == candidate.Route)
                return candidate;
        }
        DiscardSpeculation("mismatch");
        return null;
    }

    /// <summary>Two reply histories that differ at most in the case, punctuation and spacing of the latest utterance.</summary>
    private static bool SameHistory(ConversationTurn[] final, ConversationTurn[] speculated) =>
        final.Length == speculated.Length && final.Length > 0 &&
        final.Take(final.Length - 1).SequenceEqual(speculated.Take(speculated.Length - 1)) &&
        final[^1].CurrentResponseTarget == speculated[^1].CurrentResponseTarget &&
        ResponseRouting.Identity(final[^1].Text) == ResponseRouting.Identity(speculated[^1].Text);

    /// <summary>
    /// Makes the speculative reply the early reply of this final, exactly as <see cref="StartResponseAsync"/> begins one but
    /// with the generation that is already running: the same cancellation of a superseded reply, numbering and events. What
    /// it produced so far is sent at once, in order; the rest streams as usual.
    /// </summary>
    private async Task AdoptSpeculationAsync(Speculation adopted, CancellationToken lifetime)
    {
        var segments = pendingSpeech.Count;
        await CancelResponseAsync(lifetime);
        generation?.Dispose();
        generation = adopted.Cancellation;
        speculation = null;
        if (!generationTask.IsCompleted) retired.Add(generationTask);
        generationTask = adopted.Task;
        responseId = adopted.ResponseId;
        responseTurn = lastTurn;
        responseRoute = adopted.Route;
        retrievalPrefetched = adopted.Prefetched is not null;
        responseActive = true;
        var measurement = adopted.Measurement;
        measurement.Adopt(lastFinalTimestamp);
        responseMeasurement = measurement;
        measurement.Started();
        if (responseRoute == "knowledge") MeetingMetrics.Prefetched(adopted.Prefetched is not null);
        koreanScope = adopted.Scope;
        var number = ++generationNumber;
        MeetingMetrics.Speculation("adopted", MeetingMetrics.ProviderMode(provider), adopted.Route, adopted.Settings.ResponseMode,
            headStart: clock.GetElapsedTime(adopted.StartedAt, lastFinalTimestamp));
        await SendAsync(new { type = "response.started", responseId, turnId = responseTurn }, lifetime);
        retired.RemoveAll(task => task.IsCompletedSuccessfully);
        if (retired.Count > 8) throw new ProviderException("response_overload", "Too many pending responses. Reconnect.");
        earlyReply = (number, segments);
        foreach (var held in adopted.Output.Adopt(number)) await HandleReplyEventAsync(held, lifetime);
    }

    /// <summary>Drops the speculative reply, if any: its generation is cancelled, and nothing it produced is sent, logged or measured.</summary>
    private void DiscardSpeculation(string reason)
    {
        if (speculation is not { } dropped) return;
        speculation = null;
        dropped.Cancellation.Cancel();
        dropped.Output.Discard();
        dropped.Measurement.Discard();
        if (dropped.Scope is not null) dropped.Scope.Abandoned = true;
        // No reply waits for the search it took any more: newer speech cancels it, an identical final may still reuse it.
        if (dropped.Prefetched is { } taken) prefetch?.GiveBack(taken);
        // Awaited at session cleanup like a superseded reply; its cancellation source is released once it has finished.
        retired.RemoveAll(task => task.IsCompletedSuccessfully);
        retired.Add(dropped.Task);
        _ = dropped.Task.ContinueWith(_ => dropped.Cancellation.Dispose(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        MeetingMetrics.Speculation("discarded", MeetingMetrics.ProviderMode(provider), dropped.Route, dropped.Settings.ResponseMode, reason);
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellation)
    {
        try
        {
            var audioBudget = 128000d;
            var previous = System.Diagnostics.Stopwatch.GetTimestamp();
            var commands = 0;
            var window = DateTime.UtcNow;
            while (!cancellation.IsCancellationRequested)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                idle.CancelAfter(TimeSpan.FromSeconds(90));
                var message = await ReceiveAsync(idle.Token);
                if (message is null) { await events.Writer.WriteAsync(new("closed"), cancellation); return; }
                if (message.Type == WebSocketMessageType.Binary)
                {
                    var now = System.Diagnostics.Stopwatch.GetTimestamp();
                    audioBudget = Math.Min(128000, audioBudget + System.Diagnostics.Stopwatch.GetElapsedTime(previous, now).TotalSeconds * 64000);
                    previous = now;
                    audioBudget -= message.Bytes.Length;
                    if (message.Bytes.Length == 0 || message.Bytes.Length % 2 != 0 || audioBudget < 0)
                        throw new ProviderException("invalid_audio", "Audio must be bounded, even-length PCM16LE at real-time speed.");
                    await events.Writer.WriteAsync(new("audio", new AudioMessage(message.Bytes)), cancellation);
                }
                else
                {
                    if (DateTime.UtcNow - window > TimeSpan.FromSeconds(1)) { window = DateTime.UtcNow; commands = 0; }
                    if (++commands > 10) throw new ProviderException("command_limit", "Too many commands.");
                    var command = ParseCommand(message.Bytes);
                    await events.Writer.WriteAsync(new(command.Type, command.Update), cancellation);
                    if (command.Type == "session.stop") return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (OperationCanceledException) { await events.Writer.WriteAsync(new("fatal", new ProviderException("idle_timeout", "Session idle timeout.")), cancellation); }
        catch (ProviderException exception) { await events.Writer.WriteAsync(new("fatal", exception), cancellation); }
        catch (WebSocketException) { await events.Writer.WriteAsync(new("closed"), cancellation); }
    }

    public static bool IsValidStart(byte[] bytes)
    {
        try
        {
            using var json = JsonDocument.Parse(bytes);
            var root = json.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("options", out var options))
                SessionOptions.Parse(options);
            return root.ValueKind == JsonValueKind.Object && root.GetProperty("type").GetString() == "session.start" &&
                root.GetProperty("protocolVersion").GetInt32() == 1 &&
                root.GetProperty("audio").GetProperty("encoding").GetString() == "pcm_s16le" &&
                root.GetProperty("audio").GetProperty("sampleRate").GetInt32() == 16000 &&
                root.GetProperty("audio").GetProperty("channels").GetInt32() == 1;
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        { return false; }
    }

    private sealed record Command(string Type, SessionUpdate? Update = null);

    private static Command ParseCommand(byte[] bytes)
    {
        try
        {
            using var json = JsonDocument.Parse(bytes);
            var type = json.RootElement.GetProperty("type").GetString();
            if (type is "response.request" or "response.cancel" or "session.stop") return new(type);
            if (type == "session.update") return new(type, SessionOptions.ParseUpdate(json.RootElement.GetProperty("options")));
            throw new ProviderException("invalid_command", "Unsupported command.");
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        { throw new ProviderException("invalid_command", "Command must be a JSON object with a supported type and valid options."); }
    }

    private async Task<WireMessage?> ReceiveAsync(CancellationToken cancellation, int textLimit = 4096)
    {
        var buffer = new byte[8192];
        using var data = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellation);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            var limit = result.MessageType == WebSocketMessageType.Text ? textLimit : 32768;
            if (data.Length + result.Count > limit) throw new ProviderException("message_limit", "Message exceeds size limit.");
            data.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        return new(data.ToArray(), result.MessageType);
    }

    private void PreparePendingResponse(int currentSegments)
    {
        pendingResponseHistory = CreateFocusedHistory(currentSegments);
        pendingResponseTurn = lastTurn;
        // Timed from its own final, not from whatever later final arrives while it waits.
        pendingResponseTimestamp = lastFinalTimestamp;
        responsePending = true;
    }

    private const string GroundingUnavailableMessage =
        "Document search is unavailable, so this reply is general and not based on your materials.";
    private const string UnavailableNotice = "Grounding is unavailable. Please try again before relying on a factual answer.";

    /// <summary>Starts the queued reply, if any, after the active one completed or failed.</summary>
    private Task StartPendingResponseAsync(CancellationToken lifetime) => responsePending
        ? StartResponseAsync(lifetime, manual: false, currentSegments: 0,
            preparedHistory: pendingResponseHistory, preparedTurn: pendingResponseTurn, preparedTimestamp: pendingResponseTimestamp)
        : Task.CompletedTask;

    /// <summary>The fixed completion used when neither the materials nor a general model reply are available.</summary>
    private async Task CompleteWithoutGroundingAsync(CancellationToken cancellation)
    {
        // Korean started for a reply that failed after its stream ended must not arrive for text the client no longer shows.
        if (koreanScope is not null) koreanScope.Abandoned = true;
        await SendAsync(new
        {
            type = "response.completed",
            responseId,
            turnId = responseTurn,
            text = UnavailableNotice,
            suggestions = new[] { UnavailableNotice },
            sources = Array.Empty<Source>(),
            grounding = "unavailable",
            responseRoute,
            retrievalPrefetched
        }, cancellation);
        // This fixed notice is not translated server-side; tell the client so it does not wait for it.
        if (koreanScope is not null)
            await SendAsync(new { type = "response.korean", responseId, turnId = responseTurn, index = 0, failed = true }, cancellation);
        responseMeasurement?.ReplyCompleted("unavailable");
        responseActive = false;
    }

    private ConversationTurn[] CreateFocusedHistory(int currentSegments) => Focus(conversation, earlierTranscripts, currentSegments);

    /// <summary>
    /// The history a reply would get if this text were the next final: <see cref="CreateFocusedHistory"/> after that final joined
    /// the bounded conversation, worked out on copies so the session's own history is untouched.
    /// </summary>
    private ConversationTurn[] HistoryWith(string text, int currentSegments)
    {
        var turns = new List<ConversationTurn>(conversation) { new(text) };
        var earlier = earlierTranscripts.Copy();
        Bound(turns, earlier);
        return Focus(turns, earlier, currentSegments);
    }

    /// <summary>Keeps the recent conversation within 12 turns and 24,000 characters; older turns become earlier excerpts.</summary>
    private static void Bound(List<ConversationTurn> turns, EarlierTranscriptContext earlier)
    {
        while (turns.Count > 12 || turns.Sum(turn => turn.Text.Length) > 24000)
        {
            earlier.Remember(turns[0]);
            turns.RemoveAt(0);
        }
    }

    private static ConversationTurn[] Focus(List<ConversationTurn> turns, EarlierTranscriptContext earlier, int currentSegments)
    {
        // Only segments still in the bounded history can be targeted; older gathered speech is dropped, never an error.
        var focusStart = turns.Count - Math.Clamp(currentSegments, 1, Math.Max(1, turns.Count));
        var focusedConversation = turns.Select((turn, index) =>
            turn with { CurrentResponseTarget = index >= focusStart }).ToArray();
        return earlier.With(focusedConversation);
    }

    private async Task StartResponseAsync(CancellationToken lifetime, bool manual, int currentSegments,
        ConversationTurn[]? preparedHistory = null, string? preparedTurn = null, long? preparedTimestamp = null)
    {
        // A reply that starts any other way answers what a speculative reply was speculating on, so that one is dropped.
        DiscardSpeculation("target_changed");
        var history = preparedHistory ?? CreateFocusedHistory(currentSegments);
        if (history.Length == 0) throw new InvalidOperationException("Response history cannot be empty.");
        await CancelResponseAsync(lifetime);
        generation?.Dispose();
        generation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        generation.CancelAfter(ResponseDeadline);
        // The same deadline as an absolute session-clock timestamp, so a model retry only happens while the reply has time left.
        var deadline = clock.GetTimestamp() + (long)(ResponseDeadline.TotalSeconds * clock.TimestampFrequency);
        responseId = Guid.NewGuid().ToString("N");
        responseTurn = preparedTurn ?? lastTurn;
        // The reply keeps the settings it starts with, even when the client changes them while it generates.
        var settings = options;
        var prior = history.Take(history.Length - 1).ToArray();
        var targetText = history[^1].Text;
        responseRoute = ResponseRouting.Select(targetText, settings, prior);
        var prefetched = prefetch?.Take(responseTurn!, targetText, responseRoute, prior);
        retrievalPrefetched = prefetched is not null;
        responseActive = true;
        var measurement = new MeetingMetrics.ResponseMeasurement(preparedTimestamp ?? lastFinalTimestamp, MeetingMetrics.ProviderMode(provider),
            manual, clock) { Route = responseRoute, ResponseMode = settings.ResponseMode };
        responseMeasurement = measurement;
        measurement.Started();
        if (responseRoute == "knowledge") MeetingMetrics.Prefetched(prefetched is not null);
        var number = ++generationNumber;
        var output = new ReplyOutput(events.Writer, number);
        var scope = settings.ReplyKorean ? new KoreanScope(responseId, responseTurn, output, measurement) : null;
        koreanScope = scope;
        await SendAsync(new { type = "response.started", responseId, turnId = responseTurn }, lifetime);
        if (!generationTask.IsCompleted) retired.Add(generationTask);
        retired.RemoveAll(task => task.IsCompletedSuccessfully);
        if (retired.Count > 8) throw new ProviderException("response_overload", "Too many pending responses. Reconnect.");
        var token = generation.Token;
        var route = responseRoute;
        generationTask = Task.Run(() => GenerateAsync(output, history, route, prefetched, scope, settings, measurement, deadline, token, lifetime));
    }

    private async Task CancelResponseAsync(CancellationToken cancellation)
    {
        responsePending = false;
        pendingResponseHistory = null;
        pendingResponseTurn = null;
        generation?.Cancel();
        generationNumber++;
        if (!responseActive) return;
        // A reply that already completed keeps its translations; one that never completed publishes none.
        if (koreanScope is not null) koreanScope.Abandoned = true;
        responseActive = false;
        responseMeasurement = null;
        await SendAsync(new { type = "response.cancelled", responseId, turnId = responseTurn }, cancellation);
    }

    /// <summary>
    /// Sends what a reply's generation produced, through the event loop: straight from the channel, or replayed in order when
    /// a speculative reply is adopted. Events of a replaced or finished reply are dropped.
    /// </summary>
    private async Task HandleReplyEventAsync(SessionEvent message, CancellationToken lifetime)
    {
        switch (message.Kind)
        {
            case "korean":
                var line = (KoreanLine)message.Value!;
                // A cancel can be handled while this event waits in the queue; a cancelled reply publishes no Korean.
                if (line.Scope.Abandoned) break;
                object koreanMessage = line.Korean is { } meaning
                    ? new { type = "response.korean", responseId = line.Scope.ResponseId, turnId = line.Scope.TurnId, index = line.Index, korean = meaning }
                    : new { type = "response.korean", responseId = line.Scope.ResponseId, turnId = line.Scope.TurnId, index = line.Index, failed = true };
                await SendAsync(koreanMessage, lifetime);
                line.Scope.Measurement?.KoreanSent(line.Index, line.Korean is not null);
                break;
            case "delta" when message.Generation == generationNumber && responseActive:
                var deltaText = (string)message.Value!;
                // primaryDone marks, once per response, that the first line is complete; only that delta may be empty.
                await SendAsync(message.PrimaryDone
                    ? new { type = "response.delta", responseId, turnId = responseTurn, text = deltaText, primaryDone = true }
                    : (object)new { type = "response.delta", responseId, turnId = responseTurn, text = deltaText }, lifetime);
                if (message.ModelResponse && deltaText.Length > 0) responseMeasurement?.FirstDeltaSent();
                if (message.ModelResponse && message.PrimaryDone) responseMeasurement?.PrimaryDoneSent();
                break;
            case "complete" when message.Generation == generationNumber && responseActive:
                var completion = (Completion)message.Value!;
                await SendAsync(new
                {
                    type = "response.completed",
                    responseId,
                    turnId = responseTurn,
                    text = completion.Text,
                    suggestions = completion.Suggestions,
                    sources = completion.Sources,
                    grounding = completion.Grounding,
                    responseRoute = completion.ResponseRoute,
                    retrievalPrefetched = completion.RetrievalPrefetched,
                    respondNow = completion.RespondNow
                }, lifetime);
                if (message.ModelResponse) responseMeasurement?.CompletedSent();
                responseMeasurement?.ReplyCompleted(completion.Grounding);
                responseActive = false;
                await StartPendingResponseAsync(lifetime);
                break;
            case "grounding.unavailable" when message.Generation == generationNumber && responseActive:
                // Non-fatal and sent before the general reply, so older clients still learn why it is not document-based.
                await ErrorAsync("grounding_unavailable", GroundingUnavailableMessage, true, lifetime);
                break;
            case "grounding.fallback" when message.Generation == generationNumber && responseActive:
                await CompleteWithoutGroundingAsync(lifetime);
                await StartPendingResponseAsync(lifetime);
                break;
            case "generation.error" when message.Generation == generationNumber && responseActive:
                var failure = (ProviderException)message.Value!;
                await ErrorAsync(failure.Code, failure.Message, true, lifetime);
                if (failure.Code == "grounding_unavailable")
                    await CompleteWithoutGroundingAsync(lifetime);
                else
                {
                    var pending = responsePending;
                    var pendingHistory = pendingResponseHistory;
                    var pendingTurn = pendingResponseTurn;
                    await CancelResponseAsync(lifetime);
                    responsePending = pending;
                    pendingResponseHistory = pendingHistory;
                    pendingResponseTurn = pendingTurn;
                }
                await StartPendingResponseAsync(lifetime);
                break;
        }
    }

    private async Task GenerateAsync(ReplyOutput output, ConversationTurn[] history, string route, Task<RetrievalOutcome>? prefetched,
        KoreanScope? scope, SessionOptions settings, MeetingMetrics.ResponseMeasurement measurement, long deadline,
        CancellationToken cancellation, CancellationToken lifetime)
    {
        // Model calls of this reply (answer, classifier) see the time it really has left, after any retrieval wait.
        using var replyDeadline = LiveChatCall.WithinReplyDeadline(deadline, clock);
        var groundingUnavailable = false;
        try
        {
            if (route == "profile")
            {
                cancellation.ThrowIfCancellationRequested();
                measurement.GroundingReady("disabled");
                var introduction = ProfileIntroduction.Compose(settings);
                StartKorean(scope, 0, introduction, new TranslationContext(settings.Topic, settings.Phrases), lifetime);
                await output.WriteAsync(new("delta", introduction, PrimaryDone: true), cancellation);
                await output.WriteAsync(new("complete",
                    new Completion(introduction, [introduction], [], "disabled", "profile", false)), cancellation);
                return;
            }
            // Started before retrieval so the classifier has finished by the time the answer does; it only adds emphasis
            // and never delays or blocks the suggestion. Its failure is observed here so it can never go unhandled.
            var turnSignal = provider.ShouldRespondAsync(history, cancellation);
            _ = turnSignal.ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
            var grounding = new Grounding("disabled", []);
            if (route == "knowledge")
            {
                var waitStarted = clock.GetTimestamp();
                try
                {
                    grounding = prefetched is not null ? (await prefetched.WaitAsync(cancellation)).RequireGrounding() :
                        await MeetingMetrics.MeasureRetrievalAsync(provider,
                            RetrievalQuery.Build(history[^1].Text, history.Take(history.Length - 1).ToArray()), objectId, cancellation);
                }
                catch (ProviderException failure) when (failure.Code == "grounding_unavailable")
                {
                    output.Log(() => LogRetrievalFailure(logger, failure));
                    // Announced before the reply, as before, so the client knows this reply is not based on its materials.
                    await output.WriteAsync(new("grounding.unavailable"), cancellation);
                    groundingUnavailable = true;
                    grounding = new("unavailable", []);
                }
                measurement.GroundingReady(grounding.Status, clock.GetElapsedTime(waitStarted), prefetched is not null);
            }
            else measurement.GroundingReady(grounding.Status);
            var modelResponse = grounding.Status is "disabled" or "grounded" or "no_matches" or "unavailable";
            var translation = scope is null ? TranslationContext.Empty
                : new TranslationContext(settings.Topic, KoreanTranslation.Terms(settings, grounding));
            var text = new StringBuilder();
            string? alternative = null;
            var primaryDoneSent = false;
            measurement.RequestSent();
            await foreach (var update in provider.AnswerWithSuggestionsAsync(history, grounding, settings, route, cancellation).WithCancellation(cancellation))
            {
                cancellation.ThrowIfCancellationRequested();
                var delta = update.Text;
                if (delta.Length > 0) measurement.FirstTokenReceived();
                if (text.Length + delta.Length > 8000) throw new ProviderException("response_limit", "Response exceeded its size limit.");
                text.Append(delta);
                if (update.Alternative is not null) alternative = update.Alternative;
                // The delta that ends the first line carries primaryDone; an end found after that text was sent is an empty delta.
                var primaryDone = update.PrimaryDone && !primaryDoneSent && text.Length > 0;
                primaryDoneSent |= primaryDone;
                if (delta.Length > 0 || primaryDone)
                    await output.WriteAsync(new("delta", delta, ModelResponse: modelResponse, PrimaryDone: primaryDone), cancellation);
            }
            cancellation.ThrowIfCancellationRequested();
            if (text.Length == 0) throw new ProviderException("empty_response", "The model returned no text. Please retry.");
            // A single-line reply is known to be complete only when the stream ends.
            if (!primaryDoneSent)
                await output.WriteAsync(new("delta", "", ModelResponse: modelResponse, PrimaryDone: true), cancellation);
            var suggestions = ReplySuggestions.Complete(text.ToString(), alternative);
            // Translation starts only once the stream has ended normally: with asynchronous content filtering a line is not
            // checked before then, and a reply the filter stops (response_filtered) must never reach the translation call.
            for (var index = 0; index < suggestions.Length; index++)
                StartKorean(scope, index, suggestions[index].Trim(), translation, lifetime);
            var classifierStarted = clock.GetTimestamp();
            var (respondNow, signal, classifierFailure) = await TurnSignalAsync(turnSignal);
            measurement.ClassifierWaited(clock.GetElapsedTime(classifierStarted), signal);
            // A classifier cancelled together with its reply (cancelled, superseded or timed out) did not fail.
            if (classifierFailure is not null && !(classifierFailure is OperationCanceledException && cancellation.IsCancellationRequested))
                output.Log(() => logger.LogWarning("Turn classifier failed: {ExceptionType}, HTTP status {Status}; the reply is sent without emphasis.",
                    classifierFailure.GetType().Name, HttpStatusText(classifierFailure)));
            await output.WriteAsync(new("complete",
                new Completion(text.ToString(), suggestions,
                    DistinctSources(grounding.Documents), grounding.Status, route, prefetched is not null, respondNow),
                ModelResponse: modelResponse), cancellation);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // A deadline after an announced search failure still ends with the fixed completion, never a second error.
            // A user cancel bumps the generation number, so the event loop drops this stale event.
            if (!lifetime.IsCancellationRequested)
                await output.WriteAsync(groundingUnavailable
                    ? new("grounding.fallback")
                    : new("generation.error", new ProviderException("response_timeout", "Response timed out or was cancelled.")), lifetime);
        }
        catch (Exception exception)
        {
            if (!lifetime.IsCancellationRequested)
            {
                var error = exception switch
                {
                    ProviderException failure => failure,
                    System.ClientModel.ClientResultException { Status: 429 } =>
                        new ProviderException("model_busy", "Azure model request limit reached. Wait briefly, then retry."),
                    _ => new ProviderException("response_unavailable", "Response service is unavailable. Please retry.")
                };
                // Content-free, like every failure line: a withdrawn reply logs its code and generation, never its text.
                var filtered = error.Code == "response_filtered";
                var afterSearchFailure = groundingUnavailable;
                if (filtered)
                    output.Log(() => logger.LogWarning(
                        "Meeting response withdrawn: code {Code}, generation {Generation}, after search failure {AfterSearchFailure}.",
                        error.Code, output.Generation, afterSearchFailure));
                else
                    output.Log(() => logger.LogWarning(
                        "Meeting response failed with code {Code}: {ExceptionType}, HTTP status {Status}, after search failure {AfterSearchFailure}.",
                        error.Code, exception.GetType().Name, HttpStatusText(exception), afterSearchFailure));
                // The search failure was already announced; when the general reply fails too, only the fixed notice follows.
                // A reply the content filter stopped is withdrawn instead, because that notice would complete it.
                await output.WriteAsync(groundingUnavailable && !filtered
                    ? new("grounding.fallback")
                    : new("generation.error", error), lifetime);
            }
        }
    }

    /// <summary>Content-free log line for a search failure: the cause's category, exception type, HTTP status, Search code and retry.</summary>
    internal static void LogRetrievalFailure(ILogger logger, ProviderException failure)
    {
        var cause = failure.Retrieval;
        logger.LogWarning("Meeting retrieval unavailable: {Kind}, transient {Transient}, {ExceptionType}, HTTP status {Status}, search code {SearchCode}, retried {Retried}.",
            cause?.Kind ?? "unknown", cause?.Transient ?? false, cause?.ExceptionType ?? nameof(ProviderException),
            cause?.HttpStatus?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none", cause?.ErrorCode ?? "none",
            cause?.Retried ?? false);
    }

    /// <summary>
    /// One source per document, in evidence order: chunks of the same document share its URL (or, without one, its title),
    /// so they appear once. The model still receives every accepted chunk as evidence.
    /// </summary>
    internal static Source[] DistinctSources(IEnumerable<Evidence> documents)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return documents.Select(document => document.Source)
            .Where(source => seen.Add(string.IsNullOrWhiteSpace(source.Url)
                ? "title:" + source.Title.Trim().ToUpperInvariant() : "url:" + source.Url))
            .ToArray();
    }

    /// <summary>The HTTP status an Azure SDK exception carries, or "none" when no HTTP answer was received.</summary>
    internal static string HttpStatusText(Exception exception) => exception switch
    {
        System.ClientModel.ClientResultException { Status: > 0 } client => client.Status.ToString(System.Globalization.CultureInfo.InvariantCulture),
        Azure.RequestFailedException { Status: > 0 } failed => failed.Status.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => "none"
    };

    /// <summary>
    /// Translates one reply line for a client that opted in. It runs beside the answer, publishes through the event loop and
    /// never blocks the reply; a line that cannot be translated is reported as failed so the client can fall back.
    /// </summary>
    private void StartKorean(KoreanScope? scope, int index, string english, TranslationContext context, CancellationToken lifetime)
    {
        if (scope is null || english.Length == 0 || lifetime.IsCancellationRequested || !scope.Begin(index, english)) return;
        lock (translations)
        {
            // Checked under the lock the cleanup also takes, so nothing starts after the final snapshot.
            if (lifetime.IsCancellationRequested) return;
            translations.RemoveAll(task => task.IsCompleted);
            translations.Add(Task.Run(() => TranslateAsync(scope, index, english, context, lifetime), CancellationToken.None));
        }
    }

    private async Task TranslateAsync(KoreanScope scope, int index, string english, TranslationContext context, CancellationToken lifetime)
    {
        string? korean = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            korean = await provider.TranslateReplyAsync(english, context, timeout.Token);
        }
        catch (Exception exception) when (!lifetime.IsCancellationRequested)
        {
            scope.Output.Log(() => logger.LogWarning("Reply translation failed: {Failure}.",
                exception is System.ClientModel.ClientResultException client ? $"HTTP {client.Status}" : exception.GetType().Name));
        }
        catch (Exception)
        {
            return;
        }
        if (scope.Abandoned || !scope.IsCurrent(index, english) || lifetime.IsCancellationRequested) return;
        try { await scope.Output.WriteAsync(new("korean", new KoreanLine(scope, index, korean)), lifetime); }
        catch (Exception exception) when (exception is OperationCanceledException or ChannelClosedException) { }
    }

    private async Task SendAsync(object value, CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(value, Json), WebSocketMessageType.Text, true, timeout.Token);
    }
    private Task ErrorAsync(string code, string message, bool retryable, CancellationToken cancellation)
    {
        MeetingMetrics.ErrorSent(code);
        return SendAsync(new { type = "error", code, message, retryable }, cancellation);
    }
    private async Task TryErrorAsync(string code, string message, bool retryable = true)
    {
        if (socket.State != WebSocketState.Open) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await ErrorAsync(code, message, retryable, timeout.Token); }
        catch (Exception exception) when (exception is WebSocketException or OperationCanceledException)
        { logger.LogInformation("Meeting error could not be delivered to disconnected client."); }
    }
    private async Task ObserveCleanupAsync(Task task)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (OperationCanceledException) { }
        catch (Exception) { logger.LogWarning("Meeting cleanup did not complete normally; sensitive detail suppressed."); }
    }
}
