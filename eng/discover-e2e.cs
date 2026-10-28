#:property TargetFramework=net10.0
#:property TreatWarningsAsErrors=true

using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

var root = Path.GetFullPath("..", Path.GetDirectoryName(SourcePath())!);
var cases = new JsonArray();
foreach (var directory in Directory.GetDirectories(Path.Combine(root, "templates")).Order(StringComparer.Ordinal))
{
    var path = Path.Combine(directory, ".template.config", "template.json");
    var config = JsonNode.Parse(File.ReadAllText(path))!;
    var shortName = config["shortName"]!.GetValue<string>();
    if (shortName != Path.GetFileName(directory))
    {
        throw new InvalidDataException($"Template shortName must match its directory: {path}");
    }
    JsonNode entry = new JsonObject
    {
        ["template"] = shortName,
        ["channel"] = "daily",
        ["test"] = $"Aspire.Templates.EndToEnd.Tests.TemplateTests.{shortName.Replace('-', '_')}"
    };
    cases.Add(entry);
}
if (cases.Count == 0)
{
    throw new InvalidDataException("No templates were discovered.");
}
Console.WriteLine(new JsonObject { ["include"] = cases }.ToJsonString());

static string SourcePath([CallerFilePath] string path = "") => path;
