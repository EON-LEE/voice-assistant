using System.ClientModel;
using System.ClientModel.Primitives;
using Azure;
using Azure.Core;
using Azure.Search.Documents;
using OpenAI;

namespace VoiceAssistant.Api;

/// <summary>
/// Whether this replica should receive traffic. False until the startup warm-up has completed once, then true for the
/// life of the process: a later transient token or network failure never takes a serving replica out of rotation.
/// </summary>
public sealed class Readiness(bool ready = false)
{
    private const int Warming = 0, Ready = 1, Overdue = 2;
    private int state = ready ? Ready : Warming;

    public bool IsReady => Volatile.Read(ref state) == Ready;

    /// <summary><c>ready</c>, <c>warming</c>, or <c>not_ready</c> once the warm-up has failed past its report deadline.</summary>
    public string Status => Volatile.Read(ref state) switch { Ready => "ready", Overdue => "not_ready", _ => "warming" };

    internal void MarkReady() => Volatile.Write(ref state, Ready);

    /// <summary>True only for the call that moved a still-warming replica to not-ready.</summary>
    internal bool MarkOverdue() => Interlocked.CompareExchange(ref state, Overdue, Warming) == Warming;
}

/// <summary>
/// The dependency warm-up. <see cref="WarmAsync"/> covers what readiness requires and throws a <see cref="WarmupException"/>
/// when one is unreachable; <see cref="WarmOptionalAsync"/> covers best-effort dependencies that readiness never waits for.
/// </summary>
public interface IDependencyWarmup
{
    Task WarmAsync(CancellationToken cancellation);

    Task WarmOptionalAsync(CancellationToken cancellation) => Task.CompletedTask;
}

/// <summary>A failed warm-up stage. Its message names only the stage, never service, URL or credential detail.</summary>
public sealed class WarmupException(string stage, Exception inner) : Exception($"Warm-up stage {stage} failed.", inner)
{
    public string Stage { get; } = stage;
}

/// <summary>
/// Azure-mode startup warm-up through the same clients and shared transports the live reply path uses. Readiness requires
/// the Cognitive Services token (Speech and Azure OpenAI share its audience) and TLS connections to Azure OpenAI and Speech.
/// Search is best-effort: a reply without it still works (it reports grounding unavailable), so its token and connection are
/// warmed separately and readiness never waits for them. Any HTTP status counts as reached; none of the requests is a
/// billable model, speech or ranking call.
/// </summary>
internal sealed class AzureDependencyWarmup : IDependencyWarmup
{
    internal const string CognitiveServicesScope = SpeechAuthorization.Scope;
    internal const string SearchScope = "https://search.azure.com/.default";
    internal static readonly TimeSpan StageTimeout = TimeSpan.FromSeconds(10);
    private static readonly HttpClient SharedSpeechProbe = new() { Timeout = StageTimeout };
    private readonly TokenCredential credential;
    private readonly OpenAIClient openAI;
    private readonly Uri openAIEndpoint;
    private readonly SearchClient? search;
    private readonly HttpClient speechProbe;
    private readonly Uri speechEndpoint;

    public AzureDependencyWarmup(ServiceSettings settings, AzureServiceClients clients)
        : this(settings, clients.Credential, clients.RetrievalOpenAI, clients.RetrievalSearch, SharedSpeechProbe) { }

    internal AzureDependencyWarmup(ServiceSettings settings, TokenCredential credential, OpenAIClient openAI, SearchClient? search,
        HttpClient speechProbe)
    {
        this.credential = credential;
        this.openAI = openAI;
        openAIEndpoint = new Uri(settings.OpenAIEndpoint);
        this.search = search;
        this.speechProbe = speechProbe;
        speechEndpoint = SpeechEndpoint(settings);
    }

    /// <summary>The configured Speech resource endpoint, or the regional speech-to-text host the SDK uses without one.</summary>
    internal static Uri SpeechEndpoint(ServiceSettings settings) => settings.SpeechEndpoint.Length > 0
        ? new Uri(settings.SpeechEndpoint)
        : new Uri($"https://{settings.SpeechRegion.Trim().ToLowerInvariant()}.stt.speech.microsoft.com/");

    public async Task WarmAsync(CancellationToken cancellation)
    {
        // The token first, so the connection requests below (and the first live calls) find it cached in the credential.
        await StageAsync("speech_openai_token", token => credential.GetTokenAsync(new([CognitiveServicesScope]), token).AsTask(), cancellation);
        await Task.WhenAll(
            StageAsync("openai_connection", OpenAIAsync, cancellation),
            // The Speech SDK opens its own connection per session, so for Speech this checks reachability (DNS and TLS) only.
            StageAsync("speech_connection", SpeechAsync, cancellation));
    }

    public async Task WarmOptionalAsync(CancellationToken cancellation)
    {
        if (search is null) return;
        await StageAsync("search_token", token => credential.GetTokenAsync(new([SearchScope]), token).AsTask(), cancellation);
        await StageAsync("search_connection", SearchAsync, cancellation);
    }

