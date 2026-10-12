using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using VoiceAssistant.LiveProbe;
using Xunit;

namespace VoiceAssistant.LiveProbe.Tests;

public sealed class AuthDiagnosticTests
{
    [Theory]
    [InlineData("PSSecurityException secret-token", "powershell_execution_policy")]
    [InlineData("running scripts is disabled secret-token", "powershell_execution_policy")]
    [InlineData("NoAzAccountModule secret-token", "az_accounts_missing_or_unloadable")]
    [InlineData("format.ps1xml secret-token", "powershell_format_load_failed")]
    [InlineData("PowerShell is not installed. secret-token", "powershell_not_found")]
    [InlineData("AADSTS50076 secret-token", "cached_login_unavailable_or_claims_required")]
    [InlineData("timed out secret-token", "authentication_timeout")]
    [InlineData("secret-token", "credential_failed")]
    public void ClassificationIsFixedAndContentFree(string message, string reason)
    {
        Assert.Equal(reason, AuthDiagnostic.Classify(new AuthenticationFailedException(message)));
    }

    [Fact]
    public async Task TokenIsNeverPrintedAndCredentialOnlySuccessIsNotServiceAcceptance()
    {
        using var writer = new StringWriter();
        Assert.Equal(0, await AuthDiagnostic.RunAsync(writer, CancellationToken.None, new Credential()));
        using var json = JsonDocument.Parse(writer.ToString());
        Assert.Equal("AUTHENTICATED", json.RootElement.GetProperty("status").GetString());
        Assert.Equal("TestDouble", json.RootElement.GetProperty("credential").GetString());
        Assert.False(json.RootElement.GetProperty("serviceAcceptanceVerified").GetBoolean());
        Assert.DoesNotContain("secret-token", writer.ToString());
    }

    [Fact]
    public async Task ExceptionContentIsNotPrinted()
    {
        using var writer = new StringWriter();
        Assert.Equal(2, await AuthDiagnostic.RunAsync(writer, CancellationToken.None, new Credential { Fail = true }));
        Assert.Contains("az_accounts_missing_or_unloadable", writer.ToString());
        Assert.DoesNotContain("secret-token", writer.ToString());
    }

    private sealed class Credential : TokenCredential
    {
        public bool Fail { get; init; }
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            if (Fail) throw new CredentialUnavailableException("NoAzAccountModule secret-token");
            return ValueTask.FromResult(new AccessToken("secret-token", DateTimeOffset.UtcNow.AddHours(1)));
        }
    }
}
