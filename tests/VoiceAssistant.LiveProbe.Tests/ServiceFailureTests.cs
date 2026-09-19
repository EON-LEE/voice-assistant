using System.ClientModel;
using System.Text.Json;
using VoiceAssistant.LiveProbe;
using Xunit;

namespace VoiceAssistant.LiveProbe.Tests;

public sealed class ServiceFailureTests
{
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
