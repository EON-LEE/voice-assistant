using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Xunit;

namespace VoiceAssistant.Api.Tests;

public sealed partial class ApiTests
{
    [Fact]
    public async Task EveryResponseCarriesSecurityHeadersAndDevelopmentNeverSendsHsts()
    {
        await using var host = await Host.StartAsync();
        using var client = host.Client;
        foreach (var path in new[] { "/health/live", "/api/client-config", "/no-such-path" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Add("X-Forwarded-Proto", "https");
            using var response = await client.SendAsync(request);
            AssertSecurityHeaders(response);
            Assert.False(response.Headers.Contains("Strict-Transport-Security"));
        }
    }

    [Fact]
    public async Task ProductionSendsHstsOnlyForRequestsThatArrivedOverHttps()
    {
        await using var host = await Host.StartAsync(azure: true, environment: "Production");
        using var client = host.Client;
        using (var plain = await client.GetAsync("/health/live"))
        {
            AssertSecurityHeaders(plain);
            Assert.False(plain.Headers.Contains("Strict-Transport-Security"));
        }
        foreach (var forwardedProto in new[] { "https", "HTTPS, http" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
            request.Headers.Add("X-Forwarded-Proto", forwardedProto);
            using var forwarded = await client.SendAsync(request);
            AssertSecurityHeaders(forwarded);
            Assert.Equal("max-age=31536000; includeSubDomains", HeaderValue(forwarded, "Strict-Transport-Security"));
        }
        using var downgraded = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        downgraded.Headers.Add("X-Forwarded-Proto", "http");
        using var notHttps = await client.SendAsync(downgraded);
        Assert.False(notHttps.Headers.Contains("Strict-Transport-Security"));
    }

    [Theory]
    [InlineData(null, 7)]
    [InlineData("30", 30)]
    public async Task DemoTokenLastsSevenDaysByDefaultAndAtMostThirty(string? days, int expected)
    {
        await using var host = await Host.StartAsync(azure: true, configuration: DemoConfiguration(days));
        using var client = host.Client;
        using var response = await client.PostAsync("/api/demo/login",
            new StringContent("""{"username":"test","password":"test"}""", Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();
        var token = new JwtSecurityTokenHandler().ReadJwtToken(
            (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!);
        Assert.Equal(TimeSpan.FromDays(expected), token.ValidTo - token.ValidFrom);
    }

    [Theory]
    [InlineData("31")]
    [InlineData("90")]
    [InlineData("0")]
    [InlineData("seven")]
    public async Task DemoTokenLifetimeOutsideOneToThirtyDaysPreventsStartup(string days) =>
        await Assert.ThrowsAsync<InvalidOperationException>(() => Host.StartAsync(azure: true, configuration: DemoConfiguration(days)));

    private static Dictionary<string, string?> DemoConfiguration(string? days)
    {
        var configuration = new Dictionary<string, string?>
        {
            ["DemoLogin:Username"] = "test", ["DemoLogin:Password"] = "test", ["DemoLogin:ObjectId"] = ObjectId,
            ["DemoLogin:SigningKey"] = Convert.ToBase64String(Enumerable.Range(1, 48).Select(i => (byte)i).ToArray())
        };
        if (days is not null) configuration["DemoLogin:TokenDays"] = days;
        return configuration;
    }

    private static string HeaderValue(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? string.Join(", ", values) : "";

    private static void AssertSecurityHeaders(HttpResponseMessage response)
    {
        Assert.Equal("nosniff", HeaderValue(response, "X-Content-Type-Options"));
        Assert.Equal("no-referrer", HeaderValue(response, "Referrer-Policy"));
        Assert.Equal("camera=(), geolocation=(), microphone=(self), display-capture=(self)", HeaderValue(response, "Permissions-Policy"));
        Assert.Equal("frame-ancestors 'self'; object-src 'none'; base-uri 'self'", HeaderValue(response, "Content-Security-Policy"));
        Assert.Equal("no-store", HeaderValue(response, "Cache-Control"));
    }
}
