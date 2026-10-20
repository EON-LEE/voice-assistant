using Azure.Core;

namespace VoiceAssistant.Api;

internal static class SpeechAuthorization
{
    internal const string Scope = "https://cognitiveservices.azure.com/.default";
    /// <summary>How often the recognizer's token is renewed while the session runs.</summary>
    internal static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);
    /// <summary>How soon a failed renewal is tried again while the current token is still valid.</summary>
    internal static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(30);

    internal static async Task<string> GetAsync(TokenCredential credential, string resourceId, CancellationToken cancellation) =>
        (await AcquireAsync(credential, resourceId, cancellation)).Authorization;

    /// <summary>The Speech authorization value and when its token expires.</summary>
    internal static async Task<(string Authorization, DateTimeOffset ExpiresOn)> AcquireAsync(TokenCredential credential, string resourceId,
        CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var token = await credential.GetTokenAsync(new TokenRequestContext([Scope]), cancellation).AsTask().WaitAsync(cancellation);
        cancellation.ThrowIfCancellationRequested();
        return ($"aad#{resourceId}#{token.Token}", token.ExpiresOn);
    }

    /// <summary>
    /// Renews the token every <see cref="RefreshInterval"/>. A failed renewal does not end the session while the current
    /// token is still valid: it is tried again after <see cref="RetryInterval"/> (never later than the expiry), and only a
    /// token that actually expired without a successful renewal reports <c>speech_authentication_failed</c>.
    /// Without a known expiry, a failed renewal reports at once.
    /// </summary>
    internal static async Task RefreshAsync(TokenCredential credential, string resourceId,
        Action<string> apply, Action<ProviderException> error, CancellationToken cancellation,
        Func<TimeSpan, CancellationToken, Task>? delay = null, DateTimeOffset? expiresOn = null, TimeProvider? clock = null)
    {
        var time = clock ?? TimeProvider.System;
        var wait = delay ?? ((duration, token) => Task.Delay(duration, time, token));
        var validUntil = expiresOn ?? DateTimeOffset.MinValue;
        var next = RefreshInterval;
        try
        {
            while (true)
            {
                await wait(next, cancellation);
                try
                {
                    var token = await AcquireAsync(credential, resourceId, cancellation);
                    apply(token.Authorization);
                    validUntil = token.ExpiresOn;
                    next = RefreshInterval;
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
                catch (Exception)
                {
                    var remaining = validUntil - time.GetUtcNow();
                    if (remaining <= TimeSpan.Zero)
                    {
                        error(new("speech_authentication_failed", "Speech credentials could not be renewed. Reconnect to continue."));
                        return;
                    }
                    next = remaining < RetryInterval ? remaining : RetryInterval;
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
    }
}
