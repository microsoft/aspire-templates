using System.Diagnostics;
using Azure.Storage.Blobs;
using Azure.Storage.Queues;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace MediaStudio.Core;

public static class Services
{
    public static readonly ActivitySource Activities = new("MediaStudio.Pipeline");
    public static void AddMediaServices(this IHostApplicationBuilder builder)
    {
        builder.AddSqlServerDbContext<MediaDb>("mediadb");
        builder.AddAzureBlobContainerClient("images");
        builder.AddAzureQueueServiceClient("queues");
        builder.Logging.AddOpenTelemetry(options =>
        {
            options.IncludeScopes = true;
            options.IncludeFormattedMessage = true;
        });
        var telemetry = builder.Services.AddOpenTelemetry()
            .WithTracing(tracing => tracing.AddSource(Activities.Name)
                .AddAspNetCoreInstrumentation().AddHttpClientInstrumentation())
            .WithMetrics(metrics => metrics.AddRuntimeInstrumentation().AddAspNetCoreInstrumentation());
        if (!string.IsNullOrEmpty(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            telemetry.UseOtlpExporter();
        }
        builder.Services.AddHealthChecks();
    }
}

// SQL is the outbox: a crash between committing a job and sending its message is repaired here.
public sealed class Dispatcher(IServiceScopeFactory scopes, BlobContainerClient blobs,
    QueueServiceClient queues, ILogger<Dispatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await blobs.CreateIfNotExistsAsync(cancellationToken: stoppingToken);
        var queue = queues.GetQueueClient("media-jobs");
        await queue.CreateIfNotExistsAsync(cancellationToken: stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<MediaDb>();
                var now = DateTimeOffset.UtcNow;
                await db.Jobs.Where(job => job.State == "Processing" && job.LeaseUntil < now)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(job => job.State, "Queued")
                        .SetProperty(job => job.Lease, (Guid?)null), stoppingToken);
                var jobs = await db.Jobs.AsNoTracking()
                    .Where(job => job.State == "Queued" && job.NextDispatch <= now)
                    .OrderBy(job => job.Created).Take(20).ToListAsync(stoppingToken);
                foreach (var job in jobs)
                {
                    await queue.SendMessageAsync(BinaryData.FromObjectAsJson(new WorkItem(job.Id, job.Generation, job.TraceParent)),
                        cancellationToken: stoppingToken);
                    await db.Jobs.Where(row => row.Id == job.Id && row.Generation == job.Generation && row.State == "Queued")
                        .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.NextDispatch, now.AddSeconds(30)), stoppingToken);
                    logger.LogInformation("Dispatched media job {JobId} generation {Generation}", job.Id, job.Generation);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Outbox dispatch failed; durable jobs will be retried");
            }
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }
}
