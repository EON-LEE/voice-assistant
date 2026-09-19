using Azure.Core;

namespace VoiceAssistant.Api;

internal static class SpeechAuthorization
{
    internal const string Scope = "https://cognitiveservices.azure.com/.default";

    internal static async Task<string> GetAsync(TokenCredential credential, string resourceId, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var token = await credential.GetTokenAsync(new TokenRequestContext([Scope]), cancellation).AsTask().WaitAsync(cancellation);
        cancellation.ThrowIfCancellationRequested();
        return $"aad#{resourceId}#{token.Token}";
    }

    internal static async Task RefreshAsync(TokenCredential credential, string resourceId,
        Action<string> apply, Action<ProviderException> error, CancellationToken cancellation,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        try
        {
            while (true)
            {
                await (delay ?? Task.Delay)(TimeSpan.FromMinutes(5), cancellation);
                var token = await GetAsync(credential, resourceId, cancellation);
                apply(token);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception)
        {
            error(new("speech_authentication_failed", "Speech credentials could not be renewed. Reconnect to continue."));
        }
    }
}
