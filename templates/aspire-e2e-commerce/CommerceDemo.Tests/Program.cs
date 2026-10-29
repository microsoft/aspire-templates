using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Storage.Queues;
using CommerceDemo.Core;

var seed = new Order(Guid.NewGuid(), "lamp", 1, "harbor-house", "approved", "PaymentPending",
    1, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, "");
string[] states = ["PaymentAuthorized", "Picking", "Packed", "InTransit", "Delivered"];
string[] roles = ["payments", "fulfilment", "fulfilment", "shipping", "shipping"];
var unit = seed;
foreach (var (state, role) in states.Zip(roles))
{
    var transition = CommerceFlow.Advance(unit, role);
    Require(transition?.Status == state, $"Expected {state} from {role}.");
    unit = unit with { Status = transition!.Status };
}
Require(CommerceFlow.Advance(unit, "shipping") is null, "Terminal delivery must not transition again.");
Require(CommerceFlow.Advance(seed, "shipping") is null, "Wrong worker must not advance state.");
Require(CommerceFlow.Advance(seed with { MockOutcome = "declined" }, "payments") is { Status: "PaymentDeclined", ReleaseStock: true, Queue: null }, "Decline must release inventory.");
Require(CommerceFlow.Advance(seed with { MockOutcome = "retry-once" }, "payments") is { Status: "PaymentRetryScheduled", Queue: "payments" }, "Timeout must schedule durable retry.");
Require(CommerceFlow.Advance(seed with { Status = "PaymentRetryScheduled", MockOutcome = "retry-once" }, "payments")?.Status == "PaymentAuthorized", "Automatic retry must recover.");
Require(!CommerceFlow.Valid(new("lamp", 0, "harbor-house", "approved")), "Zero quantity must fail.");
Require(!CommerceFlow.Valid(new("lamp", 6, "harbor-house", "approved")), "Quantity cap must hold.");
Require(!CommerceFlow.Valid(new("lamp", 1, "personal-address", "approved")), "Only synthetic customers accepted.");
Console.WriteLine("PASS business state machine, decline compensation, retry, terminal and input guards.");

if (args is not ["--base-url", var baseUrl]) return;
using var client = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(15) };
using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
var token = timeout.Token;
var before = await client.GetFromJsonAsync<Snapshot>("/api/snapshot", token) ?? throw new Exception("No snapshot.");
var key = Guid.NewGuid();
var request = new OrderRequest("lamp", 1, "harbor-house", "approved");
var happy = await Create(key, request);
var duplicate = await Create(key, request);
Require(happy.Id == duplicate.Id, "Same idempotency key created a second order.");
var queueConnection = Environment.GetEnvironmentVariable("COMMERCE_TEST_QUEUES");
var testQueue = string.IsNullOrEmpty(queueConnection) ? null : new QueueServiceClient(queueConnection,
    new QueueClientOptions { MessageEncoding = QueueMessageEncoding.Base64 }).GetQueueClient("payments");
var initialDetail = await client.GetFromJsonAsync<OrderDetail>($"/api/orders/{happy.Id}", token) ?? throw new Exception("No initial history.");
var replay = JsonSerializer.Serialize(new WorkMessage(Guid.NewGuid(), happy.Id, 1,
    $"00-{happy.TraceId}-{initialDetail.Events[0].SpanId}-01", null), new JsonSerializerOptions(JsonSerializerDefaults.Web));
if (testQueue is not null)
{
    await testQueue.SendMessageAsync(replay, cancellationToken: token);
    await testQueue.SendMessageAsync(replay, cancellationToken: token);
}
using (var conflicting = await Post(key, request with { Quantity = 2 }))
    Require(conflicting.StatusCode == HttpStatusCode.Conflict, "Changed payload must conflict with existing key.");
