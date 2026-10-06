using System.Text;
using System.Threading.Channels;

namespace VoiceAssistant.Desktop.Protocol;

public interface IMeetingTransport : IAsyncDisposable
{
    Task ConnectAsync(ClientSettings settings, CancellationToken token);
    Task SendTextAsync(string json, CancellationToken token);
    Task SendAudioAsync(byte[] pcm, CancellationToken token);
    Task<ServerEvent> ReceiveAsync(CancellationToken token);
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
            case "session.stop":
                break;
            default:
                throw new InvalidDataException("Unknown demo client command.");
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
