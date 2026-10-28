#:package Aspire.Hosting.AppHost@HostingAppHostPackageVersion
#:package Aspire.Hosting.Dotnet@HostingDotnetPackageVersion
#:property AspireUseCliBundle=true

var builder = DistributedApplication.CreateBuilder(args);

builder.Build().Run();
