using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Splash.Services;

namespace Splash.Tests;

/// <summary>
/// E2E test: launch ConsoleWorker in --console mode, send commands via Named Pipe.
/// Tests ConPTY + shell integration + OSC parsing + command tracking.
/// </summary>
public class ConsoleWorkerTests
{
    /// <summary>
    /// Quick unit tests for UnescapeInput — runs without PTY/pipe setup.
    /// </summary>
    public static void RunUnitTests()
    {
        var pass = 0;
        var fail = 0;
        void Assert(bool condition, string name)
        {
            if (condition) { pass++; Console.WriteLine($"  PASS: {name}"); }
            else { fail++; Console.Error.WriteLine($"  FAIL: {name}"); }
        }

        Console.WriteLine("=== ConsoleWorker Unit Tests ===");

        // UnescapeInput
        Assert(ConsoleWorker.UnescapeInput("hello") == "hello", "unescape: plain text unchanged");
        Assert(ConsoleWorker.UnescapeInput("abc\\r") == "abc\r", "unescape: \\r → CR");
        Assert(ConsoleWorker.UnescapeInput("abc\\n") == "abc\n", "unescape: \\n → LF");
        Assert(ConsoleWorker.UnescapeInput("abc\\t") == "abc\t", "unescape: \\t → TAB");
        Assert(ConsoleWorker.UnescapeInput("\\x03") == "\x03", "unescape: \\x03 → ETX");
        Assert(ConsoleWorker.UnescapeInput("\\x1b[A") == "\x1b[A", "unescape: \\x1b[A → ESC[A");
        Assert(ConsoleWorker.UnescapeInput("a\\\\b") == "a\\b", "unescape: \\\\\\\\ → literal backslash");
        Assert(ConsoleWorker.UnescapeInput("\\r").Length == 1, "unescape: \\r length is 1");
        Assert(ConsoleWorker.UnescapeInput("\\x03").Length == 1, "unescape: \\x03 length is 1");

        // StripCmdInputEcho — strips ConPTY's input-echo prefix from cmd output.
        Assert(ConsoleWorker.StripCmdInputEcho("echo hello\r\nhello\r\n", "echo hello") == "hello\r\n",
            "strip cmd echo: simple single-line");
        Assert(ConsoleWorker.StripCmdInputEcho("echo hello\nhello\n", "echo hello") == "hello\n",
            "strip cmd echo: LF-only newlines");
        Assert(ConsoleWorker.StripCmdInputEcho("set\nVAR1=a\nVAR2=b\n", "set") == "VAR1=a\nVAR2=b\n",
            "strip cmd echo: command with empty args");
        Assert(ConsoleWorker.StripCmdInputEcho("echo hello world\n hello world\n", "echo hello world") == " hello world\n",
            "strip cmd echo: only first matching prefix is consumed");
        // Line wrap: ConPTY inserts \n mid-echo when the typed command exceeds terminal width.
        Assert(ConsoleWorker.StripCmdInputEcho("dir /b *.cs\n*.csproj\nProgram.cs\n", "dir /b *.cs") == "*.csproj\nProgram.cs\n",
            "strip cmd echo: trailing newline after echo is dropped");
        Assert(ConsoleWorker.StripCmdInputEcho("echo abc def\nghi\n", "echo abc defghi") == "",
            "strip cmd echo: wrap-fold absorbs entire output");
        Assert(ConsoleWorker.StripCmdInputEcho("real output", "echo no match") == "real output",
            "strip cmd echo: mismatch returns original output");
        Assert(ConsoleWorker.StripCmdInputEcho("", "echo something") == "",
            "strip cmd echo: empty output returns empty");
        Assert(ConsoleWorker.StripCmdInputEcho("hello", "") == "hello",
            "strip cmd echo: empty sent-input returns original");

        Console.WriteLine($"\n{pass} passed, {fail} failed");
        if (fail > 0) Environment.Exit(1);
    }

