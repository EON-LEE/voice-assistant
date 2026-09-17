using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.WebUtilities;

namespace VoiceAssistant.Api;

public static class OriginPolicy
{
    public static bool IsCanonicalHttpsOrigin(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https" &&
        uri.UserInfo.Length == 0 && value == uri.GetLeftPart(UriPartial.Authority);

    public static bool Allows(HttpContext context, ServiceSettings settings)
    {
        var origin = context.Request.Headers.Origin.ToString();
        if (context.Request.Headers["Sec-Fetch-Site"] == "cross-site")
            return false;
        if (!settings.Fake)
            return settings.AllowedOrigins.Contains(origin, StringComparer.Ordinal);
        return context.Connection.RemoteIpAddress is { } remote && IPAddress.IsLoopback(remote) &&
            IsLoopbackHost(context.Request.Host.Host) &&
            Uri.TryCreate(origin, UriKind.Absolute, out var uri) &&
            uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0 &&
            origin == uri.GetLeftPart(UriPartial.Authority) && IsLoopbackHost(uri.Host);
    }

    private static bool IsLoopbackHost(string host) => host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        (IPAddress.TryParse(host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address));
}

public static class MeetingIdentity
{
    public static string? ObjectId(ClaimsPrincipal user)
    {
        var oid = user.FindFirstValue("oid");
        return Guid.TryParseExact(oid, "D", out var id) ? id.ToString("D") : null;
    }

    public static bool HasScope(ClaimsPrincipal user) =>
        user.FindAll("scp").Any(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("Meeting.Access"));
}

public sealed record SessionTicket(string Ticket, DateTimeOffset ExpiresAt);
public sealed record TicketIdentity(string ObjectId, string Origin, DateTimeOffset ExpiresAt);

public sealed class TicketStore(TimeProvider clock)
{
    private readonly Dictionary<string, TicketIdentity> tickets = new(StringComparer.Ordinal);
    private readonly object gate = new();

    public SessionTicket? Issue(string objectId, string origin)
    {
        lock (gate)
        {
            var now = clock.GetUtcNow();
            foreach (var key in tickets.Where(pair => pair.Value.ExpiresAt <= now).Select(pair => pair.Key).ToArray())
                tickets.Remove(key);
            if (tickets.Count >= 4096 || tickets.Values.Count(ticket => ticket.ObjectId == objectId) >= 8)
                return null;
            var ticket = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
            var expires = now.AddSeconds(30);
            tickets.Add(ticket, new(objectId, origin, expires));
            return new(ticket, expires);
        }
    }

    public TicketIdentity? Consume(string ticket, string origin)
    {
        if (ticket.Length != 43) return null;
        lock (gate)
        {
            return tickets.Remove(ticket, out var identity) &&
                identity.ExpiresAt > clock.GetUtcNow() && identity.Origin == origin ? identity : null;
        }
    }
}
