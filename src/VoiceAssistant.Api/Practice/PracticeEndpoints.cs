using System.Text.Json;
using Microsoft.Net.Http.Headers;

namespace VoiceAssistant.Api.Practice;

public static class PracticeEndpoints
{
    public static void MapPractice(this WebApplication app, ServiceSettings settings)
    {
        foreach (var operation in new[] { "enrich", "speak", "turn", "suggest", "feedback", "summary" })
        {
            var route = "/api/" + (operation is "enrich" or "speak" ? "assist/" : "practice/") + operation;
            var endpoint = app.MapPost(route, async (HttpContext context, PracticeService service, PracticeLimiter limiter, TimeProvider clock) =>
            {
                if (!OriginPolicy.Allows(context, settings)) return Results.StatusCode(403);
                var owner = settings.Fake ? "00000000-0000-0000-0000-000000000001" : MeetingIdentity.ObjectId(context.User)!;
                var parsed = false;
                IDisposable? lease = null;
                Task? active = null;
                try
                {
                    lease = limiter.Enter(owner, operation == "speak");
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(operation == "speak" ? 15 : 20), clock);
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, deadline.Token);
                    var request = await ReadAsync(operation, context.Request, timeout.Token);
                    parsed = true;
                    if (operation == "speak")
                    {
                        var task = Task.Run(() => service.SpeakAsync(request, timeout.Token), CancellationToken.None);
                        active = task;
                        var audio = await task.WaitAsync(timeout.Token);
                        if (audio.Bytes.Length is < 1 or > 1048576 || audio.ContentType is not ("audio/mpeg" or "audio/wav"))
                            throw PracticeException.Unavailable();
                        return Results.Bytes(audio.Bytes, audio.ContentType);
                    }
                    var response = Task.Run(() => service.ExecuteAsync(request, owner, timeout.Token), CancellationToken.None);
                    active = response;
                    return Results.Ok(await response.WaitAsync(timeout.Token));
                }
                catch (PracticeException exception)
                {
                    if (exception.Status == 429) context.Response.Headers.RetryAfter = "1";
                    if (exception.Status >= 500)
                        PracticeDiagnostics.Failure(app.Logger, operation, exception, 0);
                    return Error(exception);
                }
                catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
                {
                    if (parsed) PracticeDiagnostics.Failure(app.Logger, operation, exception, 0);
                    return Error(parsed ? PracticeException.Unavailable() : PracticeException.Invalid());
                }
                catch (BadHttpRequestException exception) when (exception.StatusCode == 413)
                { return Error(PracticeException.TooLarge()); }
                catch (OperationCanceledException exception)
                {
                    PracticeDiagnostics.Failure(app.Logger, operation, exception, 0);
                    return Error(PracticeException.Timeout());
                }
                catch (Exception exception)
                {
                    PracticeDiagnostics.Failure(app.Logger, operation, exception, 0);
                    return Error(PracticeException.Unavailable());
                }
                finally
                {
                    if (lease is not null)
                    {
                        if (active is { IsCompleted: false }) _ = ReleaseWhenFinished(active, lease);
                        else lease.Dispose();
                    }
                }
            });
            if (!settings.Fake) endpoint.RequireAuthorization("Meeting");
        }
    }
    private static async Task ReleaseWhenFinished(Task active, IDisposable lease)
    {
        try { await active; }
        catch (Exception) { /* Request already reported timeout/failure; never expose provider details. */ }
        finally { lease.Dispose(); }
    }
    internal static async Task<PracticeRequest> ReadAsync(string operation, HttpRequest request, CancellationToken cancellation)
    {
        if (request.ContentLength > 16384) throw PracticeException.TooLarge();
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var media) ||
            !media.MediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase) ||
            (media.Charset.HasValue && !HeaderUtilities.RemoveQuotes(media.Charset).Equals("utf-8", StringComparison.OrdinalIgnoreCase)))
            throw PracticeException.Invalid();
        using var output = new MemoryStream();
        var buffer = new byte[4096];
        int count;
        while ((count = await request.Body.ReadAsync(buffer, cancellation)) > 0)
        {
            if (output.Length + count > 16384) throw PracticeException.TooLarge();
            output.Write(buffer, 0, count);
        }
        using var document = JsonDocument.Parse(output.ToArray(), new JsonDocumentOptions { MaxDepth = 8 });
        return PracticeRequests.Parse(operation, document.RootElement);
    }
    private static IResult Error(PracticeException error) => Results.Json(new { error = error.Code, message = error.Message }, statusCode: error.Status);
}
