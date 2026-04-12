using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace SplashShell.Services;

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
    private static readonly string LogFile = Path.Combine(Path.GetTempPath(), $"splashshell-worker-{Environment.ProcessId}.log");
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
            foreach (var path in Directory.EnumerateFiles(Path.GetTempPath(), "splashshell-worker-*.log"))
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
    private IPtySession? _pty;
    private Stream? _writer;
    private readonly OscParser _parser = new();
    private readonly CommandTracker _tracker = new();
    private bool _ready;
    private volatile int _outputLength;
    // Controls whether PTY output is mirrored to the worker's visible console.
    // Disabled during shell integration injection to hide the source echo.
    private volatile bool _mirrorVisible = true;
    // Direct stdout stream — bypasses Console.Out's TextWriter buffering.
    private Stream? _stdoutStream;

    /// <summary>
    /// Current console status for get_status requests.
    /// </summary>
    private string Status => _tracker.Busy ? "busy" : (_tracker.HasCachedOutput ? "completed" : "standby");

    private readonly string? _banner;
    private readonly string? _reason;
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
        if (!ConsoleManager.IsPowerShellFamily(ConsoleManager.NormalizeShellFamily(_shell)))
            WriteBanner();

        // Prepare shell integration script BEFORE launching the shell.
        // For pwsh, we pass it via -NoExit -Command so it doesn't echo in the console.
        var commandLine = BuildCommandLine();

        // Launch shell via platform PTY (ConPTY on Windows, forkpty on Linux/macOS)
        // Use the visible console's actual dimensions instead of hardcoded 120x30.
        // MSYS2/Git Bash needs the parent's environment (MSYSTEM, HOME, PATH with Git paths).
        // pwsh uses a clean environment to avoid inheriting MCP server variables.
        var shellName = ConsoleManager.NormalizeShellFamily(_shell);
        bool inheritEnv = !ConsoleManager.IsPowerShellFamily(shellName);
        int cols = Console.WindowWidth > 0 ? Console.WindowWidth : 120;
        int rows = Console.WindowHeight > 0 ? Console.WindowHeight : 30;
        _pty = PtyFactory.Start(commandLine, _cwd, cols, rows, inheritEnvironment: inheritEnv);
        _tracker.SetTerminalSize(cols, rows);
        _writer = _pty.InputStream;

        // Start reading PTY output on dedicated thread (feeds OscParser + CommandTracker)
        var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var readTask = ReadOutputLoop(readCts.Token);

        // Watch the child shell process. ConPTY does NOT close the output
        // pipe when the child exits, so ReadOutputLoop's blocking Read
        // would sit waiting forever if we relied on EOF. Instead wait on
        // the process handle directly and, when it fires, signal the main
        // loop via _shellExitedTcs so the worker can tear itself down.
        _ = WaitForShellExitAsync(ct);

        var enter = ConsoleManager.EnterKeyFor(shellName);

        if (ConsoleManager.IsPowerShellFamily(shellName))
        {
            // pwsh with -NoExit -Command sources the integration script during
            // startup, then drops into interactive mode where the overridden
            // prompt function emits the first OSC A automatically. Go straight
            // to WaitForReady — no settle wait, no kick Enter. This was the
            // biggest contributor to auto-start latency (~2s).
        }
        else if (shellName is "cmd")
        {
            // cmd.exe with /k prompt doesn't paint the first prompt until it
            // reads input. Let the welcome banner finish, then kick an Enter
            // so the OSC-aware prompt runs.
            await WaitForOutputSettled(ct);
            await WriteToPty(enter, ct);
        }
        else
        {
            // bash/zsh and others: wait for initial output to settle so the
            // shell is past its welcome banner and ready to accept input,
            // then inject the integration script via PTY stdin. Output
            // mirroring is suppressed during injection to hide the `source`
            // echo and any noise from loading the script.
            await WaitForOutputSettled(ct);
            _mirrorVisible = false;
            await InjectShellIntegration(ct);
            await Task.Delay(500, ct);
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

        // For shells with PTY-injected integration (bash/zsh), the prompt drawn
        // during injection was suppressed. Send a kick to draw a fresh prompt.
        if (!ConsoleManager.IsPowerShellFamily(shellName) && shellName is not "cmd")
        {
            await WriteToPty(enter, ct);
        }

        Log($"Shell ready, pipe={_pipeName}");

        // Start forwarding user's keyboard input from visible console to ConPTY
        var inputTask = InputForwardLoop(ct);

        // Monitor visible console window resizes and propagate to ConPTY
        var resizeTask = ResizeMonitorLoop(ct);

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
        var shellName = ConsoleManager.NormalizeShellFamily(_shell);

        if (ConsoleManager.IsPowerShellFamily(shellName))
        {
            var script = LoadEmbeddedScript("integration.ps1");
            if (script != null)
            {
                // Prepend Write-Host banner/reason lines so they're emitted by
                // pwsh itself AFTER ConPTY's initial `\e[?9001h...\e[2J\e[H`
                // screen-clear payload. If we wrote them to the worker's
                // stdout before the PTY started (the old WriteBanner path),
                // ConPTY wipes them almost immediately, which the user saw
                // as banner text flashing on screen for ~0.5s.
                var prefix = new StringBuilder();
                if (!string.IsNullOrEmpty(_banner))
                    prefix.AppendLine($"Write-Host '{_banner.Replace("'", "''")}' -ForegroundColor Green");
                if (!string.IsNullOrEmpty(_banner) && !string.IsNullOrEmpty(_reason))
                    prefix.AppendLine("Write-Host");
                if (!string.IsNullOrEmpty(_reason))
                    prefix.AppendLine($"Write-Host 'Reason: {_reason.Replace("'", "''")}' -ForegroundColor DarkYellow");
                if (prefix.Length > 0) prefix.AppendLine("Write-Host");

                var tmpFile = Path.Combine(Path.GetTempPath(), $".splashshell-integration-{Environment.ProcessId}.ps1");
                File.WriteAllText(tmpFile, prefix.ToString() + script);
                // -NoExit keeps the shell alive after -Command completes.
                // The command imports PSReadLine + sources the integration script silently.
                var cmd = $"Import-Module PSReadLine -ErrorAction SilentlyContinue; . '{tmpFile}'; Remove-Item '{tmpFile}' -ErrorAction SilentlyContinue";
                return $"\"{_shell}\" -NoExit -Command \"{cmd}\"";
            }
        }

        // cmd.exe: set PROMPT with OSC 633 markers via /k at startup.
        // This avoids visible injection echo in the console.
        if (shellName is "cmd")
        {
            var prompt = "$E]633;P;Cwd=$P$E\\$E]633;A$E\\$P$G$S";
            return $"\"{_shell}\" /q /k \"prompt {prompt}\"";
        }

        // bash: use --login -i to load profiles normally; integration is injected
        // later via PTY input by InjectShellIntegration().
        if (shellName is "bash" or "sh")
            return $"\"{_shell}\" --login -i";

        if (shellName is "zsh")
            return $"\"{_shell}\" -l -i";

        return $"\"{_shell}\"";
    }

    private async Task WaitForOutputSettled(CancellationToken ct)
    {
        // Wait for output to settle: no new data for 1 second, with minimum 2s wait.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        var lastLength = 0;
        var settledCount = 0;
        await Task.Delay(2000, ct); // minimum wait

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(500, ct);

            var currentLength = _outputLength;
            if (currentLength == lastLength && currentLength > 0)
            {
                settledCount++;
                if (settledCount >= 2) break; // settled for 1s
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
        var shellName = ConsoleManager.NormalizeShellFamily(_shell);
        string? script = shellName switch
        {
            "zsh" => LoadEmbeddedScript("integration.zsh"),
            _    => LoadEmbeddedScript("integration.bash"),
        };

        if (script == null)
        {
            Log($"WARNING: No shell integration script found, falling back to no-OSC mode");
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            // Windows bash (WSL, MSYS2, Git Bash) — write the script to a
            // Windows temp path directly since the worker and child share
            // the filesystem, then teach the shell how to find it in its
            // own namespace (/mnt/c/... for WSL, /c/... for MSYS2).
            var windowsPath = Path.Combine(Path.GetTempPath(), $".splashshell-integration-{Environment.ProcessId}.sh");
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
            var tmpFile = $"/tmp/.splashshell-integration-{Environment.ProcessId}.sh";
            var injection = new StringBuilder();
            injection.AppendLine($"cat > {tmpFile} << 'SPLASHSHELL_EOF'");
            injection.AppendLine(script.TrimEnd());
            injection.AppendLine("SPLASHSHELL_EOF");
            injection.AppendLine($"source {tmpFile}; rm -f {tmpFile}");
            await WriteToPty(injection.ToString(), ct);
        }
    }

    private static string? LoadEmbeddedScript(string name)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = $"SplashShell.ShellIntegration.{name}";
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null) return null;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
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
    /// Forward user keyboard input from the worker's visible console to the ConPTY input pipe.
    /// When AI is executing a command (CommandTracker.Busy), input is held until the command completes.
    /// </summary>
    private Task InputForwardLoop(CancellationToken ct)
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

                var shellName = ConsoleManager.NormalizeShellFamily(_shell);
                // pwsh and cmd.exe understand win32-input-mode natively; only Unix shells need translation
                bool needsTranslation = !ConsoleManager.IsPowerShellFamily(shellName) && shellName is not "cmd";

                var charBuf = new char[256];
                var pending = needsTranslation ? new StringBuilder() : null;
                while (!ct.IsCancellationRequested)
                {
                    // ReadConsoleW reads Unicode (UTF-16) — handles CJK characters correctly
                    if (!ReadConsoleW(hStdIn, charBuf, (uint)charBuf.Length, out var charsRead, IntPtr.Zero) || charsRead == 0)
                        break;

                    // Always forward user input — the user must be able to
                    // respond to confirmation prompts and send Ctrl+C even
                    // while an AI-initiated command is running.

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
                        _pty!.InputStream.Write(utf8, 0, utf8.Length);
                        _pty.InputStream.Flush();
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
            var cleanedOutput = ReplaceOscTitle(text, _desiredTitle);
            var outBytes = Encoding.UTF8.GetBytes(cleanedOutput);
            _stdoutStream ??= Console.OpenStandardOutput();
            _stdoutStream.Write(outBytes, 0, outBytes.Length);
            _stdoutStream.Flush();
        }
        catch { }
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
                    if (_tracker.Busy) Log($"RAW: {EscapeForLog(text)}");
                    var result = _parser.Parse(text);

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
                w.WriteString("shellFamily", ConsoleManager.NormalizeShellFamily(_shell));
                w.WriteString("shellPath", _shell);
                w.WriteStringOrNull("cwd", _tracker.LastKnownCwd);
                w.WriteStringOrNull("runningCommand", _tracker.RunningCommand);
                var elapsed = _tracker.RunningElapsedSeconds;
                if (elapsed.HasValue) w.WriteNumber("runningElapsedSeconds", elapsed.Value);
                else w.WriteNull("runningElapsedSeconds");
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
        if (ConsoleManager.IsPowerShellFamily(ConsoleManager.NormalizeShellFamily(_shell)))
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
        var timeoutMs = request.TryGetProperty("timeout", out var tp) ? tp.GetInt32() : 170_000;

        // Reject if another command is still running (e.g., timed-out command in background)
        if (_tracker.Busy)
            return SerializeResponse(w => { w.WriteString("status", "busy"); w.WriteString("command", command); });

        var shellName = ConsoleManager.NormalizeShellFamily(_shell);
        var enter = ConsoleManager.EnterKeyFor(shellName);

        // Multi-line pwsh commands have their echo emitted from inside the
        // tempfile itself via [Console]::Write so the child's virtual
        // buffer's cursor tracking stays consistent with what the visible
        // console shows — see BuildMultiLineTempfileBody. Only render the
        // echo directly here for single-line commands.
        bool isMultiLinePwsh = ConsoleManager.IsPowerShellFamily(shellName) && command.Contains('\n');
        if (ConsoleManager.IsPowerShellFamily(shellName) && !isMultiLinePwsh)
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
            return SerializeResponse(w => { w.WriteString("status", "busy"); w.WriteString("command", command); });
        }

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
        else
        {
            ptyPayload = command + enter;
        }
        await WriteToPty(ptyPayload, ct);

        try
        {
            var result = await resultTask;
            return SerializeResponse(w =>
            {
                w.WriteStringOrNull("output", result.Output);
                w.WriteNumber("exitCode", result.ExitCode);
                w.WriteStringOrNull("cwd", result.Cwd);
                w.WriteStringOrNull("duration", result.Duration);
                w.WriteBoolean("timedOut", false);
            });
        }
        catch (TimeoutException)
        {
            // Snapshot what the console is currently displaying so the AI
            // can diagnose why the command is still running (watch mode,
            // stuck at an interactive prompt, etc.). On Windows, prefer
            // the native screen buffer read (same as peek_console); fall
            // back to the VT-medium ring buffer on other platforms.
            var partial = ReadConsoleScreenText() ?? _tracker.GetRecentOutputSnapshot();
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
            // Raised by _tracker.AbortPending() when the child shell process
            // exited before delivering OSC A. Return a synthetic error result
            // so the proxy-side execute call unwinds instead of waiting for
            // an IOException from the worker process termination.
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

    private JsonElement HandleGetCachedOutput()
    {
        var cached = _tracker.ConsumeCachedOutput();
        if (cached == null)
            return SerializeResponse(w => w.WriteString("status", "no_cache"));

        return SerializeResponse(w =>
        {
            w.WriteString("status", "ok");
            w.WriteStringOrNull("output", cached.Output);
            w.WriteNumber("exitCode", cached.ExitCode);
            w.WriteStringOrNull("cwd", cached.Cwd);
            w.WriteStringOrNull("command", cached.Command);
            w.WriteStringOrNull("duration", cached.Duration);
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

    private static string UnescapeInput(string input)
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
    /// </summary>
    private static string ReplaceOscTitle(string input, string? desiredTitle)
    {
        if (desiredTitle == null) return input;

        var sb = new StringBuilder(input.Length);
        int i = 0;
        while (i < input.Length)
        {
            // Look for \x1b](0|1|2);
            if (i + 3 < input.Length && input[i] == '\x1b' && input[i + 1] == ']' &&
                (input[i + 2] == '0' || input[i + 2] == '1' || input[i + 2] == '2') && input[i + 3] == ';')
            {
                // Find terminator: BEL (\x07) or ST (\x1b\)
                int end = -1;
                int termLen = 0;
                for (int j = i + 4; j < input.Length; j++)
                {
                    if (input[j] == '\x07') { end = j; termLen = 1; break; }
                    if (input[j] == '\x1b' && j + 1 < input.Length && input[j + 1] == '\\')
                    { end = j; termLen = 2; break; }
                }

                if (end > 0)
                {
                    // Replace with our desired title (preserve the OSC type and terminator style)
                    sb.Append('\x1b').Append(']').Append(input[i + 2]).Append(';').Append(desiredTitle);
                    if (termLen == 1) sb.Append('\x07');
                    else { sb.Append('\x1b').Append('\\'); }
                    i = end + termLen;
                    continue;
                }
            }
            sb.Append(input[i]);
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
        var enter = ConsoleManager.EnterKeyFor(ConsoleManager.NormalizeShellFamily(_shell));
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
                $"This console is no longer managed by splashshell (worker v{_myVersion.ToString(3)}).",
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
            }
        }

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

        var worker = new ConsoleWorker(pipeName, int.Parse(proxyPid), shell, cwd, banner, reason);
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
