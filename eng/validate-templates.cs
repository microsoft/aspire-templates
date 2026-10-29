#:property TargetFramework=net10.0
#:property TreatWarningsAsErrors=true

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

var root = Path.GetFullPath("..", Path.GetDirectoryName(SourcePath())!);
var templates = Path.Combine(root, "templates");
string[] names =
[
    "aspire-starter", "aspire-ts-cs-starter", "aspire-ts-starter",
    "aspire-py-starter", "aspire-empty", "aspire-apphost-singlefile", "aspire-e2e-commerce"
];
const string sdkVersion = "17.0.0-test.1";
var unresolved = new Regex(
    @"!!REPLACE_WITH_|\{\{(?!(?:ApiService|Server)_HostAddress\}\})[^{}\r\n]+\}\}|" +
    @"\b\w+PackageVersion\b|^\s*(?://|#|<!--)\s*#(?:if|else|endif)",
    RegexOptions.Multiline);
var work = Directory.CreateTempSubdirectory("aspire-templates-").FullName;
var hive = Path.Combine(work, "hive");
var checks = new List<(string Name, Action Run)> { ("metadata", CheckMetadata) };
foreach (var name in names)
{
    checks.Add(($"{name}: defaults", () => CheckDefaults(name)));
    if (name != "aspire-e2e-commerce")
        checks.Add(($"{name}: Redis and .dev.localhost", () => CheckOptions(name)));
}
foreach (var name in new[] { "aspire-starter", "aspire-ts-cs-starter", "aspire-empty", "aspire-apphost-singlefile" })
{
    checks.Add(($"{name}: .NET 11 and HTTP", () => CheckFrameworkAndHttp(name)));
}
(string Framework, string Version)[] testFrameworks =
[
    ("MSTest", "v2"), ("NUnit", "v2"),
    ("xUnit.net", "v2"), ("xUnit.net", "v3"), ("xUnit.net", "v3mtp")
];
foreach (var (index, testFramework) in testFrameworks.Index())
{
    checks.Add(($"aspire-starter: {testFramework.Framework}/{testFramework.Version}", () =>
    {
        var output = Render("aspire-starter", $"tests-{index}",
            ("TestFx", testFramework.Framework), ("XUnitVersion", testFramework.Version));
        Require(File.Exists(Path.Combine(output, "LabDemo.Tests", "LabDemo.Tests.csproj")), "Missing test project.");
    }));
}

var failures = 0;
try
{
    RunDotnet("install", templates);
    foreach (var (name, check) in checks)
    {
        try
        {
            check();
            Console.WriteLine($"PASS {name}");
        }
        catch (Exception exception)
        {
            failures++;
            Console.Error.WriteLine($"FAIL {name}: {exception}");
        }
    }
}
finally
{
    Directory.Delete(work, recursive: true);
}
Console.WriteLine($"{checks.Count - failures}/{checks.Count} template checks passed.");
return failures == 0 ? 0 : 1;

void CheckMetadata()
{
    Require(names.ToHashSet(StringComparer.Ordinal).SetEquals(
        Directory.EnumerateFileSystemEntries(templates).Select(path => Path.GetRelativePath(templates, path))),
        "Template directories do not match the covered template names.");
    var identities = new HashSet<string>(StringComparer.Ordinal);
    foreach (var name in names)
    {
        var (template, host) = Metadata(name);
        identities.Add(Text(template["identity"]));
        Require(Text(template["shortName"]) == name, $"Incorrect shortName for {name}.");
        Require(host["createNuGetConfig"]!.GetValue<bool>(), $"Missing NuGet policy for {name}.");
        var symbols = template["symbols"]!.AsObject();
        foreach (var (symbol, entry) in host["symbolInfo"]!.AsObject())
        {
            Require(symbols.ContainsKey(symbol), $"Unknown host symbol {name}/{symbol}.");
            if (entry!["valueSource"] is { } source)
            {
                Require(entry["isHidden"]!.GetValue<bool>(), $"Computed symbol must be hidden: {name}/{symbol}");
                Require(Text(symbols[symbol]!["datatype"]) == "string", $"Computed symbol must be a string: {name}/{symbol}");
                Require(Text(source["kind"]) == "nugetPackageVersion", $"Unexpected value source: {name}/{symbol}");
                Require(source["allowPrerelease"]!.GetValue<bool>(), $"Prereleases must be allowed: {name}/{symbol}");
            }
        }
    }
    Require(identities.Count == names.Length, "Template identities must be unique.");
}