    private async Task OpenAIAsync(CancellationToken cancellation)
    {
        using var message = openAI.Pipeline.CreateMessage();
        message.Request.Method = "GET";
        message.Request.Uri = openAIEndpoint;
        message.Apply(new RequestOptions { CancellationToken = cancellation, ErrorOptions = ClientErrorBehaviors.NoThrow });
        // Any HTTP status (typically 404 for the endpoint root) means the token and the TLS connection are in place.
        await openAI.Pipeline.SendAsync(message);
    }

    private async Task SearchAsync(CancellationToken cancellation)
    {
        try { await search!.GetDocumentCountAsync(cancellation); }
        catch (RequestFailedException failure) when (failure.Status > 0) { }
    }

    private async Task SpeechAsync(CancellationToken cancellation)
    {
        using var response = await speechProbe.GetAsync(speechEndpoint, HttpCompletionOption.ResponseHeadersRead, cancellation);
    }

    private static async Task StageAsync(string stage, Func<CancellationToken, Task> run, CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(StageTimeout);
        try { await run(timeout.Token).WaitAsync(timeout.Token); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
        catch (Exception exception) { throw new WarmupException(stage, exception); }
    }
}

/// <summary>
/// Runs the dependency warm-up in the background until the required part succeeds once, then marks the replica ready. It
/// never blocks startup or <c>/health/live</c>; after <see cref="ReportAfter"/> of failures it reports not-ready with one
/// content-free log line and keeps retrying with a capped backoff. The optional part (Search) runs beside it with its own
/// backoff until it succeeds once; its failures are logged and counted but never affect readiness.
/// </summary>
internal sealed class StartupWarmup(IDependencyWarmup warmup, Readiness readiness, TimeProvider clock, ILogger<StartupWarmup> logger)
    : BackgroundService
{
    internal static readonly TimeSpan ReportAfter = TimeSpan.FromSeconds(60);
    internal static readonly TimeSpan FirstDelay = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan MaxOptionalDelay = TimeSpan.FromSeconds(60);

    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        var optional = WarmOptionalAsync(stopping);
        await WarmRequiredAsync(stopping);
        await optional;
    }

    private async Task WarmRequiredAsync(CancellationToken stopping)
    {
        var started = clock.GetTimestamp();
        var delay = FirstDelay;
        for (var attempt = 1; !stopping.IsCancellationRequested; attempt++)
        {
            try
            {
                await warmup.WarmAsync(stopping);
                readiness.MarkReady();
                logger.LogInformation("Startup warm-up completed after {Attempts} attempt(s) in {ElapsedMs} ms; ready for traffic.",
                    attempt, (long)clock.GetElapsedTime(started).TotalMilliseconds);
                return;
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested) { return; }
            catch (Exception exception)
            {
                var (stage, type, status) = Describe(exception);
                MeetingMetrics.WarmupFailed(stage, required: true);
                if (clock.GetElapsedTime(started) >= ReportAfter && readiness.MarkOverdue())
                    logger.LogError("Startup warm-up has not completed after {Seconds} s; /health/ready reports not ready until it does. " +
                        "Last failure: stage {Stage}, {ExceptionType}, HTTP status {Status}.",
                        (long)clock.GetElapsedTime(started).TotalSeconds, stage, type, status);
                else
                    logger.LogWarning("Startup warm-up attempt {Attempt} failed: stage {Stage}, {ExceptionType}, HTTP status {Status}.",
                        attempt, stage, type, status);
            }
            try { await Task.Delay(delay, clock, stopping); }
            catch (OperationCanceledException) { return; }
            delay = delay * 2 < MaxDelay ? delay * 2 : MaxDelay;
        }
    }

    private async Task WarmOptionalAsync(CancellationToken stopping)
    {
        var delay = FirstDelay;
        for (var attempt = 1; !stopping.IsCancellationRequested; attempt++)
        {
            try
            {
                await warmup.WarmOptionalAsync(stopping);
                if (attempt > 1) logger.LogInformation("Optional warm-up completed after {Attempts} attempts.", attempt);
                return;
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested) { return; }
            catch (Exception exception)
            {
                var (stage, type, status) = Describe(exception);
                MeetingMetrics.WarmupFailed(stage, required: false);
                logger.LogWarning("Optional warm-up attempt {Attempt} failed: stage {Stage}, {ExceptionType}, HTTP status {Status}; " +
                    "readiness does not wait for it.", attempt, stage, type, status);
            }
            try { await Task.Delay(delay, clock, stopping); }
            catch (OperationCanceledException) { return; }
            delay = delay * 2 < MaxOptionalDelay ? delay * 2 : MaxOptionalDelay;
        }
    }

    /// <summary>The failed stage, the cause's exception type and HTTP status; never a message, which could carry service detail.</summary>
    internal static (string Stage, string ExceptionType, string Status) Describe(Exception exception)
    {
        var stage = exception is WarmupException warmup ? warmup.Stage : "unknown";
        var cause = exception is WarmupException { InnerException: { } inner } ? inner : exception;
        var status = cause switch
        {
            ClientResultException { Status: > 0 } client => client.Status,
            RequestFailedException { Status: > 0 } failed => failed.Status,
            HttpRequestException { StatusCode: { } code } => (int)code,
            _ => 0
        };
        return (stage, cause.GetType().Name, status > 0 ? status.ToString(System.Globalization.CultureInfo.InvariantCulture) : "none");
    }
}
