using System.Net.WebSockets;
using System.Text;

namespace VoiceAssistant.Desktop.Protocol;

public sealed class TicketedMeetingTransport(AuthenticatedApiClient? api = null) : IMeetingTransport
{
    private readonly ClientWebSocket socket = new();
    private readonly SemaphoreSlim sendLock = new(1, 1);

    public async Task ConnectAsync(ClientSettings settings, CancellationToken token)
    {
        var endpoint = settings.Mode == ConnectionMode.Production
            ? await (api ?? throw new InvalidOperationException("Production transport requires the authenticated API client."))
                .GetMeetingSocketAsync(token)
            : settings.Validate();
        socket.Options.SetRequestHeader("Origin", settings.Origin);
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
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
        var bytes = new byte[limit];
        var count = 0;
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
        catch (WebSocketException) { }
        catch (OperationCanceledException) { }
        finally { socket.Dispose(); sendLock.Dispose(); }
    }
}