void CheckDefaults(string name)
{
    var output = Render(name, "default");
    if (name == "aspire-starter")
    {
        Require(!Directory.Exists(Path.Combine(output, "LabDemo.Tests")), "Default starter must not include tests.");
    }
    if (name == "aspire-py-starter")
    {
        Require(!File.ReadAllText(Path.Combine(output, "app", "main.py")).Contains("redis", StringComparison.OrdinalIgnoreCase),
            "Default Python app must not include Redis.");
        Require(!File.ReadAllText(Path.Combine(output, "apphost.mts")).Contains("addRedis", StringComparison.Ordinal),
            "Default Python AppHost must not include Redis.");
    }
    if (name == "aspire-e2e-commerce")
    {
        Require(File.Exists(Path.Combine(output, "LabDemo.Storefront", "LabDemo.Storefront.csproj")), "Missing storefront.");
        Require(File.Exists(Path.Combine(output, "LabDemo.Worker", "LabDemo.Worker.csproj")), "Missing commerce worker.");
        Require(File.Exists(Path.Combine(output, "LabDemo.Tests", "Program.cs")), "Missing executable business-flow tests.");
        var appHost = File.ReadAllText(Path.Combine(output, "LabDemo.AppHost", "AppHost.cs"));
        foreach (var role in new[] { "payments", "fulfilment", "shipping" })
            Require(appHost.Contains($"\"{role}\"", StringComparison.Ordinal), $"Missing {role} worker.");
        Require(!File.ReadAllText(Path.Combine(output, "LabDemo.AppHost", "LabDemo.AppHost.csproj"))
            .Contains("c549bc67-6db4-4a46-92a5-777c064d73ea", StringComparison.OrdinalIgnoreCase), "Shared UserSecretsId leaked.");
        Require(Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories)
            .All(file => !File.ReadAllText(file).Contains("CommerceDemo", StringComparison.Ordinal)), "Commerce source name leaked.");
        var second = Render(name, "second");
        string Secret(string directory) => XDocument.Load(Path.Combine(directory, "LabDemo.AppHost", "LabDemo.AppHost.csproj"))
            .Descendants("UserSecretsId").Single().Value;
        Require(Secret(output) != Secret(second), "Independent consumers must have distinct secret stores.");
        var profiles = ReadJson(Path.Combine(output, LaunchFile(name)))["profiles"]!;
        var http = profiles["http"]!;
        var ports = new[] { Text(http["applicationUrl"]), Text(http["environmentVariables"]!["ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL"]),
            Text(http["environmentVariables"]!["ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL"]) }.Select(url => new Uri(url).Port).ToArray();
        Require(ports.Distinct().Count() == 3 && ports.All(port => port is >= 15000 and < 21000), "Invalid commerce ports.");
    }
}

void CheckOptions(string name)
{
    var (template, _) = Metadata(name);
    var options = new List<(string Key, string Value)> { ("LocalhostTld", "true") };
    if (template["symbols"]!.AsObject().ContainsKey("UseRedisCache"))
    {
        options.Add(("UseRedisCache", "true"));
    }
    var output = Render(name, "options", options.ToArray());
    if (name == "aspire-py-starter")
    {
        Require(File.ReadAllText(Path.Combine(output, "apphost.mts")).Contains("addRedis", StringComparison.Ordinal), "Missing Redis resource.");
        Require(File.ReadAllText(Path.Combine(output, "app", "main.py")).Contains("import redis", StringComparison.Ordinal), "Missing Python Redis import.");
        Require(ReadJson(Path.Combine(output, "aspire.config.json"))["packages"]!.AsObject().ContainsKey("Aspire.Hosting.Redis"),
            "Missing Redis integration package.");
        Require(File.ReadAllText(Path.Combine(output, "app", "pyproject.toml")).Contains("\"redis>=", StringComparison.Ordinal),
            "Missing Python Redis dependency.");
    }
    var settings = ReadJson(Path.Combine(output, File.Exists(Path.Combine(output, "apphost.mts"))
        ? "aspire.config.json" : LaunchFile(name)));
    Require(Text(settings["profiles"]!["http"]!["applicationUrl"]).Contains("labdemo.dev.localhost", StringComparison.Ordinal),
        $"Missing .dev.localhost URL for {name}.");
}

