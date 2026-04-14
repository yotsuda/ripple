using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using Splash.Services;
using Splash.Services.Adapters;
using Splash.Tools;

namespace Splash;

public class Program
{
    public static async Task Main(string[] args)
    {
        // Load adapter registry once at startup. Shared across proxy and
        // worker processes: both call Program.Main, so SetDefault runs in
        // both modes and ConsoleWorker / ConsoleManager can read the
        // registry via AdapterRegistry.Default without plumbing it through
        // constructors. Failures are non-fatal — we log and continue with
        // the existing hardcoded shell paths still intact as fallback.
        var (registry, adapterReport) = AdapterRegistry.LoadDefault();
        AdapterRegistry.SetDefault(registry);
        bool isWorkerMode = args.Contains("--console");
        if (!isWorkerMode || adapterReport.HasErrors)
        {
            var level = adapterReport.HasErrors ? "WARNING" : "info";
            Console.Error.WriteLine($"[splash adapters {level}] {adapterReport.Summary()}");
        }

        // --console mode: run as ConPTY console worker process
        if (args.Contains("--console"))
        {
            var exitCode = await ConsoleWorker.RunConsoleMode(args);
            Environment.Exit(exitCode);
            return;
        }

        // --list-adapters: print what the registry loaded and exit.
        // Useful for debugging missing/stale external adapters under
        // ~/.splash/adapters and for verifying an adapter override is
        // actually taking effect.
        if (args.Contains("--list-adapters"))
        {
            PrintAdapterList(registry, adapterReport);
            return;
        }

        // --probe-adapters: run each adapter's probe.eval as a pre-flight
        // health check and exit. Opt-in so default startup stays fast; useful
        // when wiring a new adapter or debugging why the registry loaded
        // something that refuses to talk.
        if (args.Contains("--probe-adapters"))
        {
            var failed = await Tests.AdapterDeclaredTestsRunner.ProbeAllAsync(registry);
            if (failed > 0) Environment.Exit(1);
            return;
        }

        // --test mode: run tests
        if (args.Contains("--test"))
        {
            if (args.Contains("--conpty"))
            {
                Tests.ConPtyMinimalTest.Run();
                return;
            }
            Tests.OscParserTests.Run();
            Tests.CommandTrackerTests.Run();
            Tests.PwshColorizerTests.Run();
            Tests.ConsoleManagerTests.Run();
            Tests.ConsoleWorkerTests.RunUnitTests();
            Tests.RegexPromptDetectorTests.Run();
            Tests.BalancedParensCounterTests.Run();
            Tests.ModeDetectorTests.Run();
            Tests.AdapterLoaderTests.Run(registry, adapterReport);
            if (args.Contains("--e2e"))
            {
                await Tests.ConsoleWorkerTests.Run();
                await Tests.ConsoleWorkerTests.RunMultiShell();
                await Tests.ConsoleWorkerTests.RunIntegrationScriptGuardTest();
                var failed = await Tests.AdapterDeclaredTestsRunner.RunAsync(registry);
                if (failed > 0) Environment.Exit(1);
            }
            return;
        }

        // Default: MCP server mode
        var builder = Host.CreateApplicationBuilder(args);

        // Suppress framework logging — only warnings and errors go to stderr.
        // stdout is reserved for MCP JSON-RPC protocol.
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options =>
        {
            options.LogToStandardErrorThreshold = LogLevel.Trace;
        });
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        builder.Services
            .AddSingleton<ConsoleManager>()
            .AddSingleton<ProcessLauncher>();

        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithTools<ShellTools>()
            .WithTools<FileTools>();

        var host = builder.Build();

        var consoleManager = host.Services.GetRequiredService<ConsoleManager>();
        consoleManager.Initialize();

        await host.RunAsync();
    }

    private static void PrintAdapterList(
        Splash.Services.Adapters.AdapterRegistry registry,
        Splash.Services.Adapters.AdapterRegistry.LoadReport report)
    {
        Console.WriteLine($"splash — {registry.Count} adapter(s) loaded");
        Console.WriteLine();

        foreach (var adapter in registry.All.OrderBy(a => a.Name, StringComparer.Ordinal))
        {
            Console.WriteLine($"  {adapter.Name}");
            Console.WriteLine($"    family    : {adapter.Family}");
            Console.WriteLine($"    version   : {adapter.Version}");
            Console.WriteLine($"    schema    : v{adapter.Schema}");
            if (!string.IsNullOrEmpty(adapter.Description))
                Console.WriteLine($"    summary   : {SummarizeDescription(adapter.Description)}");
            if (adapter.Aliases is { Count: > 0 })
                Console.WriteLine($"    aliases   : {string.Join(", ", adapter.Aliases)}");
            Console.WriteLine($"    init      : {adapter.Init.Strategy} / {adapter.Init.Delivery}");
            Console.WriteLine($"    prompt    : {adapter.Prompt.Strategy}");
            if (!string.IsNullOrEmpty(adapter.Capabilities.ShellIntegration))
                Console.WriteLine($"    osc       : {adapter.Capabilities.ShellIntegration}");
            if (adapter.Capabilities.CwdTracking)
                Console.WriteLine($"    cwd       : {adapter.Capabilities.CwdFormat}");
            Console.WriteLine($"    exit_code : {adapter.Capabilities.ExitCode}");
            Console.WriteLine($"    tests     : {(adapter.Tests?.Count ?? 0)}");
            Console.WriteLine();
        }

        if (report.Overrides.Count > 0)
        {
            Console.WriteLine("Overrides:");
            foreach (var line in report.Overrides)
                Console.WriteLine($"  {line}");
            Console.WriteLine();
        }

        if (report.ParseErrors.Count > 0)
        {
            Console.WriteLine("Parse errors:");
            foreach (var (resource, error) in report.ParseErrors)
                Console.WriteLine($"  {resource}: {error}");
            Console.WriteLine();
        }

        if (report.Collisions.Count > 0)
        {
            Console.WriteLine("Collisions:");
            foreach (var line in report.Collisions)
                Console.WriteLine($"  {line}");
            Console.WriteLine();
        }

        Console.WriteLine($"External adapter directory: {Splash.Services.Adapters.AdapterRegistry.DefaultExternalDirectory}");
        Console.WriteLine(Directory.Exists(Splash.Services.Adapters.AdapterRegistry.DefaultExternalDirectory)
            ? "  (exists — YAMLs here override embedded adapters of the same name)"
            : "  (not present — drop YAMLs here to override embedded adapters)");
    }

    private static string FirstLine(string s)
    {
        var idx = s.IndexOfAny(new[] { '\n', '\r' });
        return idx >= 0 ? s[..idx] : s;
    }

    // YAML's `description: >` folded-block form collapses embedded
    // newlines into spaces, so every adapter ends up with a single
    // paragraph that can easily run to 300+ characters. --list-adapters
    // is meant to be a compact index, not a full doc dump, so clip
    // at ~120 characters and append an ellipsis when there's more.
    private const int SummaryMaxLength = 120;

    private static string SummarizeDescription(string s)
    {
        var flat = FirstLine(s).Trim();
        if (flat.Length <= SummaryMaxLength)
            return flat;
        var cut = flat.AsSpan(0, SummaryMaxLength);
        var lastSpace = cut.LastIndexOf(' ');
        if (lastSpace > SummaryMaxLength - 30)
            cut = cut[..lastSpace];
        return cut.ToString() + " …";
    }
}
