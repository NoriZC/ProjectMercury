﻿using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.ApplicationInsights.WorkerService;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace AIShell.Azure;

public class MetricHelper
{
    public static readonly bool TelemetryOptOut = GetEnvironmentVariableAsBool("COPILOT_TELEMETRY_OPTOUT", false);
    private const int CustomDomainMaximumSize = 8192;
    private readonly string _endpoint;
    private TelemetryClient _telemetryClient;

    public MetricHelper(string endpoint)
    {
        _endpoint = endpoint;
        InitializeTelemetryClient();
    }

    private void InitializeTelemetryClient()
    {
        // Create the DI container.
        IServiceCollection services = new ServiceCollection();

        // Add custom TelemetryInitializer
        services.AddSingleton(typeof(ITelemetryInitializer), new MyCustomTelemetryInitializer(_endpoint));

        // Configure TelemetryConfiguration
        services.Configure<TelemetryConfiguration>(config =>
        {
            // Optionally configure AAD
            // var credential = new DefaultAzureCredential();
            // config.SetAzureTokenCredential(credential);
        });

        // Being a regular console app, there is no appsettings.json or configuration providers enabled by default.
        // Hence connection string must be specified here.
        services.AddApplicationInsightsTelemetryWorkerService((ApplicationInsightsServiceOptions options) =>
            {
                // Application insights in the temp environment.
                options.ConnectionString = "InstrumentationKey=eea660a1-d969-44f8-abe4-96666e7fb159";
                options.EnableHeartbeat = false;
                options.EnableDiagnosticsTelemetryModule = false;
            }
        );

        // Add custom TelemetryProcessor
        services.AddApplicationInsightsTelemetryProcessor<MyCustomTelemetryProcessor>();

        // Build ServiceProvider.
        IServiceProvider serviceProvider = services.BuildServiceProvider();

        // Obtain TelemetryClient instance from DI, for additional manual tracking or to flush.
        _telemetryClient = serviceProvider.GetRequiredService<TelemetryClient>();

        // Suppress the PII recorded by default to reduce risk.
        _telemetryClient.Context.Cloud.RoleInstance = "Not Available";
    }

    public void LogTelemetry(AzTrace trace)
    {
        /*
        string historyJson;

        while (true)
        {
            historyJson = JsonSerializer.Serialize(trace.HistoryMessage);

            if (historyJson.Length > CustomDomainMaximumSize)
            {
                // Remove the oldest round of conversation from history when it exceeds Application Insights limit.
                trace.HistoryMessage.RemoveFirst();
                trace.HistoryMessage.RemoveFirst();
            }
            else
            {
                break;
            }
        }*/
        
        Dictionary<string, string> eventProperties = new()
        {
            { "CorrelationID", trace.CorrelationID },
            { "InstallationID", trace.InstallationID },
            { "Handler", trace.Handler },
            { "EventType", trace.EventType },
            { "Command", trace.Command },
            { "DetailedMessage", trace.DetailedMessage }
        };

        _telemetryClient.TrackTrace("AIShell-Test1021", eventProperties);

        // Explicitly call Flush() followed by sleep is required in Console Apps.
        // This is to ensure that even if application terminates, telemetry is sent to the back-end.
        _telemetryClient.Flush();
        // Task.Delay(500000).Wait();
    }

    private static bool GetEnvironmentVariableAsBool(string name, bool defaultValue)
    {
        var str = Environment.GetEnvironmentVariable(name);

        if (string.IsNullOrWhiteSpace(str))
        {
            return defaultValue;
        }

        if (bool.TryParse(str, out bool result))
        {
            return result;
        }

        if (string.Equals(str, "yes", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(str, "no", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return defaultValue;
    }
}

internal class MyCustomTelemetryInitializer : ITelemetryInitializer
{
    private readonly string _endpoint;
    public void Initialize(ITelemetry telemetry)
    {
        (telemetry as ISupportProperties).Properties["Endpoint"] = _endpoint;
    }

    public MyCustomTelemetryInitializer(string endpoint) 
    {
        ArgumentException.ThrowIfNullOrEmpty(endpoint);
        _endpoint = endpoint;
    }
}

internal class MyCustomTelemetryProcessor : ITelemetryProcessor
{
    private readonly ITelemetryProcessor _next;

    public MyCustomTelemetryProcessor(ITelemetryProcessor next)
    {
        _next = next;
    }

    public void Process(ITelemetry item)
    {
        // Example processor - not filtering out anything.
        // This should be replaced with actual logic.
        _next.Process(item);
    }
}