void CheckFrameworkAndHttp(string name)
{
    var output = Render(name, "net11-http", ("Framework", "net11.0"), ("NoHttps", "true"));
    if (name != "aspire-apphost-singlefile")
    {
        var project = XDocument.Load(Path.Combine(output, "LabDemo.AppHost", "LabDemo.AppHost.csproj"));
        Require(project.Descendants("TargetFramework").Single().Value == "net11.0", $"Incorrect target framework for {name}.");
    }
    var settings = ReadJson(Path.Combine(output, LaunchFile(name)));
    Require(!settings["profiles"]!.AsObject().ContainsKey("https"), $"Unexpected HTTPS profile for {name}.");
}

string Render(string name, string suffix, params (string Key, string Value)[] options)
{
    var (template, host) = Metadata(name);
    var output = Path.Combine(work, $"{name}-{suffix}");
    var arguments = new List<string> { name, "--name", "LabDemo", "--output", output };
    var versions = new Dictionary<string, string>(StringComparer.Ordinal);
    var index = 2;
    foreach (var (symbol, info) in host["symbolInfo"]!.AsObject())
    {
        var version = $"17.0.0-test.{index++}";
        if (info!["valueSource"] is { } source)
        {
            versions.Add(Text(source["packageId"]), version);
            arguments.AddRange([$"--{symbol}", version]);
        }
    }
    var symbols = template["symbols"]!.AsObject();
    if (symbols.ContainsKey("AspireVersion"))
    {
        arguments.AddRange(["--AspireVersion", sdkVersion]);
    }
    var parameters = options.ToDictionary(option => option.Key, option => option.Value, StringComparer.Ordinal);
    if (symbols.ContainsKey("skipRestore"))
    {
        parameters.TryAdd("skipRestore", "true");
    }
    var aliasesPath = Path.Combine(templates, name, ".template.config", "dotnetcli.host.json");
    var aliases = File.Exists(aliasesPath) ? ReadJson(aliasesPath)["symbolInfo"]!.AsObject() : new JsonObject();
    foreach (var (key, value) in parameters)
    {
        arguments.AddRange([$"--{aliases[key]?["longName"]?.GetValue<string>() ?? key}", value]);
    }
    RunDotnet(arguments.ToArray());
    CheckOutput(output, versions);
    return output;
}

void CheckOutput(string output, Dictionary<string, string> versions)
{
    Require(!Directory.Exists(Path.Combine(output, ".template.config")), "Template metadata leaked into generated output.");
    foreach (var file in Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories))
    {
        if (Path.GetExtension(file) is ".png" or ".ico")
        {
            continue;
        }
        var text = File.ReadAllText(file);
        Require(!unresolved.IsMatch(text), $"Unresolved template token in {file}.");
        Require(!Path.GetFileName(file).Contains("Aspire-StarterApplication.1", StringComparison.Ordinal) &&
            !Path.GetFileName(file).Contains("AspireApplication.1", StringComparison.Ordinal), $"Unreplaced template filename: {file}");
        switch (Path.GetExtension(file), Path.GetFileName(file))
        {
            case (".csproj", _):
                foreach (var package in XDocument.Parse(text).Descendants("PackageReference"))
                {
                    var id = package.Attribute("Include")!.Value;
                    if (id.StartsWith("Aspire.", StringComparison.Ordinal))
                    {
                        Require(package.Attribute("Version")!.Value == versions[id], $"Incorrect {id} version in {file}.");
                    }
                }
                break;
            case (_, "apphost.cs"):
                foreach (Match match in Regex.Matches(text, @"^#:package (\S+)@(\S+)\r?$", RegexOptions.Multiline))
                {
                    Require(match.Groups[2].Value == versions[match.Groups[1].Value], $"Incorrect file-based package version in {file}.");
                }
                break;
            case (_, "package.json" or "package-lock.json"):
                var packageJson = ReadJson(file);
                if (Path.GetDirectoryName(file) == output)
                {
                    Require(Text(packageJson["name"]) == "labdemo", $"Incorrect npm package name in {file}.");
                }
                break;
            case (_, "aspire.config.json"):
                CheckAspireConfig(output, ReadJson(file), versions);
                break;
        }
    }
}

