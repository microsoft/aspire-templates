using MediaStudio.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
builder.AddMediaServices();
builder.Services.AddHostedService<Processor>();
await builder.Build().RunAsync();
