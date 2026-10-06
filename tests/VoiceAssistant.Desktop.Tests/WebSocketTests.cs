using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using VoiceAssistant.Desktop.Audio;
using VoiceAssistant.Desktop.Protocol;

namespace VoiceAssistant.Desktop.Tests;

public sealed class WebSocketTests
{
    [Fact]
    public async Task RealSocketExchangesV1AudioFragmentsRepliesAndStopWithoutDevices()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serverDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var app = CreateServer();
        app.Map("/api/meeting", async context =>
        {
            try
            {
                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                var start = await Receive(socket, timeout.Token);
                Assert.Equal(WebSocketMessageType.Text, start.Type);
                using (var json = JsonDocument.Parse(start.Bytes))
                {
                    Assert.Equal("session.start", json.RootElement.GetProperty("type").GetString());
                    Assert.Equal(1, json.RootElement.GetProperty("protocolVersion").GetInt32());
                    var audio = json.RootElement.GetProperty("audio");
                    Assert.Equal("pcm_s16le", audio.GetProperty("encoding").GetString());
                    Assert.Equal(16000, audio.GetProperty("sampleRate").GetInt32());
                    Assert.Equal(1, audio.GetProperty("channels").GetInt32());
                }
                await Send(socket, """{"type":"session.ready"}""", timeout.Token);
                var frame = await Receive(socket, timeout.Token);
                Assert.Equal(WebSocketMessageType.Binary, frame.Type);
                Assert.Equal(640, frame.Bytes.Length);
                Assert.All(frame.Bytes, b => Assert.Equal(0, b));
                await Send(socket, """{"type":"transcript.partial","turnId":"t","revision":1,"text":"Can you"}""", timeout.Token);
                await Send(socket, """{"type":"transcript.final","turnId":"t","revision":2,"text":"Can you confirm?"}""", timeout.Token);
                await ReceiveCommand(socket, "response.request", timeout.Token);
                await Send(socket, """{"type":"response.started","responseId":"r","turnId":"t"}""", timeout.Token);
                byte[] delta = Encoding.UTF8.GetBytes("""{"type":"response.delta","responseId":"r","turnId":"t","text":"Yes."}""");
                await socket.SendAsync(delta.AsMemory(0, 10), WebSocketMessageType.Text, false, timeout.Token);
                await socket.SendAsync(delta.AsMemory(10), WebSocketMessageType.Text, true, timeout.Token);
                await Send(socket, """{"type":"response.completed","responseId":"r","turnId":"t","text":"Yes.","sources":[]}""", timeout.Token);
                await ReceiveCommand(socket, "session.stop", timeout.Token);
                serverDone.TrySetResult();
            }
            catch (Exception ex) { serverDone.TrySetException(ex); }
        });
        await app.StartAsync(timeout.Token);
        var client = new MeetingClient(new TicketedMeetingTransport());
        var final = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var events = new List<ServerEvent>();
        Task run = client.RunAsync(Settings(app), () => new SyntheticAudioSource(), message =>
        {
            events.Add(message);
            if (message.Type == "transcript.final") final.TrySetResult();
            if (message.Type == "response.completed") completed.TrySetResult();
        }, _ => { }, timeout.Token);
        try
        {
            await final.Task.WaitAsync(timeout.Token);
            await client.RequestResponseAsync(timeout.Token);
            await completed.Task.WaitAsync(timeout.Token);
            await client.StopAsync();
            await run;
            await serverDone.Task.WaitAsync(timeout.Token);
            Assert.Contains(events, e => e.Type == "response.delta" && e.Text == "Yes.");
        }
        finally { timeout.Cancel(); await Observe(run); }
    }

    [Theory]
    [InlineData("oversized")]
    [InlineData("binary")]
    [InlineData("disconnect")]
    public async Task SocketFailuresAreExplicit(string failure)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var app = CreateServer();
        app.Map("/api/meeting", async context =>
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            try
            {
                if (failure == "disconnect")
                    await socket.CloseOutputAsync(WebSocketCloseStatus.InternalServerError, "test disconnect", timeout.Token);
                else
                    await socket.SendAsync(new byte[failure == "oversized" ? 65537 : 2],
                        failure == "binary" ? WebSocketMessageType.Binary : WebSocketMessageType.Text, true, timeout.Token);
            }
            catch (WebSocketException) { }
        });
        await app.StartAsync(timeout.Token);
        await using var transport = new TicketedMeetingTransport();
        await transport.ConnectAsync(Settings(app), timeout.Token);
        if (failure == "disconnect")
            await Assert.ThrowsAsync<IOException>(() => transport.ReceiveAsync(timeout.Token));
        else
            await Assert.ThrowsAsync<InvalidDataException>(() => transport.ReceiveAsync(timeout.Token));
    }

    internal static async Task ExerciseBackend(string endpoint)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var transcript = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource<ServerEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new MeetingClient(new TicketedMeetingTransport());
        Task run = client.RunAsync(new() { Mode = ConnectionMode.Development, Endpoint = endpoint },
            () => new SyntheticAudioSource(), e =>
            {
                if (e.Type == "transcript.final") transcript.TrySetResult();
                if (e.Type == "response.completed") completed.TrySetResult(e);
            }, _ => { }, timeout.Token);
        try
        {
            await transcript.Task.WaitAsync(timeout.Token);
            await client.RequestResponseAsync(timeout.Token);
            Assert.False(string.IsNullOrWhiteSpace((await completed.Task.WaitAsync(timeout.Token)).Text));
            await client.StopAsync();
            await run;
        }
        finally { timeout.Cancel(); await Observe(run); }
    }

    private static async Task Observe(Task task)
    {
        try { await task; }
        catch (OperationCanceledException) { }
    }

    private static WebApplication CreateServer()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.UseWebSockets();
        return app;
    }

    private static ClientSettings Settings(WebApplication app) =>
        new()
        {
            Mode = ConnectionMode.Development,
            Endpoint = app.Urls.Single().Replace("http:", "ws:") + "/api/meeting",
            Origin = "http://localhost:5173"
        };

    private static Task Send(WebSocket socket, string text, CancellationToken token) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, token);

    private static async Task ReceiveCommand(WebSocket socket, string command, CancellationToken token)
    {
        while (true)
        {
            var message = await Receive(socket, token);
            if (message.Type == WebSocketMessageType.Binary) continue;
            using var document = JsonDocument.Parse(message.Bytes);
            Assert.Equal(command, document.RootElement.GetProperty("type").GetString());
            return;
        }
    }

    private static async Task<(WebSocketMessageType Type, byte[] Bytes)> Receive(WebSocket socket, CancellationToken token)
    {
        byte[] bytes = new byte[4096];
        var result = await socket.ReceiveAsync(bytes, token);
        Assert.True(result.EndOfMessage);
        return (result.MessageType, bytes[..result.Count]);
    }
}

public sealed class ExternalBackendFactAttribute : FactAttribute
{
    public ExternalBackendFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VOICE_ASSISTANT_TEST_ENDPOINT")))
            Skip = "Set VOICE_ASSISTANT_TEST_ENDPOINT to an explicit loopback fake backend to run.";
    }
}

public sealed class ExternalBackendTests
{
    [ExternalBackendFact]
    [Trait("Category", "BackendE2E")]
    public Task SyntheticAudioToActualBackend() =>
        WebSocketTests.ExerciseBackend(Environment.GetEnvironmentVariable("VOICE_ASSISTANT_TEST_ENDPOINT")!);
}