void CheckAspireConfig(string output, JsonObject config, Dictionary<string, string> versions)
{
    Require(File.Exists(Path.Combine(output, Text(config["appHost"]!["path"]))), "Configured AppHost does not exist.");
    if (config["appHost"]!["language"]?.GetValue<string>() != "typescript/nodejs")
    {
        return;
    }
    Require(Text(config["sdk"]!["version"]) == sdkVersion, "Incorrect injected SDK version.");
    foreach (var (package, version) in config["packages"]!.AsObject())
    {
        Require(Text(version) == versions[package], $"Incorrect integration version for {package}.");
    }
    var ranges = new Dictionary<(string Kind, string Scheme), int>
    {
        [("dashboard", "http")] = 15000, [("dashboard", "https")] = 17000,
        [("otlp", "http")] = 19000, [("otlp", "https")] = 21000,
        [("resource", "http")] = 20000, [("resource", "https")] = 22000
    };
    var ports = new Dictionary<(string Kind, string Scheme), int>();
    foreach (var (_, profile) in config["profiles"]!.AsObject())
    {
        var endpoints = Text(profile!["applicationUrl"]).Split(';').Select(url => (Kind: "dashboard", Url: url)).ToList();
        endpoints.Add(("otlp", Text(profile["environmentVariables"]!["ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL"])));
        endpoints.Add(("resource", Text(profile["environmentVariables"]!["ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL"])));
        foreach (var (kind, url) in endpoints)
        {
            var uri = new Uri(url, UriKind.Absolute);
            Require(uri.Host is "localhost" or "labdemo.dev.localhost", $"Unexpected hostname: {url}");
            var key = (kind, uri.Scheme);
            var low = ranges[key];
            Require(uri.Port >= low && uri.Port <= low + 299, $"Port outside the generated range: {url}");
            Require(!ports.TryGetValue(key, out var port) || port == uri.Port, $"Inconsistent port across profiles: {url}");
            ports[key] = uri.Port;
        }
    }
    Require(ports.Values.Distinct().Count() == 6, "Expected six unique generated ports.");
}

(JsonObject Template, JsonObject Host) Metadata(string name)
{
    var config = Path.Combine(templates, name, ".template.config");
    return (ReadJson(Path.Combine(config, "template.json")), ReadJson(Path.Combine(config, "aspirecli.host.json")));
}

void RunDotnet(params string[] arguments) =>
    Run("dotnet", ["new", .. arguments, "--debug:custom-hive", hive]);

static string Run(string executable, string[] arguments)
{
    var start = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var argument in arguments)
    {
        start.ArgumentList.Add(argument);
    }
    using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {executable}.");
    var output = process.StandardOutput.ReadToEndAsync();
    var error = process.StandardError.ReadToEndAsync();
    if (!process.WaitForExit(120_000))
    {
        process.Kill(entireProcessTree: true);
        process.WaitForExit();
        throw new TimeoutException($"{executable} {string.Join(' ', arguments)} exceeded 120 seconds.\n{output.GetAwaiter().GetResult()}\n{error.GetAwaiter().GetResult()}");
    }
    var stdout = output.GetAwaiter().GetResult();
    var stderr = error.GetAwaiter().GetResult();
    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException($"{executable} {string.Join(' ', arguments)} exited with code {process.ExitCode}.\n{stdout}\n{stderr}");
    }
    return stdout;
}

static void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static JsonObject ReadJson(string path) => JsonNode.Parse(File.ReadAllText(path))!.AsObject();
static string Text(JsonNode? value) => value?.GetValue<string>() ?? throw new InvalidDataException("Expected a JSON string.");
static string LaunchFile(string name) => name == "aspire-apphost-singlefile"
    ? "apphost.run.json" : Path.Combine("LabDemo.AppHost", "Properties", "launchSettings.json");
static string SourcePath([CallerFilePath] string path = "") => path;
