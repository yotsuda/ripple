using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace ShellPilot.Services;

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
    private static readonly string LogFile = Path.Combine(Path.GetTempPath(), $"shellpilot-worker-{Environment.ProcessId}.log");
    private static void Log(string msg) { try { File.AppendAllText(LogFile, $"{DateTime.Now:HH:mm:ss.fff} {msg}\n"); } catch { } }

    private string _pipeName;
    private readonly string _unownedPipeName;
    private readonly int _proxyPid;
    private readonly string _shell;
    private readonly string _cwd;
    private IPtySession? _pty;
    private Stream? _writer;
    private readonly OscParser _parser = new();
    private readonly CommandTracker _tracker = new();
    private bool _ready;
    private volatile int _outputLength;

    /// <summary>
    /// Current console status for get_status requests.
    /// </summary>
    private string Status => _tracker.Busy ? "busy" : (_tracker.HasCachedOutput ? "completed" : "standby");

    public ConsoleWorker(string pipeName, int proxyPid, string shell, string cwd)
    {
        _pipeName = pipeName;
        _proxyPid = proxyPid;
        _shell = shell;
        _cwd = cwd;
        // Unowned pipe name is constructed lazily — uses our own PID
        _unownedPipeName = $"{ConsoleManager.PipePrefix}.{Environment.ProcessId}";
    }

    public async Task RunAsync(CancellationToken ct)
    {
        // Ensure the worker's visible console uses UTF-8 for both input and output.
        Console.InputEncoding = Encoding.UTF8;
        Console.OutputEncoding = Encoding.UTF8;

        // Prepare shell integration script BEFORE launching the shell.
        // For pwsh, we pass it via -NoExit -Command so it doesn't echo in the console.
        var commandLine = BuildCommandLine();

        // Launch shell via platform PTY (ConPTY on Windows, forkpty on Linux/macOS)
        // MSYS2/Git Bash needs the parent's environment (MSYSTEM, HOME, PATH with Git paths).
        // pwsh uses a clean environment to avoid inheriting MCP server variables.
        var shellName = Path.GetFileNameWithoutExtension(_shell).ToLowerInvariant();
        bool inheritEnv = shellName is not "pwsh" and not "powershell";
        _pty = PtyFactory.Start(commandLine, _cwd, inheritEnvironment: inheritEnv);
        _writer = _pty.InputStream;

        // Start reading PTY output on dedicated thread (feeds OscParser + CommandTracker)
        var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var readTask = ReadOutputLoop(readCts.Token);

        // Wait for shell to fully start (profile + injection via -Command).
        await WaitForOutputSettled(ct);
        // Shell-specific Enter key: pwsh needs \r, bash/zsh need \n
        var enter = shellName is "pwsh" or "powershell" ? "\r" : "\n";

        if (shellName is not "pwsh" and not "powershell")
        {
            await InjectShellIntegration(ct);
            await Task.Delay(500, ct);
            await WriteToPty(enter, ct);
        }
        else
        {
            // pwsh: injection was done via -Command, just kick to trigger prompt
            await WriteToPty(enter, ct);
        }

        // Wait for PromptStart marker from shell integration (confirms OSC pipeline is working)
        await WaitForReady(TimeSpan.FromSeconds(15), ct);
        _ready = true;

        Log($"Shell ready, pipe={_pipeName}");

        // Start forwarding user's keyboard input from visible console to ConPTY
        var inputTask = InputForwardLoop(ct);

        // Run two pipe servers in parallel:
        //   - owned pipe (SP.{proxyPid}.{agentId}.{ourPid}) for the current proxy
        //   - unowned pipe (SP.{ourPid}) for future claim by another proxy
        // Also monitor parent proxy lifetime and stop when it dies.
        var serverCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var ownedTask = RunPipeServerAsync(_pipeName, serverCts.Token);
        var unownedTask = RunPipeServerAsync(_unownedPipeName, serverCts.Token);
        var monitorTask = MonitorParentProxyAsync(serverCts.Token);

        await Task.WhenAny(ownedTask, unownedTask, monitorTask);
        serverCts.Cancel();
        await Task.WhenAll(
            ownedTask.ContinueWith(_ => { }),
            unownedTask.ContinueWith(_ => { }),
            monitorTask.ContinueWith(_ => { }));

        // Cleanup
        readCts.Cancel();
        _pty.Dispose();
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
                // Parent died — owned pipe will not be claimed again, but
                // we keep serving the unowned pipe for future claim.
                // Don't cancel — just stop monitoring.
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
                var tmpFile = Path.Combine(Path.GetTempPath(), $".shellpilot-integration-{Environment.ProcessId}.ps1");
                File.WriteAllText(tmpFile, script);
                // -NoExit keeps the shell alive after -Command completes.
                // The command imports PSReadLine + sources the integration script silently.
                var cmd = $"Import-Module PSReadLine -ErrorAction SilentlyContinue; . '{tmpFile}'; Remove-Item '{tmpFile}' -ErrorAction SilentlyContinue";
                return $"\"{_shell}\" -NoExit -Command \"{cmd}\"";
            }
        }

        // For bash/zsh, add interactive flags
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
            "pwsh" or "powershell" => Path.Combine(Path.GetTempPath(), $".shellpilot-integration-{Environment.ProcessId}.ps1"),
            _ => $"/tmp/.shellpilot-integration-{Environment.ProcessId}.sh",
        };

        // For Windows paths, use pwsh-compatible approach
        if (OperatingSystem.IsWindows() && shellName is "pwsh" or "powershell")
        {
            tmpFile = Path.Combine(Path.GetTempPath(), $".shellpilot-integration-{Environment.ProcessId}.ps1");
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
            // Git Bash / MSYS2 on Windows — write file directly, then source via PTY.
            // Heredoc via PTY is unreliable because StringBuilder.AppendLine uses \r\n
            // on Windows, which corrupts the shell script content.
            var windowsPath = Path.Combine(Path.GetTempPath(), $".shellpilot-integration-{Environment.ProcessId}.sh");
            // Write with LF-only line endings (Unix format)
            await File.WriteAllTextAsync(windowsPath, script.Replace("\r\n", "\n"), ct);
            // Convert Windows path to MSYS path: C:\Users\... → /c/Users/...
            var msysPath = "/" + char.ToLower(windowsPath[0]) + windowsPath[2..].Replace('\\', '/');
            // bash expects \n for Enter (not \r which pwsh needs)
            await WriteToPty($"source '{msysPath}'; rm -f '{msysPath}'\n", ct);
        }
        else
        {
            // Linux/macOS
            var injection = new StringBuilder();
            injection.AppendLine($"cat > {tmpFile} << 'SHELLPILOT_EOF'");
            injection.AppendLine(script.TrimEnd());
            injection.AppendLine("SHELLPILOT_EOF");
            injection.AppendLine($"source {tmpFile}; rm -f {tmpFile}");
            await WriteToPty(injection.ToString(), ct);
        }
    }

    private static string? LoadEmbeddedScript(string name)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = $"ShellPilot.ShellIntegration.{name}";
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

    private async Task WaitForReady(TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (_ready) return;
            await Task.Delay(100, ct);
        }
        // If no OSC marker arrived, proceed anyway (shell integration may not have loaded)
        Log($"WARNING: No PromptStart received, proceeding without OSC markers");
        _ready = true;
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
    // VT input: arrow keys become \x1b[A etc., no line buffering, no echo
    private const uint ENABLE_VIRTUAL_TERMINAL_INPUT = 0x0200;

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

                var charBuf = new char[256];
                while (!ct.IsCancellationRequested)
                {
                    // ReadConsoleW reads Unicode (UTF-16) — handles CJK characters correctly
                    if (!ReadConsoleW(hStdIn, charBuf, (uint)charBuf.Length, out var charsRead, IntPtr.Zero) || charsRead == 0)
                        break;

                    // Don't forward while AI is executing — avoid mixed input
                    if (_tracker.Busy) continue;

                    try
                    {
                        // Convert UTF-16 → UTF-8 for ConPTY input pipe
                        var utf8 = Encoding.UTF8.GetBytes(charBuf, 0, (int)charsRead);
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

    // --- PTY output reading ---

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
                    var result = _parser.Parse(text);

                    foreach (var evt in result.Events)
                    {
                        _tracker.HandleEvent(evt);
                        if (!_ready && evt.Type == OscParser.OscEventType.PromptStart)
                            _ready = true;
                    }

                    if (result.Cleaned.Length > 0)
                    {
                        _tracker.FeedOutput(result.Cleaned);
                        _outputLength += result.Cleaned.Length;

                        // Mirror PTY output (with OSC stripped) to the worker's
                        // visible console so the user can see what AI is doing.
                        try { Console.Out.Write(result.Cleaned); Console.Out.Flush(); } catch { }
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
                server = new NamedPipeServerStream(
                    pipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
            }
            catch (IOException)
            {
                // Pipe name in use (e.g. another worker on the same PID — should not happen)
                return;
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
        var type = request.GetProperty("type").GetString();

        return type switch
        {
            "execute" => await HandleExecuteAsync(request, ct),
            "get_status" => SerializeResponse(new { status = Status, hasCachedOutput = _tracker.HasCachedOutput }),
            "get_cached_output" => HandleGetCachedOutput(),
            "ping" => SerializeResponse(new { status = "ok" }),
            _ => SerializeResponse(new { error = $"Unknown request type: {type}" }),
        };
    }

    private async Task<JsonElement> HandleExecuteAsync(JsonElement request, CancellationToken ct)
    {
        var command = request.GetProperty("command").GetString()!;
        var timeoutMs = request.TryGetProperty("timeout", out var tp) ? tp.GetInt32() : 170_000;

        // Register command with tracker (it will resolve when OSC PromptStart arrives)
        var resultTask = _tracker.RegisterCommand(command, timeoutMs);

        // Write command to PTY
        // Shell-specific Enter: pwsh → \r, bash/zsh → \n
        var shellName = Path.GetFileNameWithoutExtension(_shell).ToLowerInvariant();
        var enter = shellName is "pwsh" or "powershell" ? "\r" : "\n";
        await WriteToPty(command + enter, ct);

        try
        {
            var result = await resultTask;
            return SerializeResponse(new
            {
                output = result.Output,
                exitCode = result.ExitCode,
                cwd = result.Cwd,
                duration = result.Duration,
                timedOut = false,
            });
        }
        catch (TimeoutException)
        {
            return SerializeResponse(new
            {
                output = "",
                exitCode = 0,
                cwd = (string?)null,
                duration = (timeoutMs / 1000.0).ToString("F1"),
                timedOut = true,
            });
        }
    }

    private JsonElement HandleGetCachedOutput()
    {
        var cached = _tracker.ConsumeCachedOutput();
        if (cached == null)
            return SerializeResponse(new { status = "no_cache" });

        return SerializeResponse(new
        {
            status = "ok",
            output = cached.Output,
            exitCode = cached.ExitCode,
            cwd = cached.Cwd,
            command = cached.Command,
            duration = cached.Duration,
        });
    }

    // --- Pipe protocol (length-prefixed JSON) ---

    private static async Task<JsonElement> ReadMessageAsync(Stream stream, CancellationToken ct)
    {
        var lenBuf = new byte[4];
        await ReadExactAsync(stream, lenBuf, ct);
        var len = BitConverter.ToInt32(lenBuf);
        var msgBuf = new byte[len];
        await ReadExactAsync(stream, msgBuf, ct);
        return JsonSerializer.Deserialize<JsonElement>(msgBuf);
    }

    private static async Task WriteMessageAsync(Stream stream, JsonElement message, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(message);
        var msgBytes = Encoding.UTF8.GetBytes(json);
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

    private static JsonElement SerializeResponse(object obj)
        => JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(obj));

    // --- Entry point for --console mode ---

    public static async Task<int> RunConsoleMode(string[] args)
    {
        string? proxyPid = null, agentId = null, shell = null, cwd = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--proxy-pid" when i + 1 < args.Length: proxyPid = args[++i]; break;
                case "--agent-id" when i + 1 < args.Length: agentId = args[++i]; break;
                case "--shell" when i + 1 < args.Length: shell = args[++i]; break;
                case "--cwd" when i + 1 < args.Length: cwd = args[++i]; break;
            }
        }

        if (proxyPid == null || agentId == null || shell == null)
        {
            Console.Error.WriteLine("Usage: shellpilot --console --proxy-pid <pid> --agent-id <id> --shell <shell> [--cwd <dir>]");
            return 1;
        }

        cwd ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // Pipe name: SP.{proxyPid}.{agentId}.{ownPid}
        var ownPid = Environment.ProcessId;
        var pipeName = $"{ConsoleManager.PipePrefix}.{proxyPid}.{agentId}.{ownPid}";

        Log($"PID={ownPid} Pipe={pipeName} Shell={shell} Cwd={cwd}");

        var worker = new ConsoleWorker(pipeName, int.Parse(proxyPid), shell, cwd);
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
