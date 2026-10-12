using System.Text.Json;
using Azure.Core;
using Azure.Identity;

namespace VoiceAssistant.LiveProbe;

internal static class AuthDiagnostic
{
    internal static async Task<int> RunAsync(TextWriter output, CancellationToken cancellation, TokenCredential? credential = null)
    {
        var clock = new ProbeClock();
        var status = "BLOCKED";
        var reason = "credential_failed";
        var category = "none";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        credential ??= new AzurePowerShellCredential(new AzurePowerShellCredentialOptions { ProcessTimeout = TimeSpan.FromSeconds(20) });
        var credentialName = credential switch
        {
            AzurePowerShellCredential => "AzurePowerShellCredential",
            DefaultAzureCredential => "DefaultAzureCredential",
            _ => "TestDouble"
        };
        try
        {
            await credential.GetTokenAsync(new TokenRequestContext(["https://cognitiveservices.azure.com/.default"]), timeout.Token)
                .AsTask().WaitAsync(timeout.Token);
            status = "AUTHENTICATED";
            reason = "token_acquired_no_service_call";
        }
        catch (OperationCanceledException)
        {
            reason = cancellation.IsCancellationRequested ? "cancelled" : "authentication_timeout";
            category = "cancellation";
        }
        catch (Exception exception)
        {
            reason = Classify(exception);
            category = exception switch
            {
                CredentialUnavailableException => "credential_unavailable",
                AuthenticationFailedException => "authentication_failed",
                _ => "unexpected_error"
            };
        }
        await output.WriteLineAsync(JsonSerializer.Serialize(new
        {
            schemaVersion = 1, scope = "authentication_only", credential = credentialName,
            status, reason, category, elapsedMs = clock.ElapsedMs,
            serviceAcceptanceVerified = false
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        return status == "AUTHENTICATED" ? 0 : 2;
    }

    internal static string Classify(Exception exception)
    {
        // Raw SDK messages can contain process output and identity details; only fixed categories leave this boundary.
        var message = exception.Message;
        if (message.Contains("PSSecurityException", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("AuthorizationManagerCheckFailed", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("running scripts is disabled", StringComparison.OrdinalIgnoreCase))
            return "powershell_execution_policy";
        if (message.Contains("NoAzAccountModule", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Az.Accounts module", StringComparison.OrdinalIgnoreCase))
            return "az_accounts_missing_or_unloadable";
        if (message.Contains(".ps1xml", StringComparison.OrdinalIgnoreCase))
            return "powershell_format_load_failed";
        if (message.Contains("PowerShell is not installed", StringComparison.OrdinalIgnoreCase))
            return "powershell_not_found";
        if (message.Contains("timed out", StringComparison.OrdinalIgnoreCase))
            return "authentication_timeout";
        if (message.Contains("Connect-AzAccount", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("AADSTS", StringComparison.OrdinalIgnoreCase))
            return "cached_login_unavailable_or_claims_required";
        return "credential_failed";
    }
}
