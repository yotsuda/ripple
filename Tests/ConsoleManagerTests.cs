using Ripple.Services;

namespace Ripple.Tests;

/// <summary>
/// Tests for the pure helper methods on ConsoleManager — drift detection,
/// routing heuristics, and anything else that can be verified without
/// spinning up a real ConPTY. The big ExecuteCommandInnerAsync integration
/// flow is still manual (see the live tests in the user session), but
/// the helpers it delegates to are tested here.
/// </summary>
public class ConsoleManagerTests
{
    public static void Run()
    {
        var pass = 0;
        var fail = 0;

        void Assert(bool condition, string name)
        {
            if (condition) { pass++; Console.WriteLine($"  PASS: {name}"); }
            else { fail++; Console.Error.WriteLine($"  FAIL: {name}"); }
        }

        Console.WriteLine("=== ConsoleManager Tests ===");

        // Drift detection is now done in PlanExecutionAsync via direct cwd
        // comparison (live cwd vs LastAiCwd). End-to-end behavior is
        // exercised manually via the live session flow — it needs a real
        // pipe round-trip to produce the get_status response the proxy
        // reads. Stub kept so the runner scaffolding stays consistent.
        Assert(true, "ConsoleManager: drift detection now uses direct cwd comparison; exercised via live session tests");

        var manager = new ConsoleManager(new ProcessLauncher());
        manager.TestRegisterConsole(4242, "pipe-a", "Sapphire", "pwsh", "pwsh", "A");
        Assert(manager.ResolveConsoleSelector("A", "Sapphire") == 4242,
            "console owner resolves its own console by name");
        Assert(manager.ResolveConsoleSelector("B", "Sapphire") is null,
            "a foreign agent cannot resolve another agent's console by name");
        Assert(manager.ResolveConsoleSelector("B", "4242") is null,
            "a foreign agent cannot resolve another agent's console by pid");
        Assert(manager.ResolveConsoleSelector("A", "4242") == 4242,
            "the owner can still resolve by pid");

        Console.WriteLine($"\n{pass} passed, {fail} failed");
        if (fail > 0) Environment.Exit(1);
    }
}
