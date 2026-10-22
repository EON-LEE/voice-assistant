using System.Text.Json;
using VoiceAssistant.Api;
using Xunit;

namespace VoiceAssistant.Api.Tests;

// "내 입장·메모" (notes): set at the start or through session.update, answered with, and otherwise never repeated anywhere.
public sealed partial class ApiTests
{
    [Fact]
    public async Task NotesAreTakenAtTheStartAndChangedByUpdateWhichSaysOnlyWhetherNotesAreSet()
    {
        var provider = new ControlledProvider { AutomaticRetrieval = true };
        await using var host = await Host.StartAsync(provider: provider);
        using var socket = await host.ConnectAsync();
        await Send(socket, WithOptions(new { responseMode = "conversation", notes = " Prefer plan B.\n\nBudget under 5,000 dollars. " }));
        Assert.Equal("session.ready", TypeOf(await Receive(socket)));
        provider.Emit(new("one", 1, "Which plan should we pick?", true));
        await ReceiveUntilAsync(socket, Completed);
        Assert.Equal("Prefer plan B. Budget under 5,000 dollars.", provider.AnswerOptions!.Notes);

        await Send(socket, UpdateCommand(new { notes = "Do not commit before the third quarter." }));
        var changed = await Until(socket, "session.updated");
        Assert.Equal(JsonValueKind.True, changed.GetProperty("notes").ValueKind);
        Assert.DoesNotContain("quarter", changed.GetRawText(), StringComparison.Ordinal);
        Assert.Equal("conversation", changed.GetProperty("responseMode").GetString());
        Assert.Equal("all", changed.GetProperty("replyOn").GetString());
        provider.Emit(new("two", 1, "Can we commit to a date?", true));
        await ReceiveUntilAsync(socket, Completed);
        Assert.Equal("Do not commit before the third quarter.", provider.AnswerOptions.Notes);

        // An update without notes keeps them; empty notes clear them.
        await Send(socket, UpdateCommand(new { replyOn = "questions" }));
        Assert.Equal(JsonValueKind.True, (await Until(socket, "session.updated")).GetProperty("notes").ValueKind);
        await Send(socket, UpdateCommand(new { notes = "" }));
        Assert.Equal(JsonValueKind.False, (await Until(socket, "session.updated")).GetProperty("notes").ValueKind);
        provider.Emit(new("three", 1, "What do you think about the budget?", true));
        await ReceiveUntilAsync(socket, Completed);
        Assert.Equal("", provider.AnswerOptions.Notes);
        await Send(socket, """{"type":"session.stop"}""");
    }

    [Fact]
    public async Task ASessionThatNeverSentNotesReportsNoneWithAnyOtherChange()
    {
        await using var host = await Host.StartAsync(provider: new ControlledProvider());
        using var socket = await host.ConnectAsync();
        await Send(socket, WithOptions(new { responseMode = "grounded" }));
        await Receive(socket);
        await Send(socket, UpdateCommand(new { responseMode = "conversation" }));
        Assert.Equal(JsonValueKind.False, (await Until(socket, "session.updated")).GetProperty("notes").ValueKind);
        await Send(socket, """{"type":"session.stop"}""");
    }

    [Theory]
    [InlineData("null")]
    [InlineData("5")]
    [InlineData("[\"Prefer plan B.\"]")]
    [InlineData("\"Prefer\\u0007 plan B.\"")]
    public async Task InvalidNotesFailTheStart(string notes)
    {
        await using var host = await Host.StartAsync();
        using var socket = await host.ConnectAsync();
        await Send(socket, """{"type":"session.start","protocolVersion":1,"audio":{"encoding":"pcm_s16le","sampleRate":16000,"channels":1},"options":{"notes":"""
            + notes + "}}");
        Assert.Equal("invalid_start", (await Receive(socket)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task NotesReachOnlyTheAnswerPromptAndAreNeverLoggedMeasuredOrSentBack()
    {
        // Distinctive words, so any copy of the notes outside the answer prompt is found.
        const string first = "Prefer the Zephyr plan; budget at most 5,000 dollars.";
        const string second = "Do not commit Quillon dates before the third quarter.";
        using var capture = new MetricCapture();
        var logs = new CollectingLogs();
        using var transport = new FaultTransport();
        using var http = new HttpClient(transport);
        var provider = new AzureBackedProvider(FaultTransport.Provider(http));
        await using var host = await Host.StartAsync(provider: provider, logs: logs);
        using var socket = await host.ConnectAsync();
        // Search, the turn classifier and the Korean push all run, so each of them could see the notes.
        await Send(socket, WithOptions(new { responseMode = "grounded", replyKorean = true, notes = first }));
        Assert.Equal("session.ready", TypeOf(await Receive(socket)));
        static bool Answered(List<JsonElement> found) => Completed(found) && found.Count(message => TypeOf(message) == "response.korean") == 2;
        provider.Emit(new("one", 1, "Which plan should we choose for the rollout?", true));
        var events = await ReceiveUntilAsync(socket, Answered);
        await Send(socket, UpdateCommand(new { notes = second }));
        events.Add(await Until(socket, "session.updated"));
        provider.Emit(new("two", 1, "When can we commit to the rollout date?", true));
        events.AddRange(await ReceiveUntilAsync(socket, Answered));
        await Send(socket, """{"type":"session.stop"}""");
        events.AddRange(await ReceiveUntilCloseAsync(socket));

        static bool Streamed(string body) => body.Contains("\"stream\":true", StringComparison.Ordinal);
        static bool Mentions(string? text) => text is not null &&
            (text.Contains("Zephyr", StringComparison.Ordinal) || text.Contains("Quillon", StringComparison.Ordinal));
        // Each answer carried the notes of its time, once, in the session context.
        var answers = transport.ChatBodies.Where(Streamed).Select(ParticipantNotesTests.Messages).ToArray();
        Assert.Equal(2, answers.Length);
        Assert.Equal([first, second], answers.Select(messages =>
        {
            var context = Assert.Single(messages, message => Mentions(message.Content)).Content;
            using var json = JsonDocument.Parse(context[context.IndexOf('{')..]);
            return json.RootElement.GetProperty("ParticipantNotes").GetString();
        }));
        // Search, embeddings, the classifier and the translations never saw them.
        Assert.True(transport.RequestBodies.Count(body => !Streamed(body)) >= 6);
        Assert.DoesNotContain(transport.RequestBodies.Where(body => !Streamed(body)), Mentions);
        Assert.Equal(JsonValueKind.True, events.Single(message => TypeOf(message) == "session.updated").GetProperty("notes").ValueKind);
        Assert.DoesNotContain(events, message => Mentions(message.GetRawText()));
        Assert.DoesNotContain(logs.Messages, Mentions);
        Assert.NotEmpty(capture.Measurements);
        Assert.DoesNotContain(capture.Measurements, measurement => measurement.Tags.Any(tag => Mentions(tag.Value?.ToString())));
    }
}
