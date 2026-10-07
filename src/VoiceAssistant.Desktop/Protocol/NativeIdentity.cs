using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;

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
    private readonly SemaphoreSlim cacheGate = new(1, 1);
    private MsalCacheHelper? cache;

    internal static string CacheDirectory(ClientSettings settings) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VoiceAssistant", "Identity", Guid.Parse(settings.TenantId).ToString("D"),
        Guid.Parse(settings.ClientId).ToString("D"));

    private async Task EnsureCacheAsync(CancellationToken cancellationToken)
    {
        if (cache is not null) return;
        await cacheGate.WaitAsync(cancellationToken);
        try
        {
            if (cache is not null) return;
            var properties = new StorageCreationPropertiesBuilder("msal-cache.bin", CacheDirectory(settings)).Build();
            var helper = await MsalCacheHelper.CreateAsync(properties);
            helper.VerifyPersistence();
            helper.RegisterCache(application.UserTokenCache);
            cache = helper;
        }
        finally { cacheGate.Release(); }
    }

    public bool IsSignedIn => account is not null;

    public async Task SignInAsync(CancellationToken cancellationToken = default)
    {
        await AcquireTokenAsync(true, cancellationToken);
    }

    public async Task SignInWithDeviceCodeAsync(Action<string, string> showCode,
        CancellationToken cancellationToken = default)
    {
        await EnsureCacheAsync(cancellationToken);
        var result = await application.AcquireTokenWithDeviceCode(scopes, code =>
        {
            showCode(code.VerificationUrl, code.UserCode);
            return Task.CompletedTask;
        }).ExecuteAsync(cancellationToken);
        account = result.Account;
    }

    public async Task<string> AcquireTokenAsync(bool interactive, CancellationToken cancellationToken = default)
    {
        await EnsureCacheAsync(cancellationToken);
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
                throw new InvalidOperationException("Microsoft requires authentication again. Restart Live to reconnect your account.");
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
