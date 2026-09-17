using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using Microsoft.Identity.Client;

namespace VoiceAssistant.Desktop.Protocol;

public interface IMeetingTransport : IAsyncDisposable
{
    Task ConnectAsync(ClientSettings settings, CancellationToken token);
    Task SendTextAsync(string json, CancellationToken token);
    Task SendAudioAsync(byte[] pcm, CancellationToken token);
    Task<ServerEvent> ReceiveAsync(CancellationToken token);
}

public sealed class WebSocketMeetingTransport : IMeetingTransport
{
    private readonly ClientWebSocket socket = new();
    private readonly SemaphoreSlim sendLock = new(1, 1);

    public async Task ConnectAsync(ClientSettings settings, CancellationToken token)
    {
        var endpoint = settings.Validate();
        if (settings.Mode == ConnectionMode.Demo)
            throw new InvalidOperationException("Demo mode must use the explicit demo transport.");
        if (settings.Mode == ConnectionMode.Production)
        {
            var application = PublicClientApplicationBuilder.Create(settings.ClientId)
                .WithAuthority($"{settings.Authority.TrimEnd('/')}/{settings.TenantId}")
                .WithRedirectUri("http://localhost")
                .Build();
            var scopes = new[] { settings.Scope };
            var account = (await application.GetAccountsAsync()).FirstOrDefault();
            AuthenticationResult result;
            try { result = await application.AcquireTokenSilent(scopes, account).ExecuteAsync(token); }
            catch (MsalUiRequiredException)
            {
                result = await application.AcquireTokenInteractive(scopes)
                    .WithUseEmbeddedWebView(false).ExecuteAsync(token);
            }
            socket.Options.SetRequestHeader("Authorization", $"Bearer {result.AccessToken}");
        }
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        await socket.ConnectAsync(endpoint, timeout.Token);
    }

    public Task SendTextAsync(string json, CancellationToken token) =>
        SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, token);

    public Task SendAudioAsync(byte[] pcm, CancellationToken token) =>
        SendAsync(pcm, WebSocketMessageType.Binary, token);

    private async Task SendAsync(byte[] bytes, WebSocketMessageType type, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await sendLock.WaitAsync(timeout.Token);
        try { await socket.SendAsync(bytes.AsMemory(), type, true, timeout.Token); }
        finally { sendLock.Release(); }
    }

    public async Task<ServerEvent> ReceiveAsync(CancellationToken token)
    {
        const int limit = 65536;
        byte[] bytes = new byte[limit];
        int count = 0;
        ValueWebSocketReceiveResult result;
        do
        {
            if (count == limit) throw new InvalidDataException("Server event exceeds the 64 KiB message limit.");
            result = await socket.ReceiveAsync(bytes.AsMemory(count), token);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new IOException($"Server disconnected ({socket.CloseStatus}): {socket.CloseStatusDescription}");
            if (result.MessageType != WebSocketMessageType.Text)
                throw new InvalidDataException("Server sent an unexpected binary message.");
            count += result.Count;
        } while (!result.EndOfMessage);
        return ServerEvent.Parse(bytes.AsSpan(0, count));
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (socket.State == WebSocketState.Open)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Session ended", timeout.Token);
            }
        }
        catch (WebSocketException) { /* Peer may already have gone away; the session reports the primary failure. */ }
        catch (OperationCanceledException) { /* Closing is best effort and bounded. */ }
        finally { socket.Dispose(); sendLock.Dispose(); }
    }
}

/// <summary>Explicit offline simulation, never a fallback for live connection failures.</summary>
public sealed class DemoMeetingTransport : IMeetingTransport
{
    private readonly Channel<ServerEvent> events = Channel.CreateBounded<ServerEvent>(64);
    private bool transcriptSent;
    private int responseNumber;

    public Task ConnectAsync(ClientSettings settings, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (settings.Mode != ConnectionMode.Demo) throw new InvalidOperationException("Demo transport requires Demo mode.");
        return Task.CompletedTask;
    }

    public async Task SendTextAsync(string json, CancellationToken token)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);
        switch (document.RootElement.GetProperty("type").GetString())
        {
            case "session.start":
                await events.Writer.WriteAsync(new("session.ready"), token);
                break;
            case "response.request":
                if (!transcriptSent)
                {
                    await events.Writer.WriteAsync(new("error", Text: "Wait for the demo transcript.", Code: "no_transcript", Retryable: true), token);
                    break;
                }
                string id = $"demo-{++responseNumber}";
                await events.Writer.WriteAsync(new("response.started", "demo-turn", ResponseId: id), token);
                await events.Writer.WriteAsync(new("response.delta", "demo-turn", Text: "Yes, I can ", ResponseId: id), token);
                await events.Writer.WriteAsync(new("response.delta", "demo-turn", Text: "share an update by Friday.", ResponseId: id), token);
                await events.Writer.WriteAsync(new("response.completed", "demo-turn",
                    Text: "Yes, I can share an update by Friday.", ResponseId: id, Sources: []), token);
                break;
            case "response.cancel":
                await events.Writer.WriteAsync(new("response.cancelled", "demo-turn", ResponseId: $"demo-{responseNumber}"), token);
                break;
            case "session.stop": break;
            default: throw new InvalidDataException("Unknown demo client command.");
        }
    }

    public async Task SendAudioAsync(byte[] pcm, CancellationToken token)
    {
        if (transcriptSent) return;
        transcriptSent = true;
        await events.Writer.WriteAsync(new("transcript.partial", "demo-turn", 1, "Can you share"), token);
        await events.Writer.WriteAsync(new("transcript.final", "demo-turn", 2, "Can you share an update by Friday?"), token);
    }

    public async Task<ServerEvent> ReceiveAsync(CancellationToken token) => await events.Reader.ReadAsync(token);
    public ValueTask DisposeAsync() { events.Writer.TryComplete(); return ValueTask.CompletedTask; }
}
