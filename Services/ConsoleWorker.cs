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
        var bannerShellName = Path.GetFileNameWithoutExtension(_shell).ToLowerInvariant();
        if (bannerShellName is not "pwsh" and not "powershell")
            WriteBanner();

        // Prepare shell integration script BEFORE launching the shell.
        // For pwsh, we pass it via -NoExit -Command so it doesn't echo in the console.
        var commandLine = BuildCommandLine();

        // Launch shell via platform PTY (ConPTY on Windows, forkpty on Linux/macOS)
        // Use the visible console's actual dimensions instead of hardcoded 120x30.
        // MSYS2/Git Bash needs the parent's environment (MSYSTEM, HOME, PATH with Git paths).
        // pwsh uses a clean environment to avoid inheriting MCP server variables.
        var shellName = Path.GetFileNameWithoutExtension(_shell).ToLowerInvariant();
        bool inheritEnv = shellName is not "pwsh" and not "powershell";
        int cols = Console.WindowWidth > 0 ? Console.WindowWidth : 120;
        int rows = Console.WindowHeight > 0 ? Console.WindowHeight : 30;
        _pty = PtyFactory.Start(commandLine, _cwd, cols, rows, inheritEnvironment: inheritEnv);
        _writer = _pty.InputStream;

        // Start reading PTY output on dedicated thread (feeds OscParser + CommandTracker)
        var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var readTask = ReadOutputLoop(readCts.Token);

        // Shell-specific Enter key: Unix shells use \n, Windows shells use \r
        var enter = shellName is "bash" or "sh" or "zsh" ? "\n" : "\r";

        if (shellName is "pwsh" or "powershell")
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
        await WaitForReady(TimeSpan.FromSeconds(15), ct);
        _ready = true;
        _mirrorVisible = true;

        // For shells with PTY-injected integration (bash/zsh), the prompt drawn
        // during injection was suppressed. Send a kick to draw a fresh prompt.
        if (shellName is not "pwsh" and not "powershell" and not "cmd")
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

            // Wait for proxy death or external cancellation
            await monitorTask;

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
        // console window (which cancels `ct`), then fall through to cleanup.
        if (_obsolete)
        {
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { }
        }

        // Cleanup
        readCts.Cancel();
        _pty?.Dispose();
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
        var shellName = Path.GetFileNameWithoutExtension(_shell).ToLowerInvariant();

        if (shellName is "pwsh" or "powershell")
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
        var shellName = Path.GetFileNameWithoutExtension(_shell).ToLowerInvariant();
        string? script = shellName switch
        {
            "bash" or "sh" => LoadEmbeddedScript("integration.bash"),
            "pwsh" or "powershell" => LoadEmbeddedScript("integration.ps1"),
            "zsh" => LoadEmbeddedScript("integration.zsh"),
            _ => LoadEmbeddedScript("integration.bash"), // fallback to bash
        };

        if (script == null)
        {
            Log($"WARNING: No shell integration script found, falling back to no-OSC mode");
            return;
        }

        // Inject script via PTY — write it as a temporary file, source it, then delete
        // This avoids quoting issues with multi-line heredocs in different shells
        var tmpFile = shellName switch
        {
            "pwsh" or "powershell" => Path.Combine(Path.GetTempPath(), $".splashshell-integration-{Environment.ProcessId}.ps1"),
            _ => $"/tmp/.splashshell-integration-{Environment.ProcessId}.sh",
        };

        // For Windows paths, use pwsh-compatible approach
        if (OperatingSystem.IsWindows() && shellName is "pwsh" or "powershell")
        {
            tmpFile = Path.Combine(Path.GetTempPath(), $".splashshell-integration-{Environment.ProcessId}.ps1");
            // Write file directly from worker process (we share filesystem with shell)
            await File.WriteAllTextAsync(tmpFile, script, ct);

            // Re-import PSReadLine (ConPTY may disable it due to screen reader detection)
            // Then source integration script. Use \r only — pwsh interprets \n as LF
            // which opens a multi-line continuation (>> prompt). \r alone = Enter.
            var injection = shellName == "pwsh"
                ? $"Import-Module PSReadLine -ErrorAction SilentlyContinue; . '{tmpFile}'; Remove-Item '{tmpFile}' -ErrorAction SilentlyContinue\r"
                : $". '{tmpFile}'; Remove-Item '{tmpFile}' -ErrorAction SilentlyContinue\r";
            await WriteToPty(injection, ct);
        }
        else if (OperatingSystem.IsWindows() && shellName is "bash" or "sh")
        {
            // Bash on Windows — write file directly, then source via PTY.
            var windowsPath = Path.Combine(Path.GetTempPath(), $".splashshell-integration-{Environment.ProcessId}.sh");
            var scriptContent = script.Replace("\r\n", "\n");
            await File.WriteAllTextAsync(windowsPath, scriptContent, ct);

            // Determine the Unix-style path depending on whether bash is WSL or MSYS2/Git Bash.
            // WSL: C:\Users\... → /mnt/c/Users/...
            // MSYS2: C:\Users\... → /c/Users/...
            var unixPath = IsWslBash(_shell)
                ? "/mnt/" + char.ToLower(windowsPath[0]) + windowsPath[2..].Replace('\\', '/')
                : "/" + char.ToLower(windowsPath[0]) + windowsPath[2..].Replace('\\', '/');

            Log($"Integration script: {windowsPath} → {unixPath} (exists={File.Exists(windowsPath)}, wsl={IsWslBash(_shell)})");

            // bash expects \n for Enter (not \r which pwsh needs)
            await WriteToPty($"source '{unixPath}'; rm -f '{unixPath}'\n", ct);
        }
        else
        {
            // Linux/macOS
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

    private async Task WaitForReady(TimeSpan timeout, CancellationToken ct)
    {
        // Wait for PromptStart signal from ReadOutputLoop (set via _readyEvent)
        // instead of polling _ready every 100ms.
        await Task.Run(() => _readyEvent.Wait(timeout, ct), ct).ConfigureAwait(false);

        if (!_ready)
        {
            Log($"WARNING: No PromptStart received, proceeding without OSC markers");
            _ready = true;
        }
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

    private const int STD_INPUT_HANDLE = -10;
    private const int STD_OUTPUT_HANDLE = -11;
    // VT input: arrow keys become \x1b[A etc., no line buffering, no echo
    private const uint ENABLE_VIRTUAL_TERMINAL_INPUT = 0x0200;
    private const uint ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004;
    private const uint DISABLE_NEWLINE_AUTO_RETURN = 0x0008;

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

                var shellName = Path.GetFileNameWithoutExtension(_shell).ToLowerInvariant();
                // pwsh and cmd.exe understand win32-input-mode natively; only Unix shells need translation
                bool needsTranslation = shellName is not "pwsh" and not "powershell" and not "cmd";

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
            finally { tcs.TrySetResult(); }
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
                w.WriteString("shellFamily", Path.GetFileNameWithoutExtension(_shell).ToLowerInvariant());
                w.WriteString("shellPath", _shell);
                w.WriteStringOrNull("cwd", _tracker.LastKnownCwd);
                w.WriteStringOrNull("runningCommand", _tracker.RunningCommand);
                var elapsed = _tracker.RunningElapsedSeconds;
                if (elapsed.HasValue) w.WriteNumber("runningElapsedSeconds", elapsed.Value);
                else w.WriteNull("runningElapsedSeconds");
            }),
            "get_cached_output" => HandleGetCachedOutput(),
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
        var shellName = Path.GetFileNameWithoutExtension(_shell).ToLowerInvariant();
        if (shellName is "pwsh" or "powershell")
        {
            _tracker.ClearPostPrimary();
            return SerializeResponse(w => w.WriteNull("delta"));
        }

        var stableMs = request.TryGetProperty("stable_ms", out var sm) && sm.ValueKind == JsonValueKind.Number ? sm.GetInt32() : 100;
        var maxMs = request.TryGetProperty("max_ms", out var mm) && mm.ValueKind == JsonValueKind.Number ? mm.GetInt32() : 500;
        var delta = await _tracker.WaitAndDrainPostOutputAsync(stableMs, maxMs, ct);
        return SerializeResponse(w => w.WriteStringOrNull("delta", delta));
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

        // Write command to PTY
        // Shell-specific Enter: pwsh → \r, bash/zsh → \n
        var shellName = Path.GetFileNameWithoutExtension(_shell).ToLowerInvariant();
        var enter = shellName is "bash" or "sh" or "zsh" ? "\n" : "\r";

        // For pwsh: suppress PTY mirroring while PSReadLine renders prediction noise,
        // and display the command directly in the visible console.
        // OSC B (emitted by the Enter key handler in integration.ps1) re-enables
        // mirroring and starts output capture.
        if (shellName is "pwsh" or "powershell")
        {
            _mirrorVisible = false;
            _stdoutStream ??= Console.OpenStandardOutput();

            // pwsh 7+ ships a modern PSReadLine with vivid syntax highlighting
            // in the interactive prompt, so colorize the echo to match what
            // the human user would see if they'd typed the command. Windows
            // PowerShell 5.1 (powershell.exe) bundles an older PSReadLine
            // Both pwsh and powershell.exe (Windows PowerShell 5.1) get the
            // colorizer treatment so AI-echoed commands look the same as
            // what PSReadLine renders for human-typed input.
            var echoText = shellName is "pwsh" or "powershell"
                ? PwshColorizer.Colorize(command)
                : command;

            // Wipe the current line first and re-emit a synthetic prompt
            // before our echo. Between commands pwsh is idle in the
            // PSReadLine input loop, which writes prediction ghost text and
            // moves the cursor around while rendering. Without the clear,
            // our echo would land at whatever column PSReadLine left the
            // cursor at and the previous line would end up with our text
            // overlaid on the prompt — the "command overwrites the prompt"
            // glitch. \r moves to column 0, \e[2K clears the entire line,
            // then we re-emit a default-format prompt and the colorized
            // command on a clean slate.
            //
            // Crucially, the echo does NOT end with \r\n. Leaving the cursor
            // at the end of the command text mirrors what a human typing the
            // same command would see just before pressing Enter. PSReadLine's
            // AcceptLine finalize (mirrored to the visible console once
            // _mirrorVisible flips true on OSC B) then emits its own newline,
            // moving the cursor to the next line exactly once. Adding our own
            // \r\n would double up and leave a blank line between the echo
            // and the command's output.
            var cwd = _tracker.LastKnownCwd ?? _cwd;
            var synthPrompt = $"PS {cwd}> ";
            var cmdDisplay = Encoding.UTF8.GetBytes($"\r\x1b[2K{synthPrompt}{echoText}");
            _stdoutStream.Write(cmdDisplay, 0, cmdDisplay.Length);
            _stdoutStream.Flush();
        }

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

        await WriteToPty(command + enter, ct);

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
            return SerializeResponse(w =>
            {
                w.WriteString("output", "");
                w.WriteNumber("exitCode", 0);
                w.WriteNull("cwd");
                w.WriteString("duration", (timeoutMs / 1000.0).ToString("F1"));
                w.WriteBoolean("timedOut", true);
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
        var shellName = Path.GetFileNameWithoutExtension(_shell).ToLowerInvariant();
        var enter = shellName is "bash" or "sh" or "zsh" ? "\n" : "\r";
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

        return 0;
    }
}