    public static async Task Run()
    {
        var pass = 0;
        var fail = 0;

        void Assert(bool condition, string name)
        {
            if (condition) { pass++; Console.WriteLine($"  PASS: {name}"); }
            else { fail++; Console.Error.WriteLine($"  FAIL: {name}"); }
        }

        Console.WriteLine("=== ConsoleWorker E2E Tests ===");

        // Find splash executable
        var exePath = Process.GetCurrentProcess().MainModule?.FileName;
        if (exePath == null)
        {
            Console.Error.WriteLine("  SKIP: Cannot determine exe path");
            return;
        }

        var proxyPid = Environment.ProcessId;
        var agentId = "test";
        var shell = OperatingSystem.IsWindows() ? "pwsh.exe" : "bash";
        var cwd = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // Launch worker via ProcessLauncher (uses CREATE_NEW_CONSOLE on Windows,
        // required so the worker process has a console for ConPTY to attach to)
        var launcher = new Services.ProcessLauncher();
        int workerPid = launcher.LaunchConsoleWorker(proxyPid, agentId, shell, cwd);

        var pipeName = $"SP.{proxyPid}.{agentId}.{workerPid}";
        Console.WriteLine($"  Pipe: {pipeName}, Worker PID: {workerPid}");

        // Wait for pipe to be ready (up to 30s)
        Console.WriteLine("  Waiting for pipe...");
        var ready = await WaitForPipeAsync(pipeName, TimeSpan.FromSeconds(30));
        Assert(ready, "Worker pipe became ready");

        if (!ready)
        {
            try { Process.GetProcessById(workerPid).Kill(); } catch { }
            Console.WriteLine($"\n{pass} passed, {fail} failed");
            if (fail > 0) Environment.Exit(1);
            return;
        }

        // Test 1: ping
        {
            var resp = await SendRequest(pipeName, w => w.WriteString("type", "ping"));
            var status = resp.TryGetProperty("status", out var s) ? s.GetString() : null;
            Assert(status == "ok", "Ping returns ok");
        }

        // Test 2: get_status (should be standby)
        {
            var resp = await SendRequest(pipeName, w => w.WriteString("type", "get_status"));
            var status = resp.TryGetProperty("status", out var s) ? s.GetString() : null;
            Assert(status == "standby", $"Status is standby (got: {status})");
        }

        // Test 3: execute simple command
        {
            var command = OperatingSystem.IsWindows() ? "Write-Output 'hello splash'" : "echo 'hello splash'";
            Console.WriteLine($"  Executing: {command}");
            var resp = await SendRequest(pipeName, w => { w.WriteString("type", "execute"); w.WriteString("command", command); w.WriteNumber("timeout", 10000); }, TimeSpan.FromSeconds(15));

            var output = resp.TryGetProperty("output", out var o) ? o.GetString() ?? "" : "";
            var timedOut = resp.TryGetProperty("timedOut", out var t) && t.GetBoolean();
            var exitCode = resp.TryGetProperty("exitCode", out var e) ? e.GetInt32() : -1;
            var cwdResult = resp.TryGetProperty("cwd", out var c) ? c.GetString() : null;

            Console.WriteLine($"  Output: '{output.Replace("\n", "\\n")}'");
            Console.WriteLine($"  ExitCode: {exitCode}, TimedOut: {timedOut}, Cwd: {cwdResult}");

            Assert(!timedOut, "Command did not time out");
            Assert(output.Contains("hello splash"), "Output contains expected text");
            Assert(exitCode == 0, "Exit code is 0");
            Assert(cwdResult != null, "Cwd is reported");
        }

        // Test 4: execute command with non-zero exit (native command)
        {
            var command = OperatingSystem.IsWindows()
                ? "cmd /c exit 42"
                : "bash -c 'exit 42'";
            Console.WriteLine($"  Executing: {command}");
            var resp = await SendRequest(pipeName, w => { w.WriteString("type", "execute"); w.WriteString("command", command); w.WriteNumber("timeout", 10000); }, TimeSpan.FromSeconds(15));

            var exitCode = resp.TryGetProperty("exitCode", out var e) ? e.GetInt32() : -1;
            var timedOut = resp.TryGetProperty("timedOut", out var t) && t.GetBoolean();
            Console.WriteLine($"  ExitCode: {exitCode}, TimedOut: {timedOut}");

            Assert(!timedOut, "Non-zero exit: did not time out");
            Assert(exitCode == 42, $"Non-zero exit: code is 42 (got: {exitCode})");
        }

        // Test 5: get_status after commands (should be standby again)
        {
            var resp = await SendRequest(pipeName, w => w.WriteString("type", "get_status"));
            var status = resp.TryGetProperty("status", out var s) ? s.GetString() : null;
            Assert(status == "standby", $"Status back to standby (got: {status})");
        }

        // Test 6: session persistence — a variable set in one execute is readable in the next.
        // This guards the core value proposition of splash: persistent shell state across
        // AI tool calls. If the worker ever loses state (e.g. spawns a subshell per execute),
        // this test catches it immediately.
        {
            var setCmd = OperatingSystem.IsWindows()
                ? "$script:SPLASH_SESSION_TEST = 'persistent-42'"
                : "export SPLASH_SESSION_TEST='persistent-42'";
            var getCmd = OperatingSystem.IsWindows()
                ? "Write-Output $script:SPLASH_SESSION_TEST"
                : "echo \"$SPLASH_SESSION_TEST\"";

            await SendRequest(pipeName, w => { w.WriteString("type", "execute"); w.WriteString("command", setCmd); w.WriteNumber("timeout", 10000); }, TimeSpan.FromSeconds(15));
            var resp = await SendRequest(pipeName, w => { w.WriteString("type", "execute"); w.WriteString("command", getCmd); w.WriteNumber("timeout", 10000); }, TimeSpan.FromSeconds(15));
            var output = resp.TryGetProperty("output", out var o) ? o.GetString() ?? "" : "";
            Assert(output.Contains("persistent-42"), $"Session variable persists across execute calls (got: {output.Replace("\n", "\\n")})");
        }

        // Test 7: multi-line command (foreach loop).
        // Multi-line input flows through tempfile dot-sourcing on pwsh and
        // heredoc-style delivery on bash; either path must preserve output ordering.
        // Note: we don't assert exitCode here because pwsh's $LASTEXITCODE only updates
        // on native-command invocations; a prior `cmd /c exit 42` leaks into this test.
        // That is a pwsh semantic, not a splash bug.
        {
            var multilineCmd = OperatingSystem.IsWindows()
                ? "foreach ($i in 1..3) {\n    Write-Output \"line $i\"\n}"
                : "for i in 1 2 3; do\n    echo \"line $i\"\ndone";
            var resp = await SendRequest(pipeName, w => { w.WriteString("type", "execute"); w.WriteString("command", multilineCmd); w.WriteNumber("timeout", 10000); }, TimeSpan.FromSeconds(15));
            var output = resp.TryGetProperty("output", out var o) ? o.GetString() ?? "" : "";
            Assert(output.Contains("line 1") && output.Contains("line 2") && output.Contains("line 3"),
                $"Multi-line: all three lines emitted (got: {output.Replace("\n", "\\n")})");
        }

        // Test 8: timeout → get_cached_output retrieval.
        // Execute a command with a very short timeout so it times out, then verify the
        // worker reports busy, and after the command completes, get_cached_output returns
        // the result. This exercises the busy-tracking + cache path that AI clients rely on
        // when commands exceed their wall-clock budget.
        {
            var slowCmd = OperatingSystem.IsWindows()
                ? "Start-Sleep -Milliseconds 1500; Write-Output 'slow done'"
                : "sleep 1.5; echo 'slow done'";

            // Short timeout forces busy return.
            var resp = await SendRequest(pipeName, w => { w.WriteString("type", "execute"); w.WriteString("command", slowCmd); w.WriteNumber("timeout", 300); }, TimeSpan.FromSeconds(5));
            var timedOut = resp.TryGetProperty("timedOut", out var t) && t.GetBoolean();
            Assert(timedOut, "Slow command: timedOut flag set");

            // Worker should be busy.
            var statusResp = await SendRequest(pipeName, w => w.WriteString("type", "get_status"));
            var status = statusResp.TryGetProperty("status", out var s) ? s.GetString() : null;
            Assert(status == "busy", $"Status is busy while command runs (got: {status})");

            // Poll for cached output until it's ready (max ~3s).
            string? cachedOutput = null;
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < deadline)
            {
                var cacheResp = await SendRequest(pipeName, w => w.WriteString("type", "get_cached_output"));
                var cacheStatus = cacheResp.TryGetProperty("status", out var cs) ? cs.GetString() : null;
                if (cacheStatus == "ok")
                {
                    cachedOutput = cacheResp.TryGetProperty("output", out var co) ? co.GetString() : null;
                    break;
                }
                await Task.Delay(200);
            }
            Assert(cachedOutput != null && cachedOutput.Contains("slow done"),
                $"Cached output contains expected result (got: {(cachedOutput ?? "<null>").Replace("\n", "\\n")})");
        }

