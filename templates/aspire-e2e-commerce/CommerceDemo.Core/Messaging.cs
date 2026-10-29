using System.Diagnostics;
using System.Text.Json;
using Azure.Storage.Queues;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace CommerceDemo.Core;

public sealed class OutboxRelay(NpgsqlDataSource database, QueueServiceClient queues, ILogger<OutboxRelay> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RelayAsync(stoppingToken);
                await Task.Delay(500, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) when (exception is NpgsqlException or Azure.RequestFailedException or HttpRequestException)
            {
                logger.LogError(exception, "Outbox delivery failed; durable work will retry.");
                await Task.Delay(2000, stoppingToken);
            }
        }
    }

    private async Task RelayAsync(CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        Guid id;
        string destination, body;
        await using (var command = new NpgsqlCommand("SELECT id,queue,body FROM outbox WHERE available_at <= now() ORDER BY available_at LIMIT 1 FOR UPDATE SKIP LOCKED", connection))
        await using (var reader = await command.ExecuteReaderAsync(token))
        {
            if (!await reader.ReadAsync(token)) return;
            id = reader.GetGuid(0); destination = reader.GetString(1); body = reader.GetString(2);
        }
        var message = JsonSerializer.Deserialize<WorkMessage>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidDataException("Invalid outbox message.");
        ActivityContext.TryParse(message.TraceParent, message.TraceState, true, out var parent);
        using var activity = CommerceFlow.Traces.StartActivity("outbox.send", ActivityKind.Producer, parent);
        activity?.SetTag("order.id", message.OrderId);
        activity?.SetTag("messaging.message.id", id);
        activity?.SetTag("messaging.destination.name", destination);
        await queues.GetQueueClient(destination).SendMessageAsync(body, cancellationToken: token);
        await using var delete = new NpgsqlCommand("DELETE FROM outbox WHERE id=$1", connection);
        delete.Parameters.AddWithValue(id);
        await delete.ExecuteNonQueryAsync(token);
        await transaction.CommitAsync(token);
        logger.LogInformation("Published {MessageId} for order {OrderId} to {Queue}", id, message.OrderId, destination);
    }
}

public sealed class OrderWorker(CommerceStore store, QueueServiceClient queues, string role, ILogger<OrderWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var queue = queues.GetQueueClient(role);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var response = await queue.ReceiveMessageAsync(TimeSpan.FromSeconds(30), stoppingToken);
                if (response.Value is not { } received)
                {
                    await Task.Delay(750, stoppingToken);
                    continue;
                }
                if (received.DequeueCount > 8)
                {
                    await queues.GetQueueClient("poison").SendMessageAsync(received.Body, cancellationToken: stoppingToken);
                    await queue.DeleteMessageAsync(received.MessageId, received.PopReceipt, stoppingToken);
                    logger.LogCritical("Quarantined repeatedly failing message {MessageId} from {Queue}; inspect the poison queue.", received.MessageId, role);
                    continue;
                }
                var message = JsonSerializer.Deserialize<WorkMessage>(received.Body, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                    ?? throw new InvalidDataException("Invalid commerce message.");
                var changed = await store.ProcessAsync(message, role, stoppingToken);
                await queue.DeleteMessageAsync(received.MessageId, received.PopReceipt, stoppingToken);
                logger.LogInformation("Order {OrderId} message {MessageId}: {Result}", message.OrderId, message.Id, changed ? "committed" : "duplicate ignored");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) when (exception is NpgsqlException or Azure.RequestFailedException or HttpRequestException or JsonException or InvalidDataException or InvalidOperationException)
            {
                logger.LogError(exception, "Worker {Role} failed; unacknowledged message remains available for retry.", role);
                await Task.Delay(2000, stoppingToken);
            }
        }
    }
}
