using System.Text.Json;
using VoiceAssistant.Api;
using Xunit;

namespace VoiceAssistant.Api.Tests;

public sealed partial class ApiTests
{
    [Fact]
    public async Task PrimaryDoneComesOnceAsAnEmptyDeltaWhenLineOneEndsAfterItsText()
    {
        var provider = new ControlledProvider { AutomaticRetrieval = true, Alternative = "Let's check the next step." };
        var (deltas, completed) = await PrimaryStreamAsync(provider);
        AssertPrimaryDeltas(deltas, completed);
        // The test provider reports the end of line 1 only after its text, so the marker rides on an empty delta.
        Assert.Equal("", deltas[^1].GetProperty("text").GetString());
        Assert.Equal(2, completed.GetProperty("suggestions").GetArrayLength());
    }

    [Fact]
    public async Task SingleLineReplyMarksPrimaryDoneWhenTheStreamEnds()
    {
        var provider = new ControlledProvider { AutomaticRetrieval = true };
        var (deltas, completed) = await PrimaryStreamAsync(provider);
        AssertPrimaryDeltas(deltas, completed);
        Assert.Equal("", deltas[^1].GetProperty("text").GetString());
        Assert.Single(completed.GetProperty("suggestions").EnumerateArray());
    }

    [Fact]
    public async Task PrimaryDoneRidesOnTheDeltaThatEndsLineOneInTheAzureStream()
    {
        using var transport = new FaultTransport { ChatChunks = ["A grounded reply", " for Friday.\nThe review", " is on Friday."] };
        var messages = await AnswerThroughAzureProviderAsync(transport);
        var deltas = messages.Where(message => TypeOf(message) == "response.delta").ToArray();
        var completed = messages[^1];
        AssertPrimaryDeltas(deltas, completed);
        Assert.Equal(["A grounded reply", " for Friday."], deltas.Select(delta => delta.GetProperty("text").GetString()));
        Assert.False(deltas[0].TryGetProperty("primaryDone", out _));
        Assert.Equal("grounded", completed.GetProperty("grounding").GetString());
        Assert.Equal(["A grounded reply for Friday.", "The review is on Friday."],
            completed.GetProperty("suggestions").EnumerateArray().Select(suggestion => suggestion.GetString()));
    }

    [Fact]
    public async Task ProfileReplyMarksItsSingleDeltaAsPrimaryDone()
    {
        var provider = new ControlledProvider();
        await using var host = await Host.StartAsync(provider: provider);
        using var socket = await host.ConnectAsync();
        await Send(socket, WithOptions(new
        {
            responseMode = "balanced", profileConfirmed = true, profile = new { name = "Mina", role = "Software engineer" }
        }));
        await Receive(socket);
        provider.Emit(new("intro", 1, "Could you introduce yourself?", true));
        var messages = await ReceiveUntilAsync(socket, Completed);
        var delta = Assert.Single(messages, message => TypeOf(message) == "response.delta");
        Assert.True(delta.GetProperty("primaryDone").GetBoolean());
        Assert.Equal(messages[^1].GetProperty("text").GetString(), delta.GetProperty("text").GetString());
        await Send(socket, """{"type":"session.stop"}""");
    }

    private static async Task<(JsonElement[] Deltas, JsonElement Completed)> PrimaryStreamAsync(ControlledProvider provider)
    {
        await using var host = await Host.StartAsync(provider: provider);
        using var socket = await host.ConnectAsync();
        await Send(socket, WithOptions(new { responseMode = "grounded" }));
        await Receive(socket);
        provider.Emit(new("q", 1, "What is the next step for the rollout?", true));
        var messages = await ReceiveUntilAsync(socket, Completed);
        await Send(socket, """{"type":"session.stop"}""");
        return (messages.Where(message => TypeOf(message) == "response.delta").ToArray(), messages[^1]);
    }

    private static bool IsPrimaryDone(JsonElement delta) => delta.TryGetProperty("primaryDone", out var flag) && flag.GetBoolean();

    /// <summary>Exactly one primaryDone, on the last delta; only it may be empty; the deltas spell the completed text.</summary>
    private static void AssertPrimaryDeltas(JsonElement[] deltas, JsonElement completed)
    {
        Assert.NotEmpty(deltas);
        Assert.Single(deltas, IsPrimaryDone);
        Assert.True(IsPrimaryDone(deltas[^1]));
        Assert.All(deltas, delta => Assert.True(delta.GetProperty("text").GetString()!.Length > 0 || IsPrimaryDone(delta)));
        Assert.Equal(completed.GetProperty("text").GetString(), string.Concat(deltas.Select(delta => delta.GetProperty("text").GetString())));
        Assert.Equal(completed.GetProperty("text").GetString(), completed.GetProperty("suggestions")[0].GetString());
    }
}
