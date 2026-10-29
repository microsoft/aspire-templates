using Azure.Storage.Queues;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace CommerceDemo.Core;

public static class Hosting
{
    public static void AddCommerce(this WebApplicationBuilder builder)
    {
        builder.AddAzureNpgsqlDataSource("commerce");
        builder.AddAzureQueueServiceClient("commerce-queues", configureClientBuilder: client =>
            client.ConfigureOptions(options => options.MessageEncoding = QueueMessageEncoding.Base64));
        builder.Services.AddSingleton<CommerceStore>();
        builder.Services.AddHostedService<OutboxRelay>();
        builder.Services.AddHealthChecks().AddCheck<CommerceHealth>("commerce-ready");
        builder.Logging.AddOpenTelemetry(options => { options.IncludeFormattedMessage = true; options.IncludeScopes = true; });
        var telemetry = builder.Services.AddOpenTelemetry()
            .WithTracing(tracing => tracing.AddSource("CommerceDemo.Business")
                .AddAspNetCoreInstrumentation(options => options.Filter = context =>
                    !context.Request.Path.StartsWithSegments("/health") && !context.Request.Path.StartsWithSegments("/api/snapshot"))
                .AddHttpClientInstrumentation())
            .WithMetrics(metrics => metrics.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddRuntimeInstrumentation());
        if (!string.IsNullOrEmpty(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            telemetry.UseOtlpExporter();
        }
    }

    public static async Task InitializeCommerceAsync(this WebApplication app)
    {
        await app.Services.GetRequiredService<CommerceStore>().InitializeAsync(app.Lifetime.ApplicationStopping);
        var queues = app.Services.GetRequiredService<QueueServiceClient>();
        foreach (var role in new[] { "payments", "fulfilment", "shipping", "poison" })
        {
            await queues.GetQueueClient(role).CreateIfNotExistsAsync(cancellationToken: app.Lifetime.ApplicationStopping);
        }
        app.MapHealthChecks("/health");
    }
}

public sealed class CommerceHealth(NpgsqlDataSource database, QueueServiceClient queues) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        await using var command = database.CreateCommand("SELECT 1 FROM products LIMIT 1");
        await command.ExecuteScalarAsync(cancellationToken);
        await queues.GetQueueClient("payments").GetPropertiesAsync(cancellationToken);
        return HealthCheckResult.Healthy();
    }
}
