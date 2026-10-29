using Azure.Storage.Queues;
using CommerceDemo.Core;

var builder = WebApplication.CreateBuilder(args);
builder.AddCommerce();
var role = builder.Configuration["Commerce:Role"] ?? throw new InvalidOperationException("Commerce:Role is required.");
if (role is not ("payments" or "fulfilment" or "shipping")) throw new InvalidOperationException("Unknown commerce worker role.");
builder.Services.AddHostedService(services => new OrderWorker(
    services.GetRequiredService<CommerceStore>(), services.GetRequiredService<QueueServiceClient>(),
    role, services.GetRequiredService<ILogger<OrderWorker>>()));
var app = builder.Build();
await app.InitializeCommerceAsync();
await app.RunAsync();
