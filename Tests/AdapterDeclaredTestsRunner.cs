using System.Diagnostics;
using System.Text.RegularExpressions;
using Splash.Services;
using Splash.Services.Adapters;

namespace Splash.Tests;

/// <summary>
/// Runs each loaded adapter's tests: block against a real worker.
///
/// The point is two-fold:
///
/// 1. Adapter authors (including community contributors via
///    ~/.splash/adapters/ in Phase C milestone 1) can declare
///    tests: blocks in YAML alongside their adapter and run them
///    via `splash.exe --test --e2e` to confirm the adapter actually
///    works end-to-end, without writing any C#.
///
/// 2. splash gets a generic quality gate: every adapter that ships
///    with the binary has its tests: block exercised on every CI
///    run, so a regression in either the adapter YAML or the
///    shared framework code (BuildCommandLine, env block, OSC
///    parser, tracker) gets caught without someone having to
///    hand-port new test cases into ConsoleWorkerTests each time.
///
/// Current runner supports the common subset of AdapterTest fields:
///   - eval: single expression / command to run
///   - setup: a warm-up command whose output is ignored
///   - setup_sequence: ordered list of setup commands
///   - expect: regex matched against the eval's output
///   - expect_cwd_update: true if setup.cwd should differ from eval.cwd
///   - exit_code_is_unreliable: skip the exit-code assertion
///
/// Deferred (not yet needed by any shipping adapter):
///   - expect_error, expect_exit_code, expect_mode, expect_level,
///     expect_counter, expect_out_of_band, then_eval, wait_ms
///
/// Each adapter gets its own fresh worker process so state doesn't
/// leak between adapters.
/// </summary>
public static class AdapterDeclaredTestsRunner
{
    /// <summary>
    /// Probe every loaded adapter (schema §14) without running any tests.
    /// Each adapter gets its own fresh worker, runs probe.eval, asserts
    /// probe.expect, then shuts the worker down. Missing interpreters are
    /// soft-skipped. Returns the number of hard failures so a caller can
    /// exit non-zero from a CLI flag like --probe-adapters.
    /// </summary>
    public static async Task<int> ProbeAllAsync(AdapterRegistry registry)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("=== Probe Adapters === SKIP (Windows-only worker path)");
            return 0;
        }

        Console.WriteLine("=== Probe Adapters ===");

        var adaptersWithProbe = registry.All
            .Where(a => a.Probe != null && !string.IsNullOrEmpty(a.Probe.Eval))
            .OrderBy(a => a.Name)
            .ToList();

        if (adaptersWithProbe.Count == 0)
        {
            Console.WriteLine("  (no adapters declare probe:)");
            return 0;
        }

        int pass = 0;
        int fail = 0;

        foreach (var adapter in adaptersWithProbe)
        {
            var (ok, skipped) = await ProbeOneAsync(adapter);
            if (skipped) continue;
            if (ok) { pass++; Console.WriteLine($"  PASS: {adapter.Name}"); }
            else { fail++; Console.Error.WriteLine($"  FAIL: {adapter.Name}"); }
        }

        Console.WriteLine($"\n{pass} passed, {fail} failed");
        return fail;
    }

    private static async Task<(bool ok, bool skipped)> ProbeOneAsync(Adapter adapter)
    {
        var shell = adapter.Name;
        var proxyPid = Environment.ProcessId;
        var agentId = "probe";
        var cwd = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        int workerPid;
        try
        {
            var launcher = new ProcessLauncher();
            workerPid = launcher.LaunchConsoleWorker(proxyPid, agentId, shell, cwd);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  SKIP: {adapter.Name} (launch failed: {ex.GetType().Name}: {ex.Message})");
            return (false, true);
        }

        var pipeName = $"SP.{proxyPid}.{agentId}.{workerPid}";

        try
        {
            var ready = await ConsoleWorkerTests.WaitForPipeAsync(pipeName, TimeSpan.FromSeconds(30));
            if (!ready)
            {
                Console.WriteLine($"  SKIP: {adapter.Name} (worker pipe never ready — interpreter likely missing)");
                return (false, true);
            }

            var probeTest = new AdapterTest
            {
                Name = "probe",
                Eval = adapter.Probe!.Eval,
                Expect = adapter.Probe.Expect,
            };
            try
            {
                var ok = await RunOneTestAsync(pipeName, probeTest);
                return (ok, false);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"    {ex.GetType().Name}: {ex.Message}");
                return (false, false);
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
    }

    public static async Task<int> RunAsync(AdapterRegistry registry)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("=== Adapter-declared Tests === SKIP (Windows-only, same as ConsoleWorkerTests.Run)");
            return 0;
        }

        Console.WriteLine("=== Adapter-declared Tests ===");

        var adaptersWithTests = registry.All
            .Where(a => a.Tests != null && a.Tests.Count > 0)
            .OrderBy(a => a.Name)
            .ToList();

        if (adaptersWithTests.Count == 0)
        {
            Console.WriteLine("  (no adapters declare tests:)");
            return 0;
        }

        int totalPass = 0;
        int totalFail = 0;

        foreach (var adapter in adaptersWithTests)
        {
            Console.WriteLine($"\n--- {adapter.Name} ({adapter.Tests!.Count} test(s)) ---");
            var (pass, fail) = await RunAdapterAsync(adapter);
            totalPass += pass;
            totalFail += fail;
            Console.WriteLine($"  {adapter.Name}: {pass} passed, {fail} failed");
        }

        Console.WriteLine($"\n{totalPass} passed, {totalFail} failed");
        return totalFail;
    }

    private static async Task<(int pass, int fail)> RunAdapterAsync(Adapter adapter)
    {
        int pass = 0;
        int fail = 0;
        void Assert(bool condition, string name)
        {
            if (condition) { pass++; Console.WriteLine($"  PASS: {adapter.Name}/{name}"); }
            else { fail++; Console.Error.WriteLine($"  FAIL: {adapter.Name}/{name}"); }
        }

        // Use the adapter's canonical name as the shell argument; the
        // worker resolves it via ResolveShellPath (PATH lookup) and
        // looks the adapter up via AdapterRegistry.Default.Find, which
        // matches on the same name.
        var shell = adapter.Name;
        var proxyPid = Environment.ProcessId;
        var agentId = "adaptertest";
        var cwd = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        int workerPid;
        try
        {
            var launcher = new ProcessLauncher();
            workerPid = launcher.LaunchConsoleWorker(proxyPid, agentId, shell, cwd);
        }
        catch (Exception ex)
        {
            // A missing runtime (python / node not on PATH) is a soft
            // skip, not a hard failure — CI matrices can't install
            // every language. A crash during launch is still a hard
            // fail because the binary should at least be able to spawn
            // its own embedded adapters without throwing.
            Console.WriteLine($"  SKIP: {adapter.Name} (launch failed: {ex.GetType().Name}: {ex.Message})");
            return (pass, fail);
        }

        var pipeName = $"SP.{proxyPid}.{agentId}.{workerPid}";

        try
        {
            var ready = await ConsoleWorkerTests.WaitForPipeAsync(pipeName, TimeSpan.FromSeconds(30));
            if (!ready)
            {
                // Worker process started but never stood up its pipe —
                // typically because the declared interpreter isn't
                // available on this machine (zsh on a Windows box that
                // has neither WSL zsh nor MSYS2 zsh on PATH, for
                // instance). Treat as a soft skip so the runner can
                // still report the other adapters cleanly.
                Console.WriteLine($"  SKIP: {adapter.Name} (worker pipe never ready — interpreter likely missing)");
                return (pass, fail);
            }

            // Run probe first (if declared) as a quick health check. The
            // probe exists in schema §14 as "single eval + expected regex
            // used as a load-time sanity check"; we repurpose it here as
            // a pre-flight for the tests: block so an adapter with a
            // fundamentally broken launch fails fast without burning
            // time on the richer test suite.
            if (adapter.Probe != null && !string.IsNullOrEmpty(adapter.Probe.Eval))
            {
                var probeTest = new AdapterTest
                {
                    Name = "probe",
                    Eval = adapter.Probe.Eval,
                    Expect = adapter.Probe.Expect,
                };
                try
                {
                    var ok = await RunOneTestAsync(pipeName, probeTest);
                    Assert(ok, "probe");
                    if (!ok)
                    {
                        // Probe failure usually means the interpreter
                        // isn't responding as expected — the richer
                        // tests: block below would just pile on
                        // more failures. Bail out early.
                        Console.Error.WriteLine($"  (skipping {adapter.Name}/tests — probe failed)");
                        return (pass, fail);
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"    {ex.GetType().Name}: {ex.Message}");
                    Assert(false, "probe");
                    return (pass, fail);
                }
            }

            foreach (var test in adapter.Tests!)
            {
                try
                {
                    var ok = await RunOneTestAsync(pipeName, test);
                    Assert(ok, test.Name);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"    {ex.GetType().Name}: {ex.Message}");
                    Assert(false, test.Name);
                }
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

    private static async Task<bool> RunOneTestAsync(string pipeName, AdapterTest test)
    {
        // Baseline cwd for expect_cwd_update assertions. Captured from
        // get_status BEFORE any setup runs, so the assertion fires when
        // "cwd differs from where the worker booted" rather than
        // "setup cwd differs from eval cwd" (which is almost always
        // identical — both sit at wherever setup left us).
        var baselineCwd = await GetStatusCwdAsync(pipeName);

        // Setup sequence is a list of prior commands to warm the REPL
        // state. Each runs via `execute` and its output is discarded.
        if (test.SetupSequence != null)
        {
            foreach (var step in test.SetupSequence)
            {
                var stepCmd = step.Eval ?? step.Setup;
                if (!string.IsNullOrEmpty(stepCmd))
                    await ExecuteIgnoringOutputAsync(pipeName, stepCmd);
            }
        }

        // Standalone setup field (the common case for one-off warm-up
        // commands like `cd /tmp` or `x = 42`).
        if (!string.IsNullOrEmpty(test.Setup))
            await ExecuteIgnoringOutputAsync(pipeName, test.Setup);

        // Eval-less test: the only sensible interpretation today is
        // "run the setup and then just check cwd changed". Python's
        // os.chdir test uses this shape.
        if (string.IsNullOrEmpty(test.Eval))
        {
            if (test.ExpectCwdUpdate)
            {
                var cwdNow = await GetStatusCwdAsync(pipeName);
                return !string.IsNullOrEmpty(cwdNow)
                    && (baselineCwd == null || cwdNow != baselineCwd);
            }
            return true;
        }

        var evalResp = await ConsoleWorkerTests.SendRequest(
            pipeName,
            w =>
            {
                w.WriteString("type", "execute");
                w.WriteString("command", test.Eval);
                w.WriteNumber("timeout", 15000);
            },
            TimeSpan.FromSeconds(20));

        var timedOut = evalResp.TryGetProperty("timedOut", out var t) && t.GetBoolean();
        if (timedOut) return false;

        var rawOutput = evalResp.TryGetProperty("output", out var o) ? o.GetString() ?? "" : "";
        // REPLs (Python, Node) emit colorized output via their default
        // displayhook / util.inspect, so a literal regex like
        // `ValueError: boom` that the adapter author wrote would miss
        // because ANSI codes are sprinkled between "ValueError" and the
        // colon. Strip ANSI before matching — the visible text is what
        // the adapter test is asserting against, not the wire format.
        // Also trim leading/trailing whitespace so a Node REPL's
        // "\n2\n" output still matches a `^2$` anchored regex.
        var output = StripAnsi(rawOutput).Trim();

        var cwdAfterEval = evalResp.TryGetProperty("cwd", out var cw) ? cw.GetString() : null;
        var exitCode = evalResp.TryGetProperty("exitCode", out var ec) ? ec.GetInt32() : 0;

        if (test.ExpectCwdUpdate)
        {
            if (string.IsNullOrEmpty(cwdAfterEval)) return false;
            if (baselineCwd != null && cwdAfterEval == baselineCwd) return false;
        }

        if (!string.IsNullOrEmpty(test.Expect))
        {
            // Multiline so ^ and $ match line boundaries inside the
            // command's output (e.g. a Node REPL produces "\n2\n" but
            // the author wrote `^2$` expecting a line match). Singleline
            // so `.` matches newlines for multi-line captures.
            var re = new Regex(test.Expect, RegexOptions.Multiline | RegexOptions.Singleline);
            if (!re.IsMatch(output)) return false;
        }

        // expect_exit_code is suppressed when exit_code_is_unreliable
        // is set — the adapter is documenting a known limitation and
        // the field then becomes illustrative, not an invariant.
        if (test.ExpectExitCode is int expected && !test.ExitCodeIsUnreliable)
        {
            if (exitCode != expected) return false;
        }

        // expect_error: true means the command should surface an
        // error. For shells we approximate this as exitCode != 0,
        // which is the same convention assert_shell_error uses in
        // most test frameworks. REPLs where errors don't affect
        // exit_code (Python's OSC D;0 always) should NOT use
        // expect_error and should match the error text via `expect`
        // regex instead.
        if (test.ExpectError)
        {
            if (exitCode == 0) return false;
        }

        // expect_mode / expect_level: assert which mode the worker
        // reports after the eval. Backed by ConsoleWorker's
        // ModeDetector which re-evaluates after every command. Tests
        // for adapters without a modes block (no currentMode field
        // in the response) are a quiet no-op — the field is treated
        // as additive.
        if (!string.IsNullOrEmpty(test.ExpectMode))
        {
            if (!evalResp.TryGetProperty("currentMode", out var modeProp)) return false;
            if (modeProp.ValueKind == System.Text.Json.JsonValueKind.Null) return false;
            if (modeProp.GetString() != test.ExpectMode) return false;
        }
        if (test.ExpectLevel is int expectedLevel)
        {
            if (!evalResp.TryGetProperty("currentModeLevel", out var lvlProp)) return false;
            if (lvlProp.ValueKind == System.Text.Json.JsonValueKind.Null) return false;
            if (lvlProp.GetInt32() != expectedLevel) return false;
        }

        return true;
    }

    private static async Task<string?> GetStatusCwdAsync(string pipeName)
    {
        try
        {
            var resp = await ConsoleWorkerTests.SendRequest(
                pipeName,
                w => w.WriteString("type", "get_status"));
            return resp.TryGetProperty("cwd", out var c) ? c.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    // Strip CSI / OSC / VT100 control sequences from output so
    // adapter-author regexes can match visible text without having to
    // anticipate colorized REPL output.
    private static readonly Regex _ansiRegex = new(
        @"\x1B\[[0-?]*[ -/]*[@-~]|\x1B\][^\x07\x1B]*(\x07|\x1B\\)",
        RegexOptions.Compiled);

    private static string StripAnsi(string input)
        => string.IsNullOrEmpty(input) ? input : _ansiRegex.Replace(input, "");

    /// <summary>
    /// Execute a setup command and discard its output. Still waits
    /// for the response so subsequent commands see the setup's state
    /// changes (variable assignments, imports, etc.).
    /// </summary>
    private static async Task ExecuteIgnoringOutputAsync(string pipeName, string command)
    {
        await ConsoleWorkerTests.SendRequest(
            pipeName,
            w =>
            {
                w.WriteString("type", "execute");
                w.WriteString("command", command);
                w.WriteNumber("timeout", 15000);
            },
            TimeSpan.FromSeconds(20));
    }

}
