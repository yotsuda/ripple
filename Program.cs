using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using SplashShell.Services;
using SplashShell.Tools;

namespace SplashShell;

public class Program
{
    public static async Task Main(string[] args)
    {
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
            if (args.Contains("--e2e"))
            {
                await Tests.ConsoleWorkerTests.Run();
                await Tests.ConsoleWorkerTests.RunMultiShell();
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
