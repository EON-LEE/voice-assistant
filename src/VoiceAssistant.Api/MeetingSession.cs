using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace VoiceAssistant.Api;

public sealed class SessionSlots
{
    private readonly HashSet<string> active = new(StringComparer.Ordinal);
    public IDisposable? TryAcquire(string objectId)
    {
        lock (active)
        {
            if (active.Count >= 100 || !active.Add(objectId)) return null;
            return new Lease(() => { lock (active) active.Remove(objectId); });
        }
    }
    private sealed class Lease(Action release) : IDisposable
    {
        public void Dispose() => release();
    }
}

public sealed class MeetingSession(WebSocket socket, IMeetingProvider provider, string objectId, ILogger logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly Channel<SessionEvent> events = Channel.CreateBounded<SessionEvent>(
        new BoundedChannelOptions(64) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly List<ConversationTurn> conversation = [];
    private CancellationTokenSource? generation;
    private Task generationTask = Task.CompletedTask;
    private string? responseId;
    private string? responseTurn;
    private string? lastTurn;
    private int generationNumber;
    private bool responseActive;
    private int overflow;
    private readonly List<Task> retired = [];
    private sealed record SessionEvent(string Kind, object? Value = null, int Generation = 0);
    private sealed record AudioMessage(byte[] Bytes);
    private sealed record WireMessage(byte[] Bytes, WebSocketMessageType Type);
    private sealed record Completion(string Text, Source[] Sources, string Grounding);

    public async Task RunAsync(CancellationToken requestAborted)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        lifetime.CancelAfter(TimeSpan.FromMinutes(30));
        ISpeechStream? speech = null;
        Task receiver = Task.CompletedTask;
        try
        {
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            startup.CancelAfter(TimeSpan.FromSeconds(15));
            var start = await ReceiveAsync(startup.Token);
            if (start is null || start.Type != WebSocketMessageType.Text || !IsValidStart(start.Bytes))
                throw new ProviderException("invalid_start", "First message must be protocol v1 session.start with PCM16LE 16 kHz mono audio.");
            speech = await provider.StartSpeechAsync(
                transcript => EnqueueCallback(new("transcript", transcript), lifetime),
                error => EnqueueCallback(new("fatal", error), lifetime), startup.Token);
            await SendAsync(new { type = "session.ready", protocolVersion = 1 }, lifetime.Token);
            receiver = ReceiveLoopAsync(lifetime.Token);
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
                        if (transcript.Final)
                        {
                            conversation.Add(new(transcript.Text));
                            while (conversation.Count > 12 || conversation.Sum(turn => turn.Text.Length) > 24000) conversation.RemoveAt(0);
                            lastTurn = transcript.TurnId;
                            await StartResponseAsync(lifetime.Token);
                        }
                        else if (responseActive && transcript.TurnId != responseTurn)
                            await CancelResponseAsync(lifetime.Token);
                        break;
                    case "response.request":
                        if (lastTurn is null) await ErrorAsync("no_transcript", "Wait for a finalized utterance.", true, lifetime.Token);
                        else await StartResponseAsync(lifetime.Token);
                        break;
                    case "response.cancel":
                        await CancelResponseAsync(lifetime.Token);
                        break;
                    case "session.stop":
                        await CancelResponseAsync(lifetime.Token);
                        return;
                    case "closed": return;
                    case "fatal": throw (ProviderException)message.Value!;
                    case "delta" when message.Generation == generationNumber && responseActive:
                        await SendAsync(new { type = "response.delta", responseId, turnId = responseTurn, text = (string)message.Value! }, lifetime.Token);
                        break;
                    case "complete" when message.Generation == generationNumber && responseActive:
                        var completion = (Completion)message.Value!;
                        await SendAsync(new
                        {
                            type = "response.completed",
                            responseId,
                            turnId = responseTurn,
                            text = completion.Text,
                            sources = completion.Sources,
                            grounding = completion.Grounding
                        }, lifetime.Token);
                        responseActive = false;
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
                                sources = Array.Empty<Source>(),
                                grounding = "unavailable"
                            }, lifetime.Token);
                            responseActive = false;
                        }
                        else await CancelResponseAsync(lifetime.Token);
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            await TryErrorAsync(overflow != 0 ? "session_overloaded" : "session_ended",
                overflow != 0 ? "Session could not keep up. Reconnect." : "Session ended or reached its time limit.");
        }
        catch (OperationCanceledException)
        {
            await TryErrorAsync("startup_timeout", "Session startup timed out. Reconnect.");
        }
        catch (ProviderException exception) { await TryErrorAsync(exception.Code, exception.Message); }
        catch (WebSocketException) { logger.LogInformation("Meeting transport disconnected."); }
        catch (Exception)
        {
            logger.LogWarning("Meeting provider failed; sensitive error detail suppressed.");
            await TryErrorAsync("provider_unavailable", "The meeting provider is unavailable. Reconnect.");
        }
        finally
        {
            lifetime.Cancel();
            generation?.Cancel();
            events.Writer.TryComplete();
            await ObserveCleanupAsync(receiver);
            await ObserveCleanupAsync(generationTask);
            foreach (var task in retired) await ObserveCleanupAsync(task);
            generation?.Dispose();
            if (speech is not null)
            {
                try { await speech.DisposeAsync(); }
                catch (Exception) { logger.LogWarning("Speech cleanup failed; sensitive error detail suppressed."); }
            }
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var close = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Session ended", close.Token); }
                catch (Exception exception) when (exception is WebSocketException or OperationCanceledException)
                { logger.LogInformation("Meeting transport closed before shutdown completed."); }
            }
        }
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

    private async Task<WireMessage?> ReceiveAsync(CancellationToken cancellation)
    {
        var buffer = new byte[8192];
        using var data = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellation);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            var limit = result.MessageType == WebSocketMessageType.Text ? 4096 : 32768;
            if (data.Length + result.Count > limit) throw new ProviderException("message_limit", "Message exceeds size limit.");
            data.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        return new(data.ToArray(), result.MessageType);
    }

    private async Task StartResponseAsync(CancellationToken lifetime)
    {
        await CancelResponseAsync(lifetime);
        generation?.Dispose();
        generation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        generation.CancelAfter(TimeSpan.FromSeconds(30));
        responseId = Guid.NewGuid().ToString("N");
        responseTurn = lastTurn;
        responseActive = true;
        var number = ++generationNumber;
        await SendAsync(new { type = "response.started", responseId, turnId = responseTurn }, lifetime);
        if (!generationTask.IsCompleted) retired.Add(generationTask);
        retired.RemoveAll(task => task.IsCompletedSuccessfully);
        if (retired.Count > 8) throw new ProviderException("response_overload", "Too many pending responses. Reconnect.");
        generationTask = GenerateAsync(number, conversation.ToArray(), generation.Token, lifetime);
    }

    private async Task CancelResponseAsync(CancellationToken cancellation)
    {
        generation?.Cancel();
        generationNumber++;
        if (!responseActive) return;
        responseActive = false;
        await SendAsync(new { type = "response.cancelled", responseId, turnId = responseTurn }, cancellation);
    }

    private async Task GenerateAsync(int number, ConversationTurn[] history, CancellationToken cancellation, CancellationToken lifetime)
    {
        try
        {
            var grounding = await provider.RetrieveAsync(history[^1].Text, objectId, cancellation);
            var text = new StringBuilder();
            await foreach (var delta in provider.AnswerAsync(history, grounding, cancellation).WithCancellation(cancellation))
            {
                cancellation.ThrowIfCancellationRequested();
                if (text.Length + delta.Length > 8000) throw new ProviderException("response_limit", "Response exceeded its size limit.");
                text.Append(delta);
                await events.Writer.WriteAsync(new("delta", delta, number), cancellation);
            }
            cancellation.ThrowIfCancellationRequested();
            if (text.Length == 0) throw new ProviderException("empty_response", "The model returned no text. Please retry.");
            await events.Writer.WriteAsync(new("complete",
                new Completion(text.ToString(), grounding.Documents.Select(item => item.Source).ToArray(), grounding.Status), number), cancellation);
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
                var error = exception as ProviderException ?? new ProviderException("response_unavailable", "Response service is unavailable. Please retry.");
                await events.Writer.WriteAsync(new("generation.error", error, number), lifetime);
            }
        }
    }

    private async Task SendAsync(object value, CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(value, Json), WebSocketMessageType.Text, true, timeout.Token);
    }
    private Task ErrorAsync(string code, string message, bool retryable, CancellationToken cancellation) =>
        SendAsync(new { type = "error", code, message, retryable }, cancellation);
    private async Task TryErrorAsync(string code, string message)
    {
        if (socket.State != WebSocketState.Open) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await ErrorAsync(code, message, true, timeout.Token); }
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
