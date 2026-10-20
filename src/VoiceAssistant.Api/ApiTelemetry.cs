using Azure.Core;
using Azure.Identity;
using Azure.Monitor.OpenTelemetry.Exporter;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;

namespace VoiceAssistant.Api;

/// <summary>
/// Optional Azure Monitor export of this app's own meter, and nothing else: no ASP.NET Core or HTTP request tracing,
/// no request telemetry and no log export. WebSocket URLs carry one-use tickets, and console logs already reach
/// Log Analytics. Every exported tag value is content-free (see <see cref="MeetingMetrics"/>).
/// </summary>
public static class ApiTelemetry
{
    public const string ConnectionStringKey = "APPLICATIONINSIGHTS_CONNECTION_STRING";
    public const string DefaultServiceName = "voice-web";

    /// <summary>Registers the metric exporter only when an Application Insights connection string is configured.</summary>
    public static bool Configure(IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration[ConnectionStringKey];
        if (string.IsNullOrWhiteSpace(connectionString)) return false;
        var serviceName = ServiceName(configuration);
        var credential = Credential(configuration);
        services.AddHostedService(provider => new TelemetryExport(() => CreateProvider(connectionString, serviceName, credential),
            provider.GetRequiredService<ILogger<TelemetryExport>>()));
        return true;
    }

    /// <summary>The meter provider limited to this app's meter, exporting to Azure Monitor.</summary>
    internal static MeterProvider CreateProvider(string connectionString, string serviceName, TokenCredential credential) =>
        Sdk.CreateMeterProviderBuilder()
            .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService(serviceName))
            .AddMeter(MeetingMetrics.MeterName)
            .AddAzureMonitorMetricExporter(options =>
            {
                options.ConnectionString = connectionString;
                // Application Insights local authentication is disabled, so ingestion is authorized with Entra.
                options.Credential = credential;
                // Nothing is written to disk: a failed export is dropped rather than kept in local retry storage.
                options.DisableOfflineStorage = true;
            })
            .Build();

    /// <summary>The service name, which Application Insights shows as the cloud role.</summary>
    internal static string ServiceName(IConfiguration configuration) =>
        configuration["OTEL_SERVICE_NAME"]?.Trim() is { Length: > 0 } name ? name : DefaultServiceName;

    /// <summary>The runtime's user-assigned managed identity in Azure; the developer credential chain elsewhere.</summary>
    internal static TokenCredential Credential(IConfiguration configuration) =>
        configuration["AZURE_CLIENT_ID"]?.Trim() is { Length: > 0 } clientId && !string.IsNullOrWhiteSpace(configuration["IDENTITY_ENDPOINT"])
            ? new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(clientId))
            : new DefaultAzureCredential();

    /// <summary>
    /// Creates the meter provider when the host starts and disposes (flushes) it on shutdown. The exporter parses the
    /// connection string in its constructor, so a malformed value turns export off with one warning instead of stopping
    /// the app.
    /// </summary>
    internal sealed class TelemetryExport(Func<MeterProvider> create, ILogger<TelemetryExport> logger) : IHostedService, IDisposable
    {
        private MeterProvider? provider;

        internal bool Exporting => Volatile.Read(ref provider) is not null;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            try { Volatile.Write(ref provider, create()); }
            catch (Exception exception)
            {
                // The exception message could echo the connection string, so only its type is logged.
                logger.LogWarning("Metric export is off: the Azure Monitor exporter could not start ({ExceptionType}). " +
                    "Check APPLICATIONINSIGHTS_CONNECTION_STRING.", exception.GetType().Name);
            }
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            Dispose();
            return Task.CompletedTask;
        }

        public void Dispose() => Interlocked.Exchange(ref provider, null)?.Dispose();
    }
}
