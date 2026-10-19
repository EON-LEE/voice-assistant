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
    private sealed record SessionEvent(string Kind, object? Value = null, int Generation = 0, long Timestamp = 0, bool ModelResponse = false);
    private sealed record AudioMessage(byte[] Bytes);
    private sealed record WireMessage(byte[] Bytes, WebSocketMessageType Type);
    private sealed record Completion(string Text, string[] Suggestions, Source[] Sources, string Grounding, string ResponseRoute, bool RetrievalPrefetched, bool RespondNow = false);
    private sealed record KoreanLine(KoreanScope Scope, int Index, string? Korean);

    /// <summary>Tracks the Korean lines of one response; a response cancelled before it completed never publishes any.</summary>
    private sealed class KoreanScope(string responseId, string? turnId)
    {
        private readonly string?[] lines = new string?[2];
        public string ResponseId { get; } = responseId;
        public string? TurnId { get; } = turnId;
        public volatile bool Abandoned;
        /// <summary>False when this exact text is already being translated for the line.</summary>
        public bool Begin(int index, string english) { lock (lines) { if (lines[index] == english) return false; lines[index] = english; return true; } }
        public bool IsCurrent(int index, string english) { lock (lines) return lines[index] == english; }
    }

    public async Task RunAsync(CancellationToken requestAborted, CancellationToken superseded = default)
    {
        using var duration = new CancellationTokenSource(TimeSpan.FromMinutes(maxSessionMinutes), timeProvider ?? TimeProvider.System);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(requestAborted, duration.Token, superseded);
        using var receiving = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        ISpeechStream? speech = null;
        Task receiver = Task.CompletedTask;
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
                transcript => EnqueueCallback(new("transcript", transcript, Timestamp: TimeProvider.System.GetTimestamp()), lifetime),
                error => EnqueueCallback(new("fatal", error), lifetime), startup.Token);
            // An opted-in client is told the server will push a Korean line for every reply line, so it knows to wait for it.
            object ready = options.ReplyKorean
                ? new { type = "session.ready", protocolVersion = 1, replyKorean = true }
                : new { type = "session.ready", protocolVersion = 1 };
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
                                !(TimeProvider.System.GetElapsedTime(pendingSince) > MaxGatherTime && ReplyTrigger.InvitesReply(pendingSpeech)))
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
                    case "session.stop":
                        await CancelResponseAsync(lifetime.Token);
                        return;
                    case "closed": return;
                    case "fatal": throw (ProviderException)message.Value!;
                    case "korean":
                        var line = (KoreanLine)message.Value!;
                        // A cancel can be handled while this event waits in the queue; a cancelled reply publishes no Korean.
                        if (line.Scope.Abandoned) break;
                        object koreanMessage = line.Korean is { } meaning
                            ? new { type = "response.korean", responseId = line.Scope.ResponseId, turnId = line.Scope.TurnId, index = line.Index, korean = meaning }
                            : new { type = "response.korean", responseId = line.Scope.ResponseId, turnId = line.Scope.TurnId, index = line.Index, failed = true };
                        await SendAsync(koreanMessage, lifetime.Token);
                        break;
                    case "delta" when message.Generation == generationNumber && responseActive:
                        await SendAsync(new { type = "response.delta", responseId, turnId = responseTurn, text = (string)message.Value! }, lifetime.Token);
                        if (message.ModelResponse) responseMeasurement?.FirstDeltaSent();
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
                        responseActive = false;
                        if (responsePending)
                        {
                            var completedQueueHistory = pendingResponseHistory;
                            var completedQueueTurn = pendingResponseTurn;
                            await StartResponseAsync(lifetime.Token, manual: false, currentSegments: 0,
                                preparedHistory: completedQueueHistory, preparedTurn: completedQueueTurn);
                        }
                        break;
                    case "generation.error" when message.Generation == generationNumber && responseActive:
                        var failure = (ProviderException)message.Value!;
                        await ErrorAsync(failure.Code, failure.Message, true, lifetime.Token);
                        if (failure.Code == "grounding_unavailable")
                        {
                            await SendAsync(new
                            {
                                type = "response.completed",
                                responseId,
                                turnId = responseTurn,
                                text = "Grounding is unavailable. Please try again before relying on a factual answer.",
                                suggestions = new[] { "Grounding is unavailable. Please try again before relying on a factual answer." },
                                sources = Array.Empty<Source>(),
                                grounding = "unavailable",
                                responseRoute,
                                retrievalPrefetched
                            }, lifetime.Token);
                            // This fixed notice is not translated server-side; tell the client so it does not wait for it.
                            if (options.ReplyKorean)
                                await SendAsync(new { type = "response.korean", responseId, turnId = responseTurn, index = 0, failed = true }, lifetime.Token);
                            responseActive = false;
                        }
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
                        if (responsePending)
                        {
                            var recoveryQueueHistory = pendingResponseHistory;
                            var recoveryQueueTurn = pendingResponseTurn;
                            await StartResponseAsync(lifetime.Token, manual: false, currentSegments: 0,
                                preparedHistory: recoveryQueueHistory, preparedTurn: recoveryQueueTurn);
                        }
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            if (superseded.IsCancellationRequested)
            {
                logger.LogWarning("Meeting session superseded by a newer session of the same user.");
                // Not retryable: otherwise two clients of the same user would keep replacing each other.
                await TryErrorAsync("session_superseded",
                    "Live started in another window or device, so this session was closed.", retryable: false);
            }
            else
            {
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
            await TryErrorAsync("startup_timeout", "Session startup timed out. Reconnect.");
        }
        catch (ProviderException exception) { await TryErrorAsync(exception.Code, exception.Message); }
        catch (WebSocketException) { logger.LogWarning("Meeting transport disconnected by the client or network."); }
        catch (Exception)
        {
            logger.LogWarning("Meeting provider failed; sensitive error detail suppressed.");
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
        }
    }

    /// <summary>Statements wait for more context, but never longer than this before a reply is offered.</summary>
    private static readonly TimeSpan MaxGatherTime = TimeSpan.FromSeconds(12);

    /// <summary>A question or request that ends like a finished sentence is answered after this short pause, not the full settle window.</summary>
    private const int CompleteSettleMs = 300;

    /// <summary>The turn signal is optional: a slow or failed classifier yields "no emphasis", never an error.</summary>
    private async Task<bool> TurnSignalAsync(Task<bool> signal)
    {
        try { return await signal.WaitAsync(TimeSpan.FromMilliseconds(800)); }
        catch (Exception) { return false; }
    }

    private void ScheduleSettle(CancellationTokenSource lifetime, bool afterFinal = false)
    {
        var number = ++settleNumber;
        var delay = TimeSpan.FromMilliseconds(options.ReplySettleMs);
        if (pendingSpeech.Count > 0 && TimeProvider.System.GetElapsedTime(pendingSince) > MaxGatherTime)
            // A long-running gathered statement is answered as soon as the speaker pauses briefly.
            delay = TimeSpan.FromMilliseconds(CompleteSettleMs);
        else if (afterFinal && ReplyTrigger.InvitesReply(pendingSpeech) && ReplyTrigger.LooksComplete(pendingSpeech))
            // New speech re-arms the full window, so a continuation still merges into one reply.
            delay = TimeSpan.FromMilliseconds(Math.Min(options.ReplySettleMs, CompleteSettleMs));
        _ = Task.Delay(delay, lifetime.Token).ContinueWith(task =>
        {
            if (task.IsCompletedSuccessfully) EnqueueCallback(new("settle", Generation: number), lifetime);
        }, TaskScheduler.Default);
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
                    await events.Writer.WriteAsync(new(command), cancellation);
                    if (command == "session.stop") return;
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

    private static string ParseCommand(byte[] bytes)
    {
        try
        {
            using var json = JsonDocument.Parse(bytes);
            var type = json.RootElement.GetProperty("type").GetString();
            return type is "response.request" or "response.cancel" or "session.stop" ? type :
                throw new ProviderException("invalid_command", "Unsupported command.");
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        { throw new ProviderException("invalid_command", "Command must be a JSON object with a supported type."); }
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
        responsePending = true;
    }

    private ConversationTurn[] CreateFocusedHistory(int currentSegments)
    {
        if (currentSegments < 1 || currentSegments > conversation.Count)
            throw new InvalidOperationException("Current response segments must identify recent conversation turns.");
        var focusStart = conversation.Count - currentSegments;
        var focusedConversation = conversation.Select((turn, index) =>
            turn with { CurrentResponseTarget = index >= focusStart }).ToArray();
        return earlierTranscripts.With(focusedConversation);
    }

    private async Task StartResponseAsync(CancellationToken lifetime, bool manual, int currentSegments,
        ConversationTurn[]? preparedHistory = null, string? preparedTurn = null)
    {
        var history = preparedHistory ?? CreateFocusedHistory(currentSegments);
        if (history.Length == 0) throw new InvalidOperationException("Response history cannot be empty.");
        await CancelResponseAsync(lifetime);
        generation?.Dispose();
        generation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        generation.CancelAfter(TimeSpan.FromSeconds(30));
        responseId = Guid.NewGuid().ToString("N");
        responseTurn = preparedTurn ?? lastTurn;
        var scope = options.ReplyKorean ? new KoreanScope(responseId, responseTurn) : null;
        koreanScope = scope;
        var prior = history.Take(history.Length - 1).ToArray();
        var targetText = history[^1].Text;
        responseRoute = ResponseRouting.Select(targetText, options, prior);
        var prefetched = prefetch?.Take(responseTurn!, targetText, responseRoute, prior);
        retrievalPrefetched = prefetched is not null;
        responseActive = true;
        responseMeasurement = new(lastFinalTimestamp, MeetingMetrics.ProviderMode(provider), manual);
        var number = ++generationNumber;
        await SendAsync(new { type = "response.started", responseId, turnId = responseTurn }, lifetime);
        if (!generationTask.IsCompleted) retired.Add(generationTask);
        retired.RemoveAll(task => task.IsCompletedSuccessfully);
        if (retired.Count > 8) throw new ProviderException("response_overload", "Too many pending responses. Reconnect.");
        var token = generation.Token;
        var route = responseRoute;
        generationTask = Task.Run(() => GenerateAsync(number, history, route, prefetched, scope, token, lifetime));
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

    private async Task GenerateAsync(int number, ConversationTurn[] history, string route,
        Task<RetrievalOutcome>? prefetched, KoreanScope? scope, CancellationToken cancellation, CancellationToken lifetime)
    {
        try
        {
            if (route == "profile")
            {
                cancellation.ThrowIfCancellationRequested();
                var introduction = ProfileIntroduction.Compose(options);
                StartKorean(scope, 0, introduction, new TranslationContext(options.Topic, options.Phrases), lifetime);
                await events.Writer.WriteAsync(new("delta", introduction, number), cancellation);
                await events.Writer.WriteAsync(new("complete",
                    new Completion(introduction, [introduction], [], "disabled", "profile", false), number), cancellation);
                return;
            }
            // Started before retrieval so the classifier has finished by the time the answer does; it only adds emphasis
            // and never delays or blocks the suggestion. Its failure is observed here so it can never go unhandled.
            var turnSignal = provider.ShouldRespondAsync(history, cancellation);
            _ = turnSignal.ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
            var grounding = route != "knowledge" ? new Grounding("disabled", []) :
                prefetched is not null ? (await prefetched.WaitAsync(cancellation)).RequireGrounding() :
                await MeetingMetrics.MeasureRetrievalAsync(provider,
                    RetrievalQuery.Build(history[^1].Text, history.Take(history.Length - 1).ToArray()), objectId, cancellation);
            var modelResponse = grounding.Status is "disabled" or "grounded" or "no_matches";
            var translation = scope is null ? TranslationContext.Empty
                : new TranslationContext(options.Topic, KoreanTranslation.Terms(options, grounding));
            var text = new StringBuilder();
            string? alternative = null;
            await foreach (var update in provider.AnswerWithSuggestionsAsync(history, grounding, options, route, cancellation).WithCancellation(cancellation))
            {
                cancellation.ThrowIfCancellationRequested();
                var delta = update.Text;
                if (text.Length + delta.Length > 8000) throw new ProviderException("response_limit", "Response exceeded its size limit.");
                text.Append(delta);
                if (update.Alternative is not null) alternative = update.Alternative;
                if (delta.Length > 0)
                    await events.Writer.WriteAsync(new("delta", delta, number, ModelResponse: modelResponse), cancellation);
                // The first line is final as soon as the second begins, so its translation overlaps the alternative.
                if (update.PrimaryDone) StartKorean(scope, 0, text.ToString().Trim(), translation, lifetime);
            }
            cancellation.ThrowIfCancellationRequested();
            if (text.Length == 0) throw new ProviderException("empty_response", "The model returned no text. Please retry.");
            var suggestions = ReplySuggestions.Complete(text.ToString(), alternative);
            for (var index = 0; index < suggestions.Length; index++)
                StartKorean(scope, index, suggestions[index].Trim(), translation, lifetime);
            await events.Writer.WriteAsync(new("complete",
                new Completion(text.ToString(), suggestions,
                    grounding.Documents.Select(item => item.Source).ToArray(), grounding.Status, route, prefetched is not null,
                    await TurnSignalAsync(turnSignal)), number,
                ModelResponse: modelResponse), cancellation);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (!lifetime.IsCancellationRequested)
                await events.Writer.WriteAsync(new("generation.error", new ProviderException("response_timeout", "Response timed out or was cancelled."), number), lifetime);
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
                logger.LogWarning("Meeting response failed with code {Code}, HTTP status {Status}.", error.Code,
                    exception is System.ClientModel.ClientResultException client ? client.Status : 0);
                await events.Writer.WriteAsync(new("generation.error", error, number), lifetime);
            }
        }
    }

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
    private Task ErrorAsync(string code, string message, bool retryable, CancellationToken cancellation) =>
        SendAsync(new { type = "error", code, message, retryable }, cancellation);
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
