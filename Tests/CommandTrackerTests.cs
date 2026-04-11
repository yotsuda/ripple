using SplashShell.Services;

namespace SplashShell.Tests;

/// <summary>
/// Unit tests for CommandTracker.Busy semantics — in particular, that user-initiated
/// commands (OSCs fired without a preceding RegisterCommand) are also reflected in
/// Busy so get_status reports "busy" and the proxy switches to a different console.
/// </summary>
public class CommandTrackerTests
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

        Console.WriteLine("=== CommandTracker Tests ===");

        static OscParser.OscEvent Evt(OscParser.OscEventType t, int exit = 0, string? cwd = null)
            => new(t, exit, cwd);

        // Test 1: fresh tracker is idle.
        {
            var t = new CommandTracker();
            Assert(!t.Busy, "initial Busy is false");
        }

        // Helper: simulate shell startup (initial OSC B then first OSC A) so
        // subsequent OSC B/C events are recognised as user commands. The
        // tracker gates user-busy tracking until the shell has reached its
        // first prompt, otherwise the startup OSC B would leave new consoles
        // looking busy.
        static void PrimeShellReady(CommandTracker t)
        {
            t.HandleEvent(new OscParser.OscEvent(OscParser.OscEventType.CommandInputStart));
            t.HandleEvent(new OscParser.OscEvent(OscParser.OscEventType.PromptStart));
        }

        // Test 2: pwsh user-command pattern — OSC B on Enter, OSC A after prompt.
        {
            var t = new CommandTracker();
            PrimeShellReady(t);
            Assert(!t.Busy, "pwsh: idle after startup B → A");

            t.HandleEvent(Evt(OscParser.OscEventType.CommandInputStart));
            Assert(t.Busy, "pwsh: Busy true after OSC B (Enter)");

            t.HandleEvent(Evt(OscParser.OscEventType.CommandExecuted));
            Assert(t.Busy, "pwsh: still Busy after OSC C (fires from prompt fn after cmd)");

            t.HandleEvent(Evt(OscParser.OscEventType.CommandFinished, exit: 0));
            Assert(t.Busy, "pwsh: still Busy after OSC D");

            t.HandleEvent(Evt(OscParser.OscEventType.Cwd, cwd: "C:\\Users"));
            Assert(t.Busy, "pwsh: still Busy after OSC P");

            t.HandleEvent(Evt(OscParser.OscEventType.PromptStart));
            Assert(!t.Busy, "pwsh: Busy cleared by OSC A");
        }

        // Test 3: bash/zsh user-command pattern — OSC C on preexec, OSC D+A on precmd.
        {
            var t = new CommandTracker();
            PrimeShellReady(t);
            Assert(!t.Busy, "bash: idle after startup B → A");

            t.HandleEvent(Evt(OscParser.OscEventType.CommandExecuted));
            Assert(t.Busy, "bash: Busy true after OSC C (preexec)");

            t.HandleEvent(Evt(OscParser.OscEventType.CommandFinished, exit: 0));
            Assert(t.Busy, "bash: still Busy after OSC D");

            t.HandleEvent(Evt(OscParser.OscEventType.Cwd, cwd: "/home/user"));
            Assert(t.Busy, "bash: still Busy after OSC P");

            t.HandleEvent(Evt(OscParser.OscEventType.PromptStart));
            Assert(!t.Busy, "bash: Busy cleared by OSC A");
        }

        // Test 4: startup OSC B must NOT mark busy before the shell is ready.
        // Before the first OSC A there is no established baseline, and the
        // initial OSC B from integration scripts would otherwise race against
        // the first execute_command from the proxy.
        {
            var t = new CommandTracker();
            t.HandleEvent(Evt(OscParser.OscEventType.CommandInputStart));
            Assert(!t.Busy, "startup: initial OSC B is ignored (shell not ready)");
            t.HandleEvent(Evt(OscParser.OscEventType.PromptStart));
            Assert(!t.Busy, "startup: still idle after first OSC A");
            t.HandleEvent(Evt(OscParser.OscEventType.CommandInputStart));
            Assert(t.Busy, "startup: OSC B after first prompt does set busy");
        }

        // Test 5: LastKnownCwd is updated even for user-command OSC P.
        {
            var t = new CommandTracker();
            Assert(t.LastKnownCwd == null, "LastKnownCwd starts null");

            PrimeShellReady(t);
            t.HandleEvent(Evt(OscParser.OscEventType.CommandExecuted));
            t.HandleEvent(Evt(OscParser.OscEventType.Cwd, cwd: "/tmp"));
            Assert(t.LastKnownCwd == "/tmp", "LastKnownCwd updated from user OSC P");

            t.HandleEvent(Evt(OscParser.OscEventType.PromptStart));
            Assert(t.LastKnownCwd == "/tmp", "LastKnownCwd retained after prompt");
        }

        // Test 6: AI command wins over user state — RegisterCommand while idle, then events
        // still reflect Busy via _isAiCommand until the AI command resolves.
        {
            var t = new CommandTracker();
            var task = t.RegisterCommand("Get-Date", timeoutMs: 30_000);
            Assert(t.Busy, "AI cmd: Busy true after RegisterCommand");

            t.HandleEvent(Evt(OscParser.OscEventType.CommandInputStart));
            t.HandleEvent(Evt(OscParser.OscEventType.CommandFinished, exit: 0));
            Assert(t.Busy, "AI cmd: still Busy before prompt resolves");
        }

        // Test 7: RegisterCommand refuses when another AI command is in flight.
        {
            var t = new CommandTracker();
            _ = t.RegisterCommand("first", timeoutMs: 30_000);
            bool threw = false;
            try { _ = t.RegisterCommand("second", timeoutMs: 30_000); }
            catch (InvalidOperationException) { threw = true; }
            Assert(threw, "AI cmd: second RegisterCommand throws");
        }

        // Test 8: a late first-prompt OSC A that arrives AFTER RegisterCommand
        // but BEFORE the command's own OSC C / OSC D boundary must NOT resolve
        // the pending task. Reproduces the "slow pwsh startup + timeout
        // fallback" case where the pre-command buffer (reason banner etc.)
        // would otherwise be returned as the command's output.
        {
            var t = new CommandTracker();
            var task = t.RegisterCommand("Get-Location", timeoutMs: 30_000);

            // Simulate the bytes that arrived between worker-ready and the
            // real first prompt: the Write-Host reason text + the first
            // prompt's OSC sequence arriving AFTER RegisterCommand has
            // already fired. OSC B from integration.ps1's init-time write,
            // then the delayed first prompt's OSC D/P/A.
            t.FeedOutput("Reason: test\n\n");
            t.HandleEvent(Evt(OscParser.OscEventType.CommandInputStart));
            t.HandleEvent(Evt(OscParser.OscEventType.CommandFinished, exit: 0));
            t.HandleEvent(Evt(OscParser.OscEventType.Cwd, cwd: "C:\\Users\\foo"));
            t.HandleEvent(Evt(OscParser.OscEventType.PromptStart));

            Assert(!task.IsCompleted, "late first-prompt OSC A does NOT resolve before OSC C");
            Assert(t.Busy, "AI cmd: still Busy after stray first prompt");

            // Now simulate the real command actually running, which will
            // deliver OSC C + output + OSC D + OSC A in proper order.
            t.HandleEvent(Evt(OscParser.OscEventType.CommandExecuted));
            t.FeedOutput("C:\\MyProj\\splashshell\n");
            t.HandleEvent(Evt(OscParser.OscEventType.CommandFinished, exit: 0));
            t.HandleEvent(Evt(OscParser.OscEventType.Cwd, cwd: "C:\\MyProj\\splashshell"));
            t.HandleEvent(Evt(OscParser.OscEventType.PromptStart));

            Assert(task.IsCompleted, "real OSC C/D/A resolves the task");
            var result = task.Result;
            Assert(!result.Output.Contains("Reason"), "reason banner is NOT in captured output");
            Assert(result.Output.Contains("C:\\MyProj\\splashshell"), "real command output is captured");
            Assert(result.Cwd == "C:\\MyProj\\splashshell", "post-command cwd is reported");
        }

        Console.WriteLine($"\n{pass} passed, {fail} failed");
        if (fail > 0) Environment.Exit(1);
    }
}
