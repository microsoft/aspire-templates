using Azure.Provisioning.PostgreSql;

var builder = DistributedApplication.CreateBuilder(args);

builder.AddAzureContainerAppEnvironment("commerce-environment");
var database = builder.AddAzurePostgresFlexibleServer("commerce-postgres")
    .ConfigureInfrastructure(infrastructure =>
    {
        var server = infrastructure.GetProvisionableResources().OfType<PostgreSqlFlexibleServer>().Single();
        server.Sku = new PostgreSqlFlexibleServerSku { Name = "Standard_B1ms", Tier = PostgreSqlFlexibleServerSkuTier.Burstable };
    })
    .RunAsContainer(postgres => postgres.WithDataVolume())
    .AddDatabase("commerce");
var queues = builder.AddAzureStorage("commerce-storage")
    .RunAsEmulator(storage => storage.WithDataVolume())
    .AddQueues("commerce-queues");

var storefront = builder.AddDotnetProject("storefront", "../CommerceDemo.Storefront/CommerceDemo.Storefront.csproj")
    .WithReference(database).WithReference(queues)
    .WaitFor(database).WaitFor(queues)
    .WithHttpEndpoint(name: "http")
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health");

foreach (var role in new[] { "payments", "fulfilment", "shipping" })
{
    builder.AddDotnetProject(role, "../CommerceDemo.Worker/CommerceDemo.Worker.csproj")
        .WithEnvironment("Commerce__Role", role)
        .WithReference(database).WithReference(queues)
        .WaitFor(database).WaitFor(queues).WaitFor(storefront)
        .WithHttpEndpoint(name: "http")
        .WithHttpHealthCheck("/health");
}

builder.Build().Run();
