using System.Reflection;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[assembly: Parallelize(Workers = 4, Scope = ExecutionScope.MethodLevel)]

namespace Aspire.Templates.EndToEnd.Tests;

[TestClass]
[TestCategory("E2E")]
public sealed class TemplateTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public Task aspire_starter() => ValidateAsync("aspire-starter", "LabApp.AppHost/LabApp.AppHost.csproj", ["apiservice", "webfrontend"]);

    [TestMethod]
    public Task aspire_ts_cs_starter() => ValidateAsync("aspire-ts-cs-starter", "LabApp.AppHost/LabApp.AppHost.csproj", ["server", "webfrontend"]);

    [TestMethod]
    public Task aspire_ts_starter() => ValidateAsync("aspire-ts-starter", "apphost.mts", ["app", "frontend"]);

    [TestMethod]
    public Task aspire_py_starter() => ValidateAsync("aspire-py-starter", "apphost.mts", ["app", "frontend"]);

    [TestMethod]
    public Task aspire_empty() => ValidateAsync("aspire-empty", "LabApp.AppHost/LabApp.AppHost.csproj", []);

    [TestMethod]
    public Task aspire_apphost_singlefile() => ValidateAsync("aspire-apphost-singlefile", "apphost.cs", []);

    [TestMethod]
    public Task aspire_e2e_commerce() => ValidateAsync("aspire-e2e-commerce", "LabApp.AppHost/LabApp.AppHost.csproj", ["storefront", "payments", "fulfilment", "shipping"]);

    private async Task ValidateAsync(string template, string appHost, string[] resources)
    {
        await using var terminal = await TemplateTerminal.StartAsync(TestContext, template);
        var startAttempted = false;
        try
        {
            await terminal.RunAsync("install-cli");
            var source = terminal.SourceArguments(template);
            await terminal.RunAsync(
                $"aspire new {source} --name LabApp --output /tmp/template-app --non-interactive --suppress-agent-init --use-config-source false --nologo");
            await terminal.RunAsync($"validate-output /tmp/template-app {TemplateTerminal.Quote(appHost)}");
            await terminal.RunAsync("cd /tmp/template-app");
            await terminal.RunAsync($"aspire restore --apphost {TemplateTerminal.Quote(appHost)} --non-interactive");
            await terminal.RunAsync($"build-template {TemplateTerminal.Quote(appHost)}");
            if (template == "aspire-e2e-commerce")
            {
                await terminal.RunAsync("dotnet publish LabApp.Storefront/LabApp.Storefront.csproj --no-restore --nologo -o /tmp/commerce-publish && test -f /tmp/commerce-publish/wwwroot/index.html");
            }
            startAttempted = true;
            await terminal.RunAsync(
                $"aspire start --apphost {TemplateTerminal.Quote(appHost)} --isolated --non-interactive --format Json");
            foreach (var resource in resources)
            {
                await terminal.RunAsync(
                    $"aspire wait {resource} --apphost {TemplateTerminal.Quote(appHost)} --status healthy --timeout 180 --non-interactive");
            }
            await terminal.RunAsync($"aspire describe --apphost {TemplateTerminal.Quote(appHost)} --format Json");
            if (template == "aspire-e2e-commerce")
            {
                await terminal.RunAsync("dotnet run --project LabApp.Tests --no-build");
                await terminal.RunAsync($"aspire describe storefront --apphost {TemplateTerminal.Quote(appHost)} --format Json > /tmp/commerce-resource.json");
                await terminal.RunAsync($"commerce-flow /tmp/commerce-resource.json {TemplateTerminal.Quote(appHost)}");
            }
        }
        catch
        {
            terminal.TestFailed = true;
            throw;
        }
        finally
        {
            if (startAttempted)
            {
                await terminal.StopAppAsync(appHost);
            }
        }
    }
}

[TestClass]
[TestCategory("Harness")]
public sealed class HarnessTests
{
    [TestMethod]
    public void EveryTemplateHasExactlyOneEndToEndTest()
    {
        var templates = Directory.GetDirectories(Path.Combine(TemplateTerminal.RepositoryRoot, "templates"))
            .Select(path => JsonDocument.Parse(File.ReadAllText(Path.Combine(path, ".template.config", "template.json"))))
            .Select(document =>
            {
                using (document)
                {
                    return document.RootElement.GetProperty("shortName").GetString()!.Replace('-', '_');
                }
            }).Order().ToArray();
        var methods = typeof(TemplateTests).GetMethods()
            .Where(method => method.GetCustomAttribute<TestMethodAttribute>() is not null)
            .Select(method => method.Name).Order().ToArray();
        CollectionAssert.AreEqual(templates, methods);
    }

    [TestMethod]
    public void ShellArgumentsAreQuoted() =>
        Assert.AreEqual("'a'\\''b c'", TemplateTerminal.Quote("a'b c"));

    [TestMethod]
    public void OnlyStableAndDailyChannelsAreAccepted()
    {
        Assert.AreEqual("stable", TemplateTerminal.ValidateChannel("stable"));
        Assert.AreEqual("daily", TemplateTerminal.ValidateChannel("daily"));
        Assert.ThrowsExactly<ArgumentException>(() => TemplateTerminal.ValidateChannel("other"));
    }
}

[TestClass]
[TestCategory("TerminalHarness")]
public sealed class TerminalHarnessTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task TerminalReportsFailuresAndRunsNestedContainers()
    {
        await using var terminal = await TemplateTerminal.StartAsync(TestContext, "terminal-harness");
        try
        {
            await terminal.RunAsync("docker run --rm alpine:3.22 true");
            await Assert.ThrowsExactlyAsync<AssertFailedException>(() =>
                terminal.RunAsync("false", TimeSpan.FromSeconds(10)));
            await terminal.RunAsync("true", TimeSpan.FromSeconds(10));
            await terminal.RunAsync(
                "install -d -m 700 /root/.aspire/logs/private && " +
                "(umask 077; printf 'private diagnostic\\n' > /root/.aspire/logs/private/export.log)");
            await terminal.RunAsync("collect-diagnostics && collect-diagnostics");
            await terminal.RunAsync(
                "test ! -d /workspace/diagnostics/cli-logs/logs && " +
                "runuser -u nobody -- cat /workspace/diagnostics/cli-logs/private/export.log");
            await terminal.RunAsync("chmod -R go-rwx /workspace/diagnostics/cli-logs");
            await Assert.ThrowsExactlyAsync<AssertFailedException>(() =>
                terminal.RunAsync("collect-diagnostics /missing.csproj", TimeSpan.FromSeconds(60)));
            await terminal.RunAsync("runuser -u nobody -- cat /workspace/diagnostics/cli-logs/private/export.log");
            await terminal.RunAsync(
                "install -d -m 700 /tmp/template-app && " +
                "runuser -u nobody -- find /workspace -type f -print");
        }
        catch
        {
            terminal.TestFailed = true;
            throw;
        }
    }
}