var decline = await Create(Guid.NewGuid(), request with { ProductId = "plant", MockOutcome = "declined", CustomerId = "garden-house" });
var transient = await Create(Guid.NewGuid(), request with { ProductId = "chair", MockOutcome = "retry-once", CustomerId = "hill-house" });
var happyDetail = await Wait(happy.Id, "Delivered");
var declinedDetail = await Wait(decline.Id, "PaymentDeclined");
var transientDetail = await Wait(transient.Id, "Delivered");
Require(happyDetail.Events.Select(e => e.Status).SequenceEqual(new[] { "PaymentPending" }.Concat(states)), "Happy flow has duplicate/missing transitions.");
Require(happyDetail.Events.Select(e => e.Service).Distinct().Count() == 4, "All four business services must participate.");
Require(happyDetail.Events.All(e => e.TraceId == happyDetail.Order.TraceId && e.TraceId.Length == 32 && e.SpanId.Length == 16), "Trace context did not propagate across workers.");
Require(happyDetail.Events.Select(e => e.MessageId).Distinct().Count() == happyDetail.Events.Count, "Transition message identities must be unique.");
Require(transientDetail.Events.Count(e => e.Status == "PaymentRetryScheduled") == 1, "Retry was not recorded once.");
var afterDecline = await client.GetFromJsonAsync<Snapshot>("/api/snapshot", token) ?? throw new Exception("No snapshot.");
Require(afterDecline.Products.Single(p => p.Id == "plant").Stock == before.Products.Single(p => p.Id == "plant").Stock, "Declined stock was not restored.");
using var retryResponse = await client.PostAsJsonAsync($"/api/orders/{decline.Id}/retry",
    new { version = declinedDetail.Order.Version, mockOutcome = "approved" }, token);
retryResponse.EnsureSuccessStatusCode();
using var duplicateRetry = await client.PostAsJsonAsync($"/api/orders/{decline.Id}/retry",
    new { version = declinedDetail.Order.Version, mockOutcome = "approved" }, token);
Require(duplicateRetry.StatusCode == HttpStatusCode.Conflict, "Stale retry must not reserve stock twice.");
var recovered = await Wait(decline.Id, "Delivered");
Require(recovered.Order.Attempt == 2, "Manual recovery must increment attempt.");
Require(recovered.Events.Count(e => e.Status == "Delivered") == 1, "Duplicate fulfilment/delivery detected.");
var reload = await client.GetFromJsonAsync<OrderDetail>($"/api/orders/{happy.Id}", token);
Require(reload?.Events.Count == happyDetail.Events.Count, "History changed across reconnect.");
if (testQueue is not null)
{
    await testQueue.SendMessageAsync(replay, cancellationToken: token);
    await testQueue.SendMessageAsync(replay, cancellationToken: token);
    for (var empty = 0; empty < 4;)
    {
        var properties = await testQueue.GetPropertiesAsync(token);
        empty = properties.Value.ApproximateMessagesCount == 0 ? empty + 1 : 0;
        await Task.Delay(1000, token);
    }
    var replayed = await client.GetFromJsonAsync<OrderDetail>($"/api/orders/{happy.Id}", token);
    Require(replayed?.Events.Count == happyDetail.Events.Count && replayed.Order.Version == happyDetail.Order.Version,
        "Queue replay changed a committed order.");
    Console.WriteLine("PASS duplicate queue work before/after committed transitions.");
}
else Console.WriteLine("SKIP broker replay: set COMMERCE_TEST_QUEUES to the isolated demo's queue connection string.");
Console.WriteLine($"PASS HTTP flow, duplicate checkout, mismatched key, decline/restock, automatic/manual recovery, stale retry, durable reload and cross-service trace: {happy.Id}");

async Task<HttpResponseMessage> Post(Guid id, OrderRequest body)
{
    var message = new HttpRequestMessage(HttpMethod.Post, "/api/orders") { Content = JsonContent.Create(body) };
    message.Headers.Add("Idempotency-Key", id.ToString());
    using (message) return await client.SendAsync(message, token);
}
async Task<Order> Create(Guid id, OrderRequest body)
{
    using var response = await Post(id, body);
    response.EnsureSuccessStatusCode();
    return await response.Content.ReadFromJsonAsync<Order>(token) ?? throw new Exception("No order.");
}
async Task<OrderDetail> Wait(Guid id, string status)
{
    while (true)
    {
        var detail = await client.GetFromJsonAsync<OrderDetail>($"/api/orders/{id}", token) ?? throw new Exception("No order history.");
        if (detail.Order.Status == status) return detail;
        await Task.Delay(500, token);
    }
}
static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
record Snapshot(Product[] Products, Order[] Orders);
