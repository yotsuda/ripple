using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Splash.Services.Adapters;

namespace Splash.Services;

/// <summary>
/// Console worker process: runs in --console mode.
/// Creates a PTY, launches the shell, injects shell integration (OSC 633),
/// and serves commands via Named Pipe.
///
/// This is the "console side" — all PTY I/O, OSC parsing, and command tracking
/// happen here. The proxy side only communicates via Named Pipe protocol.
/// </summary>
public class ConsoleWorker
{
    // Worker logs go to a file, NOT to Console.Error.
    // The worker's visible console (Console.Out) is reserved for mirroring PTY output.
    // Anything to stderr would also appear there, mixed with PTY data.
    private static readonly string LogFile = Path.Combine(Path.GetTempPath(), $"splash-worker-{Environment.ProcessId}.log");
    private static void Log(string msg) { try { File.AppendAllText(LogFile, $"{DateTime.Now:HH:mm:ss.fff} {msg}\n"); } catch { } }

    /// <summary>
    /// Delete worker log files older than 24 hours so long-running sessions
    /// don't accumulate files in %TEMP%. Best-effort — failures are silent.
    /// </summary>
    private static void CleanupOldLogs()
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddHours(-24);
            foreach (var path in Directory.EnumerateFiles(Path.GetTempPath(), "splash-worker-*.log"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(path) < cutoff)
                        File.Delete(path);
                }
                catch { /* in use by another live worker, or locked — skip */ }
            }
            // Multi-line command tempfiles — HandleExecuteAsync writes the
            // command body to `.splash-exec-{pid}-{guid}.ps1` and deletes
            // it inline via `Remove-Item` after dot-sourcing. If the
            // worker crashes or the shell dies mid-dot-source the delete
            // never runs, so sweep stale ones older than 24 hours here
            // on startup just like we do for the logs.
            foreach (var path in Directory.EnumerateFiles(Path.GetTempPath(), ".splash-exec-*.ps1"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(path) < cutoff)
                        File.Delete(path);
                }
                catch { /* in use / locked — skip */ }
            }
        }
        catch { /* TEMP not readable — skip */ }
    }

    private string _pipeName;
    private readonly string _unownedPipeName;
    private int _proxyPid;
    private TaskCompletionSource<string>? _claimTcs;
    private readonly string _shell;
    private readonly string _cwd;
    /// <summary>
    /// Adapter for the shell this worker is hosting, looked up from
    /// AdapterRegistry.Default during construction. Null when no YAML
    /// adapter matches the shell name — in that case the worker falls back
    /// to the original hardcoded shell-family branches. Phase B milestones
    /// progressively replace those branches with adapter-driven reads.
    /// </summary>
    private readonly Adapter? _adapter;

    /// <summary>
    /// Milestone 2i: precomputed shell-family info so in-worker code never
    /// has to call the ConsoleManager helpers that phase B is tearing out.
    ///
    /// _shellFamily is the normalized family key (e.g. "pwsh", "bash");
    /// _isPwshFamily is true for pwsh / powershell;
    /// _defaultEnter is the PTY line-ending to use for this shell —
    /// adapter.input.line_ending when loaded, else the legacy
    /// "\r for pwsh/cmd, \n for everything else" rule.
    /// </summary>
    private readonly string _shellFamily;
    private readonly bool _isPwshFamily;
    private readonly string _defaultEnter;
    private IPtySession? _pty;

    /// <summary>
    /// Current mode for adapters that declare a <c>modes:</c> block
    /// (schema §9). Re-evaluated after each command resolves by
    /// running every auto_enter mode's detect regex against the tail
    /// of the captured output — see <see cref="ModeDetector"/>. Null
    /// when the adapter has no modes block. Surfaced on execute /
    /// get_status responses as <c>currentMode</c> so MCP clients
    /// (and AdapterDeclaredTestsRunner's expect_mode assertion) can
    /// observe mode transitions across commands.
    /// </summary>
    private string? _currentMode;
    private int? _currentModeLevel;
    private Stream? _writer;
    private readonly OscParser _parser = new();

    /// <summary>
    /// Regex-based prompt detector for adapters that declare
    /// <c>prompt.strategy: regex</c> (REPLs whose prompt cannot be replaced
    /// to emit OSC 633 markers — F# Interactive, ghci without integration,
    /// etc.). Null for adapters using <c>shell_integration</c> or
    /// <c>marker</c> strategies, in which case <see cref="OscParser"/> alone
    /// drives prompt boundary detection. Fed by the same read loop that
    /// feeds <see cref="_parser"/>; the synthetic events it produces are
    /// merged with real OSC events in TextOffset order so
    /// <see cref="CommandTracker"/> sees one coherent stream.
    /// </summary>
    private readonly RegexPromptDetector? _regexPromptDetector;
    private bool _regexFirstPromptSeen;
    private readonly CommandTracker _tracker = new();
    private bool _ready;
    private volatile int _outputLength;
    // Controls whether PTY output is mirrored to the worker's visible console.
    // Disabled during shell integration injection to hide the source echo.
    private volatile bool _mirrorVisible = true;
    // User-input hold gate: when true, InputForwardLoop buffers
    // keystrokes instead of forwarding them to the PTY. Set before
    // an AI command is written and cleared after the command
    // completes. Ctrl+C (0x03) passes through even when held so the
    // user can interrupt a stuck command. Held bytes are replayed
    // to the PTY on release so the user's typing isn't lost.
    private volatile bool _holdUserInput;
    private readonly System.Collections.Concurrent.ConcurrentQueue<byte[]> _heldUserInput = new();
    // Direct stdout stream — bypasses Console.Out's TextWriter buffering.
    private Stream? _stdoutStream;

    /// <summary>
    /// Current console status for get_status requests.
    /// </summary>
    private string Status => _tracker.Busy ? "busy" : (_tracker.HasCachedOutput ? "completed" : "standby");

    private readonly string? _banner;
    private readonly string? _reason;
    // Rolling buffer for partial OSC title sequences that straddle
    // a PTY read-chunk boundary. ReplaceOscTitle owns the scan logic
    // and pushes any unterminated opener here so the next chunk can
    // re-scan with the terminator visible. Single owner: the read
    // loop's MirrorToVisible call path.
    private string _oscTitlePending = "";

    // Title set by proxy via set_title. Used to override OSC 0 title sequences
    // emitted by shells (e.g., bash's PROMPT_COMMAND sets "user@host: cwd").
    private volatile string? _desiredTitle;
    // Set to true when a strictly newer proxy tries to claim this worker.
    // Signals the main loop to stop serving pipes while keeping the PTY alive
    // so the user can continue working in the terminal.
    private volatile bool _obsolete;
    // Fires when ReadOutputLoop sees EOF on the PTY output stream, i.e. the
    // child shell process has exited (e.g. user typed `exit`). The main loop
    // watches this so the worker process shuts down cleanly instead of
    // hanging forever on a dead PTY while pending execute requests sit
    // stuck waiting for OSC markers that will never come.
    private readonly TaskCompletionSource _shellExitedTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    // This worker's own binary version — compared against the proxy_version
    // field in incoming claim requests to detect cross-version re-claim.
    private static readonly Version _myVersion =
        typeof(ConsoleWorker).Assembly.GetName().Version ?? new Version(0, 0);

    public ConsoleWorker(string pipeName, int proxyPid, string shell, string cwd, string? banner = null, string? reason = null)
    {
        _pipeName = pipeName;
        _proxyPid = proxyPid;
        _shell = shell;
        _cwd = cwd;
        _banner = banner;
        _reason = reason;
        _unownedPipeName = $"{ConsoleManager.PipePrefix}.{Environment.ProcessId}";

        // Adapter shadow lookup (phase B, Milestone 2a). Normalize the
        // shell path to a family name and look it up in the registry.
        // Null result means we fall through to the hardcoded paths.
        _shellFamily = ConsoleManager.NormalizeShellFamily(shell);
        _adapter = AdapterRegistry.Default?.Find(_shellFamily);
        Log(_adapter != null
            ? $"Adapter matched: name={_adapter.Name} family={_adapter.Family} version={_adapter.Version}"
            : $"No adapter matched for shell family '{_shellFamily}' — using hardcoded fallback");

        // process.executable{_candidates} override: for adapters whose
        // REPL name does not match an executable on PATH (fsi → dotnet,
        // jshell → java, perldb → perl, etc.), the adapter declares
        // which binary to launch. Re-resolve _shell against that name
        // so {shell_path} expansion in BuildCommandLine produces the
        // right launcher.
        //
        // Two override forms exist:
        //  - `executable_candidates: [list]` — walked left-to-right,
        //    each entry env-var-expanded and PATH-resolved. First entry
        //    that exists on disk wins. Use this when the binary lives
        //    at multiple plausible install locations across
        //    distributions (Perl / JDK / Python / etc.).
        //  - `executable: <string>` — single override, kept for
        //    backwards compatibility and for cases where there's
        //    exactly one canonical launcher (`dotnet` for fsi).
        // Candidates take precedence when present; the single-string
        // form is the fallback. When neither resolves, the adapter
        // name is left as-is and CreateProcess will report
        // ERROR_FILE_NOT_FOUND with a hopefully-actionable message.
        if (_adapter?.Process.ExecutableCandidates is { Count: > 0 } candidates)
        {
            string? pickedResolved = null;
            string? pickedRaw = null;
            foreach (var raw in candidates)
            {
                var expanded = Environment.ExpandEnvironmentVariables(raw);
                var resolved = ConsoleManager.ResolveShellPath(expanded);
                if (File.Exists(resolved))
                {
                    pickedRaw = raw;
                    pickedResolved = resolved;
                    break;
                }
                Log($"Executable candidate miss: '{raw}' (expanded='{expanded}', resolved='{resolved}')");
            }
            if (pickedResolved != null)
            {
                Log($"Executable candidate picked: '{pickedRaw}' → {pickedResolved}");
                _shell = pickedResolved;
            }
            else
            {
                Log($"WARNING: all {candidates.Count} executable_candidates failed for adapter '{_adapter.Name}'; launch will likely fail");
            }
        }
        else if (!string.IsNullOrEmpty(_adapter?.Process.Executable))
        {
            var expanded = Environment.ExpandEnvironmentVariables(_adapter.Process.Executable);
            var resolved = ConsoleManager.ResolveShellPath(expanded);
            Log($"Executable override: '{_adapter.Process.Executable}' → {resolved}");
            _shell = resolved;
        }

        // Adapters with prompt.strategy == "regex" don't speak OSC 633 at
        // all; the prompt is a literal visible string like "> " (F# Interactive)
        // or "irb(main):001:0> " (irb). Construct a RegexPromptDetector now
        // so the read loop can scan the cleaned PTY output for prompt
        // boundaries and synthesize PromptStart events for the tracker.
        if (_adapter?.Prompt.Strategy == "regex")
        {
            var pattern = _adapter.Prompt.Primary ?? _adapter.Prompt.PrimaryRegex;
            if (!string.IsNullOrEmpty(pattern))
            {
                _regexPromptDetector = new RegexPromptDetector(pattern);
                Log($"Regex prompt detector active: pattern={pattern}");
            }
            else
            {
                Log("WARNING: prompt.strategy == regex but no prompt.primary / prompt.primary_regex set");
            }
        }

        // Milestone 2i: bake shell-family booleans into readonly fields so
        // no in-worker code needs ConsoleManager.IsPowerShellFamily /
        // EnterKeyFor after this constructor returns. Those helpers are
        // removed from ConsoleManager as part of this milestone.
        _isPwshFamily = _shellFamily is "pwsh" or "powershell";
        _defaultEnter = _adapter?.Input.LineEnding
            ?? (_isPwshFamily || _shellFamily == "cmd" ? "\r" : "\n");
    }

    public async Task RunAsync(CancellationToken ct)
    {
        // Ensure the worker's visible console uses UTF-8 for both input and output.
        Console.InputEncoding = Encoding.UTF8;
        Console.OutputEncoding = Encoding.UTF8;

        // Set initial window title (unowned format — proxy will update after claiming)
        Console.Title = $"#{Environment.ProcessId} ~~~~";

        // Enable Virtual Terminal Processing on stdout so the visible console
        // interprets ANSI/VT escape sequences (cursor movement, clear-to-EOL,
        // colors) emitted by the shell instead of writing them as literal chars.
        EnableVirtualTerminalOutput();

        // Display banner/reason. For pwsh/powershell, banner emission is
        // delegated to the generated integration tempfile so it survives
        // ConPTY's startup `\e[2J\e[H` wipe; see BuildCommandLine. For
        // other shells, write directly to the worker's stdout here. (TODO:
        // bash/zsh/cmd have the same ConPTY-wipe issue and would also
        // benefit from shell-side emission.)
        if (!_isPwshFamily)
            WriteBanner();

        // Prepare shell integration script BEFORE launching the shell.
        // For pwsh, we pass it via -NoExit -Command so it doesn't echo in the console.
        var commandLine = BuildCommandLine();

        // Launch shell via platform PTY (ConPTY on Windows, forkpty on Linux/macOS)
        // Use the visible console's actual dimensions instead of hardcoded 120x30.
        // MSYS2/Git Bash needs the parent's environment (MSYSTEM, HOME, PATH with Git paths).
        // pwsh uses a clean environment to avoid inheriting MCP server variables.
        var shellName = _shellFamily;
        // Milestone 2b: inherit_environment comes from the adapter when
        // one is loaded for this shell, else falls back to the hardcoded
        // "pwsh family = clean env, everyone else = inherit" rule.
        bool inheritEnv = _adapter?.Process.InheritEnvironment
            ?? !_isPwshFamily;
        int cols = Console.WindowWidth > 0 ? Console.WindowWidth : 120;
        int rows = Console.WindowHeight > 0 ? Console.WindowHeight : 30;
        var envOverrides = _adapter?.Process.Env;

        // init.delivery: rc_file — stage the integration script as a
        // shell-specific rc file that the interpreter sources on its own
        // startup, before any PTY interaction. The hooks are registered
        // by the time the first prompt is drawn, so the ready-phase
        // inject cycle is bypassed entirely. zsh is the motivating case:
        // MSYS2 zsh under ConPTY runs ZLE in a mode that swallows
        // PTY-written `source <tmpfile>` bytes without submitting them
        // (neither \n nor \r\n is a reliable Enter from our write path),
        // so pty_inject hangs on WaitForReady forever. ZDOTDIR sidesteps
        // ZLE entirely. The same mechanism extends to any shell with an
        // rc-directory env var (future fish / bash --init-file use cases).
        if (_adapter?.Init.Delivery == "rc_file"
            && _adapter.Init.RcFile is { DirEnvVar: string envVar, FileName: string fileName }
            && !string.IsNullOrEmpty(envVar) && !string.IsNullOrEmpty(fileName)
            && _adapter.IntegrationScript is string rcScript)
        {
            var rcDir = Path.Combine(Path.GetTempPath(), $".splash-{_shellFamily}-{Environment.ProcessId}");
            Directory.CreateDirectory(rcDir);
            // Line endings are normalised to LF before write: the shells
            // that read rc files under this delivery mode (zsh on MSYS2,
            // future fish, etc.) all come from the POSIX lineage and
            // parse CRLF as part of the command text.
            await File.WriteAllTextAsync(Path.Combine(rcDir, fileName), rcScript.Replace("\r\n", "\n"), ct);
            envOverrides = new Dictionary<string, string>(envOverrides ?? new Dictionary<string, string>())
            {
                [envVar] = rcDir
            };
            Log($"rc_file delivery: staged {rcDir}\\{fileName}; {envVar} set in child env");
        }

        _pty = PtyFactory.Start(commandLine, _cwd, cols, rows,
            inheritEnvironment: inheritEnv,
            envOverrides: envOverrides);
        _tracker.SetTerminalSize(cols, rows);
        _writer = _pty.InputStream;

        // Start reading PTY output on dedicated thread (feeds OscParser + CommandTracker)
        var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var readTask = ReadOutputLoop(readCts.Token);

        // Start user-input forwarding immediately — before shell integration
        // loading, before WaitForReady. Early start is critical on Unix:
        // PSReadLine (pwsh) fires DSR during its own startup sync, BEFORE the
        // first OSC A that WaitForReady gates on. The outer terminal emulator
        // answers DSR with the real cursor position, and that reply has to
        // reach the shell for PSReadLine to continue. If input forwarding
        // starts after WaitForReady the reply sits in splash's stdin buffer
        // long enough that PSReadLine either times out on DSR wait
        // (degraded-mode rendering — wrong cursor row) or consumes the
        // buffered reply as typed characters once forwarding catches up
        // (garbage in the command line). Forwarding from t=0 keeps the
        // byte relay bidirectional for every sequence the shell and the
        // outer terminal exchange during startup handshake. Windows is
        // unaffected: ConPTY is its own terminal emulator so the outer /
        // inner distinction doesn't exist there.
        var inputTask = InputForwardLoop(ct);

        // Watch the child shell process. ConPTY does NOT close the output
        // pipe when the child exits, so ReadOutputLoop's blocking Read
        // would sit waiting forever if we relied on EOF. Instead wait on
        // the process handle directly and, when it fires, signal the main
        // loop via _shellExitedTcs so the worker can tear itself down.
        _ = WaitForShellExitAsync(ct);

        // Milestone 2c: PTY line-ending sequence for submitting input.
        // Comes from the adapter's input.line_ending when loaded (pwsh/cmd
        // = "\r", bash/zsh = "\n"), else falls back to the hardcoded
        // family-based rule.
        var enter = _adapter?.Input.LineEnding
            ?? _defaultEnter;

        // Milestone 2f: ready-phase orchestration is driven by adapter.Ready
        // fields. Three paths emerge from the four shells:
        //
        //   pwsh  - integration was loaded via -Command at launch; the first
        //           OSC A fires automatically. No settle, no inject, no kick.
        //   cmd   - /k prompt doesn't paint until it reads input. Settle,
        //           then kick Enter to render the OSC-aware prompt — that
        //           kick IS the ready signal, so it happens BEFORE
        //           WaitForReady.
        //   bash/zsh - settle, suppress mirror, inject the integration
        //           script via PTY stdin, settle again. The kick happens
        //           AFTER WaitForReady so the suppressed prompt is redrawn.
        //
        // suppress_mirror_during_inject is the discriminator between the
        // cmd path (kick-before-ready) and the bash/zsh path (inject +
        // kick-after-ready), since both have kick_enter_after_ready=true.
        var settleMs = _adapter?.Ready.SettleBeforeInjectMs
            ?? (_isPwshFamily ? 0 : 2000);
        var suppressMirror = _adapter?.Ready.SuppressMirrorDuringInject
            ?? (!_isPwshFamily && shellName is not "cmd");
        var kickEnter = _adapter?.Ready.KickEnterAfterReady
            ?? !_isPwshFamily;
        var delayAfterInject = _adapter?.Ready.DelayAfterInjectMs
            ?? (suppressMirror ? 500 : 0);

        if (settleMs > 0)
            await WaitForOutputSettled(ct);

        if (suppressMirror)
        {
            _mirrorVisible = false;
            await InjectShellIntegration(ct);
            if (delayAfterInject > 0)
                await Task.Delay(delayAfterInject, ct);
        }
        else if (kickEnter)
        {
            await WriteToPty(enter, ct);
        }

        // Wait for PromptStart marker from shell integration (confirms OSC pipeline is working)
        // Wait until the shell reports its first OSC A (PromptStart).
        // No wall-clock timeout: a cold pwsh startup + Defender first-
        // scan of splash.exe + Import-Module PSReadLine + sourcing
        // integration.ps1 can take arbitrary real time, and any fallback
        // "proceed without OSC markers" path lets the worker accept AI
        // commands before the shell is actually interactive — the next
        // stray first-prompt OSC A then resolves them against a stale
        // pre-command buffer, returning reason banner / PSReadLine
        // prediction rendering as if it were command output. Instead we
        // patiently wait; if the shell process dies during startup
        // WaitForShellExitAsync fires _shellExitedTcs and bails us out.
        await WaitForReady(ct);
        _ready = true;
        _mirrorVisible = true;

        // For regex-strategy adapters, the FIRST visible prompt may
        // appear before the REPL's eval loop has finished wiring up
        // (fsi --use:script.fsx is the canonical case: the post-script
        // -load prompt fires ~200ms before stdin input is actually
        // accepted by the eval loop). The detector has no way to
        // distinguish "true REPL ready" from "post-script-load
        // intermediate prompt", so honor an adapter-declared settle
        // window before letting the worker accept commands. Reuses
        // ready.delay_after_inject_ms because the semantics are the
        // same: "wait this long after the ready signal before
        // declaring the worker open for business".
        if (_regexPromptDetector != null && _adapter?.Ready.DelayAfterInjectMs is int dms && dms > 0)
        {
            Log($"regex strategy: settling {dms} ms after first prompt before pipe ready");
            await Task.Delay(dms, ct);
        }

        // For shells with PTY-injected integration (bash/zsh), the prompt drawn
        // during injection was suppressed. Send a kick to draw a fresh prompt.
        // Milestone 2f: gated on suppressMirror (the inject path) so cmd's
        // pre-ready kick isn't duplicated here.
        if (suppressMirror && kickEnter)
        {
            await WriteToPty(enter, ct);
        }

        Log($"Shell ready, pipe={_pipeName}");

        // Monitor visible console window resizes and propagate to ConPTY
        var resizeTask = ResizeMonitorLoop(ct);

        // cmd has no preexec hook (no PROMPT-time access to %ERRORLEVEL%, no
        // way to fire OSC 633 C when the user starts a command), so the
        // OSC-driven user-busy tracking that pwsh and bash rely on is silent
        // for cmd. Run a side-channel polling loop that watches the cmd
        // process's CPU usage and child-process count to derive a busy hint:
        // CPU > 0 → cmd is running an internal builtin, child present → cmd
        // launched an external command. Either signal flips the tracker to
        // busy so execute_command auto-routes around the user.
        //
        // Milestone 2h: gated on adapter.capabilities.user_busy_detection
        // (falls back to the old shellName == "cmd" check for unknown shells).
        Task? userBusyTask = null;
        var userBusyMethod = _adapter?.Capabilities.UserBusyDetection
            ?? (shellName == "cmd" ? "process_polling" : null);
        if (OperatingSystem.IsWindows() && userBusyMethod == "process_polling")
            userBusyTask = UserBusyDetectorLoop(ct);

        // Run owned + unowned pipe servers. Two owned listeners share the
        // pipe name via NamedPipeServerStream.MaxAllowedServerInstances — a
        // long-running execute occupies one instance, the other stays free
        // to handle get_status / get_cached_output without stalling. When
        // the proxy dies, both owned instances are cancelled and the
        // unowned pipe keeps running for re-claim by another proxy.
        const int OwnedListenerCount = 2;
        while (!ct.IsCancellationRequested && !_obsolete)
        {
            using var ownedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var ownedTasks = new Task[OwnedListenerCount];
            for (int i = 0; i < OwnedListenerCount; i++)
                ownedTasks[i] = RunPipeServerAsync(_pipeName, ownedCts.Token);
            var unownedTask = RunPipeServerAsync(_unownedPipeName, ct);
            var monitorTask = MonitorParentProxyAsync(ct);

            // Wait for proxy death, shell exit, or external cancellation.
            // Shell exit (user typed `exit`, or the shell crashed) wins and
            // shuts the whole worker down so the proxy's next pipe request
            // gets a clean IOException and reports "Previous console died"
            // to the AI. Otherwise proxy death triggers re-claim flow.
            var mainWaitWinner = await Task.WhenAny(monitorTask, _shellExitedTcs.Task);
            if (mainWaitWinner == _shellExitedTcs.Task)
            {
                Log("Shell process exited; worker shutting down");
                ownedCts.Cancel();
                await Task.WhenAll(ownedTasks).ContinueWith(_ => { });
                break;
            }

            // Proxy died — stop owned listeners, keep unowned running
            ownedCts.Cancel();
            await Task.WhenAll(ownedTasks).ContinueWith(_ => { });

            // Wait for re-claim via unowned pipe (blocks until _claimTcs is set)
            _claimTcs = new TaskCompletionSource<string>();
            Console.Title = $"#{Environment.ProcessId} ~~~~";
            Log("Proxy died, waiting for re-claim on unowned pipe...");

            string newPipeName;
            try
            {
                newPipeName = await _claimTcs.Task;
            }
            catch (InvalidOperationException) when (_obsolete)
            {
                Log("Claim refused (obsolete); exiting pipe service loop. Shell remains available for user.");
                break;
            }
            _pipeName = newPipeName;
            _proxyPid = GetProxyPidFromPipeName(newPipeName);
            _claimTcs = null;
            Log($"Re-claimed by proxy {_proxyPid}, new pipe={_pipeName}");
        }

        // Obsolete mode: the pipe service has stopped, but the shell is still
        // alive and the user may still be working in it. readTask/inputTask/
        // resizeTask are running on `ct`; wait here until the user closes the
        // console window (which cancels `ct`) or the shell process exits,
        // then fall through to cleanup.
        if (_obsolete)
        {
            try { await Task.WhenAny(Task.Delay(Timeout.Infinite, ct), _shellExitedTcs.Task); }
            catch (OperationCanceledException) { }
        }

        // Cleanup — guarded so any late-stage race (ReadOutputLoop still
        // blocked on the now-dead PTY, double-dispose of handles, etc.)
        // can't escape and turn an otherwise-clean shutdown into a non-
        // zero process exit. Windows Terminal's "close on exit" default
        // only fires on exit code 0, so a thrown exception here would
        // leave the visible window stuck after the shell died.
        try { readCts.Cancel(); } catch { }
        try { _pty?.Dispose(); } catch (Exception ex) { Log($"PTY dispose: {ex.GetType().Name}: {ex.Message}"); }
        Log("RunAsync completed cleanly");
    }

    /// <summary>
    /// Watch the parent proxy process. When it dies, the worker continues
    /// running on the unowned pipe so another proxy can claim it.
    /// </summary>
    private async Task MonitorParentProxyAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(2000, ct);
            try
            {
                using var _ = System.Diagnostics.Process.GetProcessById(_proxyPid);
            }
            catch
            {
                // Parent died — main loop handles title revert and re-claim.
                return;
            }
        }
    }

    // --- Shell integration injection ---

    /// <summary>
    /// Build the command line for launching the shell.
    /// For pwsh: injects shell integration via -NoExit -Command (no echo in console).
    /// For bash/zsh: plain shell, injection happens later via PTY input.
    /// </summary>
    private string BuildCommandLine()
    {
        var shellName = _shellFamily;

        if (_isPwshFamily)
        {
            // Milestone 2d: integration script comes from the adapter
            // (AdapterLoader resolves YAML's script_resource to the
            // embedded ShellIntegration/integration.ps1 content at
            // startup). Milestone 2j: the pre-phase-B LoadEmbeddedScript
            // fallback was removed after every in-tree shell got a YAML
            // adapter; unknown pwsh-family shells without an adapter now
            // fall through to the generic launch path below.
            if (_adapter?.IntegrationScript is { } script)
            {
                // Milestone 2e-3: assemble the tempfile body, init invocation,
                // and outer command line by expanding adapter templates. The
                // fallbacks below match the pre-adapter hardcoded strings so
                // unknown pwsh-family shells still boot.
                //
                // Prepend Write-Host banner/reason lines so they're emitted by
                // pwsh itself AFTER ConPTY's initial `\e[?9001h...\e[2J\e[H`
                // screen-clear payload. If we wrote them to the worker's
                // stdout before the PTY started (the old WriteBanner path),
                // ConPTY wipes them almost immediately, which the user saw
                // as banner text flashing on screen for ~0.5s.
                var bannerTpl = _adapter?.Init.BannerInjection?.BannerTemplate
                    ?? "Write-Host '{banner}' -ForegroundColor Green\n";
                var reasonTpl = _adapter?.Init.BannerInjection?.ReasonTemplate
                    ?? "Write-Host 'Reason: {reason}' -ForegroundColor DarkYellow\n";

                var prefix = new StringBuilder();
                if (!string.IsNullOrEmpty(_banner))
                    prefix.Append(ExpandTemplate(bannerTpl, ("banner", _banner.Replace("'", "''"))));
                if (!string.IsNullOrEmpty(_banner) && !string.IsNullOrEmpty(_reason))
                    prefix.AppendLine("Write-Host");
                if (!string.IsNullOrEmpty(_reason))
                    prefix.Append(ExpandTemplate(reasonTpl, ("reason", _reason.Replace("'", "''"))));
                if (prefix.Length > 0) prefix.AppendLine("Write-Host");

                var tmpPrefix = _adapter?.Init.Tempfile?.Prefix ?? ".splash-integration-";
                var tmpExt = _adapter?.Init.Tempfile?.Extension ?? ".ps1";
                var tmpFile = Path.Combine(
                    Path.GetTempPath(),
                    $"{tmpPrefix}{Environment.ProcessId}{tmpExt}");
                File.WriteAllText(tmpFile, prefix.ToString() + script);

                var initInvocationTpl = _adapter?.Init.InitInvocationTemplate
                    ?? "Import-Module PSReadLine -ErrorAction SilentlyContinue; . '{tempfile_path}'; Remove-Item '{tempfile_path}' -ErrorAction SilentlyContinue";
                var initInvocation = ExpandTemplate(initInvocationTpl,
                    ("tempfile_path", tmpFile));

                var commandTpl = _adapter?.Process.CommandTemplate
                    ?? "\"{shell_path}\" -NoExit -Command \"{init_invocation}\"";
                return ExpandTemplate(commandTpl,
                    ("shell_path", _shell),
                    ("init_invocation", initInvocation));
            }
        }

        // Phase C: REPL-style adapters whose integration lives in a
        // tempfile the interpreter sources at startup (Python's `-i
        // script.py`, and in principle any future REPL with a similar
        // launch convention). Same shape as the pwsh branch above but
        // without banner_injection — the worker's pre-PTY WriteBanner
        // handles banner/reason rendering for non-pwsh adapters, so
        // the tempfile body stays pure integration code.
        //
        // The script body may itself reference {tempfile_path} (e.g. to
        // hardcode the absolute path for self-deletion), so substitute
        // it once before writing the file. Guarded on launch_command +
        // script_resource + non-pwsh + non-cmd so pwsh and cmd keep
        // their existing branches.
        if (!_isPwshFamily && shellName != "cmd" &&
            _adapter is { Init: { Delivery: "launch_command", ScriptResource: not null } } replAdapter &&
            replAdapter.IntegrationScript is { } replScript &&
            !string.IsNullOrEmpty(replAdapter.Process.CommandTemplate))
        {
            var tmpPrefix = replAdapter.Init.Tempfile?.Prefix ?? ".splash-integration-";
            var tmpExt = replAdapter.Init.Tempfile?.Extension ?? "";
            var tmpFile = Path.Combine(
                Path.GetTempPath(),
                $"{tmpPrefix}{Environment.ProcessId}{tmpExt}");

            File.WriteAllText(tmpFile, replScript.Replace("{tempfile_path}", tmpFile));

            var initInvocationTpl = replAdapter.Init.InitInvocationTemplate
                ?? "\"{tempfile_path}\"";
            var initInvocation = ExpandTemplate(initInvocationTpl,
                ("tempfile_path", tmpFile));

            return ExpandTemplate(replAdapter.Process.CommandTemplate,
                ("shell_path", _shell),
                ("init_invocation", initInvocation));
        }

        // cmd.exe: set PROMPT with OSC 633 markers via /k at startup.
        // The D;0 between P and A is a fake CommandFinished marker so the AI
        // command tracker resolves. cmd has no way to expand %ERRORLEVEL% at
        // PROMPT-display time, so the reported exit code is always 0 — a
        // documented limitation. Without this marker, AI commands hang
        // forever because Resolve() requires _commandEnd >= 0.
        //
        // Milestone 2e-2: command_template + prompt_template come from the
        // adapter. The prompt_template payload is substituted into the outer
        // command_template first (so any literal braces in it don't collide
        // with subsequent {shell_path} expansion).
        if (shellName is "cmd")
        {
            if (_adapter is { } a2e2 &&
                !string.IsNullOrEmpty(a2e2.Process.CommandTemplate) &&
                !string.IsNullOrEmpty(a2e2.Process.PromptTemplate))
            {
                return ExpandTemplate(
                    ExpandTemplate(a2e2.Process.CommandTemplate,
                        ("prompt_template", a2e2.Process.PromptTemplate)),
                    ("shell_path", _shell));
            }

            var prompt = "$E]633;P;Cwd=$P$E\\$E]633;D;0$E\\$E]633;A$E\\$P$G$S";
            return $"\"{_shell}\" /q /k \"prompt {prompt}\"";
        }

        // Milestone 2e-1: bash / zsh (and any future simple shell) can
        // be driven entirely from adapter.process.command_template with
        // just {shell_path} substitution. The integration script is
        // injected later via PTY input by InjectShellIntegration().
        if (_adapter is { } a2e1 &&
            !string.IsNullOrEmpty(a2e1.Process.CommandTemplate) &&
            shellName is "bash" or "sh" or "zsh")
        {
            return ExpandTemplate(a2e1.Process.CommandTemplate,
                ("shell_path", _shell));
        }

        // Generic REPL adapter: command_template without an integration
        // script. Used by adapters whose REPL has no script-load
        // mechanism (or doesn't need one), e.g. groovy (regex strategy,
        // direct java invocation with a jar classpath) and jshell
        // (regex strategy, bare `jshell` invocation). Unlike the
        // "REPL-style with script" branch above (which requires
        // Init.Delivery == launch_command + script_resource), this
        // path fires whenever an adapter declares a command_template
        // but no integration script. `{init_invocation}` is NOT
        // substituted here — adapters on this path must not reference
        // it. ExpandTemplate additionally applies %ENVVAR% expansion
        // so paths like `%LOCALAPPDATA%\splash-deps\...` resolve.
        if (!_isPwshFamily && shellName != "cmd" &&
            _adapter is { } cmdTplAdapter &&
            !string.IsNullOrEmpty(cmdTplAdapter.Process.CommandTemplate))
        {
            return ExpandTemplate(cmdTplAdapter.Process.CommandTemplate,
                ("shell_path", _shell));
        }

        // Fallback: no YAML adapter for this shell family — keep the
        // hardcoded launch strings so unknown shells still boot.
        if (shellName is "bash" or "sh")
            return $"\"{_shell}\" --login -i";

        if (shellName is "zsh")
            return $"\"{_shell}\" -l -i";

        return $"\"{_shell}\"";
    }

    /// <summary>
    /// Minimal {name} → value substitution for adapter templates.
    /// Deliberately non-recursive: placeholder values are inserted as-is so
    /// they can't reference other placeholders. Callers that need layered
    /// expansion (pwsh's init_invocation inside command_template) call this
    /// multiple times from innermost to outermost.
    /// </summary>
    private static string ExpandTemplate(string template, params (string Name, string Value)[] vars)
    {
        var result = template;
        foreach (var (name, value) in vars)
            result = result.Replace("{" + name + "}", value);
        // After the named-placeholder substitution, also expand Windows
        // `%ENVVAR%` references so adapter authors can reference user-
        // specific paths like `%LOCALAPPDATA%\splash-deps\...` without
        // splash having to mint a new named placeholder for every
        // possible env var. No-op on non-Windows (the method just
        // returns the input unchanged there).
        if (OperatingSystem.IsWindows())
            result = Environment.ExpandEnvironmentVariables(result);
        return result;
    }

    private async Task WaitForOutputSettled(CancellationToken ct)
    {
        // Timings come from adapter.ready.output_settled_* (schema v1), with
        // the pre-schema hardcoded 2s/1s/30s as the ReadySpec defaults.
        var ready = _adapter?.Ready;
        int minMs    = ready?.OutputSettledMinMs    ?? 2000;
        int stableMs = ready?.OutputSettledStableMs ?? 1000;
        int maxMs    = ready?.OutputSettledMaxMs    ?? 30000;

        int pollMs = Math.Max(50, stableMs / 2);
        int requiredConsecutive = Math.Max(1, (int)Math.Ceiling(stableMs / (double)pollMs));

        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(maxMs);
        var lastLength = 0;
        var settledCount = 0;
        await Task.Delay(minMs, ct);

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(pollMs, ct);

            var currentLength = _outputLength;
            if (currentLength == lastLength && currentLength > 0)
            {
                settledCount++;
                if (settledCount >= requiredConsecutive) break;
            }
            else
            {
                settledCount = 0;
            }
            lastLength = currentLength;
        }
    }

    private async Task InjectShellIntegration(CancellationToken ct)
    {
        // This path is only reached for POSIX-style shells (bash / sh / zsh
        // and whatever else falls through the RunAsync shell dispatch into
        // the "inject via PTY" branch). pwsh/powershell integration is
        // injected via BuildCommandLine's -Command argument, and cmd sets
        // its PROMPT at /k startup. So the old dead pwsh branch here used
        // to never fire — it's gone now.
        //
        // Milestone 2d: adapter.IntegrationScript is resolved from YAML's
        // script_resource at startup. Milestone 2j: the embedded-resource
        // fallback was removed once every in-tree POSIX shell had a YAML
        // adapter; shells without an adapter fall through to no-OSC mode.
        var shellName = _shellFamily;
        string? script = _adapter?.IntegrationScript;

        if (script == null)
        {
            Log($"WARNING: No shell integration script found, falling back to no-OSC mode");
            return;
        }

        // init.delivery: rc_file — the script was staged before
        // CreateProcess (see the env setup in RunAsync) and the shell
        // sources it on its own startup, so pty_inject is a no-op.
        // InjectShellIntegration is still called from the ready path
        // because the settle/suppress/kick orchestration around it
        // applies identically; the delivery-mode check short-circuits
        // only the PTY write.
        if (_adapter?.Init.Delivery == "rc_file")
        {
            Log("rc_file delivery: integration already staged before spawn; skipping pty_inject");
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            // Windows bash (WSL, MSYS2, Git Bash) — write the script to a
            // Windows temp path directly since the worker and child share
            // the filesystem, then teach the shell how to find it in its
            // own namespace (/mnt/c/... for WSL, /c/... for MSYS2).
            var windowsPath = Path.Combine(Path.GetTempPath(), $".splash-integration-{Environment.ProcessId}.sh");
            var scriptContent = script.Replace("\r\n", "\n");
            await File.WriteAllTextAsync(windowsPath, scriptContent, ct);

            var unixPath = IsWslBash(_shell)
                ? "/mnt/" + char.ToLower(windowsPath[0]) + windowsPath[2..].Replace('\\', '/')
                : "/" + char.ToLower(windowsPath[0]) + windowsPath[2..].Replace('\\', '/');

            Log($"Integration script: {windowsPath} → {unixPath} (exists={File.Exists(windowsPath)}, wsl={IsWslBash(_shell)})");

            await WriteToPty($"source '{unixPath}'; rm -f '{unixPath}'\n", ct);
        }
        else
        {
            // Linux/macOS: heredoc the script into the child's own /tmp so
            // we don't need the worker to see the child's filesystem.
            var tmpFile = $"/tmp/.splash-integration-{Environment.ProcessId}.sh";
            var injection = new StringBuilder();
            injection.AppendLine($"cat > {tmpFile} << 'SPLASH_EOF'");
            injection.AppendLine(script.TrimEnd());
            injection.AppendLine("SPLASH_EOF");
            injection.AppendLine($"source {tmpFile}; rm -f {tmpFile}");
            await WriteToPty(injection.ToString(), ct);
        }
    }

    // --- cmd user-busy detector (CPU + child polling) ---

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    private const uint TH32CS_SNAPPROCESS = 0x00000002;
    private const uint INVALID_HANDLE_VALUE_UINT = 0xFFFFFFFF;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32FirstW(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32NextW(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    /// <summary>
    /// Walk the live process snapshot and return true if any process has
    /// the given PID as its direct parent. Used by the cmd polling loop to
    /// detect external commands the user has launched (notepad, git, etc).
    /// </summary>
    private static bool HasChildProcess(int parentPid)
    {
        var snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == IntPtr.Zero || (ulong)snap.ToInt64() == INVALID_HANDLE_VALUE_UINT)
            return false;
        try
        {
            var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            if (!Process32FirstW(snap, ref entry)) return false;
            do
            {
                if (entry.th32ParentProcessID == (uint)parentPid)
                    return true;
            } while (Process32NextW(snap, ref entry));
            return false;
        }
        finally
        {
            CloseHandle(snap);
        }
    }

    /// <summary>
    /// Sample the cmd process's CPU time and child-process count every
    /// 500 ms and forward the OR'd result to the tracker as a user-busy
    /// hint. The threshold of 50 ms over 500 ms is well above Windows's
    /// 15.625 ms timer-tick noise floor (idle cmd shows 0–1 ticks per
    /// window) and well below any real workload (`dir /s C:\Windows`
    /// measured at 200–340 ms per window).
    /// </summary>
    private async Task UserBusyDetectorLoop(CancellationToken ct)
    {
        // Milestone 2h: tuning params come from
        // adapter.capabilities.user_busy_detection_params when available,
        // else fall back to the values cmd.yaml documents (500ms / 50ms /
        // children=true).
        var tuning = _adapter?.Capabilities.UserBusyDetectionParams;
        int pollIntervalMs = tuning?.PollIntervalMs > 0 ? tuning.PollIntervalMs : 500;
        var cpuBusyThreshold = TimeSpan.FromMilliseconds(
            tuning?.CpuBusyThresholdMs > 0 ? tuning.CpuBusyThresholdMs : 50);
        bool includeChildren = tuning?.IncludeChildren ?? true;

        Process? proc;
        try { proc = Process.GetProcessById(_pty!.ProcessId); }
        catch { return; }

        long lastCpuTicks = 0;
        bool firstSample = true;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(pollIntervalMs, ct); }
                catch (OperationCanceledException) { break; }

                try
                {
                    proc.Refresh();
                    if (proc.HasExited) break;

                    long currentTicks = proc.TotalProcessorTime.Ticks;
                    bool cpuBusy = false;
                    if (!firstSample)
                    {
                        var delta = currentTicks - lastCpuTicks;
                        cpuBusy = delta > cpuBusyThreshold.Ticks;
                    }
                    lastCpuTicks = currentTicks;
                    firstSample = false;

                    bool hasChild = includeChildren && HasChildProcess(_pty.ProcessId);
                    _tracker.SetUserBusyHint(cpuBusy || hasChild);
                }
                catch (Exception ex)
                {
                    Log($"UserBusyDetector tick failed: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
        finally
        {
            try { _tracker.SetUserBusyHint(false); } catch { }
            proc.Dispose();
        }
    }

    /// <summary>
    /// Strip the leading bytes that ConPTY echoed back when an AI command
    /// was written to cmd.exe's PTY input. cmd has no preexec hook to fire
    /// OSC 633 C at the right moment, so the proxy tracker captures the
    /// command echo as part of the output. We know exactly which bytes were
    /// written, so the cleanup is deterministic: walk the output forwards,
    /// matching characters from the expected echo, skipping CR/LF inserted
    /// by ConPTY's terminal-width line wrap. After the echo is consumed,
    /// drop any trailing newline that separates it from the real output.
    ///
    /// On any mismatch (escape characters that don't roundtrip exactly,
    /// unexpected wrap behaviour) the original output is returned unchanged
    /// so the AI gets at-worst the pre-fix ugliness, never lost data.
    /// </summary>
    internal static string StripCmdInputEcho(string output, string sentInput)
    {
        if (string.IsNullOrEmpty(output) || string.IsNullOrEmpty(sentInput))
            return output;

        int oi = 0;
        int ci = 0;
        while (ci < sentInput.Length && oi < output.Length)
        {
            var oc = output[oi];
            // ConPTY wraps long input echo at terminal width by injecting
            // CR/LF into the output stream — those bytes were never in the
            // typed command, so skip them while continuing to match.
            if (oc is '\r' or '\n')
            {
                oi++;
                continue;
            }

            if (oc != sentInput[ci])
                return output;

            oi++;
            ci++;
        }

        if (ci < sentInput.Length)
            return output;

        while (oi < output.Length && output[oi] is '\r' or '\n')
            oi++;

        return output[oi..];
    }

    private async Task WriteToPty(string text, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await _writer!.WriteAsync(bytes, ct);
        await _writer.FlushAsync(ct);
    }

    private readonly ManualResetEventSlim _readyEvent = new(false);

    private async Task WaitForReady(CancellationToken ct)
    {
        // Wait for the first PromptStart signal from ReadOutputLoop, or
        // the shell process dying. No timeout — see the call site in
        // RunAsync for the reasoning.
        var readyTask = Task.Run(() => _readyEvent.Wait(ct), ct);
        var winner = await Task.WhenAny(readyTask, _shellExitedTcs.Task).ConfigureAwait(false);
        if (winner == _shellExitedTcs.Task)
        {
            Log("Shell process exited before first prompt; aborting startup");
            throw new InvalidOperationException("Shell process exited during startup");
        }
        // Surface any exception from readyTask (e.g. OperationCanceledException
        // if the worker ct cancelled while we were waiting).
        await readyTask.ConfigureAwait(false);
    }

    // --- User input forwarding ---

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ReadConsoleW(IntPtr hConsoleInput, char[] lpBuffer, uint nNumberOfCharsToRead, out uint lpNumberOfCharsRead, IntPtr pInputControl);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleScreenBufferInfo(IntPtr hConsoleOutput, out CONSOLE_SCREEN_BUFFER_INFO lpConsoleScreenBufferInfo);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ReadConsoleOutputCharacterW(IntPtr hConsoleOutput, char[] lpCharacter, uint nLength, SCREEN_COORD dwReadCoord, out uint lpNumberOfCharsRead);

    [StructLayout(LayoutKind.Sequential)]
    private struct SCREEN_COORD { public short X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CONSOLE_SCREEN_BUFFER_INFO
    {
        public SCREEN_COORD dwSize;
        public SCREEN_COORD dwCursorPosition;
        public short wAttributes;
        public SMALL_RECT srWindow;
        public SCREEN_COORD dwMaximumWindowSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SMALL_RECT
    {
        public short Left, Top, Right, Bottom;
    }

    private const int STD_INPUT_HANDLE = -10;
    private const int STD_OUTPUT_HANDLE = -11;
    // VT input: arrow keys become \x1b[A etc., no line buffering, no echo
    private const uint ENABLE_VIRTUAL_TERMINAL_INPUT = 0x0200;
    private const uint ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004;
    private const uint DISABLE_NEWLINE_AUTO_RETURN = 0x0008;

    /// <summary>
    /// Read the visible portion of the worker's console screen buffer
    /// via Win32 API. Returns the screen content as a multi-line string
    /// with trailing whitespace trimmed per row and trailing empty rows
    /// dropped. Returns null if the API call fails or on non-Windows
    /// platforms (caller should fall back to VT-lite ring snapshot).
    ///
    /// This is the primary peek mechanism on Windows: it reads exactly
    /// what the user sees in the terminal window, with no VT parsing
    /// needed. PSReadLine prediction artifacts, cursor-dance noise,
    /// and ConPTY redraw patterns are all invisible because the
    /// console host has already rendered them into the final cell grid.
    /// </summary>
    private static string? ReadConsoleScreenText()
    {
        if (!OperatingSystem.IsWindows()) return null;

        try
        {
            var hOut = GetStdHandle(STD_OUTPUT_HANDLE);
            if (hOut == IntPtr.Zero || hOut == (IntPtr)(-1)) return null;
            if (!GetConsoleScreenBufferInfo(hOut, out var info)) return null;

            // Read the visible window portion of the buffer.
            int width = info.srWindow.Right - info.srWindow.Left + 1;
            int height = info.srWindow.Bottom - info.srWindow.Top + 1;
            if (width <= 0 || height <= 0) return null;

            var sb = new StringBuilder();
            var rowBuf = new char[width];

            // Track last non-blank row to trim trailing empties.
            int lastNonBlankRow = -1;
            var rows = new List<string>(height);

            for (int row = 0; row < height; row++)
            {
                var coord = new SCREEN_COORD
                {
                    X = (short)(info.srWindow.Left),
                    Y = (short)(info.srWindow.Top + row)
                };
                if (!ReadConsoleOutputCharacterW(hOut, rowBuf, (uint)width, coord, out var charsRead))
                    return null;

                // Trim trailing spaces for this row.
                int end = (int)charsRead - 1;
                while (end >= 0 && rowBuf[end] == ' ') end--;

                var line = end >= 0 ? new string(rowBuf, 0, end + 1) : "";
                rows.Add(line);
                if (line.Length > 0) lastNonBlankRow = row;
            }

            if (lastNonBlankRow < 0) return "";

            for (int r = 0; r <= lastNonBlankRow; r++)
            {
                if (r > 0) sb.Append('\n');
                sb.Append(rows[r]);
            }
            return sb.ToString();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Enable VT escape sequence processing on the worker's stdout console
    /// so cursor movement, clear-to-EOL, and color codes from the shell
    /// are interpreted instead of written as literal characters.
    /// </summary>
    private static void EnableVirtualTerminalOutput()
    {
        if (!OperatingSystem.IsWindows()) return;
        var hStdOut = GetStdHandle(STD_OUTPUT_HANDLE);
        if (hStdOut == IntPtr.Zero || hStdOut == (IntPtr)(-1)) return;
        if (!GetConsoleMode(hStdOut, out var mode)) return;
        SetConsoleMode(hStdOut, mode | ENABLE_VIRTUAL_TERMINAL_PROCESSING);
    }

    /// <summary>
    /// Detect whether the bash executable is WSL bash (vs Git Bash / MSYS2).
    /// WSL bash: C:\Windows\System32\bash.exe or WindowsApps\bash.exe
    /// MSYS2/Git Bash: C:\Program Files\Git\usr\bin\bash.exe etc.
    /// </summary>
    private static bool IsWslBash(string shellPath)
    {
        // If user specified a full path, check it directly
        if (Path.IsPathRooted(shellPath))
        {
            return shellPath.Contains(@"\System32\", StringComparison.OrdinalIgnoreCase) ||
                   shellPath.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase);
        }

        // Resolve "bash" via PATH — check which one we'd get
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "where",
                Arguments = shellPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            var firstLine = proc?.StandardOutput.ReadLine();
            proc?.WaitForExit();
            if (firstLine != null)
            {
                return firstLine.Contains(@"\System32\", StringComparison.OrdinalIgnoreCase) ||
                       firstLine.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch (Exception ex) { Log($"IsWslBash detection failed: {ex.Message}"); }
        return false;
    }

    /// <summary>
    /// Poll the visible console window size and notify ConPTY when it changes.
    /// This keeps the shell's COLUMNS/LINES in sync with the actual window.
    /// </summary>
    private async Task ResizeMonitorLoop(CancellationToken ct)
    {
        int lastCols = 0, lastRows = 0;
        try { lastCols = Console.WindowWidth; lastRows = Console.WindowHeight; } catch { }

        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(500, ct);
            try
            {
                int cols = Console.WindowWidth;
                int rows = Console.WindowHeight;
                if (cols > 0 && rows > 0 && (cols != lastCols || rows != lastRows))
                {
                    lastCols = cols;
                    lastRows = rows;
                    _pty?.Resize(cols, rows);
                    _tracker.SetTerminalSize(cols, rows);
                    Log($"Resized PTY to {cols}x{rows}");
                }
            }
            catch { }
        }
    }

    /// <summary>
    /// Forward user keyboard input from the worker's visible console to the PTY input pipe.
    /// When AI is executing a command (CommandTracker.Busy), input is held until the command completes.
    /// Dispatches to the Windows or Unix implementation — stdin acquisition and raw-mode handling
    /// differ enough between ConPTY + Win32 console and forkpty + termios that a single code path
    /// would be unreadable.
    /// </summary>
    private Task InputForwardLoop(CancellationToken ct)
    {
        if (OperatingSystem.IsWindows()) return InputForwardLoopWindows(ct);
        return InputForwardLoopUnix(ct);
    }

    private Task InputForwardLoopWindows(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) return Task.CompletedTask;

        var tcs = new TaskCompletionSource();
        var thread = new Thread(() =>
        {
            try
            {
                var hStdIn = GetStdHandle(STD_INPUT_HANDLE);
                if (hStdIn == IntPtr.Zero || hStdIn == (IntPtr)(-1)) return;

                // Switch stdin to VT input mode:
                //   - ENABLE_VIRTUAL_TERMINAL_INPUT: special keys → VT sequences
                //   - No ENABLE_LINE_INPUT: character-at-a-time (not line-buffered)
                //   - No ENABLE_ECHO_INPUT: ConPTY handles echo via its output pipe
                //   - No ENABLE_PROCESSED_INPUT: Ctrl+C → \x03 (not signal)
                SetConsoleMode(hStdIn, ENABLE_VIRTUAL_TERMINAL_INPUT);

                var shellName = _shellFamily;
                // pwsh and cmd.exe understand win32-input-mode natively; only Unix shells need translation
                bool needsTranslation = !_isPwshFamily && shellName is not "cmd";

                var charBuf = new char[256];
                var pending = needsTranslation ? new StringBuilder() : null;
                while (!ct.IsCancellationRequested)
                {
                    // ReadConsoleW reads Unicode (UTF-16) — handles CJK characters correctly
                    if (!ReadConsoleW(hStdIn, charBuf, (uint)charBuf.Length, out var charsRead, IntPtr.Zero) || charsRead == 0)
                        break;

                    try
                    {
                        byte[] utf8;
                        if (needsTranslation)
                        {
                            // Decode win32-input-mode rich key events into plain text/VT sequences.
                            // Bash/zsh readline does not understand `ESC [ Vk;Sc;Uc;Kd;Cs;Rc _` sequences.
                            pending!.Append(charBuf, 0, (int)charsRead);
                            var translated = TranslateWin32InputMode(pending);
                            if (translated.Length == 0) continue;
                            utf8 = Encoding.UTF8.GetBytes(translated);
                        }
                        else
                        {
                            // pwsh/powershell: PSReadLine understands win32-input-mode natively.
                            // Forward the raw sequences without translation.
                            utf8 = Encoding.UTF8.GetBytes(charBuf, 0, (int)charsRead);
                        }

                        if (_holdUserInput)
                        {
                            // AI command in flight — hold user keystrokes for
                            // replay after the command completes. Ctrl+C (0x03)
                            // passes through immediately so the user can still
                            // interrupt a stuck command.
                            bool isCtrlC = utf8.Length == 1 && utf8[0] == 0x03;
                            if (isCtrlC)
                            {
                                _pty!.InputStream.Write(utf8, 0, utf8.Length);
                                _pty.InputStream.Flush();
                            }
                            else
                            {
                                _heldUserInput.Enqueue((byte[])utf8.Clone());
                            }
                        }
                        else
                        {
                            _pty!.InputStream.Write(utf8, 0, utf8.Length);
                            _pty.InputStream.Flush();
                        }
                    }
                    catch (IOException) { break; }
                }
            }
            catch (Exception ex) { Log($"InputForwardLoop error: {ex.Message}"); }
            finally { tcs.TrySetResult(); }
        });
        thread.IsBackground = true;
        thread.Name = "Console-Input";
        thread.Start();
        return tcs.Task;
    }

    /// <summary>
    /// Unix equivalent of InputForwardLoopWindows: read keystrokes the human typed
    /// into the worker's visible terminal (our stdin fd 0), and forward them to the
    /// forkpty master so the hosted shell receives them.
    ///
    /// Raw mode is critical — without it the kernel tty driver cooks input
    /// (line-buffered until Enter, local echo, ^C → SIGINT), which would prevent
    /// bash's readline from seeing arrow keys / tab / Ctrl-shortcuts and would
    /// double-echo every keystroke (once locally, once from readline). We
    /// snapshot the original termios, cfmakeraw() a copy, install it, and
    /// restore the snapshot on worker exit.
    ///
    /// If stdin isn't a tty (proxy mode with a piped stdin, --test harness,
    /// smoke tests), we skip raw mode and the loop falls through immediately;
    /// the loop is harmless to start even when no human will ever type.
    /// </summary>
    [UnsupportedOSPlatform("windows")]
    private Task InputForwardLoopUnix(CancellationToken ct)
    {
        var tcs = new TaskCompletionSource();
        var thread = new Thread(() =>
        {
            IntPtr saved = IntPtr.Zero;
            IntPtr modified = IntPtr.Zero;
            bool rawInstalled = false;
            try
            {
                if (UnixPty.IsATty(0) != 1)
                {
                    // Proxy mode / tests: stdin is a pipe, no user typing to forward.
                    return;
                }

                saved = Marshal.AllocHGlobal(UnixPty.TermiosBufSize);
                modified = Marshal.AllocHGlobal(UnixPty.TermiosBufSize);
                if (UnixPty.TcGetAttr(0, saved) != 0)
                {
                    Log($"InputForwardLoopUnix: tcgetattr failed errno={Marshal.GetLastPInvokeError()}");
                    return;
                }
                unsafe
                {
                    System.Buffer.MemoryCopy((void*)saved, (void*)modified, UnixPty.TermiosBufSize, UnixPty.TermiosBufSize);
                }
                UnixPty.CfMakeRaw(modified);
                if (UnixPty.TcSetAttr(0, UnixPty.TCSANOW, modified) != 0)
                {
                    Log($"InputForwardLoopUnix: tcsetattr raw failed errno={Marshal.GetLastPInvokeError()}");
                    return;
                }
                rawInstalled = true;

                // Belt-and-braces termios restore: the finally block handles the
                // normal path, this catches an out-of-band process exit (signal,
                // crash) that skips managed cleanup.
                var savedSnapshot = saved;
                AppDomain.CurrentDomain.ProcessExit += (_, _) =>
                {
                    try { UnixPty.TcSetAttr(0, UnixPty.TCSANOW, savedSnapshot); } catch { }
                };

                var buf = new byte[256];
                while (!ct.IsCancellationRequested)
                {
                    int n = UnixPty.ReadFd(0, buf, buf.Length);
                    if (n <= 0) break;

                    try
                    {
                        if (_holdUserInput)
                        {
                            // Mirror the Windows hold-gate behaviour: Ctrl+C
                            // (0x03) always passes through so the human can
                            // interrupt a stuck AI command; other keystrokes
                            // are queued for replay once the AI command
                            // finishes and the gate is released.
                            bool isCtrlC = n == 1 && buf[0] == 0x03;
                            if (isCtrlC)
                            {
                                _pty!.InputStream.Write(buf, 0, n);
                                _pty.InputStream.Flush();
                            }
                            else
                            {
                                var slice = new byte[n];
                                Array.Copy(buf, slice, n);
                                _heldUserInput.Enqueue(slice);
                            }
                        }
                        else
                        {
                            _pty!.InputStream.Write(buf, 0, n);
                            _pty.InputStream.Flush();
                        }
                    }
                    catch (IOException) { break; }
                }
            }
            catch (Exception ex) { Log($"InputForwardLoopUnix error: {ex.GetType().Name}: {ex.Message}"); }
            finally
            {
                if (rawInstalled && saved != IntPtr.Zero)
                {
                    try { UnixPty.TcSetAttr(0, UnixPty.TCSANOW, saved); } catch { }
                }
                if (saved != IntPtr.Zero) Marshal.FreeHGlobal(saved);
                if (modified != IntPtr.Zero) Marshal.FreeHGlobal(modified);
                tcs.TrySetResult();
            }
        });
        thread.IsBackground = true;
        thread.Name = "Unix-Input";
        thread.Start();
        return tcs.Task;
    }

    /// <summary>
    /// Translate win32-input-mode escape sequences (`ESC [ Vk;Sc;Uc;Kd;Cs;Rc _`) into
    /// plain text or standard VT escape sequences that bash/zsh readline can understand.
    ///
    /// The pending buffer is consumed up to the last complete sequence; any partial
    /// trailing sequence is preserved for the next read.
    /// </summary>
    private static string TranslateWin32InputMode(StringBuilder pending)
    {
        var output = new StringBuilder();
        int i = 0;
        var input = pending.ToString();

        while (i < input.Length)
        {
            char c = input[i];

            // Detect ESC [ ... _  (win32-input-mode sequence)
            if (c == '\x1b' && i + 1 < input.Length && input[i + 1] == '[')
            {
                // Find terminator '_'
                int end = input.IndexOf('_', i + 2);
                if (end < 0)
                {
                    // Incomplete sequence — keep in pending
                    break;
                }

                // Parse fields between ESC [ and _
                var payload = input.Substring(i + 2, end - i - 2);
                var fields = payload.Split(';');
                if (fields.Length == 6 &&
                    int.TryParse(fields[0], out var vk) &&
                    int.TryParse(fields[2], out var uc) &&
                    int.TryParse(fields[3], out var kd) &&
                    int.TryParse(fields[4], out var cs) &&
                    int.TryParse(fields[5], out var rc))
                {
                    // Skip key-up events
                    if (kd == 1)
                    {
                        var seq = MapKeyToVt(vk, uc, cs);
                        for (int r = 0; r < Math.Max(1, rc); r++)
                            output.Append(seq);
                    }
                    i = end + 1;
                    continue;
                }

                // Not a recognized win32-input-mode sequence — pass through as-is
                output.Append(input, i, end - i + 1);
                i = end + 1;
                continue;
            }

            // Plain character — pass through
            output.Append(c);
            i++;
        }

        // Consume processed portion from pending
        pending.Remove(0, i);
        return output.ToString();
    }

    /// <summary>
    /// Map a Windows virtual key + Unicode char + control state to the bytes/sequence
    /// that bash/zsh readline expects.
    /// </summary>
    private static string MapKeyToVt(int vk, int uc, int controlState)
    {
        const int LEFT_CTRL = 0x0008;
        const int RIGHT_CTRL = 0x0004;
        const int LEFT_ALT = 0x0002;
        const int RIGHT_ALT = 0x0001;
        bool ctrl = (controlState & (LEFT_CTRL | RIGHT_CTRL)) != 0;
        bool alt = (controlState & (LEFT_ALT | RIGHT_ALT)) != 0;

        // Special keys (Uc == 0)
        if (uc == 0)
        {
            return vk switch
            {
                0x25 => "\x1b[D", // Left
                0x26 => "\x1b[A", // Up
                0x27 => "\x1b[C", // Right
                0x28 => "\x1b[B", // Down
                0x24 => "\x1b[H", // Home
                0x23 => "\x1b[F", // End
                0x21 => "\x1b[5~", // PgUp
                0x22 => "\x1b[6~", // PgDn
                0x2D => "\x1b[2~", // Insert
                0x2E => "\x1b[3~", // Delete
                0x70 => "\x1bOP",  // F1
                0x71 => "\x1bOQ",  // F2
                0x72 => "\x1bOR",  // F3
                0x73 => "\x1bOS",  // F4
                0x74 => "\x1b[15~", // F5
                0x75 => "\x1b[17~", // F6
                0x76 => "\x1b[18~", // F7
                0x77 => "\x1b[19~", // F8
                0x78 => "\x1b[20~", // F9
                0x79 => "\x1b[21~", // F10
                0x7A => "\x1b[23~", // F11
                0x7B => "\x1b[24~", // F12
                _ => "",
            };
        }

        // Backspace (BS or DEL): bash readline expects \x7f (DEL) for the Backspace key
        if (vk == 0x08)
            return "\x7f";

        // Tab
        if (vk == 0x09)
            return "\t";

        // Enter
        if (vk == 0x0D)
            return "\r";

        // Escape
        if (vk == 0x1B)
            return "\x1b";

        // Alt + char → ESC + char
        if (alt && uc >= 0x20 && uc < 0x7f)
            return "\x1b" + (char)uc;

        // Plain Unicode character
        return char.ConvertFromUtf32(uc);
    }

    // --- PTY output reading ---

    /// <summary>
    /// Forward a slice of OSC-stripped PTY output to the worker's visible
    /// console so the human user sees what the AI is doing. Rewrites any
    /// OSC 0 (set window title) sequences on the way so the title stays as
    /// splash's "#PID Name" tag instead of being overwritten by whatever
    /// the shell's prompt decided to set.
    /// </summary>
    private void MirrorToVisible(string text)
    {
        if (!_mirrorVisible || text.Length == 0) return;
        try
        {
            var cleanedOutput = ReplaceOscTitle(text, _desiredTitle, ref _oscTitlePending);
            var outBytes = Encoding.UTF8.GetBytes(cleanedOutput);
            _stdoutStream ??= Console.OpenStandardOutput();
            _stdoutStream.Write(outBytes, 0, outBytes.Length);
            _stdoutStream.Flush();
        }
        catch { }
    }

    /// <summary>
    /// Scan recent PTY output for in-band terminal queries from the hosted
    /// shell and inject synthetic responses back into the PTY input. On Unix
    /// the inner forkpty has no real terminal emulator behind it — splash is
    /// the middleman relaying bytes between the outer xfce4-terminal /
    /// Terminal.app and the shell — so line-editor libraries that sync their
    /// internal state by querying the terminal (PSReadLine fires DSR on
    /// startup, various REPLs probe DA1 / DA2) never see a reply and can
    /// hang indefinitely waiting for one. Answering here keeps splash a
    /// pure relay on Windows (the real ConPTY handles these queries itself,
    /// so the loop below no-ops unless the specific sequence actually
    /// appears) while unblocking the Unix path.
    ///
    /// Returns the text with any handled query sequences stripped so the
    /// downstream mirror (to the outer Terminal / xfce4-terminal) and
    /// OSC/CSI parser never see them. Stripping is critical: if a query
    /// leaks into the mirror, the outer terminal ALSO answers it, and
    /// the reply races back through the stdin relay into the shell — the
    /// shell's line editor then treats the duplicate reply as typed
    /// characters and prints garbage like "R24" or "21R" into the command
    /// buffer. Answering on splash's side and swallowing the query is the
    /// single-authoritative path.
    ///
    /// Currently handled:
    ///   • CSI 6 n (DSR — Device Status Report: cursor position). Answered
    ///     with a "near the bottom of the screen" row so PSReadLine's
    ///     upward-relative moves (history recall re-renders the previous
    ///     prompt line by moving cursor up and rewriting) land on real
    ///     screen rows instead of walking off the top. The row we pick
    ///     is max(terminal.Rows - 1, 1). Col is 1, matching the typical
    ///     state right after a fresh prompt. The outer terminal would
    ///     report a more precise value, but forwarding its reply through
    ///     our stdin relay has subtle timing + xrdp-passthrough issues —
    ///     a static "safe lower bound" is a pragmatic compromise until a
    ///     full virtual-terminal-tracking implementation lands.
    ///
    /// Future queries to consider: CSI c / CSI &gt; c (DA1 / DA2 device
    /// attribute probes), CSI 14 t / CSI 18 t (window pixel / cell size),
    /// OSC 10 / 11 ? (foreground / background colour query). Added on
    /// demand when a new adapter needs them.
    /// </summary>
    private string AnswerAndStripTerminalQueries(string text)
    {
        // CSI 6 n  —  "\x1b[6n" is the only DSR variant PSReadLine uses.
        // The longer form \x1b[?6n (private-marker DSR) isn't emitted by
        // any adapter we host, so a plain substring check is enough.
        const string Dsr = "\x1b[6n";
        if (!text.Contains(Dsr)) return text;

        if (_writer is not null)
        {
            try
            {
                // Static row = Console.WindowHeight (near the bottom of the
                // visible viewport): once a shell has printed more than a
                // screenful of output the real cursor is pinned there by
                // scrolling anyway, and PSReadLine's relative-move renders
                // survive a static-bottom answer far better than an
                // optimistic \n-count tracker that under-shoots whenever
                // the shell wraps a long line or emits a cursor-position
                // CSI without an explicit '\n'. The user-observed
                // "content jumps to the screen bottom after an AI
                // command" is the cost of this compromise — small empty
                // space above the prompt instead of wrong-row rendering.
                int row = 24;
                try { if (Console.WindowHeight > 1) row = Console.WindowHeight; } catch { }
                int col = EstimateCursorCol();
                var reply = System.Text.Encoding.UTF8.GetBytes($"\x1b[{row};{col}R");
                _writer.Write(reply, 0, reply.Length);
                _writer.Flush();
            }
            catch (IOException) { /* PTY closed — worker is tearing down */ }
        }

        return text.Replace(Dsr, string.Empty);
    }

    /// <summary>
    /// Best-effort cursor column for DSR replies when the outer terminal's
    /// real reply isn't available through the relay. Used only by
    /// <see cref="AnswerAndStripTerminalQueries"/> on Unix.
    ///
    /// PSReadLine fires DSR right after the shell writes its prompt, so the
    /// honest answer is "prompt end + 1". We compute that from
    /// adapter-specific knowledge of the prompt format rather than parsing
    /// the output stream (which would require a mini terminal emulator
    /// that's out of scope for the Phase 2 relay):
    ///
    ///   • pwsh / powershell — default prompt is "PS &lt;cwd&gt;&gt; ",
    ///     so column = 3 ("PS ") + cwd.Length + 2 ("&gt; ") + 1 (1-indexed
    ///     cursor position after the space). Custom $PROFILE prompts
    ///     break this approximation; ok as the common case.
    ///
    ///   • everyone else — we don't have a portable way to guess the
    ///     prompt length (bash PS1 can be anything), so fall back to
    ///     column 1. New keystrokes still land at the correct on-screen
    ///     column because readline emits a `\r` + re-render cycle on
    ///     input. History recall via up-arrow picks the wrong column
    ///     in this fallback and renders into the prompt area — a known
    ///     cosmetic limitation until proper virtual terminal tracking
    ///     is added.
    /// </summary>
    /// <summary>
    /// Best-effort cursor column for DSR replies when the outer terminal's
    /// real reply isn't available through the relay. Used only by
    /// <see cref="AnswerAndStripTerminalQueries"/> on Unix.
    ///
    /// A byte-level virtual-terminal tracker was tried and regressed worse
    /// than a static guess: PSReadLine renders prompts with SGR colour
    /// escapes whose widths don't match the counted byte advance, and
    /// PSReadLine carries its own cursor model — when our estimate and
    /// its model diverge even slightly, the two compound into bigger
    /// visual drift than either alone. The current compromise:
    ///
    ///   • pwsh / powershell — compute column from the default prompt
    ///     format "PS &lt;cwd&gt;&gt; ". Column = 3 ("PS ") + cwd.Length
    ///     + 2 ("&gt; ") + 1 (1-indexed cursor after the space). Accurate
    ///     for the out-of-the-box prompt, which matches what PSReadLine
    ///     expects since it built its own model from the same string.
    ///     Custom $PROFILE prompts defeat this approximation.
    ///
    ///   • everyone else — fall back to column 1. New keystrokes still
    ///     land at the correct on-screen column because readline emits
    ///     a \r + re-render cycle on input, so the initial column
    ///     estimate drops out after the first render. History recall
    ///     (up-arrow) uses absolute positioning tied to the DSR reply,
    ///     so the column-1 fallback draws the recalled command into
    ///     the prompt area — a known cosmetic gap until a full virtual
    ///     terminal layer lands.
    /// </summary>
    private int EstimateCursorCol()
    {
        if (_isPwshFamily)
        {
            var cwd = _tracker.LastKnownCwd ?? _cwd ?? "/";
            // "PS " + cwd + "> " + cursor-after-space = 3 + len + 2 + 1
            return 3 + cwd.Length + 2 + 1;
        }
        if (_shellFamily == "bash" || _shellFamily == "zsh")
        {
            // Debian / Ubuntu / Fedora / Arch default PS1 is
            //   "\u@\h:\w\$ "  →  "user@host:path$ ". Home directory and
            // prefixes of home get "~"-substituted (the \w expansion).
            // Length = user + "@" + host + ":" + displayPath + "$ "
            //        = user + 1 + host + 1 + displayPath + 2
            // Plus 1 for 1-indexed cursor position after the space.
            var user = Environment.GetEnvironmentVariable("USER") ?? Environment.UserName;
            var host = Environment.MachineName;
            var home = Environment.GetEnvironmentVariable("HOME") ?? "";
            var cwd = _tracker.LastKnownCwd ?? _cwd ?? "/";
            string displayCwd = cwd;
            if (!string.IsNullOrEmpty(home))
            {
                if (cwd == home) displayCwd = "~";
                else if (cwd.StartsWith(home + "/")) displayCwd = "~" + cwd.Substring(home.Length);
            }
            return user.Length + 1 + host.Length + 1 + displayCwd.Length + 2 + 1;
        }
        return 1;
    }

    /// <summary>
    /// Wait for the child shell process to exit, then signal the main loop.
    /// Needed because ConPTY keeps the output pipe open indefinitely even
    /// after the child process dies, so ReadOutputLoop's blocking Read is
    /// not a reliable shell-death signal. We watch the Windows process
    /// handle directly via Process.WaitForExitAsync.
    /// </summary>
    private async Task WaitForShellExitAsync(CancellationToken ct)
    {
        try
        {
            using var proc = System.Diagnostics.Process.GetProcessById(_pty!.ProcessId);
            await proc.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException) { return; }
        catch (ArgumentException) { /* already gone */ }
        catch (Exception ex) { Log($"WaitForShellExit: {ex.GetType().Name}: {ex.Message}"); }

        Log("Shell process exited");
        _tracker.AbortPending();
        _shellExitedTcs.TrySetResult();
    }

    private Task ReadOutputLoop(CancellationToken ct)
    {
        // Dedicated thread with synchronous ReadFile in a tight loop.
        // This pattern matches the ConPTY minimal test where ReadFile worked correctly.
        // Stream.ReadAsync/Task.Run wrappers don't reliably work with ConPTY pipe handles.
        var tcs = new TaskCompletionSource();
        var thread = new Thread(() =>
        {
            var stream = _pty!.OutputStream;
            var buffer = new byte[4096];
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    int read = stream.Read(buffer, 0, buffer.Length);
                    if (read == 0) break;

                    var text = Encoding.UTF8.GetString(buffer, 0, read);
                    text = AnswerAndStripTerminalQueries(text);
                    if (_tracker.Busy) Log($"RAW: {EscapeForLog(text)}");
                    var result = _parser.Parse(text);

                    // For regex-strategy adapters: scan the cleaned chunk
                    // for prompt boundaries and inject synthetic OSC events
                    // (CommandFinished + PromptStart, mirroring what the
                    // python adapter emits for real on each prompt). The
                    // synthetic events are merged with any real OSC events
                    // in TextOffset order so the tracker downstream sees
                    // one coherent stream regardless of which strategy
                    // fired.
                    if (_regexPromptDetector != null)
                    {
                        // RegexPromptDetector is CSI-aware: it strips
                        // CSI escapes from result.Cleaned internally
                        // before running the adapter's prompt regex,
                        // so the offsets come back in original-byte
                        // coordinates that line up with the same
                        // cleaned text we feed to the tracker below.
                        var promptOffsets = _regexPromptDetector.Scan(result.Cleaned);
                        if (promptOffsets.Count > 0)
                        {
                            var merged = new List<OscParser.OscEvent>(result.Events);
                            foreach (var offset in promptOffsets)
                            {
                                if (_regexFirstPromptSeen)
                                {
                                    merged.Add(new OscParser.OscEvent(
                                        OscParser.OscEventType.CommandFinished,
                                        ExitCode: 0, TextOffset: offset));
                                }
                                merged.Add(new OscParser.OscEvent(
                                    OscParser.OscEventType.PromptStart,
                                    TextOffset: offset));
                                _regexFirstPromptSeen = true;
                            }
                            merged.Sort((a, b) => a.TextOffset.CompareTo(b.TextOffset));
                            result = result with { Events = merged };
                        }
                    }

                    // Interleave FeedOutput and HandleEvent in source order
                    // using each event's TextOffset (position in Cleaned where
                    // the event fired in the original byte stream). This is
                    // how the tracker knows "_output was N bytes long when
                    // OSC C arrived", so it can slice out just the region
                    // between OSC C and OSC D when producing the command
                    // result — no first-\r\n stripping or AcceptLine heuristics.
                    int lastOffset = 0;
                    foreach (var evt in result.Events)
                    {
                        if (evt.TextOffset > lastOffset)
                        {
                            var slice = result.Cleaned.Substring(lastOffset, evt.TextOffset - lastOffset);
                            _tracker.FeedOutput(slice);
                            _outputLength += slice.Length;
                            MirrorToVisible(slice);
                        }
                        lastOffset = evt.TextOffset;

                        if (!_ready && evt.Type == OscParser.OscEventType.PromptStart)
                        {
                            _ready = true;
                            _readyEvent.Set();
                        }
                        _tracker.HandleEvent(evt);
                        if (_ready && evt.Type == OscParser.OscEventType.CommandInputStart)
                            _mirrorVisible = true;
                    }

                    // Any text after the last event in this chunk.
                    if (lastOffset < result.Cleaned.Length)
                    {
                        var tail = result.Cleaned.Substring(lastOffset);
                        _tracker.FeedOutput(tail);
                        _outputLength += tail.Length;
                        MirrorToVisible(tail);
                    }
                }
            }
            catch (IOException) { }
            catch (Exception ex) { Log($"ReadOutputLoop error: {ex.GetType().Name}: {ex.Message}"); }
            finally
            {
                // Signal the main loop that the child shell process is gone
                // (Read returned 0 or threw). The main loop wakes up and
                // tears the worker down so the pipe closes promptly and the
                // proxy can surface a "Previous console died" to the AI.
                Log("ReadOutputLoop exited; shell process has gone");
                _tracker.AbortPending();
                _shellExitedTcs.TrySetResult();
                tcs.TrySetResult();
            }
        });
        thread.IsBackground = true;
        thread.Name = "ConPTY-Reader";
        thread.Start();
        return tcs.Task;
    }

    // --- Named Pipe server ---

    private async Task RunPipeServerAsync(string pipeName, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream server;
            try
            {
                // Multiple server instances share the same pipe name so a
                // long-running execute on one instance doesn't starve
                // get_status / get_cached_output arriving on another. The
                // instance count caps at NamedPipeServerStream.MaxAllowedServerInstances
                // (~256 on Windows) but the worker only spawns a fixed few
                // listening loops (see RunAsync), so the cap is never hit.
                server = new NamedPipeServerStream(
                    pipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
            }
            catch (IOException ex)
            {
                Log($"Pipe create error: {ex.Message}");
                try { await Task.Delay(500, ct); } catch (OperationCanceledException) { break; }
                continue;
            }
            using var _server = server;

            try
            {
                await server.WaitForConnectionAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                var request = await ReadMessageAsync(server, ct);
                var response = await HandleRequestAsync(request, ct);
                await WriteMessageAsync(server, response, ct);
            }
            catch (IOException)
            {
                // Pipe closed mid-handshake (proxy disconnected before the
                // worker's response fully drained). Benign; the proxy already
                // has whatever response it was going to get.
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log($"Pipe error: {ex.Message}");
            }
            finally
            {
                if (server.IsConnected)
                    server.Disconnect();
            }
        }
    }

    private async Task<JsonElement> HandleRequestAsync(JsonElement request, CancellationToken ct)
    {
        var type = request.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : null;
        if (type == null)
            return SerializeResponse(w => w.WriteString("error", "Missing 'type' field in request"));

        return type switch
        {
            "execute" => await HandleExecuteAsync(request, ct),
            "get_status" => SerializeResponse(w =>
            {
                w.WriteString("status", Status);
                w.WriteBoolean("hasCachedOutput", _tracker.HasCachedOutput);
                w.WriteString("shellFamily", _shellFamily);
                w.WriteString("shellPath", _shell);
                w.WriteStringOrNull("cwd", _tracker.LastKnownCwd);
                w.WriteStringOrNull("runningCommand", _tracker.RunningCommand);
                var elapsed = _tracker.RunningElapsedSeconds;
                if (elapsed.HasValue) w.WriteNumber("runningElapsedSeconds", elapsed.Value);
                else w.WriteNull("runningElapsedSeconds");
                if (_adapter?.Modes is { Count: > 0 })
                {
                    w.WriteStringOrNull("currentMode", _currentMode);
                    if (_currentModeLevel.HasValue) w.WriteNumber("currentModeLevel", _currentModeLevel.Value);
                }
            }),
            "get_cached_output" => HandleGetCachedOutput(),
            "peek" => SerializeResponse(w =>
            {
                // On Windows, prefer reading the console screen buffer
                // directly — this gives us exactly what the user sees,
                // with no VT-lite parsing artifacts. On other platforms
                // fall back to the ring buffer + VT-lite interpreter.
                var snapshot = ReadConsoleScreenText() ?? _tracker.GetRecentOutputSnapshot();
                w.WriteString("status", Status);
                w.WriteBoolean("busy", _tracker.Busy);
                w.WriteStringOrNull("runningCommand", _tracker.RunningCommand);
                var elapsed = _tracker.RunningElapsedSeconds;
                if (elapsed.HasValue) w.WriteNumber("runningElapsedSeconds", elapsed.Value);
                else w.WriteNull("runningElapsedSeconds");
                w.WriteString("recentOutput", snapshot);
                var wantRaw = request.TryGetProperty("raw", out var rawProp) && rawProp.ValueKind == JsonValueKind.True;
                if (wantRaw)
                {
                    var raw = _tracker.GetRawRecentBytes();
                    var rawBytes = Encoding.UTF8.GetBytes(raw);
                    w.WriteString("rawBase64", Convert.ToBase64String(rawBytes));
                }
            }),
            "send_input" => await HandleSendInputAsync(request, ct),
            "drain_post_output" => await HandleDrainPostOutputAsync(request, ct),
            "set_title" => HandleSetTitle(request),
            "display_banner" => HandleDisplayBanner(request),
            "claim" => HandleClaim(request),
            "ping" => SerializeResponse(w => w.WriteString("status", "ok")),
            _ => SerializeResponse(w => w.WriteString("error", $"Unknown request type: {type}")),
        };
    }

    /// <summary>
    /// Wait for trailing output (bytes arriving after the primary Resolve)
    /// to stabilise, then return the cleaned delta. Called by the proxy
    /// immediately after a successful execute response so the AI receives
    /// any output the shell streamed after OSC PromptStart.
    ///
    /// For pwsh/powershell the drain is skipped entirely and an empty delta
    /// is returned: pwsh emits OSC A as part of its prompt function return
    /// value, so by the time the worker sees OSC A all command output has
    /// already been captured. The only bytes that arrive after OSC A are
    /// the prompt text ("PS C:\foo> ") and PSReadLine prediction rendering
    /// (ghost text based on history), both of which are noise for the AI
    /// and would otherwise leak into the delta.
    /// </summary>
    private async Task<JsonElement> HandleDrainPostOutputAsync(JsonElement request, CancellationToken ct)
    {
        if (_isPwshFamily)
        {
            _tracker.ClearPostPrimary();
            return SerializeResponse(w => w.WriteNull("delta"));
        }

        var stableMs = request.TryGetProperty("stable_ms", out var sm) && sm.ValueKind == JsonValueKind.Number ? sm.GetInt32() : 100;
        var maxMs = request.TryGetProperty("max_ms", out var mm) && mm.ValueKind == JsonValueKind.Number ? mm.GetInt32() : 500;
        var delta = await _tracker.WaitAndDrainPostOutputAsync(stableMs, maxMs, ct);
        return SerializeResponse(w => w.WriteStringOrNull("delta", delta));
    }

    /// <summary>
    /// Write a colorized echo of the AI command to the visible console for
    /// pwsh / powershell.exe, matching PSReadLine's own rendering style.
    /// We bypass the usual PTY mirror path because PSReadLine is idle in the
    /// input loop between commands and writes prediction ghost text /
    /// cursor moves that would clash with the echo. Instead we suppress
    /// the mirror (flipped back on by OSC B), reset the current line via
    /// \r + \e[2K, paint a synthetic prompt, then write the colorized
    /// command. Crucially the echo does NOT end with \r\n — PSReadLine's
    /// own AcceptLine finalize emits the newline once OSC B re-enables
    /// the mirror, so adding our own here would leave a blank line
    /// between the echo and the first line of real command output.
    /// </summary>
    private void RenderPwshCommandEcho(string command)
    {
        _mirrorVisible = false;
        _stdoutStream ??= Console.OpenStandardOutput();

        var echoText = PwshColorizer.Colorize(command);
        var cwd = _tracker.LastKnownCwd ?? _cwd;
        var synthPrompt = $"PS {cwd}> ";

        // Multi-line commands read much better when the body starts on its
        // own line instead of being glued to the prompt. Insert a newline
        // right after the prompt when the command contains an embedded
        // newline. Strip any trailing newline from echoText so the last
        // line of the echo sits on its own line without an extra blank
        // row before PSReadLine's AcceptLine finalizes.
        string payload;
        if (command.Contains('\n'))
        {
            var trimmed = echoText.TrimEnd('\n', '\r');
            payload = $"\r\x1b[2K{synthPrompt}\r\n{trimmed}";
        }
        else
        {
            payload = $"\r\x1b[2K{synthPrompt}{echoText}";
        }

        var cmdDisplay = Encoding.UTF8.GetBytes(payload);
        _stdoutStream.Write(cmdDisplay, 0, cmdDisplay.Length);
        _stdoutStream.Flush();
    }

    /// <summary>
    /// Build the body of a tempfile that runs a multi-line AI command. The
    /// wrapper does three things in order:
    ///   1. Move the cursor up one row and clear the line. PSReadLine
    ///      displays `. 'path/to/tempfile.ps1'` as the "command being run"
    ///      on the previous prompt row when the user presses Enter; we
    ///      overwrite that line with a synthesized prompt + the real AI
    ///      command text so the visible console ends up looking like the
    ///      user just typed the multi-line command at a fresh prompt.
    ///   2. Write the colorized echo (PS prompt + newline + colorized
    ///      multi-line body) via a single [Console]::Write call. The
    ///      payload is base64-encoded so the raw ESC sequences inside the
    ///      colorizer output don't have to be escaped for a PowerShell
    ///      string literal. Crucially, this is emitted by the child shell
    ///      itself — NOT by the worker writing to _stdoutStream — so the
    ///      child's virtual buffer (and therefore PSReadLine's _initialY
    ///      for future input loops) stays in sync with the rows the
    ///      visible console actually shows. Rendering the echo from the
    ///      worker side bypassed ConPTY and left PSReadLine's history
    ///      display N rows above where the user expected it.
    ///   3. Emit an OSC 633;C marker. The PreCommandLookupAction that
    ///      fires OSC C naturally was already triggered at the first
    ///      real cmdlet invocation, but its _commandStart was captured
    ///      BEFORE the echo was written. We re-emit OSC C right after
    ///      the echo so _commandStart gets reset to the end of the echo,
    ///      and the captured output window the AI sees excludes the
    ///      echo lines.
    /// Finally the AI's actual multi-line command body follows.
    /// </summary>
    private string BuildMultiLineTempfileBody(string command, int wrapRowCount)
    {
        var colorizedBody = PwshColorizer.Colorize(command).TrimEnd('\r', '\n');
        var cwd = _tracker.LastKnownCwd ?? _cwd;
        var synthPrompt = $"PS {cwd}> ";

        // Build the full payload as a single blob:
        //   \e[<N>F          — cursor previous line (CPL) — move up N
        //                      rows to col 0 of the start of the
        //                      dot-source input. N is sized by the
        //                      caller based on terminal width so we
        //                      cover the entire wrapped input area,
        //                      not just the last row.
        //   \e[0J            — erase display from the cursor to end
        //                      (wipes the dot-source input + any
        //                      trailing rows, leaves scrollback above
        //                      the prompt untouched).
        //   PS cwd> \r\n     — synthetic prompt + newline so the
        //                      command body reads as a clean block
        //                      below it.
        //   <colorized body> — the AI command, ANSI-colored.
        //   \r\n             — terminator.
        //   \e]633;C\a       — manual OSC C so CommandTracker rewinds
        //                      _commandStart past the echo and the AI
        //                      doesn't see this noise in its captured
        //                      output.
        // Pack it base64 and decode at runtime. Decoded bytes go straight
        // through OpenStandardOutput, bypassing pwsh's host TextWriter
        // layer (which was transforming our cursor-control escapes).
        var payload = new StringBuilder();
        payload.Append('\x1b').Append($"[{Math.Max(1, wrapRowCount)}F"); // CPL N
        payload.Append('\x1b').Append("[0J");                            // ED 0
        payload.Append(synthPrompt);
        payload.Append("\r\n");
        payload.Append(colorizedBody);
        payload.Append("\r\n");
        payload.Append('\x1b').Append("]633;C").Append('\x07');          // OSC C
        var payloadBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload.ToString()));

        var sb = new StringBuilder();
        // Bypass pwsh's host Console wrapper by grabbing the raw stdout
        // stream and writing the payload as bytes directly.
        sb.AppendLine("$__sp_out = [System.Console]::OpenStandardOutput()");
        sb.AppendLine($"$__sp_bytes = [System.Convert]::FromBase64String('{payloadBase64}')");
        sb.AppendLine("$__sp_out.Write($__sp_bytes, 0, $__sp_bytes.Length)");
        sb.AppendLine("$__sp_out.Flush()");
        // and finally the real command body, normalized to LF line endings
        sb.AppendLine(command.Replace("\r\n", "\n"));
        return sb.ToString();
    }

    private async Task<JsonElement> HandleExecuteAsync(JsonElement request, CancellationToken ct)
    {
        var command = request.TryGetProperty("command", out var cmdProp) ? cmdProp.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(command))
            return SerializeResponse(w => w.WriteString("error", "Missing 'command' field in request"));
        var timeoutMs = request.TryGetProperty("timeout", out var tp) ? tp.GetInt32() : CommandTracker.PreemptiveTimeoutMs;

        // Reject if another command is still running (e.g., timed-out command in background).
        // The arrival of this execute_command is definitive proof that the prior
        // command's response channel has been lost — the caller cannot both be
        // awaiting the prior result and issuing a new execute at the same time
        // (splash has no concept of "target console" in the tool shape — each
        // new execute replaces the previous expectation). Flip the in-flight
        // command to cache mode so its result gets appended to _cachedResults
        // and surfaces on the next drain instead of being silently discarded.
        if (_tracker.Busy)
        {
            _tracker.FlipToCacheMode();
            return SerializeResponse(w => { w.WriteString("status", "busy"); w.WriteString("command", command); });
        }

        // Pre-send syntactic gate for adapters that declare
        // multiline_detect: balanced_parens (Racket today, future Lisp
        // family). Catches AI mistakes like a half-finished
        // (define (f x) before they deadlock the REPL waiting for the
        // closing paren. The counter understands string literals, line
        // and block comments, and the schema §18 Q1 reader-macro
        // extensions (char_literal_prefix, datum_comment_prefix).
        //
        // Only gates when the adapter asks for it — every other
        // adapter's execute path is untouched.
        if (_adapter?.Input.MultilineDetect == "balanced_parens"
            && _adapter.Input.BalancedParens is { } bpSpec)
        {
            var check = BalancedParensCounter.Evaluate(bpSpec, command);
            if (!check.IsComplete)
            {
                var diag = check.Diagnostic ?? "syntactically incomplete input";
                return SerializeResponse(w =>
                {
                    w.WriteString("status", "error");
                    w.WriteString("error", "incomplete_input");
                    w.WriteString("message", diag);
                    w.WriteString("command", command);
                });
            }
        }

        var shellName = _shellFamily;
        var enter = _adapter?.Input.LineEnding
            ?? _defaultEnter;

        // Multi-line pwsh commands have their echo emitted from inside the
        // tempfile itself via [Console]::Write so the child's virtual
        // buffer's cursor tracking stays consistent with what the visible
        // console shows — see BuildMultiLineTempfileBody. Only render the
        // echo directly here for single-line commands.
        bool isMultiLinePwsh = _isPwshFamily && command.Contains('\n');
        bool isMultiLineCmd = shellName is "cmd" && command.Contains('\n');
        bool isMultiLinePosix = shellName is "bash" or "sh" or "zsh" && command.Contains('\n');
        if (_isPwshFamily && !isMultiLinePwsh)
            RenderPwshCommandEcho(command);

        // Register command with tracker (it will resolve when OSC PromptStart arrives).
        // With concurrent pipe listeners, two execute requests can race between
        // the `_tracker.Busy` check above and here — the tracker's internal lock
        // serialises RegisterCommand and throws if another command is already
        // registered. Turn that back into a clean "busy" response so the proxy
        // routes the loser to a different console instead of surfacing an error.
        Task<CommandTracker.CommandResult> resultTask;
        try
        {
            resultTask = _tracker.RegisterCommand(command, timeoutMs);
        }
        catch (InvalidOperationException)
        {
            // Concurrent race path — another execute snuck in between the
            // Busy check above and here. Flip whatever won the race so its
            // result still makes it into the cache.
            _tracker.FlipToCacheMode();
            return SerializeResponse(w => { w.WriteString("status", "busy"); w.WriteString("command", command); });
        }

        // Adapters whose input echo strategy is deterministic_byte_match
        // (cmd, python, any REPL without a stdlib pre-input hook that can
        // emit OSC B/C) have no way to fire OSC C at the moment the
        // command starts, so the tracker would wait forever for a marker
        // that never arrives. Paper over the gap: RegisterCommand has
        // just reset _aiOutput to "", so position 0 is the true start
        // of the command's captured window.
        if (_adapter?.Output.InputEchoStrategy == "deterministic_byte_match")
            _tracker.SkipCommandStartMarker();

        // Multi-line commands can't be written straight to the PTY: pwsh
        // (and bash) would treat each embedded \n as "submit line now",
        // push subsequent lines into the continuation-prompt input, and
        // fragment the OSC markers so the capture window is meaningless.
        // Bracketed paste and raw passthrough both proved unreliable
        // under ConPTY, so we fall back to the robust approach: write
        // the full multi-line body to a temp file and dot-source it.
        // The shell parses the file as-is — heredocs, comments, nested
        // scriptblocks and multi-line pipelines all survive — and the
        // `. 'file'` form runs in the caller's scope so variables and
        // functions defined by the command persist for later calls.
        // Single-line commands skip the temp file and go straight to the
        // PTY as before so echo / history quality stays highest for the
        // common case.
        string ptyPayload;
        if (isMultiLinePwsh)
        {
            var tmpFile = Path.Combine(Path.GetTempPath(), $".splash-exec-{Environment.ProcessId}-{Guid.NewGuid():N}.ps1");
            var ptyInput = $". '{tmpFile}'; Remove-Item '{tmpFile}' -ErrorAction SilentlyContinue";

            // Work out how many terminal rows the dot-source + Remove-Item
            // input occupies once it wraps at the PTY's current width, so
            // BuildMultiLineTempfileBody can emit `\e[<N>F\e[0J` and wipe
            // every wrapped row rather than just the last one. Prompt is
            // "PS <cwd>> " followed by the ptyInput itself.
            int termWidth = 120;
            try { if (Console.WindowWidth > 0) termWidth = Console.WindowWidth; } catch { }
            var cwdForPrompt = _tracker.LastKnownCwd ?? _cwd ?? "";
            var promptLen = 3 /* "PS " */ + cwdForPrompt.Length + 2 /* "> " */;
            var totalCols = promptLen + ptyInput.Length;
            var wrapRowCount = Math.Max(1, (totalCols + termWidth - 1) / termWidth);

            await File.WriteAllTextAsync(tmpFile, BuildMultiLineTempfileBody(command, wrapRowCount), ct);
            ptyPayload = ptyInput + enter;
        }
        else if (isMultiLineCmd)
        {
            // cmd can't parse embedded newlines from a PTY keystroke stream —
            // each \n is treated as Enter and the second line drops into a
            // fresh prompt, fragmenting command parsing and the OSC markers.
            // Mirror the pwsh tempfile strategy: write the body to a .cmd
            // batch file, `call` it from the PTY as a single-line input, then
            // `del` it. `@echo off` up front suppresses re-echo of each line
            // inside the batch so the output mirrors single-line cmd usage.
            var tmpFile = Path.Combine(Path.GetTempPath(), $"splash-exec-{Environment.ProcessId}-{Guid.NewGuid():N}.cmd");
            var body = "@echo off\r\n" + command.Replace("\r\n", "\n").Replace("\n", "\r\n") + "\r\n";
            await File.WriteAllTextAsync(tmpFile, body, ct);
            ptyPayload = $"call \"{tmpFile}\" & del \"{tmpFile}\"" + enter;
        }
        else if (isMultiLinePosix)
        {
            // bash/zsh/sh share the same problem cmd does: writing newlines
            // into the PTY makes the shell submit each line as Enter, so
            // multi-line constructs (for/done, if/fi, function definitions)
            // either capture wrong output (the tracker resolves on the first
            // OSC A) or drop iterations entirely. Drop the body into a temp
            // .sh file and dot-source it so the shell parses the whole block
            // as a single source file. State (variables, functions, cwd)
            // persists because dot-sourcing runs in the caller's scope.
            var windowsPath = Path.Combine(Path.GetTempPath(), $"splash-exec-{Environment.ProcessId}-{Guid.NewGuid():N}.sh");
            var body = command.Replace("\r\n", "\n");
            if (!body.EndsWith('\n')) body += "\n";
            await File.WriteAllTextAsync(windowsPath, body, ct);

            // bash sees the Windows temp dir under its own mount namespace —
            // /mnt/c/... for WSL, /c/... for MSYS2 / Git Bash. On Linux/macOS
            // the worker and shell share a real POSIX filesystem so the
            // Windows-path translation is skipped.
            string unixPath;
            if (OperatingSystem.IsWindows())
            {
                unixPath = IsWslBash(_shell)
                    ? "/mnt/" + char.ToLower(windowsPath[0]) + windowsPath[2..].Replace('\\', '/')
                    : "/" + char.ToLower(windowsPath[0]) + windowsPath[2..].Replace('\\', '/');
            }
            else
            {
                unixPath = windowsPath;
            }
            ptyPayload = $". '{unixPath}'; rm -f '{unixPath}'" + enter;
        }
        else if (command.Contains('\n') &&
                 _adapter?.Input.MultilineDelivery == "tempfile" &&
                 _adapter.Input.Tempfile?.InvocationTemplate is { } invocationTpl)
        {
            // Generic adapter-driven tempfile delivery for multi-line REPL
            // commands. Used today by Python (whose parser-based REPL needs
            // a trailing blank line to close def/class/if blocks and would
            // otherwise capture output from a half-submitted block). A
            // future Node or Ruby adapter that declares multiline_delivery:
            // tempfile picks this path up for free.
            //
            // The body is written to adapter.input.tempfile.{prefix,extension}
            // and the adapter-supplied invocation_template (e.g.
            // _splash_exec_file(r"{path}") for python) is sent to the PTY
            // as a single line. The helper referenced by the template is
            // expected to have been registered by the adapter's
            // init.script_resource at REPL startup and is responsible for
            // cleanup so interrupted commands still delete their tempfile.
            var tmpPrefix = _adapter.Input.Tempfile.Prefix ?? ".splash-exec-";
            var tmpExt = _adapter.Input.Tempfile.Extension ?? "";
            var tmpFile = Path.Combine(
                Path.GetTempPath(),
                $"{tmpPrefix}{Environment.ProcessId}-{Guid.NewGuid():N}{tmpExt}");
            var body = command.Replace("\r\n", "\n");
            if (!body.EndsWith('\n')) body += "\n";
            await File.WriteAllTextAsync(tmpFile, body, ct);
            ptyPayload = ExpandTemplate(invocationTpl, ("path", tmpFile)) + enter;
        }
        else
        {
            ptyPayload = command + enter;
        }

        // Hold user input while the AI command is in flight. Any
        // keystrokes the user types into the visible console window
        // are buffered by InputForwardLoop instead of being forwarded
        // to the PTY — this prevents stray characters from being
        // prepended to the AI command. Held bytes are replayed
        // automatically after the command completes so the user's
        // typing isn't lost. Ctrl+C passes through even while held
        // so the user can always interrupt a stuck command.
        //
        // This replaces the old adapter-level clear_line approach
        // (which depended on the shell's readline supporting Ctrl-A +
        // Ctrl-K and didn't work at all for shells without a line
        // editor). The hold gate operates at splash's own forwarding
        // layer, above the shell, so it works universally.
        _holdUserInput = true;

        // Legacy clear_line: still useful as a belt-and-suspenders
        // defense for characters that slipped into the line editor
        // buffer before the hold gate was set. Eventually removable
        // once the hold approach proves sufficient in the field.
        var clearLine = _adapter?.Input.ClearLine;
        if (!string.IsNullOrEmpty(clearLine))
        {
            try { await WriteToPty(clearLine, ct); }
            catch { /* best-effort */ }
        }

        await WriteToPty(ptyPayload, ct);

        try
        {
            var result = await resultTask;
            // Release user-input hold BEFORE building the response so
            // the held keystrokes replay into the shell's fresh prompt
            // while the proxy is still formatting the JSON. This gives
            // the shell a head start on processing any user-typed
            // partial command so it's visible in the console window by
            // the time the AI's next tool call arrives.
            ReleaseHeldUserInput();
            var cleanedOutput = result.Output;
            if (_adapter?.Output.InputEchoStrategy == "deterministic_byte_match")
            {
                // Shells/REPLs that can't fire OSC C (cmd, python, any
                // interpreter without a stdlib pre-input hook) have no
                // marker separating ConPTY's input echo from the command
                // output. Strip the echoed input bytes from the head of
                // the captured slice instead — the exact bytes we wrote
                // to the PTY (single-line: the literal command;
                // multi-line on cmd: the `call "tmp" & del "tmp"`
                // wrapper). enter is dropped because it's the line
                // submission and never appears in the echo.
                var echoExpected = ptyPayload;
                if (echoExpected.EndsWith(enter))
                    echoExpected = echoExpected[..^enter.Length];
                cleanedOutput = StripCmdInputEcho(cleanedOutput, echoExpected);
            }
            // Re-evaluate which mode the REPL is now in. The detector
            // scans the tail of recent terminal output against every
            // auto_enter mode's detect regex; falls back to the
            // adapter's default mode otherwise. Result is cached on
            // the worker so get_status / cached_output can report the
            // same value without re-scanning.
            //
            // Scans the recent-output ring rather than the OSC-C..D
            // slice because the mode transition is visible in the
            // NEXT prompt (e.g. CCL drops to `1 > ` after an error),
            // which arrives AFTER the OSC A that fires Resolve — so
            // it's never inside cleanedOutput. The ring is updated
            // unconditionally by FeedOutput and captures the post-A
            // prompt as soon as its bytes land. Because ConPTY
            // typically delivers OSC A and the following prompt
            // bytes in the same chunk, the ring has the new prompt
            // by the time we read it; a short poll with a fresh
            // ring snapshot on each tick covers the rare case where
            // the prompt trails by a few milliseconds.
            if (_adapter?.Modes is { Count: > 0 } modes)
            {
                // Start with an explicit "no match yet" ModeMatch rather
                // than `default` — records are reference types, so `default`
                // is null and the compiler (rightly) can't prove the while
                // loop reassigns it before the post-loop `match.Name`
                // access. The loop body always runs at least once and
                // overwrites this value, but flowing through a non-null
                // sentinel makes the safety invariant explicit.
                var match = new ModeMatch(Name: null, Level: null);
                string? defaultModeName = null;
                foreach (var m in modes)
                {
                    if (m.Default) { defaultModeName = m.Name; break; }
                }
                defaultModeName ??= modes[0].Name;

                var deadline = DateTime.UtcNow.AddMilliseconds(150);
                while (true)
                {
                    // Scan the RAW ring bytes rather than the VtLite-
                    // rendered snapshot. VtLite reshapes the terminal
                    // grid and may collapse the post-A prompt into a
                    // cell position that no longer matches the mode's
                    // anchored regex (`^<prompt>$`). The raw stream
                    // preserves the prompt as its own final line, which
                    // is what the adapter-author wrote the regex
                    // against.
                    var snap = _tracker.GetRawRecentBytes();
                    match = ModeDetector.Detect(modes, snap);
                    // Stop as soon as we see a non-default auto_enter
                    // mode match — that's the signal the REPL moved.
                    if (match.Name != null && match.Name != defaultModeName) break;
                    if (DateTime.UtcNow >= deadline) break;
                    try { await Task.Delay(15, ct); }
                    catch (OperationCanceledException) { break; }
                }
                _currentMode = match.Name;
                _currentModeLevel = match.Level;
            }
            return SerializeResponse(w =>
            {
                w.WriteStringOrNull("output", cleanedOutput);
                w.WriteNumber("exitCode", result.ExitCode);
                w.WriteStringOrNull("cwd", result.Cwd);
                w.WriteStringOrNull("duration", result.Duration);
                w.WriteBoolean("timedOut", false);
                if (_adapter?.Modes is { Count: > 0 })
                {
                    w.WriteStringOrNull("currentMode", _currentMode);
                    if (_currentModeLevel.HasValue) w.WriteNumber("currentModeLevel", _currentModeLevel.Value);
                }
            });
        }
        catch (TimeoutException)
        {
            // Timeout = command is still running in the background.
            // Release held user input so the user can interact with
            // the running command (type a response, send Ctrl+C, etc.).
            ReleaseHeldUserInput();
            var partial = _tracker.GetCurrentAiOutputSnapshot();
            return SerializeResponse(w =>
            {
                w.WriteString("output", "");
                w.WriteNumber("exitCode", 0);
                w.WriteNull("cwd");
                w.WriteString("duration", (timeoutMs / 1000.0).ToString("F1"));
                w.WriteBoolean("timedOut", true);
                if (!string.IsNullOrEmpty(partial))
                    w.WriteString("partialOutput", partial);
            });
        }
        catch (InvalidOperationException ex)
        {
            ReleaseHeldUserInput();
            return SerializeResponse(w =>
            {
                w.WriteString("output", ex.Message);
                w.WriteNumber("exitCode", -1);
                w.WriteNull("cwd");
                w.WriteString("duration", "0.0");
                w.WriteBoolean("timedOut", false);
                w.WriteBoolean("shellExited", true);
            });
        }
    }

    /// <summary>
    /// Release the user-input hold gate and replay any keystrokes that
    /// were buffered while the AI command was in flight. Called from
    /// every exit path of HandleExecuteAsync (success, timeout, error)
    /// so the hold is never accidentally left on.
    /// </summary>
    private void ReleaseHeldUserInput()
    {
        _holdUserInput = false;
        while (_heldUserInput.TryDequeue(out var bytes))
        {
            try { _pty!.InputStream.Write(bytes, 0, bytes.Length); }
            catch (IOException) { break; }
        }
        try { _pty?.InputStream.Flush(); } catch { }
    }

    private JsonElement HandleGetCachedOutput()
    {
        var cached = _tracker.ConsumeCachedOutputs();
        if (cached.Count == 0)
            return SerializeResponse(w => w.WriteString("status", "no_cache"));

        return SerializeResponse(w =>
        {
            w.WriteString("status", "ok");
            w.WriteStartArray("results");
            foreach (var r in cached)
            {
                w.WriteStartObject();
                w.WriteStringOrNull("output", r.Output);
                w.WriteNumber("exitCode", r.ExitCode);
                w.WriteStringOrNull("cwd", r.Cwd);
                w.WriteStringOrNull("command", r.Command);
                w.WriteStringOrNull("duration", r.Duration);
                w.WriteString("statusLine", r.StatusLine);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        });
    }

    /// <summary>
    /// Send raw input to the PTY while a command is running. Rejects
    /// if the console is idle — use execute_command for that. The
    /// input is written as-is: the caller is responsible for including
    /// \r for Enter, \x03 for Ctrl+C, escape sequences for arrow keys,
    /// etc. Capped at 256 chars to prevent accidental bulk injection.
    /// </summary>
    private async Task<JsonElement> HandleSendInputAsync(JsonElement request, CancellationToken ct)
    {
        if (!_tracker.Busy)
            return SerializeResponse(w => { w.WriteString("status", "rejected"); w.WriteString("error", "Console is not busy. Use execute_command to run commands."); });

        var input = request.TryGetProperty("input", out var inputProp) ? inputProp.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(input))
            return SerializeResponse(w => { w.WriteString("status", "error"); w.WriteString("error", "Missing 'input' field"); });

        if (input.Length > 256)
            return SerializeResponse(w => { w.WriteString("status", "error"); w.WriteString("error", $"Input too long ({input.Length} chars, max 256)"); });

        // Interpret C-style escape sequences so the AI can express
        // control characters naturally: \r for Enter, \n for LF,
        // \t for Tab, \x03 for Ctrl+C, \x1b for ESC, \\ for literal \.
        var unescaped = UnescapeInput(input);
        await WriteToPty(unescaped, ct);
        Log($"SendInput: {EscapeForLog(unescaped)}");

        return SerializeResponse(w =>
        {
            w.WriteString("status", "ok");
            w.WriteNumber("bytesSent", unescaped.Length);
        });
    }

    internal static string UnescapeInput(string input)
    {
        var sb = new StringBuilder(input.Length);
        for (int i = 0; i < input.Length; i++)
        {
            if (input[i] == '\\' && i + 1 < input.Length)
            {
                switch (input[i + 1])
                {
                    case 'r': sb.Append('\r'); i++; break;
                    case 'n': sb.Append('\n'); i++; break;
                    case 't': sb.Append('\t'); i++; break;
                    case 'a': sb.Append('\a'); i++; break;
                    case '\\': sb.Append('\\'); i++; break;
                    case 'x' when i + 3 < input.Length:
                        var hex = input.Substring(i + 2, 2);
                        if (byte.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var b))
                        { sb.Append((char)b); i += 3; }
                        else sb.Append(input[i]);
                        break;
                    default: sb.Append(input[i]); break;
                }
            }
            else
            {
                sb.Append(input[i]);
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Replace OSC 0/1/2 (set window title) sequences in shell output with our
    /// desired title. Prevents shells like bash from overriding the title set
    /// by the proxy via set_title pipe command.
    /// Format: \x1b]N;text\x07 (BEL terminator) or \x1b]N;text\x1b\\ (ST terminator)
    /// where N is 0, 1, or 2.
    ///
    /// <para><b>Cross-chunk buffering.</b> The PTY read loop calls this
    /// function on every chunk it produces, and a shell emitting a title
    /// OSC near a read-buffer boundary can easily split the sequence
    /// (opener in chunk N, body or terminator in chunk N+1). Without
    /// state, a split opener leaks into the visible stream and the
    /// terminal interprets it as an open-ended title write — the shell's
    /// title ends up displayed instead of splash's desired one. The
    /// <paramref name="pendingTail"/> ref parameter carries a
    /// not-yet-classified or not-yet-terminated OSC fragment forward
    /// between calls: on entry it's prepended to the current chunk, on
    /// exit it contains any unterminated opener that belongs to a title
    /// sequence (or a partial ESC at chunk end that could begin one).
    /// Callers are expected to keep one tail buffer per stream.</para>
    /// </summary>
    internal static string ReplaceOscTitle(string input, string? desiredTitle, ref string pendingTail)
    {
        // Prepend any fragment carried over from the previous chunk.
        // The carried fragment is always either `\x1b`, `\x1b]`, or
        // a partial `\x1b]N;...` opener with no terminator — i.e.
        // something we deliberately refused to emit last time because
        // we couldn't tell whether it was a title OSC that needed
        // rewriting. Clear it now; it's re-set below if still partial.
        var combined = pendingTail.Length == 0 ? input : pendingTail + input;
        pendingTail = "";
        if (desiredTitle == null) return combined;

        var sb = new StringBuilder(combined.Length);
        int i = 0;
        while (i < combined.Length)
        {
            if (combined[i] == '\x1b')
            {
                int remain = combined.Length - i;

                // Lone ESC at end of chunk — we can't classify what
                // kind of escape sequence this starts, and emitting it
                // bare would leave the visible terminal in "waiting for
                // sequence" state. Buffer until the next call.
                if (remain < 2)
                {
                    pendingTail = combined[i..];
                    break;
                }

                if (combined[i + 1] == ']')
                {
                    // OSC. Need at least \x1b + ] + type byte + ; = 4 bytes
                    // to decide whether this is a title sequence we care about.
                    if (remain < 4)
                    {
                        pendingTail = combined[i..];
                        break;
                    }

                    var typeByte = combined[i + 2];
                    var isTitleOsc = (typeByte == '0' || typeByte == '1' || typeByte == '2')
                                     && combined[i + 3] == ';';

                    if (isTitleOsc)
                    {
                        // Look for the terminator: BEL (\x07) or ST (\x1b\).
                        int end = -1;
                        int termLen = 0;
                        for (int j = i + 4; j < combined.Length; j++)
                        {
                            if (combined[j] == '\x07') { end = j; termLen = 1; break; }
                            if (combined[j] == '\x1b' && j + 1 < combined.Length && combined[j + 1] == '\\')
                            { end = j; termLen = 2; break; }
                        }

                        if (end >= 0)
                        {
                            // Fully terminated — rewrite with desired title,
                            // preserving OSC type and terminator style.
                            sb.Append('\x1b').Append(']').Append(typeByte).Append(';').Append(desiredTitle);
                            if (termLen == 1) sb.Append('\x07');
                            else { sb.Append('\x1b').Append('\\'); }
                            i = end + termLen;
                            continue;
                        }

                        // Terminator hasn't arrived yet — buffer the whole
                        // opener + partial body so the next chunk can
                        // re-scan with the terminator visible. Crucially
                        // do NOT emit these bytes, or the visible terminal
                        // would interpret them as an open-ended title and
                        // display the shell's title once the terminator
                        // eventually shows up.
                        pendingTail = combined[i..];
                        break;
                    }
                    // Non-title OSC (OSC 4, 7, 112, 633, ...) — fall
                    // through and pass the `\x1b` through byte-by-byte.
                    // The rest of the sequence flows through the plain
                    // copy path below without being rewritten.
                }
                // Non-OSC escape (CSI `\x1b[`, charset `\x1b(`, ...) —
                // pass through unchanged.
            }

            sb.Append(combined[i]);
            i++;
        }
        return sb.ToString();
    }

    private JsonElement HandleSetTitle(JsonElement request)
    {
        var title = request.TryGetProperty("title", out var tp) ? tp.GetString() : null;
        if (title != null)
        {
            _desiredTitle = title;
            Console.Title = title;
            // Also write OSC 0 (Set Window Title) directly to stdout.
            // Some shells (cmd.exe) override Console.Title via ConPTY;
            // the OSC 0 sequence ensures the visible console title is set.
            _stdoutStream ??= Console.OpenStandardOutput();
            var osc = Encoding.UTF8.GetBytes($"\x1b]0;{title}\x07");
            _stdoutStream.Write(osc, 0, osc.Length);
            _stdoutStream.Flush();

            // Keep the tracker's cached-result status lines in sync with
            // the proxy's current display name. set_title is the earliest
            // point at which a freshly launched worker learns its name,
            // so without this, commands registered before the claim path
            // runs would bake the wrong identity into their cached
            // status lines.
            _tracker.SetDisplayContext(title, _shellFamily);
        }
        return SerializeResponse(w => w.WriteString("status", "ok"));
    }

    /// <summary>
    /// Write banner/reason text directly to the visible console at startup.
    /// Called from RunAsync before the first prompt is drawn.
    /// </summary>
    private void WriteBanner()
    {
        WriteBannerText(_banner, _reason, isReuse: false);
    }

    /// <summary>
    /// Write banner and/or reason text directly to the visible console.
    /// Uses ANSI colors: banner in green, reason in dark yellow.
    /// Shell-agnostic — writes to worker's stdout, not through the shell.
    /// </summary>
    private void WriteBannerText(string? banner, string? reason, bool isReuse = true)
    {
        var sb = new StringBuilder();
        if (isReuse)
            sb.Append("\r\n\r\n"); // blank line separating previous prompt from banner

        if (!string.IsNullOrEmpty(banner))
            sb.Append($"\x1b[32m{banner}\x1b[0m\r\n");

        if (!string.IsNullOrEmpty(reason))
        {
            if (sb.Length > 0) sb.Append("\r\n");
            sb.Append($"\x1b[33mReason: {reason}\x1b[0m\r\n");
        }

        if (sb.Length > 0 && (!string.IsNullOrEmpty(banner) || !string.IsNullOrEmpty(reason)))
        {
            sb.Append("\r\n"); // blank line after banner before shell output/prompt
            _stdoutStream ??= Console.OpenStandardOutput();
            var bytes = Encoding.UTF8.GetBytes(sb.ToString());
            _stdoutStream.Write(bytes, 0, bytes.Length);
            _stdoutStream.Flush();
        }
    }

    private JsonElement HandleDisplayBanner(JsonElement request)
    {
        var banner = request.TryGetProperty("banner", out var bp) ? bp.GetString() : null;
        var reason = request.TryGetProperty("reason", out var rp) ? rp.GetString() : null;
        WriteBannerText(banner, reason);

        // Kick the shell to draw a fresh prompt after the banner
        var enter = _adapter?.Input.LineEnding
            ?? _defaultEnter;
        try
        {
            var bytes = Encoding.UTF8.GetBytes(enter);
            _pty?.InputStream.Write(bytes, 0, bytes.Length);
            _pty?.InputStream.Flush();
        }
        catch (Exception ex) { Log($"DisplayBanner kick failed: {ex.Message}"); }

        return SerializeResponse(w => w.WriteString("status", "ok"));
    }

    /// <summary>
    /// Handle claim request from a new proxy. Constructs a new owned pipe name
    /// and signals the main loop to start serving it.
    /// </summary>
    private JsonElement HandleClaim(JsonElement request)
    {
        var proxyPid = request.TryGetProperty("proxy_pid", out var pp) ? pp.GetInt32() : 0;
        var agentId = request.TryGetProperty("agent_id", out var ai) ? ai.GetString() : "default";
        var title = request.TryGetProperty("title", out var tp) ? tp.GetString() : null;
        var proxyVersionStr = request.TryGetProperty("proxy_version", out var vp) ? vp.GetString() : null;

        if (proxyPid == 0)
            return SerializeResponse(w => { w.WriteString("status", "error"); w.WriteString("error", "proxy_pid required"); });

        // Version check: if the calling proxy is strictly newer than this worker,
        // the pipe protocol may have changed in incompatible ways. Refuse the claim,
        // mark the console as obsolete, and stop serving pipes. The shell itself
        // keeps running so the user can continue working in the terminal.
        if (proxyVersionStr != null
            && Version.TryParse(proxyVersionStr, out var proxyVer)
            && proxyVer > _myVersion)
        {
            _obsolete = true;
            _desiredTitle = $"#{Environment.ProcessId} (obsolete v{_myVersion.ToString(3)})";
            Console.Title = _desiredTitle;
            // Show a prominent banner so the human user understands what happened:
            // AI/MCP control has been detached, but the shell itself is still usable.
            WriteBannerText(
                $"This console is no longer managed by splash (worker v{_myVersion.ToString(3)}).",
                $"A newer proxy (v{proxyVer}) tried to re-claim this console. The shell is still available for you to use directly, but AI commands via MCP will no longer route here. Close the window when you're done.",
                isReuse: true);
            Log($"Claim refused: proxy v{proxyVer} > worker v{_myVersion}. Marking obsolete.");
            _claimTcs?.TrySetException(new InvalidOperationException("obsolete"));
            return SerializeResponse(w =>
            {
                w.WriteString("status", "obsolete");
                w.WriteString("worker_version", _myVersion.ToString(3));
            });
        }

        var newPipeName = $"{ConsoleManager.PipePrefix}.{proxyPid}.{agentId}.{Environment.ProcessId}";
        if (title != null) Console.Title = title;

        // Propagate the proxy-supplied display name (sent in the claim's
        // `title` field, already in "Fox" / "Reggae" form) and the
        // worker's own shell family into the tracker so any command
        // that ends up cached on this console carries a self-contained
        // status line matching how inline results are rendered.
        _tracker.SetDisplayContext(title, _shellFamily);

        // New proxy taking ownership — drop whatever is in the
        // recent-output ring. Anything captured before this moment
        // belonged to a previous MCP session (the shell was already
        // running when the human restarted Claude Code or launched a
        // new proxy), and exposing those bytes via peek_console would
        // show content that isn't part of the current session's
        // terminal view. The ring will refill with current-session
        // bytes as the new proxy issues commands.
        _tracker.ClearRecentOutput();

        // Signal the main loop to start the new owned pipe
        _claimTcs?.TrySetResult(newPipeName);

        return SerializeResponse(w => { w.WriteString("status", "ok"); w.WriteString("pipe", newPipeName); });
    }

    private static int GetProxyPidFromPipeName(string pipeName)
    {
        // SP.{proxyPid}.{agentId}.{consolePid}
        var parts = pipeName.Split('.');
        return parts.Length >= 2 && int.TryParse(parts[1], out var pid) ? pid : 0;
    }

    // --- Pipe protocol (length-prefixed JSON) ---

    private static async Task<JsonElement> ReadMessageAsync(Stream stream, CancellationToken ct)
    {
        var lenBuf = new byte[4];
        await ReadExactAsync(stream, lenBuf, ct);
        var len = BitConverter.ToInt32(lenBuf);
        var msgBuf = new byte[len];
        await ReadExactAsync(stream, msgBuf, ct);
        return PipeJson.ParseElement(msgBuf);
    }

    private static async Task WriteMessageAsync(Stream stream, JsonElement message, CancellationToken ct)
    {
        var msgBytes = PipeJson.ElementToBytes(message);
        var lenBytes = BitConverter.GetBytes(msgBytes.Length);
        await stream.WriteAsync(lenBytes, ct);
        await stream.WriteAsync(msgBytes, ct);
        await stream.FlushAsync(ct);
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

    private static string EscapeForLog(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s)
        {
            if (c < 0x20 || c == 0x7f) sb.Append($"\\x{(int)c:x2}");
            else sb.Append(c);
        }
        return sb.ToString();
    }

    private static JsonElement SerializeResponse(Action<Utf8JsonWriter> writeFields)
        => PipeJson.BuildObjectElement(writeFields);

    // --- Entry point for --console mode ---

    public static async Task<int> RunConsoleMode(string[] args)
    {
        CleanupOldLogs();
        string? proxyPid = null, agentId = null, shell = null, cwd = null, banner = null, reason = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--proxy-pid" when i + 1 < args.Length: proxyPid = args[++i]; break;
                case "--agent-id" when i + 1 < args.Length: agentId = args[++i]; break;
                case "--shell" when i + 1 < args.Length: shell = args[++i]; break;
                case "--cwd" when i + 1 < args.Length: cwd = args[++i]; break;
                case "--banner" when i + 1 < args.Length: banner = args[++i]; break;
                case "--reason" when i + 1 < args.Length: reason = args[++i]; break;
                case "--no-user-input": break; // parsed below
            }
        }
        bool noUserInput = args.Contains("--no-user-input");

        if (proxyPid == null || agentId == null || shell == null)
        {
            Console.Error.WriteLine("Usage: splash --console --proxy-pid <pid> --agent-id <id> --shell <shell> [--cwd <dir>]");
            return 1;
        }

        cwd ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // Pipe name: SP.{proxyPid}.{agentId}.{ownPid}
        var ownPid = Environment.ProcessId;
        var pipeName = $"{ConsoleManager.PipePrefix}.{proxyPid}.{agentId}.{ownPid}";

        Log($"PID={ownPid} Pipe={pipeName} Shell={shell} Cwd={cwd}");

        var worker = new ConsoleWorker(pipeName, int.Parse(proxyPid), shell, cwd, banner, reason)
        {
            _holdUserInput = noUserInput  // --no-user-input: permanently hold (suppress) input forwarding
        };
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        try
        {
            await worker.RunAsync(cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            // Swallow any late-stage exception so Program.cs still sees
            // exit code 0. Windows Terminal's "close on exit" default is
            // graceful, which means exit != 0 leaves the window open with
            // "[process exited with code ...]" instead of closing. A
            // clean exit 0 after shell death lets the console window
            // close automatically alongside the dead shell.
            Log($"RunConsoleMode exception: {ex.GetType().Name}: {ex.Message}");
        }

        return 0;
    }
}
