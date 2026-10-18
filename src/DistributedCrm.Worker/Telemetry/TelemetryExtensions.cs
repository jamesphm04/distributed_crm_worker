using Azure.Core;
using Azure.Monitor.OpenTelemetry.Exporter;
using DistributedCrm.Infrastructure.Telemetry;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace DistributedCrm.Worker.Telemetry;

public static class TelemetryExtensions
{
    // The service's identity in every backend (dashboard, App Insights' "cloud role name"), so the code owns it.
    public const string ServiceName = "distributed-crm-worker";

    // Exports traces, metrics and logs to Application Insights when APPLICATIONINSIGHTS_CONNECTION_STRING is set (Azure),
    // otherwise over OTLP (OTEL_EXPORTER_OTLP_ENDPOINT says where; unset means localhost:4317, the Aspire dashboard).
    public static IHostApplicationBuilder AddTelemetry(this IHostApplicationBuilder builder, TokenCredential credential)
    {
        // The Service Bus SDK only emits Activities when this switch is on.
        AppContext.SetSwitch("Azure.Experimental.EnableActivitySource", true);

        // Opt-in, for debugging without the dashboard. Logs already reach the console through the default logger.
        var exportToConsole = builder.Configuration.GetValue<bool>("Telemetry:ConsoleExporter");

        var appInsightsConnectionString = builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];

        var openTelemetry = builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(ServiceName))
            .WithTracing(tracing =>
            {
                tracing
                    // The only HTTP traffic is platform probes. Tracing them adds nothing, and filtering the request
                    // also drops the health checks' SQL, Redis and Service Bus spans underneath it.
                    .AddAspNetCoreInstrumentation(options =>
                        options.Filter = context => !context.Request.Path.StartsWithSegments("/health"))
                    .AddNpgsql()
                    .AddRedisInstrumentation()
                    .AddSource("Azure.Messaging.ServiceBus.*")
                    .AddSource(InfrastructureTelemetry.ActivitySourceName);

                if (exportToConsole)
                {
                    tracing.AddConsoleExporter();
                }
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .AddAspNetCoreInstrumentation()
                    .AddRuntimeInstrumentation()
                    .AddNpgsqlInstrumentation();

                if (exportToConsole)
                {
                    metrics.AddConsoleExporter();
                }
            })
            .WithLogging(configureBuilder: null, configureOptions: logging =>
            {
                logging.IncludeFormattedMessage = true;
                logging.IncludeScopes = true;
            });

        if (string.IsNullOrEmpty(appInsightsConnectionString))
        {
            openTelemetry.UseOtlpExporter();
        }
        else
        {
            openTelemetry.UseAzureMonitorExporter(options =>
            {
                options.ConnectionString = appInsightsConnectionString;
                // App Insights accepts only Entra-authenticated telemetry (local auth is off in Bicep), so the
                // connection string alone can't be used to write to it.
                options.Credential = credential;
                // Keep every trace: a dev environment's volume is small, and Log Analytics' daily cap bounds the bill.
                // TracesPerSecond would take precedence over the ratio, so it is cleared explicitly.
                options.TracesPerSecond = null;
                options.SamplingRatio = 1.0F;
            });
        }

        return builder;
    }
}
