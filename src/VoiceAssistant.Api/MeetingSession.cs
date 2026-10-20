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
    int maxSessionMinutes = 30, TimeProvider? timeProvider = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
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
    private int settleNumber;
    private int overflow;
    private SessionOptions options = SessionOptions.Legacy;
    private PartialRetrieval? prefetch;
    private string responseRoute = "knowledge";
    private bool retrievalPrefetched;
    private readonly List<Task> retired = [];
    private readonly List<Task> translations = [];
    private KoreanScope? koreanScope;
    private sealed record SessionEvent(string Kind, object? Value = null, int Generation = 0, long Timestamp = 0, bool ModelResponse = false,
        bool PrimaryDone = false);
    private sealed record AudioMessage(byte[] Bytes);
    private sealed record WireMessage(byte[] Bytes, WebSocketMessageType Type);
    private sealed record Completion(string Text, string[] Suggestions, Source[] Sources, string Grounding, string ResponseRoute, bool RetrievalPrefetched, bool RespondNow = false);
    private sealed record KoreanLine(KoreanScope Scope, int Index, string? Korean);

    /// <summary>Tracks the Korean lines of one response; a response cancelled before it completed never publishes any.</summary>
    private sealed class KoreanScope(string responseId, string? turnId, MeetingMetrics.ResponseMeasurement? measurement = null)
    {
        private readonly string?[] lines = new string?[2];
        public string ResponseId { get; } = responseId;
        public string? TurnId { get; } = turnId;
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
            speech = await provider.StartSpeechAsync(
                options,
                transcript => EnqueueCallback(new("transcript", transcript, Timestamp: clock.GetTimestamp()), lifetime),
                error => EnqueueCallback(new("fatal", error), lifetime), startup.Token);
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
                        speech.Write(((AudioMessage)message.Value!).Bytes);
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
                            while (conversation.Count > 12 || conversation.Sum(turn => turn.Text.Length) > 24000)
                            {
                                earlierTranscripts.Remember(conversation[0]);
                                conversation.RemoveAt(0);
                            }
                            lastTurn = transcript.TurnId;
                            lastFinalTimestamp = message.Timestamp;
                            if (options.ReplySettleMs > 0)
                            {
                                if (pendingSpeech.Count == 0) pendingSince = message.Timestamp;
                                pendingSpeech.Add(transcript.Text);
                                // Gathered speech that already left the bounded history can no longer be a reply target.
                                if (pendingSpeech.Count > conversation.Count)
                                    pendingSpeech.RemoveRange(0, pendingSpeech.Count - conversation.Count);
                                // The reply waits out the settle window anyway, so retrieve for this final in the meantime.
                                prefetch!.Update(transcript, earlierTranscripts.With(conversation.GetRange(0, conversation.Count - 1)));
                                ScheduleSettle(lifetime, afterFinal: true);
                            }
                            else if (responseActive)
                            {
                                PreparePendingResponse(1);
                            }
                            else await StartResponseAsync(lifetime.Token, manual: false, currentSegments: 1);
                        }
                        else
                        {
                            prefetch!.Update(transcript, earlierTranscripts.With(conversation));
                            // Ongoing speech postpones the reply, except a question or request already waiting too long.
                            if (options.ReplySettleMs > 0 && pendingSpeech.Count > 0 &&
                                !(clock.GetElapsedTime(pendingSince) > MaxGatherTime && ReplyTrigger.InvitesReply(pendingSpeech)))
                                ScheduleSettle(lifetime);
                        }
                        break;
                    case "settle" when message.Generation == settleNumber && pendingSpeech.Count > 0:
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
                            pendingSpeech.Clear(); settleNumber++;
                            await StartResponseAsync(lifetime.Token, manual: true, currentSegments: 1);
                        }
                        break;
                    case "response.cancel":
                        prefetch?.Cancel();
                        pendingSpeech.Clear(); settleNumber++;
                        await CancelResponseAsync(lifetime.Token);
                        break;
                    case "session.update":
                        if (options.TranscribeOnly)
                        {
                            await ErrorAsync("transcribe_only", "This session only transcribes speech.", false, lifetime.Token);
                            break;
                        }
                        // A reply already generating keeps the settings it started with; the change applies from the next one.
                        options = options.Apply((SessionUpdate)message.Value!);
                        prefetch?.UpdateOptions(options);
                        await SendAsync(new { type = "session.updated", responseMode = options.ResponseMode, replyKorean = options.ReplyKorean }, lifetime.Token);
                        break;
                    case "session.stop":
                        endReason = "stopped";
                        await CancelResponseAsync(lifetime.Token);
                        return;
                    case "closed":
                        endReason = "client_closed";
                        return;
                    case "fatal": throw (ProviderException)message.Value!;
                    case "korean":
                        var line = (KoreanLine)message.Value!;
                        // A cancel can be handled while this event waits in the queue; a cancelled reply publishes no Korean.
                        if (line.Scope.Abandoned) break;
                        object koreanMessage = line.Korean is { } meaning
                            ? new { type = "response.korean", responseId = line.Scope.ResponseId, turnId = line.Scope.TurnId, index = line.Index, korean = meaning }
                            : new { type = "response.korean", responseId = line.Scope.ResponseId, turnId = line.Scope.TurnId, index = line.Index, failed = true };
                        await SendAsync(koreanMessage, lifetime.Token);
                        line.Scope.Measurement?.KoreanSent(line.Index, line.Korean is not null);
                        break;
                    case "delta" when message.Generation == generationNumber && responseActive:
                        var deltaText = (string)message.Value!;
                        // primaryDone marks, once per response, that the first line is complete; only that delta may be empty.
                        await SendAsync(message.PrimaryDone
                            ? new { type = "response.delta", responseId, turnId = responseTurn, text = deltaText, primaryDone = true }
                            : (object)new { type = "response.delta", responseId, turnId = responseTurn, text = deltaText }, lifetime.Token);
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
                        }, lifetime.Token);
                        if (message.ModelResponse) responseMeasurement?.CompletedSent();
                        responseMeasurement?.ReplyCompleted(completion.Grounding);
                        responseActive = false;
                        await StartPendingResponseAsync(lifetime.Token);
                        break;
                    case "grounding.unavailable" when message.Generation == generationNumber && responseActive:
                        // Non-fatal and sent before the general reply, so older clients still learn why it is not document-based.
                        await ErrorAsync("grounding_unavailable", GroundingUnavailableMessage, true, lifetime.Token);
                        break;
                    case "grounding.fallback" when message.Generation == generationNumber && responseActive:
                        await CompleteWithoutGroundingAsync(lifetime.Token);
                        await StartPendingResponseAsync(lifetime.Token);
                        break;
                    case "generation.error" when message.Generation == generationNumber && responseActive:
                        var failure = (ProviderException)message.Value!;
                        await ErrorAsync(failure.Code, failure.Message, true, lifetime.Token);
                        if (failure.Code == "grounding_unavailable")
                            await CompleteWithoutGroundingAsync(lifetime.Token);
                        else
                        {
                            var pending = responsePending;
                            var pendingHistory = pendingResponseHistory;
                            var pendingTurn = pendingResponseTurn;
                            await CancelResponseAsync(lifetime.Token);
                            responsePending = pending;
                            pendingResponseHistory = pendingHistory;
                            pendingResponseTurn = pendingTurn;
                        }
                        await StartPendingResponseAsync(lifetime.Token);
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
            // Send the close frame before cancelling the pending receive: cancelling it aborts the socket
            // and the client would see an unexplained reset instead of a normal close.
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var close = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Session ended", close.Token); }
                catch (Exception exception) when (exception is WebSocketException or OperationCanceledException)
                { logger.LogInformation("Meeting transport closed before shutdown completed."); }
            }
            receiving.Cancel();
            generation?.Cancel();
            if (prefetch is not null) await prefetch.DisposeAsync();
            events.Writer.TryComplete();
            Task[] pendingTranslations;
            lock (translations) pendingTranslations = translations.ToArray();
            await ObserveCleanupAsync(Task.WhenAll(retired.Concat(pendingTranslations).Append(receiver).Append(generationTask)));
            generation?.Dispose();
            if (speech is not null)
            {
                try { await speech.DisposeAsync(); }
                catch (Exception) { logger.LogWarning("Speech cleanup failed; sensitive error detail suppressed."); }
            }
            MeetingMetrics.SessionEnded(endReason);
        }
    }

    /// <summary>Statements wait for more context, but never longer than this before a reply is offered.</summary>
    private static readonly TimeSpan MaxGatherTime = TimeSpan.FromSeconds(12);

    /// <summary>A question or request that ends like a finished sentence is answered after this short pause, not the full settle window.</summary>
    private const int CompleteSettleMs = 300;

    /// <summary>The turn signal is optional: a slow or failed classifier yields "no emphasis", never an error.</summary>
    private static async Task<(bool RespondNow, string Outcome)> TurnSignalAsync(Task<bool> signal)
    {
        try
        {
            var respond = await signal.WaitAsync(TimeSpan.FromMilliseconds(800));
            return (respond, respond ? "respond" : "listen");
        }
        catch (TimeoutException) { return (false, "timeout"); }
        catch (Exception) { return (false, "failed"); }
    }

    private void ScheduleSettle(CancellationTokenSource lifetime, bool afterFinal = false)
    {
        var number = ++settleNumber;
        var delay = TimeSpan.FromMilliseconds(options.ReplySettleMs);
        if (pendingSpeech.Count > 0 && clock.GetElapsedTime(pendingSince) > MaxGatherTime)
            // A long-running gathered statement is answered as soon as the speaker pauses briefly.
            delay = TimeSpan.FromMilliseconds(CompleteSettleMs);
        else if (afterFinal && ReplyTrigger.InvitesReply(pendingSpeech) && ReplyTrigger.LooksComplete(pendingSpeech))
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
        // Korean started for a reply that failed mid-stream must not arrive for text the client no longer shows.
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

    private ConversationTurn[] CreateFocusedHistory(int currentSegments)
    {
        // Only segments still in the bounded history can be targeted; older gathered speech is dropped, never an error.
        var focusStart = conversation.Count - Math.Clamp(currentSegments, 1, Math.Max(1, conversation.Count));
        var focusedConversation = conversation.Select((turn, index) =>
            turn with { CurrentResponseTarget = index >= focusStart }).ToArray();
        return earlierTranscripts.With(focusedConversation);
    }

    private async Task StartResponseAsync(CancellationToken lifetime, bool manual, int currentSegments,
        ConversationTurn[]? preparedHistory = null, string? preparedTurn = null, long? preparedTimestamp = null)
    {
        var history = preparedHistory ?? CreateFocusedHistory(currentSegments);
        if (history.Length == 0) throw new InvalidOperationException("Response history cannot be empty.");
        await CancelResponseAsync(lifetime);
        generation?.Dispose();
        generation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        generation.CancelAfter(TimeSpan.FromSeconds(30));
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
        var scope = settings.ReplyKorean ? new KoreanScope(responseId, responseTurn, measurement) : null;
        koreanScope = scope;
        var number = ++generationNumber;
        await SendAsync(new { type = "response.started", responseId, turnId = responseTurn }, lifetime);
        if (!generationTask.IsCompleted) retired.Add(generationTask);
        retired.RemoveAll(task => task.IsCompletedSuccessfully);
        if (retired.Count > 8) throw new ProviderException("response_overload", "Too many pending responses. Reconnect.");
        var token = generation.Token;
        var route = responseRoute;
        generationTask = Task.Run(() => GenerateAsync(number, history, route, prefetched, scope, settings, measurement, token, lifetime));
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

    private async Task GenerateAsync(int number, ConversationTurn[] history, string route, Task<RetrievalOutcome>? prefetched,
        KoreanScope? scope, SessionOptions settings, MeetingMetrics.ResponseMeasurement measurement, CancellationToken cancellation,
        CancellationToken lifetime)
    {
        var groundingUnavailable = false;
        try
        {
            if (route == "profile")
            {
                cancellation.ThrowIfCancellationRequested();
                measurement.GroundingReady("disabled");
                var introduction = ProfileIntroduction.Compose(settings);
                StartKorean(scope, 0, introduction, new TranslationContext(settings.Topic, settings.Phrases), lifetime);
                await events.Writer.WriteAsync(new("delta", introduction, number, PrimaryDone: true), cancellation);
                await events.Writer.WriteAsync(new("complete",
                    new Completion(introduction, [introduction], [], "disabled", "profile", false), number), cancellation);
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
                    LogRetrievalFailure(logger, failure);
                    // Announced before the reply, as before, so the client knows this reply is not based on its materials.
                    await events.Writer.WriteAsync(new("grounding.unavailable", Generation: number), cancellation);
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
                    await events.Writer.WriteAsync(new("delta", delta, number, ModelResponse: modelResponse, PrimaryDone: primaryDone), cancellation);
                // The first line is final as soon as the second begins, so its translation overlaps the alternative.
                if (update.PrimaryDone) StartKorean(scope, 0, text.ToString().Trim(), translation, lifetime);
            }
            cancellation.ThrowIfCancellationRequested();
            if (text.Length == 0) throw new ProviderException("empty_response", "The model returned no text. Please retry.");
            // A single-line reply is known to be complete only when the stream ends.
            if (!primaryDoneSent)
                await events.Writer.WriteAsync(new("delta", "", number, ModelResponse: modelResponse, PrimaryDone: true), cancellation);
            var suggestions = ReplySuggestions.Complete(text.ToString(), alternative);
            for (var index = 0; index < suggestions.Length; index++)
                StartKorean(scope, index, suggestions[index].Trim(), translation, lifetime);
            var classifierStarted = clock.GetTimestamp();
            var (respondNow, signal) = await TurnSignalAsync(turnSignal);
            measurement.ClassifierWaited(clock.GetElapsedTime(classifierStarted), signal);
            await events.Writer.WriteAsync(new("complete",
                new Completion(text.ToString(), suggestions,
                    DistinctSources(grounding.Documents), grounding.Status, route, prefetched is not null, respondNow), number,
                ModelResponse: modelResponse), cancellation);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // A deadline after an announced search failure still ends with the fixed completion, never a second error.
            // A user cancel bumps the generation number, so the event loop drops this stale event.
            if (!lifetime.IsCancellationRequested)
                await events.Writer.WriteAsync(groundingUnavailable
                    ? new("grounding.fallback", Generation: number)
                    : new("generation.error", new ProviderException("response_timeout", "Response timed out or was cancelled."), number), lifetime);
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
                logger.LogWarning("Meeting response failed with code {Code}: {ExceptionType}, HTTP status {Status}, after search failure {AfterSearchFailure}.",
                    error.Code, exception.GetType().Name, HttpStatusText(exception), groundingUnavailable);
                // The search failure was already announced; when the general reply fails too, only the fixed notice follows.
                await events.Writer.WriteAsync(groundingUnavailable
                    ? new("grounding.fallback", Generation: number)
                    : new("generation.error", error, number), lifetime);
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
            logger.LogWarning("Reply translation failed: {Failure}.",
                exception is System.ClientModel.ClientResultException client ? $"HTTP {client.Status}" : exception.GetType().Name);
        }
        catch (Exception)
        {
            return;
        }
        if (scope.Abandoned || !scope.IsCurrent(index, english) || lifetime.IsCancellationRequested) return;
        try { await events.Writer.WriteAsync(new("korean", new KoreanLine(scope, index, korean)), lifetime); }
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
