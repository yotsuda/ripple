using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using Ripple.Services;
using Ripple.Services.Adapters;
using Ripple.Tools;
using System.Reflection;
using System.Text;

namespace Ripple;

public class Program
{
    public static async Task Main(string[] args)
    {
        // Early exits that don't need adapter registry or encoding
        // setup — kept above the heavy init so `--version` / `--help`
        // stay dependency-free, instant, and quiet on stderr (the
        // adapter report would otherwise leak before the version line).
        if (args.Contains("--version") || args.Contains("-v"))
        {
            var asm = Assembly.GetExecutingAssembly();
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            Console.WriteLine($"ripple {info?.InformationalVersion ?? "unknown"}");
            return;
        }
        if (args.Contains("--help") || args.Contains("-h"))
        {
            PrintUsage(Console.Out);
            return;
        }

        // Register legacy codepages (Shift-JIS, EUC-JP, GBK, Big5, windows-125x, …)
        // so FileTools can read/write non-UTF-8 text files. CodePagesEncodingProvider
        // carries its own tables, so this works under InvariantGlobalization + AOT.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        // Load adapter registry once at startup. Shared across proxy and
        // worker processes: both call Program.Main, so SetDefault runs in
        // both modes and ConsoleWorker / ConsoleManager can read the
        // registry via AdapterRegistry.Default without plumbing it through
        // constructors. Failures are non-fatal — we log and continue with
        // the existing hardcoded shell paths still intact as fallback.
        var (registry, adapterReport) = AdapterRegistry.LoadDefault();
        AdapterRegistry.SetDefault(registry, adapterReport);
        // Two-tier stderr policy.
        //
        // CLI modes (--test, --adapter-tests, --list-adapters, etc.) —
        // a human is reading the terminal. Print the full summary as
        // before so ad-hoc runs retain the startup-state roll-up.
        //
        // Silent modes (MCP stdio server with no args, ConPTY worker
        // via --console) — no human watching stderr in the moment.
        // Split the report:
        //   - User-actionable issues (parse errors / collisions that
        //     involve an EXTERNAL YAML the user dropped in
        //     ~/.ripple/adapters) still print as WARNING, because the
        //     user is the only one who can fix them and silently
        //     swallowing the failure leaves them confused about why
        //     their override isn't working.
        //   - Embedded-only failures (ripple bugs the user cannot fix)
        //     and routine info (the "N loaded (...)" roll-up,
        //     overrides) stay off the wire. AI consumers that need the
        //     full picture call the list_adapters MCP tool, whose
        //     response carries every parse error / collision / override
        //     with its user-actionable flag.
        //   - Use-site failures (start_console / execute_command against
        //     an adapter that failed to load) surface at the point of
        //     use as the MCP tool response error — that's where the
        //     problem actually matters to the caller.
        // Modes that don't surface adapter status to a human reader:
        //   - no args / --console → MCP/worker; adapter info goes to
        //     list_adapters tool callers, not stderr
        //   - any non-recognized arg combination → falls through to the
        //     unrecognized-args branch below, where the error message
        //     is the only useful stderr line
        bool isCliMode =
            args.Contains("--test")
            || args.Contains("--list-adapters")
            || args.Contains("--probe-adapters")
            || args.Contains("--adapter-tests")
            || args.Contains("--spill-tests");
        bool isSilentMode = !isCliMode;
        if (!isSilentMode)
        {
            var level = adapterReport.HasErrors ? "WARNING" : "info";
            Console.Error.WriteLine($"[ripple adapters {level}] {adapterReport.Summary()}");
        }
        else if (adapterReport.HasUserActionableIssues)
        {
            Console.Error.WriteLine($"[ripple adapters WARNING] {adapterReport.UserActionableSummary()}");
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
        // ~/.ripple/adapters and for verifying an adapter override is
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

        // --adapter-tests: run each adapter's declared `tests:` block
        // without the surrounding ConsoleWorkerTests.Run harness, whose
        // pre-existing Ctrl+C / obsolete-state flakes hard-exit the process
        // on failure and would otherwise mask downstream adapter-declared
        // results. Opt-in standalone path — useful after adding a new
        // adapter to verify just its own tests without rerunning the full
        // unit + E2E suite. Accepts an optional `--only <name>` filter.
        if (args.Contains("--adapter-tests"))
        {
            string? only = null;
            var idx = Array.IndexOf(args, "--only");
            if (idx >= 0 && idx + 1 < args.Length)
                only = args[idx + 1];
            var failed = await Tests.AdapterDeclaredTestsRunner.RunAsync(registry, only);
            if (failed > 0) Environment.Exit(1);
            return;
        }

        // --spill-tests: run only the Windows-only spill integration
        // suite without the surrounding --test / --e2e harness. Lets
        // the spill path be exercised in isolation (faster feedback
        // when iterating on OutputTruncationHelper / finalize-window
        // changes) and keeps the fail-fast contract: any scenario
        // failure exits the process with code 1.
        if (args.Contains("--spill-tests"))
        {
            await Tests.SpillIntegrationTests.Run();
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
            Tests.VtLiteStateTests.Run();
            Tests.PwshColorizerTests.Run();
            Tests.ConsoleManagerTests.Run();
            Tests.ConsoleWorkerTests.RunUnitTests();
            Tests.ConsoleWorkerTests.RunCacheUnitTests();
            Tests.RegexPromptDetectorTests.Run();
            Tests.BalancedParensCounterTests.Run();
            Tests.ModeDetectorTests.Run();
            Tests.OutputTruncationHelperTests.Run();
            Tests.CommandOutputCaptureTests.Run();
            Tests.CommandOutputRendererTests.Run();
            Tests.CommandOutputFinalizerTests.Run();
            Tests.FileToolsTests.Run();
            Tests.AdapterLoaderTests.Run(registry, adapterReport);
            if (args.Contains("--e2e"))
            {
                await Tests.ConsoleWorkerTests.Run();
                // Run the issue #1 spill suite before the multi-shell
                // block so it is always reachable — RunMultiShell's
                // per-suite Environment.Exit on failure (currently hit
                // by pre-existing bash subshell timeout / exit-code
                // assertions on some boxes) would otherwise abort
                // --e2e before the spill assertions get to execute.
                await Tests.SpillIntegrationTests.Run();
                await Tests.ConsoleWorkerTests.RunMultiShell();
                await Tests.ConsoleWorkerTests.RunIntegrationScriptGuardTest();
                var failed = await Tests.AdapterDeclaredTestsRunner.RunAsync(registry);
                if (failed > 0) Environment.Exit(1);
            }
            return;
        }

        // No args → MCP stdio server. Any other arg combination that
        // reaches here is unrecognized; silently entering MCP server
        // mode would block on stdin waiting for JSON-RPC and look like
        // a hang to the human typing `ripple --some-typo`. Reject up
        // front with a clear message + non-zero exit so a typo or an
        // unknown flag fails fast instead of freezing the terminal.
        if (args.Length > 0)
        {
            Console.Error.WriteLine($"ripple: unrecognized argument(s): {string.Join(" ", args)}");
            PrintUsage(Console.Error);
            Environment.Exit(2);
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
            .WithTools<FileTools>()
            .WithTools<AdapterTools>();

        var host = builder.Build();

        var consoleManager = host.Services.GetRequiredService<ConsoleManager>();
        consoleManager.Initialize();

        await host.RunAsync();
    }

    private static void PrintUsage(TextWriter writer)
    {
        writer.WriteLine("Usage: ripple [option]");
        writer.WriteLine();
        writer.WriteLine("Modes (mutually exclusive):");
        writer.WriteLine("  (no args)            run as MCP stdio server (default)");
        writer.WriteLine("  --console <args>     run as ConPTY console worker (internal use)");
        writer.WriteLine("  --test [--e2e] [--conpty]");
        writer.WriteLine("                       run unit / e2e / ConPTY-minimal tests");
        writer.WriteLine("  --adapter-tests [--only <name>]");
        writer.WriteLine("                       run each adapter's declared `tests:` block");
        writer.WriteLine("  --probe-adapters     run each adapter's probe.eval health check");
        writer.WriteLine("  --spill-tests        run the Windows-only spill integration suite");
        writer.WriteLine("  --list-adapters      print loaded adapter registry and exit");
        writer.WriteLine("  --version, -v        print version and exit");
        writer.WriteLine("  --help, -h           print this usage and exit");
    }

    private static void PrintAdapterList(
        Ripple.Services.Adapters.AdapterRegistry registry,
        Ripple.Services.Adapters.AdapterRegistry.LoadReport report)
    {
        Console.WriteLine($"ripple — {registry.Count} adapter(s) loaded");
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
            foreach (var e in report.ParseErrors)
                Console.WriteLine($"  [{e.Source.ToString().ToLowerInvariant()}] {e.Resource}: {e.Error}");
            Console.WriteLine();
        }

        if (report.Collisions.Count > 0)
        {
            Console.WriteLine("Collisions:");
            foreach (var c in report.Collisions)
                Console.WriteLine($"  {c.Message}{(c.IsUserActionable ? " (user-actionable)" : "")}");
            Console.WriteLine();
        }

        Console.WriteLine($"External adapter directory: {Ripple.Services.Adapters.AdapterRegistry.DefaultExternalDirectory}");
        Console.WriteLine(Directory.Exists(Ripple.Services.Adapters.AdapterRegistry.DefaultExternalDirectory)
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
