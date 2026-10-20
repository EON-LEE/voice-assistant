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
        services.AddSingleton(_ => Sdk.CreateMeterProviderBuilder()
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
            .Build());
        services.AddHostedService<TelemetryStarter>();
        return true;
    }

    /// <summary>The service name, which Application Insights shows as the cloud role.</summary>
    internal static string ServiceName(IConfiguration configuration) =>
        configuration["OTEL_SERVICE_NAME"]?.Trim() is { Length: > 0 } name ? name : DefaultServiceName;

    /// <summary>The runtime's user-assigned managed identity in Azure; the developer credential chain elsewhere.</summary>
    internal static TokenCredential Credential(IConfiguration configuration) =>
        configuration["AZURE_CLIENT_ID"]?.Trim() is { Length: > 0 } clientId && !string.IsNullOrWhiteSpace(configuration["IDENTITY_ENDPOINT"])
            ? new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(clientId))
            : new DefaultAzureCredential();

    /// <summary>Creates the meter provider when the host starts; the container disposes (and flushes) it on shutdown.</summary>
    private sealed class TelemetryStarter(MeterProvider provider) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            GC.KeepAlive(provider);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
