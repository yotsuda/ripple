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

        // IsCwdDrifted — null handling
        Assert(!ConsoleManager.IsCwdDrifted(null, null), "null + null is not drifted");
        Assert(!ConsoleManager.IsCwdDrifted(null, @"C:\foo"), "null LastAiCwd is not drifted (no prior expectation)");
        Assert(!ConsoleManager.IsCwdDrifted(@"C:\foo", null), "null live cwd is not drifted (worker not reporting yet)");

        // IsCwdDrifted — equality
        Assert(!ConsoleManager.IsCwdDrifted(@"C:\foo", @"C:\foo"), "identical cwds are not drifted");

        // IsCwdDrifted — real drift
        Assert(ConsoleManager.IsCwdDrifted(@"C:\foo", @"C:\bar"), "distinct cwds are drifted");
        Assert(ConsoleManager.IsCwdDrifted(@"C:\Users\yoshi", @"C:\Users"), "parent cwd is drifted");

        // IsCwdDrifted — Windows path comparison is case-insensitive
        if (OperatingSystem.IsWindows())
        {
            Assert(!ConsoleManager.IsCwdDrifted(@"C:\Foo", @"c:\foo"), "Windows: case-insensitive path match");
            Assert(!ConsoleManager.IsCwdDrifted(@"C:\MyProj\ripple", @"C:\myproj\RIPPLE"), "Windows: mixed case matches");
        }

        // IsCwdDrifted — POSIX-style paths (bash on WSL reports /mnt/c/... )
        Assert(!ConsoleManager.IsCwdDrifted("/mnt/c/foo", "/mnt/c/foo"), "POSIX path match");
        Assert(ConsoleManager.IsCwdDrifted("/mnt/c/foo", "/mnt/c/bar"), "POSIX path drift");

        Console.WriteLine($"\n{pass} passed, {fail} failed");
        if (fail > 0) Environment.Exit(1);
    }
}
