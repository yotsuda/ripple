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
}
