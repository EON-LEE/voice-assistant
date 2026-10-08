using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace VoiceAssistant.Api;

/// <summary>
/// Optional shared demo login: one configured username/password issues a signed bearer token for one fixed
/// owner identity. It is an alternative to Entra for demos; Entra tokens remain accepted.
/// </summary>
public sealed class DemoLoginSettings
{
    public const string Scheme = "DemoLogin";
    public const string Issuer = "voice-assistant-demo";
    public string Username { get; init; } = "";
    public string Password { get; init; } = "";
    public string ObjectId { get; init; } = "";
    public string SigningKey { get; init; } = "";
    public int TokenDays { get; init; } = 30;
    public bool Enabled => Username.Length > 0;

    public static DemoLoginSettings Read(IConfiguration config)
    {
        var settings = new DemoLoginSettings
        {
            Username = config["DemoLogin:Username"] ?? "",
            Password = config["DemoLogin:Password"] ?? "",
            ObjectId = config["DemoLogin:ObjectId"] ?? "",
            SigningKey = config["DemoLogin:SigningKey"] ?? "",
            TokenDays = int.TryParse(config["DemoLogin:TokenDays"], out var days) ? days : 30
        };
        if (!settings.Enabled) return settings;
        if (settings.Password.Length == 0 || !Guid.TryParseExact(settings.ObjectId, "D", out _) ||
            KeyBytes(settings.SigningKey) is not { Length: >= 32 } || settings.TokenDays is < 1 or > 90)
            throw new InvalidOperationException(
                "DemoLogin requires Username, Password, a GUID ObjectId, a base64 SigningKey of at least 32 bytes and TokenDays 1..90.");
        return settings;
    }

    internal SymmetricSecurityKey Key() => new(KeyBytes(SigningKey)!);

    private static byte[]? KeyBytes(string value)
    {
        try { return Convert.FromBase64String(value); }
        catch (FormatException) { return null; }
    }

    internal bool Matches(string username, string password) =>
        FixedEquals(username, Username) & FixedEquals(password, Password);

    private static bool FixedEquals(string actual, string expected) =>
        CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(actual)),
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)));

    public static void Configure(JwtBearerOptions options, DemoLoginSettings demo)
    {
        options.MapInboundClaims = false;
        options.IncludeErrorDetails = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true, ValidIssuer = Issuer,
            ValidateAudience = true, ValidAudience = Issuer,
            ValidateLifetime = true, ValidateIssuerSigningKey = true, RequireSignedTokens = true,
            IssuerSigningKey = demo.Key(), ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            ClockSkew = TimeSpan.FromSeconds(30)
        };
    }
}

/// <summary>Per-client-address login attempt limiter (10 failures per 10 minutes).</summary>
public sealed class DemoLoginLimiter(TimeProvider clock)
{
    private const int MaxFailures = 10;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(10);
    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> failures = new();

    public bool Blocked(string client)
    {
        if (!failures.TryGetValue(client, out var queue)) return false;
        lock (queue) { Prune(queue); return queue.Count >= MaxFailures; }
    }

    public void Fail(string client)
    {
        if (failures.Count > 10000) failures.Clear();
        var queue = failures.GetOrAdd(client, _ => new());
        lock (queue) { Prune(queue); queue.Enqueue(clock.GetUtcNow()); }
    }

    private void Prune(Queue<DateTimeOffset> queue)
    {
        var cutoff = clock.GetUtcNow() - Window;
        while (queue.Count > 0 && queue.Peek() < cutoff) queue.Dequeue();
    }
}

public static class DemoLoginEndpoints
{
    public static void MapDemoLogin(this WebApplication app, ServiceSettings settings, DemoLoginSettings demo)
    {
        if (!demo.Enabled) return;
        app.MapPost("/api/demo/login", async (HttpContext context, DemoLoginLimiter limiter, TimeProvider clock) =>
        {
            if (!OriginPolicy.Allows(context, settings)) return Results.StatusCode(403);
            var client = ClientAddress(context);
            if (limiter.Blocked(client))
            {
                context.Response.Headers.RetryAfter = "600";
                return Results.Json(new { error = "busy", message = "Too many failed sign-in attempts. Try again later." }, statusCode: 429);
            }
            string username, password;
            try
            {
                if (context.Request.ContentLength is > 1024) return Results.StatusCode(413);
                using var document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
                username = document.RootElement.GetProperty("username").GetString() ?? "";
                password = document.RootElement.GetProperty("password").GetString() ?? "";
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
            { return Results.Json(new { error = "invalid_request", message = "Enter a username and password." }, statusCode: 400); }
            if (username.Length > 100 || password.Length > 200 || !demo.Matches(username, password))
            {
                limiter.Fail(client);
                return Results.Json(new { error = "invalid_login", message = "The username or password is incorrect." }, statusCode: 401);
            }
            var now = clock.GetUtcNow().UtcDateTime;
            var expires = now.AddDays(demo.TokenDays);
            var token = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
                DemoLoginSettings.Issuer, DemoLoginSettings.Issuer,
                [new Claim("oid", demo.ObjectId), new Claim("scp", "Meeting.Access"), new Claim("name", demo.Username)],
                now, expires, new SigningCredentials(demo.Key(), SecurityAlgorithms.HmacSha256)));
            return Results.Ok(new { token, username = demo.Username, expiresAt = new DateTimeOffset(expires) });
        });
    }

    private static string ClientAddress(HttpContext context)
    {
        // Azure Container Apps ingress appends the client address to X-Forwarded-For.
        var forwarded = context.Request.Headers["X-Forwarded-For"].ToString().Split(',').Select(value => value.Trim())
            .LastOrDefault(value => IPAddress.TryParse(value, out _));
        return forwarded ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }
}
