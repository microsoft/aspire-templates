using System.Diagnostics;
using System.Text.RegularExpressions;
using Hex1b;
using Hex1b.Automation;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Aspire.Templates.EndToEnd.Tests;

internal sealed class TemplateTerminal : IAsyncDisposable
{
    private readonly TestContext _context;
    private readonly Hex1bTerminal _terminal;
    private readonly Hex1bTerminalAutomator _automator;
    private readonly CancellationTokenSource _cancellation = new(TimeSpan.FromMinutes(25));
    private readonly string _containerName = $"aspire-template-{Guid.NewGuid():N}";
    private readonly string _artifacts;
    private readonly string? _repository;
    private readonly string? _revision;
    private Task? _run;
    private int _sequence;
    private bool _ready;

    public bool TestFailed { get; set; }

    public static string RepositoryRoot
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "templates")))
            {
                directory = directory.Parent;
            }
            return directory?.FullName ?? throw new DirectoryNotFoundException("Cannot find the repository's templates directory.");
        }
    }

    private TemplateTerminal(TestContext context, string template)
    {
        _context = context;
        var channel = ValidateChannel(Environment.GetEnvironmentVariable("ASPIRE_E2E_CHANNEL") ?? "daily");
        _repository = Environment.GetEnvironmentVariable("ASPIRE_E2E_TEMPLATE_REPOSITORY");
        _revision = Environment.GetEnvironmentVariable("ASPIRE_E2E_TEMPLATE_REF");
        if ((_repository is null) != (_revision is null) ||
            (_revision is not null && !Regex.IsMatch(_revision, "^[0-9a-fA-F]{40}$")))
        {
            throw new ArgumentException("Supply both ASPIRE_E2E_TEMPLATE_REPOSITORY and a full 40-character ASPIRE_E2E_TEMPLATE_REF, or neither.");
        }
        if (_repository is not null &&
            (!Uri.TryCreate(_repository, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
             uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0))
        {
            throw new ArgumentException("The template repository must be a credential-free HTTPS Git URL.");
        }
        _artifacts = Path.Combine(
            Environment.GetEnvironmentVariable("ASPIRE_E2E_RESULTS") ?? Path.Combine(RepositoryRoot, "TestResults"),
            $"{template}-{channel}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_artifacts);
        context.WriteLine($"Channel: {channel}; source: {_repository ?? "local working tree"}; revision: {_revision ?? "uncommitted snapshot"}");
        context.WriteLine($"Artifacts: {_artifacts}");
        _terminal = Hex1bTerminal.CreateBuilder()
            .WithHeadless()
            .WithDimensions(180, 50)
            .WithAsciinemaRecording(Path.Combine(_artifacts, "terminal.cast"))
            .WithDockerContainer(options =>
            {
                options.Image = Environment.GetEnvironmentVariable("ASPIRE_E2E_IMAGE") ?? "aspire-template-validation";
                options.Name = _containerName;
                options.AdditionalRunArgs.Add("--privileged");
                options.WorkingDirectory = "/workspace";
                options.Volumes.Add($"{_artifacts}:/workspace");
                options.Volumes.Add($"{Path.Combine(RepositoryRoot, "templates")}:/template-source:ro");
                options.Environment["ASPIRE_E2E_CHANNEL"] = channel;
            })
            .Build();
        _automator = new Hex1bTerminalAutomator(_terminal, defaultTimeout: TimeSpan.FromMinutes(8));
    }

    public static async Task<TemplateTerminal> StartAsync(TestContext context, string template)
    {
        var session = new TemplateTerminal(context, template);
        try
        {
            session._run = session._terminal.RunAsync(session._cancellation.Token);
            await session._automator.WaitUntilTextAsync("TEMPLATE_SHELL_READY", timeout: TimeSpan.FromMinutes(2));
            session._ready = true;
            await session.RunAsync("docker info && dotnet --info && node --version && uv --version");
            return session;
        }
        catch
        {
            session.TestFailed = true;
            await session.DisposeAsync();
            throw;
        }
    }

    public static string ValidateChannel(string channel) => channel is "stable" or "daily"
        ? channel : throw new ArgumentException($"Unsupported CLI channel: {channel}");

    public static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    public string SourceArguments(string template) => _repository is null
        ? $"--template-path /template-source/{template}"
        : $"--template-repository {Quote(_repository)} --template-repository-ref {Quote(_revision!)} --template-path templates/{template}";

    public async Task RunAsync(string command, TimeSpan? timeout = null)
    {
        var marker = $"HEX_DONE_{++_sequence}";
        _context.WriteLine($"$ {command}");
        // The echoed command cannot match the completion token: printf assembles it only after execution.
        await _automator.TypeAsync(
            $"{{ {command}; }}; result=$?; printf '\\n%s_%s\\n' '{marker}' \"$result\"");
        await _automator.EnterAsync();
        var succeeded = false;
        await _automator.WaitUntilAsync(snapshot =>
        {
            succeeded = new CellPatternSearcher().FindPattern($"{marker}_0").Search(snapshot).Count > 0;
            if (succeeded)
            {
                return true;
            }
            return new CellPatternSearcher().FindPattern($"{marker}_").Search(snapshot).Count > 0;
        }, timeout: timeout ?? TimeSpan.FromMinutes(8), description: command);
        Assert.IsTrue(succeeded, $"Command failed: {command}. See {_artifacts}/terminal.cast.");
    }

    public async Task StopAppAsync(string appHost)
    {
        Exception? diagnosticsFailure = null;
        try
        {
            await RunAsync($"collect-diagnostics {Quote(appHost)}", TimeSpan.FromSeconds(60));
        }
        catch (Exception exception)
        {
            diagnosticsFailure = exception;
            _context.WriteLine($"App diagnostics failed: {exception}");
        }
        try
        {
            await RunAsync($"aspire stop --apphost {Quote(appHost)} --non-interactive", TimeSpan.FromSeconds(60));
        }
        catch (Exception exception)
        {
            _context.WriteLine($"AppHost cleanup failed: {exception}");
            if (!TestFailed)
            {
                TestFailed = true;
                throw;
            }
        }
        if (diagnosticsFailure is not null && !TestFailed)
        {
            TestFailed = true;
            throw new InvalidOperationException("App diagnostic collection failed.", diagnosticsFailure);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Exception? cleanupFailure = null;
        try
        {
            if (_ready)
            {
                await RunAsync("collect-diagnostics", TimeSpan.FromSeconds(30));
                await _automator.TypeAsync("exit");
                await _automator.EnterAsync();
            }
            if (_run is not null)
            {
                await _run.WaitAsync(TimeSpan.FromSeconds(30));
            }
        }
        catch (Exception exception)
        {
            cleanupFailure = exception;
            _context.WriteLine($"Terminal cleanup/diagnostics failed: {exception}");
        }
        finally
        {
            await _cancellation.CancelAsync();
            try
            {
                _terminal.Dispose();
                if (_run is not null)
                {
                    try
                    {
                        await _run.WaitAsync(TimeSpan.FromSeconds(10));
                    }
                    catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
                    {
                    }
                }
            }
            catch (Exception exception)
            {
                cleanupFailure ??= exception;
                _context.WriteLine($"Terminal disposal failed: {exception}");
            }
            finally
            {
                _cancellation.Dispose();
                try
                {
                    using var process = Process.Start(new ProcessStartInfo("docker")
                    {
                        ArgumentList = { "rm", "--force", _containerName },
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    }) ?? throw new InvalidOperationException("Could not start Docker container cleanup.");
                    var error = process.StandardError.ReadToEndAsync();
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
                    var message = await error;
                    if (process.ExitCode != 0 && !message.Contains("No such container", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException($"Docker cleanup failed: {message}");
                    }
                }
                catch (Exception exception)
                {
                    cleanupFailure ??= exception;
                    _context.WriteLine($"Container removal failed: {exception}");
                }
            }
            var recording = Path.Combine(_artifacts, "terminal.cast");
            if (File.Exists(recording))
            {
                _context.AddResultFile(recording);
            }
        }
        if (cleanupFailure is not null && !TestFailed)
        {
            throw new InvalidOperationException("Test cleanup failed.", cleanupFailure);
        }
    }
}
