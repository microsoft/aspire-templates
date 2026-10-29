using System.Threading.RateLimiting;
using CommerceDemo.Core;
using Microsoft.AspNetCore.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
builder.AddCommerce();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddFixedWindowLimiter("checkout", policy => { policy.PermitLimit = 30; policy.Window = TimeSpan.FromMinutes(1); });
});
var app = builder.Build();
await app.InitializeCommerceAsync();
app.UseExceptionHandler(error => error.Run(async context =>
{
    context.Response.StatusCode = 500;
    await context.Response.WriteAsJsonAsync(new { error = "Commerce service error. Check the correlated Aspire logs." });
}));
app.UseRateLimiter();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapGet("/api/snapshot", async (CommerceStore store, CancellationToken token) => new
{
    products = await store.ProductsAsync(token), orders = await store.OrdersAsync(token),
    customers = CommerceFlow.Customers, serverTime = DateTimeOffset.UtcNow,
    dashboardUrl = builder.Configuration["Commerce:DashboardUrl"]
});
app.MapGet("/api/orders/{id:guid}", async (Guid id, CommerceStore store, CancellationToken token) =>
    await store.DetailAsync(id, token) is { } detail ? Results.Ok(detail) : Results.NotFound());
app.MapPost("/api/orders", async (HttpContext context, OrderRequest request, CommerceStore store, ILoggerFactory logs, CancellationToken token) =>
{
    if (!Guid.TryParse(context.Request.Headers["Idempotency-Key"], out var key))
        return Results.BadRequest(new { error = "A UUID Idempotency-Key header is required." });
    try { return Results.Ok(await store.CreateAsync(key, request, token)); }
    catch (ArgumentException exception) { return Results.BadRequest(new { error = exception.Message }); }
    catch (CommerceConflictException exception)
    {
        logs.CreateLogger("Ordering").LogWarning("Checkout rejected: {Reason}", exception.Message);
        return Results.Conflict(new { error = exception.Message });
    }
}).RequireRateLimiting("checkout");
app.MapPost("/api/orders/{id:guid}/retry", async (Guid id, RetryRequest request, CommerceStore store, CancellationToken token) =>
{
    try { return await store.RetryAsync(id, request.Version, request.MockOutcome, token) is { } order ? Results.Ok(order) : Results.NotFound(); }
    catch (ArgumentException exception) { return Results.BadRequest(new { error = exception.Message }); }
    catch (CommerceConflictException exception) { return Results.Conflict(new { error = exception.Message }); }
}).RequireRateLimiting("checkout");
app.MapFallbackToFile("index.html");
await app.RunAsync();

record RetryRequest(int Version, string MockOutcome);
