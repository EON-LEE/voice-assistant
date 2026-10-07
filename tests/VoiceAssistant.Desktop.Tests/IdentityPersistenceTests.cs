using System.Reflection;
using System.Text;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;
using VoiceAssistant.Desktop.Protocol;

namespace VoiceAssistant.Desktop.Tests;

public sealed class IdentityPersistenceTests
{
    [Fact]
    public async Task CacheSurvivesNewIdentityAndIsEncryptedOutsidePortableFolder()
    {
        var settings = new ClientSettings
        {
            Mode = ConnectionMode.Production,
            ClientId = Guid.NewGuid().ToString("D")
        };
        var directory = CacheDirectory(settings);
        Assert.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), directory);
        Assert.DoesNotContain(AppContext.BaseDirectory, directory);
        var filename = Path.Combine(directory, "msal-cache.bin");
        var properties = new StorageCreationPropertiesBuilder("msal-cache.bin", directory).Build();
        var helper = await MsalCacheHelper.CreateAsync(properties);
        try
        {
            helper.VerifyPersistence();
            var clientInfo = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                $"{{\"uid\":\"test\",\"utid\":\"{settings.TenantId}\"}}")).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var data = Encoding.UTF8.GetBytes($$$"""
                {"Account":{"test.{{{settings.TenantId}}}-login.microsoftonline.com-{{{settings.TenantId}}}":{
                "home_account_id":"test.{{{settings.TenantId}}}",
                "environment":"login.microsoftonline.com","realm":"{{{settings.TenantId}}}",
                "local_account_id":"test","username":"cache-test@example.invalid","authority_type":"MSSTS",
                "client_info":"{{{clientInfo}}}"} },
                "RefreshToken":{"test.{{{settings.TenantId}}}-login.microsoftonline.com-refreshtoken-{{{settings.ClientId}}}--":{
                "home_account_id":"test.{{{settings.TenantId}}}","environment":"login.microsoftonline.com",
                "credential_type":"RefreshToken","client_id":"{{{settings.ClientId}}}",
                "secret":"synthetic-test-only-not-a-real-token"} } }
                """);
            helper.SaveUnencryptedTokenCache(data);
            var disk = await File.ReadAllBytesAsync(filename);
            Assert.NotEqual(data, disk);
            Assert.DoesNotContain("cache-test@example.invalid", Encoding.UTF8.GetString(disk));
            var reopened = await MsalCacheHelper.CreateAsync(properties);
            Assert.Equal(data, reopened.LoadUnencryptedTokenCache());

            for (int i = 0; i < 2; i++)
            {
                var identity = new NativeIdentity(settings);
                await (Task)typeof(NativeIdentity).GetMethod("EnsureCacheAsync",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(identity, [CancellationToken.None])!;
                var application = (IPublicClientApplication)typeof(NativeIdentity).GetField("application",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(identity)!;
                var account = Assert.Single(await application.GetAccountsAsync());
                Assert.Equal("cache-test@example.invalid", account.Username);
                var cache = (MsalCacheHelper)typeof(NativeIdentity).GetField("cache",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(identity)!;
                cache.UnregisterCache(application.UserTokenCache);
            }
            Assert.NotEqual(CacheDirectory(settings),
                CacheDirectory(settings with { TenantId = Guid.NewGuid().ToString("D") }));
            Assert.NotEqual(directory, CacheDirectory(settings with { ClientId = Guid.NewGuid().ToString("D") }));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static string CacheDirectory(ClientSettings settings) => (string)typeof(NativeIdentity)
        .GetMethod("CacheDirectory", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [settings])!;
}
