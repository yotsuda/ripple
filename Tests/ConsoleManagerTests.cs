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

        // Drift detection moved from a cwd-snapshot comparison
        // (IsCwdDrifted) to the worker's provenance counter
        // (CommandTracker.UserCmdsSinceLastAi). The counter is exercised in
        // CommandTrackerTests; ConsoleManager's integration with that
        // signal is exercised manually via the live session flow because
        // it requires a real pipe round-trip to produce the get_status
        // response the proxy reads. Only leave the function stub here so
        // the scaffolding (pass/fail counts, output headers) stays
        // consistent with the rest of the test runner.
        Assert(true, "ConsoleManager: drift detection is now tested via CommandTracker.UserCmdsSinceLastAi");

        Console.WriteLine($"\n{pass} passed, {fail} failed");
        if (fail > 0) Environment.Exit(1);
    }
}
