using System.Diagnostics;
using System.Text.Json;
using Npgsql;

namespace CommerceDemo.Core;

public sealed class CommerceStore(NpgsqlDataSource database)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task InitializeAsync(CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        await Execute(connection, """
            SELECT pg_advisory_xact_lock(720461);
            CREATE TABLE IF NOT EXISTS products (id text PRIMARY KEY, name text NOT NULL, price_cents int NOT NULL, stock int NOT NULL CHECK(stock >= 0));
            CREATE TABLE IF NOT EXISTS orders (id uuid PRIMARY KEY, request_key uuid UNIQUE NOT NULL, fingerprint text NOT NULL, body jsonb NOT NULL);
            CREATE TABLE IF NOT EXISTS events (sequence bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY, order_id uuid NOT NULL REFERENCES orders(id), body jsonb NOT NULL);
            CREATE INDEX IF NOT EXISTS events_order ON events(order_id, sequence);
            CREATE TABLE IF NOT EXISTS outbox (id uuid PRIMARY KEY, queue text NOT NULL, body text NOT NULL, available_at timestamptz NOT NULL);
            INSERT INTO products VALUES ('lamp','Arc desk lamp',6900,120),('chair','Studio chair',14900,80),('plant','Little olive tree',3900,150) ON CONFLICT DO NOTHING;
            """, token);
        await transaction.CommitAsync(token);
    }

    public async Task<IReadOnlyList<Product>> ProductsAsync(CancellationToken token)
    {
        await using var command = database.CreateCommand("SELECT id,name,price_cents,stock FROM products ORDER BY id");
        await using var reader = await command.ExecuteReaderAsync(token);
        var result = new List<Product>();
        while (await reader.ReadAsync(token))
        {
            result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3)));
        }
        return result;
    }

    public async Task<IReadOnlyList<Order>> OrdersAsync(CancellationToken token)
    {
        await using var command = database.CreateCommand("SELECT body::text FROM orders ORDER BY (body->>'createdAt') DESC LIMIT 100");
        await using var reader = await command.ExecuteReaderAsync(token);
        var result = new List<Order>();
        while (await reader.ReadAsync(token)) result.Add(Deserialize<Order>(reader.GetString(0)));
        return result;
    }

    public async Task<OrderDetail?> DetailAsync(Guid id, CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, token);
        var order = await ReadOrder(connection, id, false, token);
        if (order is null) return null;
        await using var command = new NpgsqlCommand("SELECT sequence,body::text FROM events WHERE order_id=$1 ORDER BY sequence", connection);
        command.Parameters.AddWithValue(id);
        await using var reader = await command.ExecuteReaderAsync(token);
        var events = new List<OrderEvent>();
        while (await reader.ReadAsync(token)) events.Add(Deserialize<OrderEvent>(reader.GetString(1)) with { Sequence = reader.GetInt64(0) });
        return new(order, events);
    }

    public async Task<Order> CreateAsync(Guid key, OrderRequest request, CancellationToken token)
    {
        if (!CommerceFlow.Valid(request)) throw new ArgumentException("Choose a seeded product, 1-5 items, a synthetic address and a mock outcome.");
        using var activity = CommerceFlow.Traces.StartActivity("order.reserve", ActivityKind.Internal);
        await using var connection = await database.OpenConnectionAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        // A transaction-scoped lock serializes checkout and idempotency; workers use row locks.
        await Execute(connection, "SELECT pg_advisory_xact_lock(720462)", token);
        var fingerprint = JsonSerializer.Serialize(request, Json);
        await using (var existing = new NpgsqlCommand("SELECT fingerprint,body::text FROM orders WHERE request_key=$1", connection))
        {
            existing.Parameters.AddWithValue(key);
            await using var reader = await existing.ExecuteReaderAsync(token);
            if (await reader.ReadAsync(token))
            {
                if (reader.GetString(0) != fingerprint) throw new CommerceConflictException("Idempotency key already belongs to different order details.");
                return Deserialize<Order>(reader.GetString(1));
            }
        }
        await Reserve(connection, request.ProductId, request.Quantity, token);
        var now = DateTimeOffset.UtcNow;
        var order = new Order(Guid.NewGuid(), request.ProductId, request.Quantity, request.CustomerId, request.MockOutcome,
            "PaymentPending", 1, 1, now, now, now.AddSeconds(3), Activity.Current?.TraceId.ToString() ?? "");
        activity?.SetTag("order.id", order.Id);
        await using (var insert = new NpgsqlCommand("INSERT INTO orders VALUES ($1,$2,$3,$4::jsonb)", connection))
        {
            insert.Parameters.AddWithValue(order.Id);
            insert.Parameters.AddWithValue(key);
            insert.Parameters.AddWithValue(fingerprint);
            insert.Parameters.AddWithValue(JsonSerializer.Serialize(order, Json));
            await insert.ExecuteNonQueryAsync(token);
        }
        await Record(connection, order, "ordering", "Order accepted and stock reserved. Awaiting mock payment.", "payments", Guid.NewGuid(), token);
        await transaction.CommitAsync(token);
        return order;
    }

    public async Task<Order?> RetryAsync(Guid id, int expectedVersion, string outcome, CancellationToken token)
    {
        if (!CommerceFlow.Outcomes.Contains(outcome)) throw new ArgumentException("Unknown mock payment outcome.");
        using var activity = CommerceFlow.Traces.StartActivity("order.retry");
        activity?.SetTag("order.id", id);
        await using var connection = await database.OpenConnectionAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        var order = await ReadOrder(connection, id, true, token);
        if (order is null) return null;
        if (order.Version != expectedVersion || order.Status != "PaymentDeclined")
            throw new CommerceConflictException("This order changed or is not declined. Refresh before retrying.");
        await Reserve(connection, order.ProductId, order.Quantity, token);
        order = order with { Status = "PaymentPending", MockOutcome = outcome, Version = order.Version + 1,
            Attempt = order.Attempt + 1, UpdatedAt = DateTimeOffset.UtcNow, NextAt = DateTimeOffset.UtcNow.AddSeconds(3) };
        await Save(connection, order, token);
        await Record(connection, order, "ordering", "Retry requested; stock reserved again.", "payments", Guid.NewGuid(), token);
        await transaction.CommitAsync(token);
        return order;
    }

    public async Task<bool> ProcessAsync(WorkMessage message, string role, CancellationToken token)
    {
        ActivityContext.TryParse(message.TraceParent, message.TraceState, isRemote: true, out var parent);
        using var activity = CommerceFlow.Traces.StartActivity($"{role}.process", ActivityKind.Consumer, parent);
        activity?.SetTag("order.id", message.OrderId);
        activity?.SetTag("messaging.message.id", message.Id);
        activity?.SetTag("messaging.destination.name", role);
        await using var connection = await database.OpenConnectionAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        var order = await ReadOrder(connection, message.OrderId, true, token);
        if (order is null || order.Version != message.Version) return false;
        var transition = CommerceFlow.Advance(order, role);
        if (transition is null) throw new InvalidOperationException($"Unexpected {role} message for {order.Status}.");
        if (transition.ReleaseStock)
        {
            await using var release = new NpgsqlCommand("UPDATE products SET stock=stock+$1 WHERE id=$2", connection);
            release.Parameters.AddWithValue(order.Quantity);
            release.Parameters.AddWithValue(order.ProductId);
            await release.ExecuteNonQueryAsync(token);
        }
        var now = DateTimeOffset.UtcNow;
        order = order with { Status = transition.Status, Version = order.Version + 1, UpdatedAt = now,
            NextAt = transition.Queue is null ? null : now.AddSeconds(transition.DelaySeconds) };
        await Save(connection, order, token);
        await Record(connection, order, role, transition.Detail, transition.Queue, message.Id, token);
        await transaction.CommitAsync(token);
        return true;
    }

    private static async Task Reserve(NpgsqlConnection connection, string productId, int quantity, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("UPDATE products SET stock=stock-$1 WHERE id=$2 AND stock >= $1", connection);
        command.Parameters.AddWithValue(quantity);
        command.Parameters.AddWithValue(productId);
        if (await command.ExecuteNonQueryAsync(token) != 1) throw new CommerceConflictException("Not enough stock. Choose a smaller quantity or another product.");
    }

    private static async Task<Order?> ReadOrder(NpgsqlConnection connection, Guid id, bool forUpdate, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("SELECT body::text FROM orders WHERE id=$1" + (forUpdate ? " FOR UPDATE" : ""), connection);
        command.Parameters.AddWithValue(id);
        var body = await command.ExecuteScalarAsync(token);
        return body is string json ? Deserialize<Order>(json) : null;
    }

    private static async Task Save(NpgsqlConnection connection, Order order, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("UPDATE orders SET body=$1::jsonb WHERE id=$2", connection);
        command.Parameters.AddWithValue(JsonSerializer.Serialize(order, Json));
        command.Parameters.AddWithValue(order.Id);
        await command.ExecuteNonQueryAsync(token);
    }

    private static async Task Record(NpgsqlConnection connection, Order order, string service, string detail, string? queue, Guid messageId, CancellationToken token)
    {
        var current = Activity.Current;
        var entry = new OrderEvent(0, order.Id, order.Status, service, detail,
            current?.TraceId.ToString() ?? "", current?.SpanId.ToString() ?? "", messageId, order.UpdatedAt);
        await using (var command = new NpgsqlCommand("INSERT INTO events(order_id,body) VALUES($1,$2::jsonb)", connection))
        {
            command.Parameters.AddWithValue(order.Id);
            command.Parameters.AddWithValue(JsonSerializer.Serialize(entry, Json));
            await command.ExecuteNonQueryAsync(token);
        }
        if (queue is not null)
        {
            using var send = CommerceFlow.Traces.StartActivity($"{queue}.enqueue", ActivityKind.Producer);
            send?.SetTag("messaging.destination.name", queue);
            send?.SetTag("order.id", order.Id);
            var message = new WorkMessage(Guid.NewGuid(), order.Id, order.Version, Activity.Current?.Id ?? "", Activity.Current?.TraceStateString);
            send?.SetTag("messaging.message.id", message.Id);
            await using var command = new NpgsqlCommand("INSERT INTO outbox VALUES($1,$2,$3,$4)", connection);
            command.Parameters.AddWithValue(message.Id);
            command.Parameters.AddWithValue(queue);
            command.Parameters.AddWithValue(JsonSerializer.Serialize(message, Json));
            command.Parameters.AddWithValue(order.NextAt!.Value);
            await command.ExecuteNonQueryAsync(token);
        }
    }

    private static T Deserialize<T>(string text) => JsonSerializer.Deserialize<T>(text, Json)
        ?? throw new InvalidDataException($"Invalid persisted {typeof(T).Name}.");

    private static async Task Execute(NpgsqlConnection connection, string sql, CancellationToken token)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(token);
    }
}
