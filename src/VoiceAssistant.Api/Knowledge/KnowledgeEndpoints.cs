using System.Net;
using System.Text;
using System.Text.Json;

namespace VoiceAssistant.Api.Knowledge;

public static class KnowledgeEndpoints
{
    private const string FakeOwner = "00000000-0000-0000-0000-000000000001";

    public static void MapKnowledge(this WebApplication app, ServiceSettings settings)
    {
        var routes = app.MapGroup("/api/knowledge");
        if (!settings.Fake) routes.RequireAuthorization("Meeting");
        routes.MapGet("", async (HttpContext context, KnowledgeService service) => await Run(context, settings, false, async cancellation =>
        {
            var documents = await service.ListAsync(Owner(context, settings), cancellation);
            return Results.Ok(new
            {
                documents, limits = KnowledgeLimits.Public,
                usage = new { documents = documents.Count, chunks = documents.Sum(document => document.Chunks) },
                extensions = MaterialExtractor.Extensions
            });
        }));
        routes.MapPost("", async (HttpContext context, KnowledgeService service, KnowledgeRequestReader reader) =>
            await Run(context, settings, true, async cancellation =>
            {
                var owner = Owner(context, settings);
                using var admission = service.AdmitUpload(owner);
                var input = await reader.ReadAsync(context.Request, cancellation);
                var uploaded = await service.UploadAsync(owner, input, cancellation);
                return Results.Json(uploaded, statusCode: StatusCodes.Status201Created);
            }));
        routes.MapDelete("/{id}", async (HttpContext context, string id, KnowledgeService service) =>
            await Run(context, settings, true, async cancellation =>
            {
                await service.DeleteAsync(Owner(context, settings), id, cancellation);
                return Results.NoContent();
            }));
    }

    internal static bool Allowed(HttpContext context, ServiceSettings settings, bool mutation)
    {
        if (mutation || context.Request.Headers.ContainsKey("Origin")) return OriginPolicy.Allows(context, settings);
        if (!settings.Fake) return true;
        return context.Connection.RemoteIpAddress is { } remote && IPAddress.IsLoopback(remote) &&
            (context.Request.Host.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
             (IPAddress.TryParse(context.Request.Host.Host.Trim('[', ']'), out var host) && IPAddress.IsLoopback(host))) &&
            context.Request.Headers["Sec-Fetch-Site"] != "cross-site";
    }

    private static string Owner(HttpContext context, ServiceSettings settings) =>
        settings.Fake ? FakeOwner : MeetingIdentity.ObjectId(context.User)!;

    private static async Task<IResult> Run(HttpContext context, ServiceSettings settings, bool mutation,
        Func<CancellationToken, Task<IResult>> action)
    {
        if (!Allowed(context, settings, mutation)) return Results.StatusCode(403);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        try { return await action(timeout.Token); }
        catch (KnowledgeException exception) { return Error(exception); }
        catch (DecoderFallbackException) { return Error(KnowledgeException.Unsupported()); }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or FormatException)
        { return Error(KnowledgeException.Invalid()); }
        catch (BadHttpRequestException exception) when (exception.StatusCode == 413)
        { return Error(KnowledgeException.TooLarge()); }
        catch (Exception)
        {
            context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Knowledge")
                .LogWarning("Meeting materials operation failed; sensitive details suppressed.");
            return Error(KnowledgeException.Unavailable());
        }
    }

    private static IResult Error(KnowledgeException error) =>
        Results.Json(new { error = error.Code, message = error.Message }, statusCode: error.Status);
}
