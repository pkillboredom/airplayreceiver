using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace AirPlay.Telemetry
{
    public static class ServiceExtensions
    {
        /// <summary>
        /// Adds OpenTelemetry instrumentation for AirPlay to the service collection.
        /// </summary>
        /// <param name="builder">The host builder</param>
        /// <param name="serviceName">Name of the service (default: AirPlayReceiver)</param>
        /// <param name="configureTracing">Optional additional configuration action for the TracerProviderBuilder</param>
        /// <param name="configureMetrics">Optional additional configuration action for the MeterProviderBuilder</param>
        /// <param name="attributes">Optional service resource attributes</param>
        /// <returns>The updated host builder</returns>
        public static IHostBuilder AddAirPlayTelemetry(
            this IHostBuilder builder,
            string serviceName = "AirPlayReceiver",
            Action<TracerProviderBuilder> configureTracing = null,
            Action<MeterProviderBuilder> configureMetrics = null,
            params KeyValuePair<string, object>[] attributes)
        {
            return builder.ConfigureServices((context, services) =>
            {
                // Create a resource builder with service information
                var resourceBuilder = ResourceBuilder.CreateDefault()
                    .AddService(
                        serviceName: serviceName, 
                        serviceVersion: typeof(AirPlayTelemetry).Assembly.GetName().Version?.ToString() ?? "1.0.0")
                    .AddTelemetrySdk()
                    .AddAttributes(attributes);

                // Add OpenTelemetry tracing
                services.AddOpenTelemetry()
                    .WithTracing(tracerProviderBuilder =>
                    {
                        tracerProviderBuilder
                            .SetResourceBuilder(resourceBuilder)
                            .AddSource(AirPlayTelemetry.ActivitySource.Name)
                            .AddHttpClientInstrumentation()
                            // Add console exporter for development
                            .AddConsoleExporter();

                        // Add any additional configuration if provided
                        configureTracing?.Invoke(tracerProviderBuilder);
                    })
                    .WithMetrics(meterProviderBuilder =>
                    {
                        meterProviderBuilder
                            .SetResourceBuilder(resourceBuilder)
                            .AddMeter(AirPlayTelemetry.AirPlayMeter.Name)
                            .AddMeter(AirPlayTelemetry.ConnectionMeter.Name)
                            .AddRuntimeInstrumentation()
                            .AddConsoleExporter();
                        
                        // Add any additional configuration if provided
                        configureMetrics?.Invoke(meterProviderBuilder);
                    });
            });
        }
    }
}