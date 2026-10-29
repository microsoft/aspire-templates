#pragma warning disable ASPIREAZURE002
using Aspire.Hosting.Azure;
using Azure.Provisioning.AppContainers;
using Azure.Provisioning.Expressions;

var builder = DistributedApplication.CreateBuilder(args);
builder.AddAzureContainerAppEnvironment("env");
var storage = builder.AddAzureStorage("storage")
    .RunAsEmulator(emulator => emulator.WithDataVolume().WithLifetime(ContainerLifetime.Persistent));
var images = storage.AddBlobContainer("images");
var queues = storage.AddQueues("queues");
var database = builder.AddAzureSqlServer("sql")
    .RunAsContainer(container => container.WithDataVolume().WithLifetime(ContainerLifetime.Persistent))
    .AddDatabase("mediadb");

var api = builder.AddProject("api", "../MediaStudio.Api/MediaStudio.Api.csproj")
    .WithReference(images).WithReference(queues).WithReference(database)
    .WaitFor(database).WaitFor(images).WaitFor(queues)
    .WithHttpEndpoint(env: "ASPNETCORE_HTTP_PORTS")
    .WithHttpHealthCheck("/health")
    .WithExternalHttpEndpoints()
    .PublishAsAzureContainerApp((_, app) => app.Template.Scale.MinReplicas = 1);

var worker = builder.AddProject("worker", "../MediaStudio.Worker/MediaStudio.Worker.csproj")
    .WithReference(images).WithReference(queues).WithReference(database)
    .WaitFor(api);

if (builder.ExecutionContext.IsRunMode)
{
    api.WithEnvironment("Studio__Token", "local-media-walkthrough");
    worker.WithEnvironment("Studio__Continuous", "true");
}
else
{
    var token = builder.AddParameter("studio-token", secret: true);
    api.WithEnvironment("Studio__Token", token);
    worker.PublishAsAzureContainerAppJob((infra, job) =>
    {
        var account = storage.Resource.NameOutputReference.AsProvisioningParameter(infra);
        if (!worker.Resource.TryGetLastAnnotation<AppIdentityAnnotation>(out var identity))
        {
            throw new InvalidOperationException("Worker managed identity was not configured.");
        }
        job.Configuration.TriggerType = ContainerAppJobTriggerType.Event;
        job.Configuration.ReplicaTimeout = 300;
        job.Configuration.ReplicaRetryLimit = 1;
        job.Configuration.EventTriggerConfig.Parallelism = 1;
        job.Configuration.EventTriggerConfig.ReplicaCompletionCount = 1;
        job.Configuration.EventTriggerConfig.Scale.MinExecutions = 0;
        job.Configuration.EventTriggerConfig.Scale.MaxExecutions = 2;
        job.Configuration.EventTriggerConfig.Scale.PollingIntervalInSeconds = 15;
        job.Configuration.EventTriggerConfig.Scale.Rules.Add(new ContainerAppJobScaleRule
        {
            Name = "media-queue",
            JobScaleRuleType = "azure-queue",
            Metadata = new ObjectExpression(
                new PropertyExpression("accountName", new IdentifierExpression(account.BicepIdentifier)),
                new PropertyExpression("queueName", new StringLiteralExpression("media-jobs")),
                new PropertyExpression("queueLength", new IntLiteralExpression(1))),
            Identity = identity.IdentityResource.Id.AsProvisioningParameter(infra)
        });
    });
}

var frontend = builder.AddViteApp("frontend", "../frontend")
    .WithEnvironment("API_HTTP", api.GetEndpoint("http"))
    .WithReference(api).WaitFor(api);
api.PublishWithContainerFiles(frontend, "wwwroot");
builder.Build().Run();
