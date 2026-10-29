using System.Diagnostics;
using System.Text.Json;
using Azure.Storage.Blobs;
using Azure.Storage.Queues;
using MediaStudio.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SkiaSharp;

public sealed class Processor(IServiceScopeFactory scopes, BlobContainerClient blobs, QueueServiceClient queues,
    IConfiguration config, IHostApplicationLifetime lifetime, ILogger<Processor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var queue = queues.GetQueueClient("media-jobs");
        var poison = queues.GetQueueClient("media-poison");
        await queue.CreateIfNotExistsAsync(cancellationToken: stoppingToken);
        await poison.CreateIfNotExistsAsync(cancellationToken: stoppingToken);
        var continuous = config.GetValue<bool>("Studio:Continuous");
        do
        {
            var messages = await queue.ReceiveMessagesAsync(1, TimeSpan.FromMinutes(2), stoppingToken);
            if (messages.Value.Length == 0)
            {
                if (!continuous) break;
                await Task.Delay(1000, stoppingToken);
                continue;
            }
            var message = messages.Value[0];
            WorkItem item;
            try
            {
                item = message.Body.ToObjectFromJson<WorkItem>() ?? throw new JsonException("Empty work item.");
            }
            catch (JsonException exception)
            {
                logger.LogError(exception, "Malformed queue message {MessageId} moved to poison", message.MessageId);
                await poison.SendMessageAsync(message.Body, cancellationToken: stoppingToken);
                await queue.DeleteMessageAsync(message.MessageId, message.PopReceipt, stoppingToken);
                continue;
            }
            using var activity = Services.Activities.StartActivity("process-media", ActivityKind.Consumer, item.TraceParent);
            using var logScope = logger.BeginScope(new Dictionary<string, object> { ["JobId"] = item.Id, ["Generation"] = item.Generation });
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MediaDb>();
            var lease = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            var claimed = await db.Jobs.Where(job => job.Id == item.Id && job.Generation == item.Generation &&
                    job.State == "Queued" && job.Attempts < 3)
                .ExecuteUpdateAsync(setters => setters.SetProperty(job => job.State, "Processing")
                    .SetProperty(job => job.Attempts, job => job.Attempts + 1)
                    .SetProperty(job => job.Lease, lease).SetProperty(job => job.LeaseUntil, now.AddSeconds(90))
                    .SetProperty(job => job.Updated, now), stoppingToken);
            if (claimed == 0)
            {
                // Expired final attempts are terminal too; never leave crash-recovered work queued forever.
                await db.Jobs.Where(job => job.Id == item.Id && job.Generation == item.Generation &&
                        job.State == "Queued" && job.Attempts >= 3)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(job => job.State, "Failed")
                        .SetProperty(job => job.Error, "Processing lease expired after the final attempt."), stoppingToken);
                await queue.DeleteMessageAsync(message.MessageId, message.PopReceipt, stoppingToken);
                continue;
            }
            var job = await db.Jobs.AsNoTracking().SingleAsync(job => job.Id == item.Id, stoppingToken);
            try
            {
                logger.LogInformation("Processing attempt {Attempt}", job.Attempts);
                if (job.SimulateFailure) throw new InvalidOperationException("Walkthrough failure: retry clears the simulated fault.");
                await using var source = await blobs.GetBlobClient($"original/{job.Id}").OpenReadAsync(cancellationToken: stoppingToken);
                using var image = SKBitmap.Decode(source) ?? throw new InvalidDataException("Unable to decode image.");
                var scale = Math.Min(1d, 640d / Math.Max(image.Width, image.Height));
                var width = (int)Math.Round(image.Width * scale);
                var height = (int)Math.Round(image.Height * scale);
                using var target = new SKBitmap(width, height);
                using (var canvas = new SKCanvas(target))
                {
                    canvas.Clear(SKColors.White);
                    canvas.DrawBitmap(image, new SKRect(0, 0, width, height), new SKSamplingOptions(SKFilterMode.Linear));
                }
                using var output = SKImage.FromBitmap(target);
                using var encoded = output.Encode(SKEncodedImageFormat.Jpeg, 82);
                using var rendition = encoded.AsStream();
                await blobs.GetBlobClient($"rendition/{job.Id}.jpg").UploadAsync(rendition, overwrite: true, cancellationToken: stoppingToken);
                await db.Jobs.Where(row => row.Id == job.Id && row.Lease == lease && row.Generation == item.Generation)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.State, "Ready")
                        .SetProperty(row => row.Lease, (Guid?)null).SetProperty(row => row.Error, (string?)null)
                        .SetProperty(row => row.Updated, DateTimeOffset.UtcNow), stoppingToken);
                logger.LogInformation("Rendition ready");
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Media processing attempt {Attempt} failed", job.Attempts);
                var failed = job.Attempts >= 3;
                if (failed)
                    await poison.SendMessageAsync(BinaryData.FromObjectAsJson(item), cancellationToken: stoppingToken);
                await db.Jobs.Where(row => row.Id == job.Id && row.Lease == lease && row.Generation == item.Generation)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.State, failed ? "Failed" : "Queued")
                        .SetProperty(row => row.Lease, (Guid?)null)
                        .SetProperty(row => row.Error, job.SimulateFailure ? "Walkthrough failure: retry to recover." : "Processing failed; inspect correlated worker logs.")
                        .SetProperty(row => row.NextDispatch, DateTimeOffset.UtcNow.AddSeconds(2 * job.Attempts))
                        .SetProperty(row => row.Updated, DateTimeOffset.UtcNow), stoppingToken);
            }
            await queue.DeleteMessageAsync(message.MessageId, message.PopReceipt, stoppingToken);
        } while (!stoppingToken.IsCancellationRequested);
        lifetime.StopApplication();
    }
}