        // Test 9: send_input rejected on idle console.
        // The worker must refuse send_input when there is no running command, so the AI
        // can't accidentally inject keystrokes into the next prompt.
        {
            var resp = await SendRequest(pipeName, w =>
            {
                w.WriteString("type", "send_input");
                w.WriteString("input", "garbage\\r");
            });
            var status = resp.TryGetProperty("status", out var s) ? s.GetString() : null;
            Assert(status == "rejected", $"send_input on idle console rejected (got: {status})");
        }

        // Test 10: send_input Ctrl+C interrupts a running command.
        // Start a long sleep, send \x03, and confirm the next get_status shows standby
        // (shell returned to prompt) within a couple of seconds.
        {
            var sleepCmd = OperatingSystem.IsWindows()
                ? "Start-Sleep -Seconds 60"
                : "sleep 60";

            var execResp = await SendRequest(pipeName, w => { w.WriteString("type", "execute"); w.WriteString("command", sleepCmd); w.WriteNumber("timeout", 300); }, TimeSpan.FromSeconds(5));
            var execTimedOut = execResp.TryGetProperty("timedOut", out var t) && t.GetBoolean();
            Assert(execTimedOut, "Long sleep: timed out as expected");

            var inputResp = await SendRequest(pipeName, w =>
            {
                w.WriteString("type", "send_input");
                w.WriteString("input", "\\x03");
            });
            var inputStatus = inputResp.TryGetProperty("status", out var isp) ? isp.GetString() : null;
            Assert(inputStatus == "ok", $"send_input Ctrl+C accepted while busy (got: {inputStatus})");

            // Drain cached output so the tracker clears HasCachedOutput and the next
            // get_status can return "standby" instead of "completed".
            var interrupted = false;
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                await SendRequest(pipeName, w => w.WriteString("type", "get_cached_output"));
                var statusResp = await SendRequest(pipeName, w => w.WriteString("type", "get_status"));
                var st = statusResp.TryGetProperty("status", out var s) ? s.GetString() : null;
                if (st == "standby") { interrupted = true; break; }
                await Task.Delay(200);
            }
            Assert(interrupted, "Shell returned to standby after Ctrl+C interrupt");
        }

        // Test 11: version check (worker refuses claim from strictly newer proxy)
        // Send claim with a fake proxy_version that is strictly greater than any real
        // version. The worker's HandleClaim is version-aware: it marks itself obsolete
        // and returns status="obsolete". The shell (PTY) must remain alive afterwards
        // so the human user can keep working in the terminal.
        {
            var unownedPipe = $"SP.{workerPid}";
            var resp = await SendRequest(unownedPipe, w =>
            {
                w.WriteString("type", "claim");
                w.WriteNumber("proxy_pid", proxyPid);
                w.WriteString("proxy_version", "99.99.99");
                w.WriteString("agent_id", "v2test");
                w.WriteString("title", "#fake high version");
            });
            var status = resp.TryGetProperty("status", out var s) ? s.GetString() : null;
            Assert(status == "obsolete", $"Claim with higher proxy_version returns obsolete (got: {status})");

            var workerVersion = resp.TryGetProperty("worker_version", out var wv) ? wv.GetString() : null;
            Assert(!string.IsNullOrEmpty(workerVersion), $"Response includes worker_version (got: {workerVersion})");

            // PTY must still be alive so the user can continue working.
            var execResp = await SendRequest(pipeName,
                w => { w.WriteString("type", "execute"); w.WriteString("command", "Write-Output 'still-alive'"); w.WriteNumber("timeout", 10000); },
                TimeSpan.FromSeconds(15));
            var execOutput = execResp.TryGetProperty("output", out var eo) ? eo.GetString() ?? "" : "";
            Assert(execOutput.Contains("still-alive"), $"PTY still alive after obsolete state (output: {execOutput.Replace("\n", "\\n")})");
        }

        // Cleanup
        try
        {
            var proc = Process.GetProcessById(workerPid);
            proc.Kill();
            await proc.WaitForExitAsync();
        }
        catch { }

        Console.WriteLine($"\n{pass} passed, {fail} failed");
        if (fail > 0) Environment.Exit(1);
    }

    /// <summary>
    /// Cross-shell smoke test. The main <see cref="Run"/> suite only covers pwsh
    /// because that's splash's primary target; this runs a smaller set of
    /// assertions against Windows PowerShell 5.1 (powershell.exe) and cmd.exe so
    /// both are exercised end-to-end from the Pipe protocol.
    ///
    /// Each shell profile declares how to echo a literal, set a session variable,
    /// and read it back. cmd.exe has a documented limitation: its PROMPT can't
    /// expand %ERRORLEVEL% at display time, so the exit code is always reported
    /// as 0 and the command echo from ConPTY appears in the output — the cmd
    /// assertions here are deliberately loose about both.
    /// </summary>
    public static async Task RunMultiShell()
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("=== ConsoleWorker Multi-Shell Tests === SKIP (Windows-only)");
            return;
        }

        var totalPass = 0;
        var totalFail = 0;

        Console.WriteLine("=== ConsoleWorker Multi-Shell Tests ===");

        var profiles = new[]
        {
            new ShellProfile(
                label: "powershell (5.1)",
                shellExe: "powershell.exe",
                simpleEcho: "Write-Output 'hello ps51'",
                simpleEchoExpect: "hello ps51",
                setVar: "$script:SPLASH_MS_TEST = 'ps51-persist'",
                getVar: "$global:LASTEXITCODE = 0; Write-Output $script:SPLASH_MS_TEST",
                getVarExpect: "ps51-persist",
                multiLine: "foreach ($i in 1..3) {\n    Write-Output \"ps51-line $i\"\n}",
                multiLineExpects: new[] { "ps51-line 1", "ps51-line 2", "ps51-line 3" },
                assertExitCode: true),
            new ShellProfile(
                label: "cmd",
                shellExe: "cmd.exe",
                simpleEcho: "echo hello cmd",
                simpleEchoExpect: "hello cmd",
                setVar: "set SPLASH_MS_TEST=cmd-persist",
                getVar: "echo %SPLASH_MS_TEST%",
                getVarExpect: "cmd-persist",
                // Multi-line cmd goes through a tempfile .cmd batch (see
                // HandleExecuteAsync). Verifies both that the tempfile path
                // works and that cmd batch block syntax (nested blocks with
                // line breaks) survives the round-trip. cwd-independent —
                // uses a variable set inside the block.
                multiLine: "set _SPLASH_MSH=ok\nif \"%_SPLASH_MSH%\"==\"ok\" (\n    echo cmd-line-a\n    echo cmd-line-b\n) else (\n    echo cmd-else\n)",
                multiLineExpects: new[] { "cmd-line-a", "cmd-line-b" },
                // cmd's PROMPT fires a fake D;0 after every command — exit code
                // assertions would always see 0, so don't bother.
                assertExitCode: false),
            new ShellProfile(
                label: "bash",
                shellExe: "bash.exe",
                simpleEcho: "echo hello bash",
                simpleEchoExpect: "hello bash",
                setVar: "export SPLASH_MS_TEST=bash-persist",
                getVar: "echo \"$SPLASH_MS_TEST\"",
                getVarExpect: "bash-persist",
                // Multi-line bash goes through a tempfile .sh dot-source
                // (see HandleExecuteAsync). Also exercises the bash
                // integration's "single OSC C per command line submit"
                // gate — without that gate, the for-loop's per-iteration
                // DEBUG trap firings would clobber _commandStart and the
                // tracker would only capture the last iteration.
                multiLine: "for i in 1 2 3; do\n    echo \"bash-iter $i\"\ndone",
                multiLineExpects: new[] { "bash-iter 1", "bash-iter 2", "bash-iter 3" },
                assertExitCode: true),
        };

        foreach (var profile in profiles)
        {
            Console.WriteLine($"\n--- {profile.label} ---");
            var (pass, fail) = await RunShellProfileAsync(profile);
            totalPass += pass;
            totalFail += fail;
            Console.WriteLine($"  {profile.label}: {pass} passed, {fail} failed");
        }

        Console.WriteLine($"\n{totalPass} passed, {totalFail} failed");
        if (totalFail > 0) Environment.Exit(1);
    }

    /// <summary>
    /// Verify integration.ps1 doesn't crash when PSReadLine isn't loaded.
    /// Spawns a fresh pwsh, removes PSReadLine, then dot-sources the
    /// embedded integration script. The script has best-effort guards
    /// around its PSReadLine cmdlets — if they regress, this test catches
    /// it before it becomes a worker-startup hang.
    /// </summary>
    public static async Task RunIntegrationScriptGuardTest()
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("=== ConsoleWorker PSReadLine Guard Test === SKIP (Windows-only)");
            return;
        }

        Console.WriteLine("=== ConsoleWorker PSReadLine Guard Test ===");
        var pass = 0;
        var fail = 0;
        void Assert(bool condition, string name)
        {
            if (condition) { pass++; Console.WriteLine($"  PASS: {name}"); }
            else { fail++; Console.Error.WriteLine($"  FAIL: {name}"); }
        }

        // Read the embedded integration script the same way ConsoleWorker does.
        string? scriptBody;
        using (var stream = typeof(ConsoleWorker).Assembly.GetManifestResourceStream("Splash.ShellIntegration.integration.ps1"))
        {
            if (stream == null)
            {
                Assert(false, "integration.ps1 embedded resource located");
                Console.WriteLine($"\n{pass} passed, {fail} failed");
                if (fail > 0) Environment.Exit(1);
                return;
            }
            using var reader = new StreamReader(stream);
            scriptBody = reader.ReadToEnd();
        }
        Assert(true, "integration.ps1 embedded resource located");

        var tmp = Path.Combine(Path.GetTempPath(), $"splash-psrl-guard-{Guid.NewGuid():N}.ps1");
        await File.WriteAllTextAsync(tmp, scriptBody);

        try
        {
            // Run pwsh with PSReadLine forcibly removed, then source the
            // integration. Exit 0 + empty stderr = clean load.
            var psi = new ProcessStartInfo
            {
                FileName = "pwsh.exe",
                Arguments = $"-NoProfile -NonInteractive -Command \"$ErrorActionPreference='Stop'; Remove-Module PSReadLine -ErrorAction Ignore; . '{tmp}'; exit 0\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc == null)
            {
                Assert(false, "pwsh.exe started");
                return;
            }
            var stderr = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();

            Assert(proc.ExitCode == 0, $"integration loads cleanly without PSReadLine (exit={proc.ExitCode}, stderr={stderr.Trim().Replace("\n", "\\n")})");
            Assert(string.IsNullOrWhiteSpace(stderr), $"no stderr noise from integration load (got: {stderr.Trim().Replace("\n", "\\n")})");
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }

        Console.WriteLine($"\n{pass} passed, {fail} failed");
        if (fail > 0) Environment.Exit(1);
    }

    private record ShellProfile(
        string label,
        string shellExe,
        string simpleEcho,
        string simpleEchoExpect,
        string setVar,
        string getVar,
        string getVarExpect,
        string multiLine,
        string[] multiLineExpects,
        bool assertExitCode);

    private static async Task<(int pass, int fail)> RunShellProfileAsync(ShellProfile profile)
    {
        var pass = 0;
        var fail = 0;
        void Assert(bool condition, string name)
        {
            if (condition) { pass++; Console.WriteLine($"  PASS: {name}"); }
            else { fail++; Console.Error.WriteLine($"  FAIL: {name}"); }
        }

        var proxyPid = Environment.ProcessId;
        var agentId = "multi";
        var cwd = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var launcher = new Services.ProcessLauncher();
        int workerPid;
        try
        {
            workerPid = launcher.LaunchConsoleWorker(proxyPid, agentId, profile.shellExe, cwd);
        }
        catch (Exception ex)
        {
            Assert(false, $"{profile.label}: launch worker ({ex.GetType().Name}: {ex.Message})");
            return (pass, fail);
        }

        var pipeName = $"SP.{proxyPid}.{agentId}.{workerPid}";

        try
        {
            var ready = await WaitForPipeAsync(pipeName, TimeSpan.FromSeconds(30));
            Assert(ready, $"{profile.label}: worker pipe became ready");
            if (!ready) return (pass, fail);

            // get_status should report standby once the shell is fully initialised.
            {
                var resp = await SendRequest(pipeName, w => w.WriteString("type", "get_status"));
                var status = resp.TryGetProperty("status", out var s) ? s.GetString() : null;
                Assert(status == "standby", $"{profile.label}: initial status is standby (got: {status})");

                var shellFamily = resp.TryGetProperty("shellFamily", out var sf) ? sf.GetString() : null;
                Assert(!string.IsNullOrEmpty(shellFamily), $"{profile.label}: shellFamily reported ({shellFamily})");
            }

            // Basic echo command returns expected text.
            {
                var resp = await SendRequest(pipeName,
                    w => { w.WriteString("type", "execute"); w.WriteString("command", profile.simpleEcho); w.WriteNumber("timeout", 15000); },
                    TimeSpan.FromSeconds(20));
                var output = resp.TryGetProperty("output", out var o) ? o.GetString() ?? "" : "";
                var timedOut = resp.TryGetProperty("timedOut", out var t) && t.GetBoolean();
                Assert(!timedOut, $"{profile.label}: simple echo did not time out");
                Assert(output.Contains(profile.simpleEchoExpect),
                    $"{profile.label}: simple echo output contains expected text (got: {output.Replace("\n", "\\n")})");
                // Strict echo check: the captured output must NOT contain the
                // typed command itself (e.g. "echo hello cmd"). pwsh and bash
                // strip the input echo via OSC 633 C; cmd's StripCmdInputEcho
                // does the same job for the cmd path. Regression guard for
                // the cmd cleanup we added.
                Assert(!output.Contains(profile.simpleEcho),
                    $"{profile.label}: input echo stripped from output (got: {output.Replace("\n", "\\n")})");
                if (profile.assertExitCode)
                {
                    var exitCode = resp.TryGetProperty("exitCode", out var e) ? e.GetInt32() : -1;
                    Assert(exitCode == 0, $"{profile.label}: simple echo exit code 0 (got: {exitCode})");
                }
            }

            // Session variable persists across separate execute calls.
            {
                var setResp = await SendRequest(pipeName,
                    w => { w.WriteString("type", "execute"); w.WriteString("command", profile.setVar); w.WriteNumber("timeout", 15000); },
                    TimeSpan.FromSeconds(20));
                var setTimedOut = setResp.TryGetProperty("timedOut", out var t) && t.GetBoolean();
                Assert(!setTimedOut, $"{profile.label}: set variable did not time out");

                var getResp = await SendRequest(pipeName,
                    w => { w.WriteString("type", "execute"); w.WriteString("command", profile.getVar); w.WriteNumber("timeout", 15000); },
                    TimeSpan.FromSeconds(20));
                var getOutput = getResp.TryGetProperty("output", out var o) ? o.GetString() ?? "" : "";
                Assert(getOutput.Contains(profile.getVarExpect),
                    $"{profile.label}: session variable persists (got: {getOutput.Replace("\n", "\\n")})");
            }

            // Multi-line commands: powershell uses tempfile dot-sourcing,
            // cmd uses tempfile `call`. Both must preserve newlines through
            // the PTY round-trip so block-level syntax (if/else, foreach)
            // still works. Each expected fragment must appear in the output
            // in order of declaration.
            {
                var resp = await SendRequest(pipeName,
                    w => { w.WriteString("type", "execute"); w.WriteString("command", profile.multiLine); w.WriteNumber("timeout", 15000); },
                    TimeSpan.FromSeconds(20));
                var output = resp.TryGetProperty("output", out var o) ? o.GetString() ?? "" : "";
                var timedOut = resp.TryGetProperty("timedOut", out var t) && t.GetBoolean();
                Assert(!timedOut, $"{profile.label}: multi-line did not time out");

                int cursor = 0;
                bool allInOrder = true;
                foreach (var frag in profile.multiLineExpects)
                {
                    var idx = output.IndexOf(frag, cursor, StringComparison.Ordinal);
                    if (idx < 0) { allInOrder = false; break; }
                    cursor = idx + frag.Length;
                }
                Assert(allInOrder,
                    $"{profile.label}: multi-line output contains all fragments in order (got: {output.Replace("\n", "\\n")})");
            }

            // bash subshell regression guard. The PS0-based OSC C emission
            // (replacing the old DEBUG-trap approach) must fire OSC C in the
            // PARENT shell before the subshell forks, so output captured
            // inside `(...)` lands in the AI command slice. Without this the
            // command either hangs forever waiting for a missing OSC D or
            // resolves with empty output.
            if (profile.label == "bash")
            {
                var resp = await SendRequest(pipeName,
                    w => { w.WriteString("type", "execute"); w.WriteString("command", "(echo bash-sub-foo; echo bash-sub-bar)"); w.WriteNumber("timeout", 10000); },
                    TimeSpan.FromSeconds(15));
                var output = resp.TryGetProperty("output", out var o) ? o.GetString() ?? "" : "";
                var timedOut = resp.TryGetProperty("timedOut", out var t) && t.GetBoolean();
                Assert(!timedOut, "bash: subshell did not time out");
                Assert(output.Contains("bash-sub-foo") && output.Contains("bash-sub-bar"),
                    $"bash: subshell output captured (got: {output.Replace("\n", "\\n")})");

                // Subshell with non-zero exit must propagate to the AI tracker.
                var exitResp = await SendRequest(pipeName,
                    w => { w.WriteString("type", "execute"); w.WriteString("command", "(exit 17)"); w.WriteNumber("timeout", 10000); },
                    TimeSpan.FromSeconds(15));
                var exitCode = exitResp.TryGetProperty("exitCode", out var e) ? e.GetInt32() : -1;
                Assert(exitCode == 17, $"bash: subshell exit code propagated (got: {exitCode})");
            }

            // After commands finish, the worker goes back to standby.
            {
                var resp = await SendRequest(pipeName, w => w.WriteString("type", "get_status"));
                var status = resp.TryGetProperty("status", out var s) ? s.GetString() : null;
                Assert(status == "standby", $"{profile.label}: status back to standby (got: {status})");
            }
        }
        finally
        {
            try
            {
                var proc = Process.GetProcessById(workerPid);
                proc.Kill();
                await proc.WaitForExitAsync();
            }
            catch { }
        }

        return (pass, fail);
    }

    internal static async Task<bool> WaitForPipeAsync(string pipeName, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var resp = await SendRequest(pipeName, w => w.WriteString("type", "ping"), TimeSpan.FromSeconds(2));
                return true;
            }
            catch
            {
                await Task.Delay(500);
            }
        }
        return false;
    }

    internal static async Task<JsonElement> SendRequest(string pipeName, Action<Utf8JsonWriter> writeBody, TimeSpan? timeout = null)
    {
        timeout ??= TimeSpan.FromSeconds(5);
        using var cts = new CancellationTokenSource(timeout.Value);
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

        await client.ConnectAsync(cts.Token);

        var msgBytes = PipeJson.BuildObjectBytes(writeBody);
        var lenBytes = BitConverter.GetBytes(msgBytes.Length);

        await client.WriteAsync(lenBytes, cts.Token);
        await client.WriteAsync(msgBytes, cts.Token);
        await client.FlushAsync(cts.Token);

        var recvLenBytes = new byte[4];
        await ReadExactAsync(client, recvLenBytes, cts.Token);
        var recvLen = BitConverter.ToInt32(recvLenBytes);

        var recvBytes = new byte[recvLen];
        await ReadExactAsync(client, recvBytes, cts.Token);

        return PipeJson.ParseElement(recvBytes);
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), ct);
            if (read == 0) throw new IOException("Pipe closed");
            offset += read;
        }
    }
}
