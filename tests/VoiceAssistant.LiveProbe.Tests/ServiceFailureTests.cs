using System.ClientModel;
using System.Text.Json;
using VoiceAssistant.LiveProbe;
using Xunit;
using VoiceAssistant.Api;
using Microsoft.CognitiveServices.Speech;

namespace VoiceAssistant.LiveProbe.Tests;

public sealed class ServiceFailureTests
{
    [Theory]
    [InlineData(CancellationReason.Error, CancellationErrorCode.AuthenticationFailure, "Error", "AuthenticationFailure")]
    [InlineData(CancellationReason.Error, CancellationErrorCode.Forbidden, "Error", "Forbidden")]
    [InlineData(CancellationReason.EndOfStream, CancellationErrorCode.NoError, "EndOfStream", "NoError")]
    [InlineData((CancellationReason)999, (CancellationErrorCode)999, "Unknown", "Unknown")]
    public void SpeechCancellationExposesOnlyDefinedEnumNames(CancellationReason reason, CancellationErrorCode code,
        string expectedReason, string expectedCode)
    {
        var exception = new ProviderException("secret-token", "private transcript https://private.example")
        {
            SpeechCancellation = new(reason, code)
        };
        var failure = ServiceFailure.From(exception, "speech");
        Assert.Equal(expectedReason, failure.SpeechCancellationReason);
        Assert.Equal(expectedCode, failure.SpeechCancellationErrorCode);
        Assert.Null(failure.HttpStatus);
        var json = JsonSerializer.Serialize(failure);
        Assert.DoesNotContain("secret-token", json);
        Assert.DoesNotContain("private", json);
        Assert.Equal("speech_cancelled", failure.Code);
    }

    [Fact]
    public void KnownCodeAndParameterArePreservedWithoutBodyContent()
    {
        var failure = ServiceFailure.Parse(400, BinaryData.FromString("""
            {"error":{"code":"unsupported_parameter","param":"max_tokens","message":"secret-token private-corporate-text"}}
            """), "openai");
        Assert.Equal(400, failure.HttpStatus);
        Assert.Equal("unsupported_parameter", failure.Code);
        Assert.Equal("max_tokens", failure.Parameter);
        Assert.DoesNotContain("secret-token", JsonSerializer.Serialize(failure));
        Assert.DoesNotContain("private-corporate-text", JsonSerializer.Serialize(failure));
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(429)]
    [InlineData(500)]
    public void HttpStatusIsKeptButUntrustedFieldsNeverPassThrough(int status)
    {
        var failure = ServiceFailure.Parse(status, BinaryData.FromString("""
            {"error":{"code":"secret-token","param":"private-company-field","message":"private"}}
            """), "openai");
        Assert.Equal(status, failure.HttpStatus);
        Assert.Equal("unknown", failure.Code);
        Assert.Null(failure.Parameter);
        Assert.DoesNotContain("secret-token", JsonSerializer.Serialize(failure));
    }

    [Fact]
    public void InvalidBodiesAndUnexpectedExceptionsAreSafe()
    {
        Assert.Equal("unparseable_error", ServiceFailure.Parse(400, BinaryData.FromString("not-json-secret"), "openai").Code);
        Assert.Equal("unknown", ServiceFailure.Parse(400, BinaryData.FromString(new string('x', 70000)), "openai").Code);
        var failure = ServiceFailure.From(new ClientResultException("secret-token"), "openai");
        Assert.DoesNotContain("secret-token", JsonSerializer.Serialize(failure));
    }
}
