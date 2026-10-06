using Microsoft.Identity.Client;

namespace VoiceAssistant.Desktop.Protocol;

public interface IAccessTokenProvider
{
    Task<string> AcquireTokenAsync(bool interactive, CancellationToken cancellationToken = default);
}

public sealed class NativeIdentity(ClientSettings settings) : IAccessTokenProvider
{
    private readonly IPublicClientApplication application = PublicClientApplicationBuilder.Create(settings.ClientId)
        .WithAuthority($"{settings.Authority.TrimEnd('/')}/{settings.TenantId}")
        .WithRedirectUri("http://localhost")
        .Build();
    private readonly string[] scopes = [settings.Scope];
    private IAccount? account;

    public bool IsSignedIn => account is not null;

    public async Task SignInAsync(CancellationToken cancellationToken = default)
    {
        await AcquireTokenAsync(true, cancellationToken);
    }

    public async Task SignInWithDeviceCodeAsync(Action<string, string> showCode,
        CancellationToken cancellationToken = default)
    {
        var result = await application.AcquireTokenWithDeviceCode(scopes, code =>
        {
            showCode(code.VerificationUrl, code.UserCode);
            return Task.CompletedTask;
        }).ExecuteAsync(cancellationToken);
        account = result.Account;
    }

    public async Task<string> AcquireTokenAsync(bool interactive, CancellationToken cancellationToken = default)
    {
        account ??= (await application.GetAccountsAsync()).FirstOrDefault();
        if (account is not null)
        {
            try
            {
                return (await application.AcquireTokenSilent(scopes, account).ExecuteAsync(cancellationToken)).AccessToken;
            }
            catch (MsalUiRequiredException) when (!interactive)
            {
                account = null;
                throw new InvalidOperationException("Sign-in expired. Select Sign in and authenticate again.");
            }
            catch (MsalUiRequiredException) { account = null; }
        }
        if (!interactive) throw new InvalidOperationException("Sign in before starting a meeting, practice, or materials request.");
        var result = await application.AcquireTokenInteractive(scopes)
            .WithUseEmbeddedWebView(false)
            .ExecuteAsync(cancellationToken);
        account = result.Account;
        return result.AccessToken;
    }
}
