using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace SplashShell.Services;

/// <summary>
/// Manages shell console processes via Named Pipe discovery.
/// Pipe naming: SP.{proxyPid}.{agentId}.{consolePid} (owned) / SP.{consolePid} (unowned)
/// Category naming: each proxy instance gets a unique category (Animals, Gems, etc.)
/// and assigns names from that category to consoles.
/// </summary>
public class ConsoleManager
{
    public const string PipePrefix = "SP";

    private readonly ProcessLauncher _launcher;
    private readonly object _lock = new();
    private readonly SemaphoreSlim _toolLock = new(1, 1);
    private readonly Dictionary<int, ConsoleInfo> _consoles = new();
    private readonly Dictionary<string, AgentSessionState> _agentSessions = new();

    // Category naming
    private readonly int _categoryIndex;
    private readonly Queue<string> _nameQueue = new();
    private string[]? _fixedNameOrder;
    private readonly Dictionary<int, string> _pidToTitle = new();

    public int ProxyPid { get; } = Environment.ProcessId;

    // This proxy's own binary version — sent in claim requests so workers
    // can detect cross-version re-claim (a strictly newer proxy reaching an
    // older worker with potentially incompatible pipe protocol).
    public static readonly string ProxyVersion =
        (typeof(ConsoleManager).Assembly.GetName().Version ?? new Version(0, 0)).ToString(3);

    // Shared memory for category allocation (same pattern as PowerShell.MCP)
    private static readonly string SharedMemoryFile = Path.Combine(Path.GetTempPath(), "SplashShell.AllocatedConsoleCategories.dat");
    private const string MutexName = "SplashShell.AllocatedConsoleCategories";
    private const int MaxEntries = 64;
    private const int EntrySize = 8;        // 4 bytes PID + 4 bytes category index
    private const int HeaderSize = 8;       // 4 bytes magic + 4 bytes count
    private const int SharedMemorySize = HeaderSize + (MaxEntries * EntrySize);
    private const int MagicNumber = 0x53504C54; // "SPLT"

    /// <summary>
    /// Console name categories — each proxy gets a unique category.
    /// Same set as PowerShell.MCP for consistency.
    /// </summary>
    private static readonly (string Name, string[] Words)[] Categories = new[]
    {
        ("Animals",      new[] { "Cat", "Dog", "Fox", "Wolf", "Bear", "Lion", "Tiger", "Panda", "Koala", "Rabbit", "Deer", "Zebra", "Gorilla", "Horse", "Elephant" }),
        ("Zodiac",       new[] { "Aries", "Taurus", "Gemini", "Capricorn", "Leo", "Virgo", "Libra", "Scorpio", "Aquarius", "Pisces" }),
        ("Gems",         new[] { "Sapphire", "Emerald", "Diamond", "Pearl", "Opal", "Topaz", "Ruby", "Amethyst", "Jade", "Garnet", "Onyx" }),
        ("Planets",      new[] { "Venus", "Mars", "Jupiter", "Saturn", "Neptune", "Pluto", "Titan", "Europa", "Luna" }),
        ("Colors",       new[] { "Red", "Blue", "Green", "Yellow", "Cyan", "Pink", "Purple", "Brown", "Gray", "White", "Black", "Indigo" }),
        ("Flowers",      new[] { "Rose", "Lily", "Iris", "Daisy", "Lotus", "Orchid", "Tulip", "Jasmine", "Peony", "Poppy", "Magnolia", "Hibiscus", "Sunflower" }),
        ("Birds",        new[] { "Eagle", "Falcon", "Sparrow", "Robin", "Swan", "Dove", "Parrot", "Penguin", "Owl", "Flamingo", "Hawk", "Raven", "Crow" }),
        ("Trees",        new[] { "Oak", "Pine", "Maple", "Cedar", "Willow", "Birch", "Elm", "Ash", "Cypress", "Bamboo", "Sequoia" }),
        ("Mountains",    new[] { "Mt.Fuji", "Everest", "K2", "Kilimanjaro", "Mt.Olympus", "Denali", "Mt.Blanc", "Matterhorn", "Vesuvius", "Etna", "Ararat" }),
        ("Seas",         new[] { "Pacific", "Atlantic", "Arctic", "Baltic", "Caspian", "Adriatic", "Norwegian", "Arabian", "Tasman", "Caribbean", "Coral" }),
        ("Mythology",    new[] { "Zeus", "Athena", "Hermes", "Artemis", "Hera", "Hades", "Poseidon", "Demeter", "Ares", "Apollo", "Aphrodite", "Dionysus", "Prometheus", "Orpheus" }),
        ("Music",        new[] { "Jazz", "Blues", "Rock", "Soul", "Funk", "Reggae", "Pop", "Punk", "Classical", "Techno", "Disco", "Gospel" }),
        ("Weather",      new[] { "Sunny", "Cloudy", "Misty", "Sleet", "Drizzle", "Haze", "Stormy", "Foggy", "Snowy", "Frosty", "Windy", "Thunder" }),
        ("Fruits",       new[] { "Mango", "Apple", "Peach", "Grape", "Melon", "Banana", "Plum", "Lemon", "Fig", "Cherry" }),
        ("Fish",         new[] { "Salmon", "Tuna", "Goldfish", "Swordfish", "Catfish", "Trout", "Piranha", "Angelfish", "Koi", "Sardine", "Marlin" }),
    };

    public ConsoleManager(ProcessLauncher launcher)
    {
        _launcher = launcher;
        _categoryIndex = InitializeCategory();
    }

    public void Initialize()
    {
        // Category already initialized in constructor
        AppDomain.CurrentDomain.ProcessExit += (_, _) => CleanupCategory();
    }

    private class AgentSessionState
    {
        public int ActivePid { get; set; }
        public readonly HashSet<int> KnownBusyPids = new();
    }

    private AgentSessionState GetOrCreateAgentState(string agentId)
    {
        if (!_agentSessions.TryGetValue(agentId, out var state))
        {
            state = new AgentSessionState();
            _agentSessions[agentId] = state;
        }
        return state;
    }

    private void MarkPipeBusy(string agentId, int pid)
    {
        lock (_lock) GetOrCreateAgentState(agentId).KnownBusyPids.Add(pid);
    }

    private void UnmarkPipeBusy(string agentId, int pid)
    {
        lock (_lock) GetOrCreateAgentState(agentId).KnownBusyPids.Remove(pid);
    }

    private List<int> SnapshotBusyPids(string agentId)
    {
        lock (_lock) return GetOrCreateAgentState(agentId).KnownBusyPids.ToList();
    }

    public string AllocateSubAgentId()
    {
        // 8 hex chars from a v4 GUID = 32 bits of entropy. Collisions across a
        // single proxy's lifetime are vanishingly unlikely, and if one ever
        // happens the two sub-agents land on the same AgentSessionState bucket
        // — the same failure mode as if the caller passed a duplicate agent_id
        // manually. No tracking table needed.
        return $"sa-{Guid.NewGuid():N}"[..11];
    }

    /// <summary>
    /// Assigns a console name from the category and returns display name like "#12345 Sparrow".
    /// </summary>
    private string AssignConsoleName(int pid)
    {
        lock (_lock)
        {
            if (_pidToTitle.TryGetValue(pid, out var existing))
                return existing;

            if (_nameQueue.Count == 0)
                RefillNames();

            var name = _nameQueue.Dequeue();
            var title = $"#{pid} {name}";
            _pidToTitle[pid] = title;
            return title;
        }
    }

    /// <summary>
    /// Start or reuse a console. Enforces single-shell-type per session.
    /// Serialized via _toolLock to prevent concurrent state mutations.
    /// </summary>
    public async Task<StartConsoleResult> StartConsoleAsync(string? shell, string? cwd, string? reason, string agentId = "default", string? banner = null)
    {
        await _toolLock.WaitAsync();
        try { return await StartConsoleInnerAsync(shell, cwd, reason, agentId, banner); }
        finally { _toolLock.Release(); }
    }

    private async Task<StartConsoleResult> StartConsoleInnerAsync(string? shell, string? cwd, string? reason, string agentId, string? banner = null)
    {
        var rawShell = shell ?? GetDefaultShell();

        // Reject shell values that contain command-line options (e.g., "bash --login -i").
        var fileName = Path.GetFileName(rawShell);
        if (fileName.Contains(' '))
            throw new InvalidOperationException(
                $"Shell parameter must be a shell name or path, not a command line. Got: '{rawShell}'");

        // Resolve to full path via PATH search (e.g., "pwsh" → "C:\Program Files\PowerShell\7\pwsh.exe")
        var resolvedShell = ResolveShellPath(rawShell);
        var shellFamily = NormalizeShellFamily(resolvedShell);
        bool forceNew = !string.IsNullOrEmpty(reason);

        if (!forceNew)
        {
            var standby = await FindStandbyConsoleAsync(agentId, resolvedShell);
            if (standby != null)
            {
                lock (_lock) GetOrCreateAgentState(agentId).ActivePid = standby.Value.Pid;

                var reusePipe = _consoles.GetValueOrDefault(standby.Value.Pid)?.PipePath;

                // If cwd was explicitly specified, cd the reused console to it
                if (!string.IsNullOrEmpty(cwd) && reusePipe != null)
                {
                    var cdPreamble = BuildCdPreamble(shellFamily, cwd);
                    if (cdPreamble != null)
                    {
                        try
                        {
                            await SendPipeRequestAsync(reusePipe, w =>
                            {
                                w.WriteString("type", "execute");
                                w.WriteString("command", cdPreamble.TrimEnd('&', ' '));
                                w.WriteNumber("timeout", 5000);
                            }, TimeSpan.FromSeconds(8));
                            lock (_lock)
                            {
                                var info = _consoles.GetValueOrDefault(standby.Value.Pid);
                                if (info != null) info.LastAiCwd = cwd;
                            }
                        }
                        catch { /* best-effort */ }
                    }
                }

                // Display banner on reused console via pipe
                if (!string.IsNullOrEmpty(banner) || !string.IsNullOrEmpty(reason))
                {
                    if (reusePipe != null)
                        try { await SendPipeRequestAsync(reusePipe, w => { w.WriteString("type", "display_banner"); w.WriteStringOrNull("banner", banner); w.WriteStringOrNull("reason", reason); }, TimeSpan.FromSeconds(3)); } catch { }
                }

                string? reusedCwd;
                lock (_lock) reusedCwd = _consoles.GetValueOrDefault(standby.Value.Pid)?.LastAiCwd;
                return new StartConsoleResult("reused", standby.Value.Pid, standby.Value.DisplayName, shellFamily, reusedCwd);
            }
        }

        // Launch splash.exe --console mode with ConPTY.
        // Banner/reason passed as CLI args so the worker can display them before the first prompt.
        int pid = _launcher.LaunchConsoleWorker(ProxyPid, agentId, resolvedShell, cwd, banner, reason);

        var displayName = AssignConsoleName(pid);
        var pipeName = GetPipeName(agentId, pid);
        lock (_lock)
        {
            _consoles[pid] = new ConsoleInfo(pipeName, displayName, shellFamily, resolvedShell);
            GetOrCreateAgentState(agentId).ActivePid = pid;
        }

        await WaitForPipeReadyAsync(pipeName, TimeSpan.FromSeconds(30));

        // Query the worker for the actual cwd in shell-native format
        // (e.g., /mnt/c/foo for WSL bash, C:\foo for pwsh).
        var initialCwd = await QueryConsoleCwdAsync(pipeName);
        if (initialCwd != null)
        {
            lock (_lock)
            {
                var info = _consoles.GetValueOrDefault(pid);
                if (info != null) info.LastAiCwd = initialCwd;
            }
        }

        try { await SendPipeRequestAsync(pipeName, w => { w.WriteString("type", "set_title"); w.WriteString("title", displayName); }, TimeSpan.FromSeconds(3)); }
        catch { /* best-effort */ }

        return new StartConsoleResult("started", pid, displayName, shellFamily, initialCwd);
    }

    /// <summary>
    /// Execute a command on the active console via Named Pipe.
    /// Serialized via _toolLock.
    /// </summary>
    public async Task<ExecuteResult> ExecuteCommandAsync(string command, int timeoutSeconds, string agentId = "default", string? shell = null)
    {
        await _toolLock.WaitAsync();
        try { return await ExecuteCommandInnerAsync(command, timeoutSeconds, agentId, shell); }
        finally { _toolLock.Release(); }
    }

    private async Task<ExecuteResult> ExecuteCommandInnerAsync(string command, int timeoutSeconds, string agentId, string? shell)
    {
        // Resolve shell to full path for consistent matching
        var resolvedShell = shell != null ? ResolveShellPath(shell) : null;

        int initialActivePid;
        int consolePid;
        string pipeName;
        string? sourceShellFamily;

        lock (_lock)
        {
            initialActivePid = GetOrCreateAgentState(agentId).ActivePid;
            consolePid = initialActivePid;
            sourceShellFamily = _consoles.GetValueOrDefault(consolePid)?.ShellFamily;

            // Check if active console matches the requested shell (by full path)
            if (consolePid != 0 && resolvedShell != null)
            {
                var info = _consoles.GetValueOrDefault(consolePid);
                if (info != null && !info.ShellPath.Equals(resolvedShell, PathComparison))
                    consolePid = 0; // Force finding/starting a matching console
            }

            pipeName = consolePid != 0
                ? _consoles.GetValueOrDefault(consolePid)?.PipePath ?? GetPipeName(agentId, consolePid)
                : "";
        }

        // Query the active console's status: get cwd (for cd preamble) and detect busy.
        // If busy, treat it like the active console is unavailable → trigger switch.
        string? sourceCwd = null;
        bool activeBusy = false;
        if (initialActivePid != 0 && IsProcessAlive(initialActivePid))
        {
            string? sourcePipe;
            lock (_lock) sourcePipe = _consoles.GetValueOrDefault(initialActivePid)?.PipePath;
            if (sourcePipe != null)
            {
                try
                {
                    var statusResp = await SendPipeRequestAsync(sourcePipe,
                        w => w.WriteString("type", "get_status"), TimeSpan.FromSeconds(3));
                    sourceCwd = statusResp.TryGetProperty("cwd", out var cwdProp) ? cwdProp.GetString() : null;
                    var statusStr = statusResp.TryGetProperty("status", out var stProp) ? stProp.GetString() : null;
                    activeBusy = statusStr == "busy";
                }
                catch { }
            }
        }

        // If the active console is busy, force a switch to another console.
        // When the caller didn't request a specific shell, pin the search/auto-start
        // to the busy console's own shell path so we stay same-family — crossing
        // shell families here would strand the AI in a foreign cwd it doesn't
        // know how to translate (bash /mnt/c/... vs pwsh C:\...).
        if (activeBusy && consolePid == initialActivePid)
        {
            consolePid = 0;
            if (resolvedShell == null)
            {
                lock (_lock) resolvedShell = _consoles.GetValueOrDefault(initialActivePid)?.ShellPath;
            }
        }

        bool isSwitching = false;

        // No active console, or active console is wrong shell type, or busy → switch or auto-start
        if (consolePid == 0 || !IsProcessAlive(consolePid))
        {
            isSwitching = true;

            var standby = await FindStandbyConsoleAsync(agentId, resolvedShell);
            if (standby != null)
            {
                consolePid = standby.Value.Pid;
                lock (_lock)
                {
                    GetOrCreateAgentState(agentId).ActivePid = consolePid;
                    pipeName = _consoles.GetValueOrDefault(consolePid)?.PipePath ?? GetPipeName(agentId, consolePid);
                }
            }
            else
            {
                // Auto-start a new console. We pass cwd=null so the worker starts
                // in the default user profile: the source cwd is in shell-native
                // format (e.g. bash returns /mnt/c/foo) and cannot be handed to
                // Windows CreateProcess as a workingDirectory. The same-family
                // cd preamble a few lines below positions the new console correctly
                // before the user's command runs.
                var startResult = await StartConsoleInnerAsync(resolvedShell ?? GetDefaultShell(), null, null, agentId);
                consolePid = startResult.Pid;
                lock (_lock)
                    pipeName = _consoles.GetValueOrDefault(consolePid)?.PipePath ?? GetPipeName(agentId, consolePid);
            }
        }

        // Determine target shell family and check cross-shell compatibility for cd preamble
        string? targetShellFamily;
        lock (_lock) targetShellFamily = _consoles.GetValueOrDefault(consolePid)?.ShellFamily;

        bool sameShellFamily = sourceShellFamily != null && targetShellFamily != null &&
                                sourceShellFamily.Equals(targetShellFamily, StringComparison.OrdinalIgnoreCase);

        if (isSwitching && sourceCwd != null && sameShellFamily)
        {
            // Same-shell switch with a known source cwd: prepend cd preamble so
            // the user's command runs in the source cwd. Makes switching transparent.
            var cdPreamble = BuildCdPreamble(targetShellFamily!, sourceCwd);
            if (cdPreamble != null)
            {
                command = cdPreamble + command;
                lock (_lock)
                {
                    var info = _consoles.GetValueOrDefault(consolePid);
                    if (info != null) info.LastAiCwd = sourceCwd;
                }
            }
        }
        else if (isSwitching)
        {
            // Cases requiring user confirmation:
            //   - Explicit cross-shell switch by the caller (shell=X with a
            //     different active console). Path translation between bash
            //     and Windows-native shells is not attempted — the caller
            //     asked for a different shell on purpose, so we warn and let
            //     the AI re-execute with the cwd it actually wants.
            //   - Fresh start (no previous active console — AI doesn't know cwd)
            //   - Switch to standby with no source cwd
            // Involuntary cross-shell switches (active busy with no shell param)
            // no longer reach this branch — resolvedShell is pinned to the busy
            // source earlier, so the find/auto-start above stays same-family.
            var displayName = _consoles.GetValueOrDefault(consolePid)?.DisplayName ?? $"#{consolePid}";
            return new ExecuteResult
            {
                Switched = true,
                DisplayName = displayName,
                Output = $"Switched to console {displayName}. Pipeline NOT executed — cd to the correct directory and re-execute.",
            };
        }
        else
        {
            // Same console — check if user manually cd'd since the last AI command
            var currentCwd = await QueryConsoleCwdAsync(pipeName);
            string? lastAiCwd;
            ConsoleInfo? consoleInfo;
            lock (_lock)
            {
                consoleInfo = _consoles.GetValueOrDefault(consolePid);
                lastAiCwd = consoleInfo?.LastAiCwd;
            }

            if (currentCwd != null && lastAiCwd != null &&
                !currentCwd.Equals(lastAiCwd, PathComparison))
            {
                lock (_lock) { if (consoleInfo != null) consoleInfo.LastAiCwd = currentCwd; }
                var displayName = consoleInfo?.DisplayName ?? $"#{consolePid}";
                return new ExecuteResult
                {
                    Switched = true,
                    DisplayName = displayName,
                    Output = $"Console {displayName} cwd is now '{currentCwd}' (was '{lastAiCwd}'). Pipeline NOT executed — verify and re-execute.",
                };
            }
        }

        try
        {
            var response = await SendPipeRequestAsync(pipeName, w =>
            {
                w.WriteString("type", "execute");
                w.WriteString("id", Guid.NewGuid().ToString());
                w.WriteString("command", command);
                w.WriteNumber("timeout", timeoutSeconds * 1000);
            }, TimeSpan.FromSeconds(timeoutSeconds + 5));

            // Check if the worker reported a command timeout or busy
            var timedOut = response.TryGetProperty("timedOut", out var toProp) && toProp.GetBoolean();
            var busy = response.TryGetProperty("status", out var stProp) && stProp.GetString() == "busy";
            if (timedOut || busy)
            {
                var displayName = _consoles.GetValueOrDefault(consolePid)?.DisplayName ?? $"#{consolePid}";
                var shellFamily = _consoles.GetValueOrDefault(consolePid)?.ShellFamily;
                MarkPipeBusy(agentId, consolePid);
                return new ExecuteResult { TimedOut = true, DisplayName = displayName, ShellFamily = shellFamily, Command = command };
            }

            var output = response.TryGetProperty("output", out var outputProp) ? outputProp.GetString() ?? "" : "";
            var exitCode = response.TryGetProperty("exitCode", out var exitProp) ? exitProp.GetInt32() : 0;
            var duration = response.TryGetProperty("duration", out var durProp) ? durProp.GetString() ?? "0" : "0";
            var cwdResult = response.TryGetProperty("cwd", out var cwdProp) ? cwdProp.GetString() : null;
            var displayName2 = _consoles.GetValueOrDefault(consolePid)?.DisplayName ?? $"#{consolePid}";

            // Update LastAiCwd with the result cwd (the cwd the command ended at)
            lock (_lock)
            {
                var info2 = _consoles.GetValueOrDefault(consolePid);
                if (info2 != null && cwdResult != null) info2.LastAiCwd = cwdResult;
            }

            return new ExecuteResult
            {
                Output = output,
                ExitCode = exitCode,
                Duration = duration,
                Command = command,
                DisplayName = displayName2,
                ShellFamily = _consoles.GetValueOrDefault(consolePid)?.ShellFamily,
                Cwd = cwdResult,
            };
        }
        catch (TimeoutException)
        {
            // Pipe communication timeout (worker didn't respond in time)
            var displayName = _consoles.GetValueOrDefault(consolePid)?.DisplayName ?? $"#{consolePid}";
            MarkPipeBusy(agentId, consolePid);
            return new ExecuteResult { TimedOut = true, DisplayName = displayName, Command = command };
        }
        catch (OperationCanceledException)
        {
            // Pipe CancellationToken fired
            var displayName = _consoles.GetValueOrDefault(consolePid)?.DisplayName ?? $"#{consolePid}";
            MarkPipeBusy(agentId, consolePid);
            return new ExecuteResult { TimedOut = true, DisplayName = displayName, Command = command };
        }
        catch (IOException)
        {
            ClearDeadConsole(agentId, consolePid);
            var startResult = await StartConsoleInnerAsync(shell ?? GetDefaultShell(), null, null, agentId);
            return new ExecuteResult
            {
                Switched = true,
                DisplayName = startResult.DisplayName,
                Output = $"Previous console died. Switched to {startResult.DisplayName}. Pipeline NOT executed — re-execute.",
            };
        }
    }

    /// <summary>
    /// Remove a dead console's tracking info (process gone or pipe broken).
    /// </summary>
    private void ClearDeadConsole(string agentId, int consolePid)
    {
        lock (_lock)
        {
            _consoles.Remove(consolePid);
            _pidToTitle.Remove(consolePid);
            var state = GetOrCreateAgentState(agentId);
            state.KnownBusyPids.Remove(consolePid);
            if (state.ActivePid == consolePid)
                state.ActivePid = 0;
        }
    }

    // --- Pipe communication ---

    public static string GetPipeName(string agentId, int consolePid)
        => $"{PipePrefix}.{Environment.ProcessId}.{agentId}.{consolePid}";

    private async Task<JsonElement> SendPipeRequestAsync(string pipeName, Action<Utf8JsonWriter> writeBody, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

        await client.ConnectAsync(cts.Token);

        var msgBytes = PipeJson.BuildObjectBytes(writeBody);
        var lenBytes = BitConverter.GetBytes(msgBytes.Length);

        await client.WriteAsync(lenBytes, cts.Token);
        await client.WriteAsync(msgBytes, cts.Token);
        await client.FlushAsync(cts.Token);

        // Read response
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

    // --- Pipe readiness ---

    private static async Task WaitForPipeReadyAsync(string pipeName, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await client.ConnectAsync(cts.Token);

                // Send a ping to verify the worker is fully ready
                var msgBytes = PipeJson.BuildObjectBytes(w => w.WriteString("type", "ping"));
                var lenBytes = BitConverter.GetBytes(msgBytes.Length);
                await client.WriteAsync(lenBytes);
                await client.WriteAsync(msgBytes);
                await client.FlushAsync();

                // Read response
                var recvLenBytes = new byte[4];
                await ReadExactAsync(client, recvLenBytes, CancellationToken.None);
                // If we got here, pipe is ready
                return;
            }
            catch
            {
                await Task.Delay(300);
            }
        }
        throw new TimeoutException($"Console worker pipe '{pipeName}' did not become ready within {timeout.TotalSeconds}s");
    }

    // --- Discovery ---

    /// <summary>
    /// Find a standby console by enumerating pipes and querying get_status.
    /// Checks owned pipes for this agent first, then unowned pipes (orphaned by previous proxies).
    /// </summary>
    private async Task<(int Pid, string DisplayName)?> FindStandbyConsoleAsync(string agentId, string? shellPath = null)
    {
        // 1. Try owned pipes for this proxy + agent
        var found = await TryFindInPipesAsync(EnumeratePipes(ProxyPid, agentId), shellPath);
        if (found.HasValue) return found;

        // 2. Try unowned pipes (workers whose original proxy died)
        found = await TryFindInPipesAsync(EnumerateUnownedPipes(), shellPath);
        return found;
    }

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private async Task<(int Pid, string DisplayName)?> TryFindInPipesAsync(IEnumerable<string> pipes, string? shellPath = null)
    {
        foreach (var pipe in pipes)
        {
            var pid = GetPidFromPipeName(pipe);
            if (!pid.HasValue || !IsProcessAlive(pid.Value)) continue;

            try
            {
                var response = await SendPipeRequestAsync(pipe,
                    w => w.WriteString("type", "get_status"),
                    TimeSpan.FromSeconds(3));

                var status = response.TryGetProperty("status", out var sp) ? sp.GetString() : null;
                if (status is not ("standby" or "completed")) continue;

                // Shell path filter: match by full path (tracked consoles) or family (unowned)
                if (shellPath != null)
                {
                    bool alreadyTrackedForFilter;
                    ConsoleInfo? infoForFilter;
                    lock (_lock)
                    {
                        alreadyTrackedForFilter = _consoles.TryGetValue(pid.Value, out infoForFilter);
                    }

                    if (alreadyTrackedForFilter && infoForFilter != null)
                    {
                        // Tracked: match by full path
                        if (!infoForFilter.ShellPath.Equals(shellPath, PathComparison))
                            continue;
                    }
                    else
                    {
                        // Unowned: try full path first, fall back to family
                        var workerPath = response.TryGetProperty("shellPath", out var spProp) ? spProp.GetString() : null;
                        if (workerPath != null && workerPath.Length > 0)
                        {
                            if (!workerPath.Equals(shellPath, PathComparison))
                                continue;
                        }
                        else
                        {
                            var workerShell = response.TryGetProperty("shellFamily", out var sf) ? sf.GetString() : null;
                            var requestedFamily = NormalizeShellFamily(shellPath);
                            if (workerShell != null && !workerShell.Equals(requestedFamily, StringComparison.OrdinalIgnoreCase))
                                continue;
                        }
                    }
                }

                // Already tracked by this proxy — just return it (no re-claim needed)
                bool alreadyTracked;
                lock (_lock) { alreadyTracked = _consoles.ContainsKey(pid.Value); }

                if (alreadyTracked)
                {
                    var displayName = _consoles[pid.Value].DisplayName;
                    return (pid.Value, displayName);
                }

                // Unowned console — claim it
                var displayNameNew = AssignConsoleName(pid.Value);
                var newPipeName = GetPipeName("default", pid.Value);
                try
                {
                    var claimResponse = await SendPipeRequestAsync(pipe, w =>
                    {
                        w.WriteString("type", "claim");
                        w.WriteNumber("proxy_pid", ProxyPid);
                        w.WriteString("proxy_version", ProxyVersion);
                        w.WriteString("agent_id", "default");
                        w.WriteString("title", displayNameNew);
                    }, TimeSpan.FromSeconds(3));

                    // Worker refused claim because our proxy is strictly newer than it
                    // (pipe protocol may be incompatible). Skip this orphan — the worker
                    // has marked itself obsolete and stopped serving pipes, but its shell
                    // is still alive for the human user.
                    if (claimResponse.TryGetProperty("status", out var claimStatus)
                        && claimStatus.GetString() == "obsolete")
                    {
                        continue;
                    }

                    await WaitForPipeReadyAsync(newPipeName, TimeSpan.FromSeconds(5));
                }
                catch { newPipeName = pipe; }

                lock (_lock)
                {
                    var claimShellFamily = response.TryGetProperty("shellFamily", out var sfClaim) ? sfClaim.GetString() ?? "unknown" : "unknown";
                    var claimShellPath = response.TryGetProperty("shellPath", out var spClaim) ? spClaim.GetString() ?? "" : "";
                    _consoles[pid.Value] = new ConsoleInfo(newPipeName, displayNameNew, claimShellFamily, claimShellPath);
                }

                return (pid.Value, displayNameNew);
            }
            catch
            {
                // Pipe dead or unresponsive — skip
            }
        }
        return null;
    }

    // --- Cached output collection ---

    /// <summary>
    /// Collect cached outputs from all owned consoles (single scan, no polling).
    /// Called from every MCP tool to drain completed background commands.
    /// </summary>
    /// <summary>
    /// Build a shell-specific "cd 'path' && " preamble that can be prepended to a command.
    /// Returns null if the shell family is not supported.
    /// </summary>
    private static string? BuildCdPreamble(string shellFamily, string cwd)
    {
        return shellFamily.ToLowerInvariant() switch
        {
            "bash" or "sh" or "zsh" => $"cd '{cwd.Replace("'", "'\\''")}' && ",
            "pwsh" or "powershell" => $"Set-Location '{cwd.Replace("'", "''")}'; ",
            "cmd" => $"cd /d \"{cwd.Replace("\"", "\"\"")}\" && ",
            _ => null,
        };
    }

    /// <summary>
    /// Query a console's current cwd via get_status pipe command.
    /// Returns null if the query fails or the worker doesn't have a tracked cwd yet.
    /// </summary>
    private async Task<string?> QueryConsoleCwdAsync(string pipeName)
    {
        try
        {
            var resp = await SendPipeRequestAsync(pipeName,
                w => w.WriteString("type", "get_status"),
                TimeSpan.FromSeconds(3));
            return resp.TryGetProperty("cwd", out var cwdProp) ? cwdProp.GetString() : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Detect consoles that have been closed since the last check.
    /// Removes them from _consoles and returns their display names + shell families.
    /// </summary>
    public List<(string DisplayName, string ShellFamily)> DetectClosedConsoles(string agentId)
    {
        var closed = new List<(string, string)>();
        lock (_lock)
        {
            var deadPids = _consoles
                .Where(kv => !IsProcessAlive(kv.Key))
                .Select(kv => kv.Key)
                .ToList();

            foreach (var pid in deadPids)
            {
                var info = _consoles[pid];
                closed.Add((info.DisplayName, info.ShellFamily));
                _consoles.Remove(pid);
                _pidToTitle.Remove(pid);
                var state = GetOrCreateAgentState(agentId);
                state.KnownBusyPids.Remove(pid);
                if (state.ActivePid == pid)
                    state.ActivePid = 0;
            }
        }
        return closed;
    }

    public async Task<List<ExecuteResult>> CollectCachedOutputsAsync(string agentId)
    {
        var results = new List<ExecuteResult>();

        foreach (var pipe in EnumeratePipes(ProxyPid, agentId))
        {
            var pid = GetPidFromPipeName(pipe);
            if (!pid.HasValue) continue;

            try
            {
                var statusResp = await SendPipeRequestAsync(pipe,
                    w => w.WriteString("type", "get_status"),
                    TimeSpan.FromSeconds(3));

                var hasCached = statusResp.TryGetProperty("hasCachedOutput", out var hc) && hc.GetBoolean();
                if (!hasCached) continue;

                var cachedResp = await SendPipeRequestAsync(pipe,
                    w => w.WriteString("type", "get_cached_output"),
                    TimeSpan.FromSeconds(5));

                var cacheStatus = cachedResp.TryGetProperty("status", out var cs) ? cs.GetString() : null;
                if (cacheStatus != "ok") continue;

                var consoleInfo = _consoles.GetValueOrDefault(pid.Value);
                var displayName = consoleInfo?.DisplayName ?? $"#{pid.Value}";
                results.Add(new ExecuteResult
                {
                    Output = cachedResp.TryGetProperty("output", out var o) ? o.GetString() ?? "" : "",
                    ExitCode = cachedResp.TryGetProperty("exitCode", out var e) ? e.GetInt32() : 0,
                    Duration = cachedResp.TryGetProperty("duration", out var d) ? d.GetString() ?? "0" : "0",
                    Command = cachedResp.TryGetProperty("command", out var c) ? c.GetString() : null,
                    DisplayName = displayName,
                    ShellFamily = consoleInfo?.ShellFamily,
                    Cwd = cachedResp.TryGetProperty("cwd", out var w) ? w.GetString() : null,
                });
                UnmarkPipeBusy(agentId, pid.Value);
            }
            catch { }
        }

        return results;
    }

    // --- Wait for completion ---

    /// <summary>
    /// Result of a wait_for_completion call. Distinguishes three states so the
    /// tool can give the AI an actionable response:
    ///   - HadNoBusyPids=true: nothing was running when the wait started → the
    ///     AI should not keep calling wait_for_completion; there is nothing to wait for.
    ///   - Completed has entries: one or more busy commands finished during the wait.
    ///   - StillBusy has entries: wait timed out before these consoles finished;
    ///     the AI can call wait_for_completion again to continue waiting.
    /// </summary>
    public record WaitForCompletionResult(
        List<ExecuteResult> Completed,
        List<(int Pid, string DisplayName, string? ShellFamily)> StillBusy,
        bool HadNoBusyPids);

    /// <summary>
    /// Wait for any commands this agent left running (execute_command returning
    /// TimedOut) to finish, and drain their cached output. The set of "still
    /// running" consoles is the KnownBusyPids tracked on the agent session.
    ///
    /// Contract:
    ///   - If KnownBusyPids is empty on entry → return HadNoBusyPids=true
    ///     immediately. The AI should report "nothing running" rather than loop.
    ///   - Otherwise poll only those pipes until each one either produces cached
    ///     output (completed/timed out from worker side) or its process dies
    ///     (closed externally). Unmark busy on drain.
    ///   - On overall timeout, return whatever we drained plus the set of pids
    ///     that are still busy so the tool can report them.
    /// </summary>
    public async Task<WaitForCompletionResult> WaitForCompletionAsync(int timeoutSeconds, string agentId)
    {
        var busyPids = SnapshotBusyPids(agentId);

        // Drop any pids whose process is already dead — a console closed while
        // still flagged busy counts as "not running anymore", and we don't want
        // to pretend there's something to wait for.
        for (int i = busyPids.Count - 1; i >= 0; i--)
        {
            if (!IsProcessAlive(busyPids[i]))
            {
                UnmarkPipeBusy(agentId, busyPids[i]);
                busyPids.RemoveAt(i);
            }
        }

        if (busyPids.Count == 0)
        {
            return new WaitForCompletionResult(
                new List<ExecuteResult>(),
                new List<(int, string, string?)>(),
                HadNoBusyPids: true);
        }

        var completed = new List<ExecuteResult>();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(timeoutSeconds);

        while (busyPids.Count > 0 && DateTime.UtcNow < deadline)
        {
            for (int i = busyPids.Count - 1; i >= 0; i--)
            {
                var pid = busyPids[i];

                // Console process died while busy — stop waiting for it, and
                // surface it in the result so the tool can show a notification.
                if (!IsProcessAlive(pid))
                {
                    var info = _consoles.GetValueOrDefault(pid);
                    completed.Add(new ExecuteResult
                    {
                        DisplayName = info?.DisplayName ?? $"#{pid}",
                        ShellFamily = info?.ShellFamily,
                        Output = "(console closed before command completed)",
                        ExitCode = -1,
                    });
                    UnmarkPipeBusy(agentId, pid);
                    busyPids.RemoveAt(i);
                    continue;
                }

                string? pipeName;
                lock (_lock) pipeName = _consoles.GetValueOrDefault(pid)?.PipePath;
                if (pipeName == null)
                {
                    UnmarkPipeBusy(agentId, pid);
                    busyPids.RemoveAt(i);
                    continue;
                }

                try
                {
                    var statusResp = await SendPipeRequestAsync(pipeName,
                        w => w.WriteString("type", "get_status"),
                        TimeSpan.FromSeconds(3));

                    var hasCached = statusResp.TryGetProperty("hasCachedOutput", out var hc) && hc.GetBoolean();
                    if (!hasCached) continue;

                    var cachedResp = await SendPipeRequestAsync(pipeName,
                        w => w.WriteString("type", "get_cached_output"),
                        TimeSpan.FromSeconds(5));

                    var cacheStatus = cachedResp.TryGetProperty("status", out var cs) ? cs.GetString() : null;
                    if (cacheStatus != "ok") continue;

                    var info2 = _consoles.GetValueOrDefault(pid);
                    completed.Add(new ExecuteResult
                    {
                        Output = cachedResp.TryGetProperty("output", out var o) ? o.GetString() ?? "" : "",
                        ExitCode = cachedResp.TryGetProperty("exitCode", out var e) ? e.GetInt32() : 0,
                        Duration = cachedResp.TryGetProperty("duration", out var d) ? d.GetString() ?? "0" : "0",
                        Command = cachedResp.TryGetProperty("command", out var c) ? c.GetString() : null,
                        DisplayName = info2?.DisplayName ?? $"#{pid}",
                        ShellFamily = info2?.ShellFamily,
                        Cwd = cachedResp.TryGetProperty("cwd", out var w) ? w.GetString() : null,
                    });
                    UnmarkPipeBusy(agentId, pid);
                    busyPids.RemoveAt(i);
                }
                catch
                {
                    // Transient pipe error — keep the pid in the busy list and
                    // retry on the next poll tick.
                }
            }

            if (busyPids.Count == 0) break;
            await Task.Delay(300);
        }

        var stillBusy = busyPids
            .Select(pid =>
            {
                var info = _consoles.GetValueOrDefault(pid);
                return (pid, info?.DisplayName ?? $"#{pid}", info?.ShellFamily);
            })
            .ToList();

        return new WaitForCompletionResult(completed, stillBusy, HadNoBusyPids: false);
    }

    // --- Pipe enumeration ---

    /// <summary>
    /// Enumerates splashshell Named Pipes.
    /// Windows: \\.\pipe\SP.*
    /// Linux/macOS: /tmp/CoreFxPipe_SP.*
    /// </summary>
    public IEnumerable<string> EnumeratePipes(int? proxyPid = null, string? agentId = null)
    {
        bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        string filterPattern;
        if (proxyPid.HasValue && agentId != null)
            filterPattern = $"{PipePrefix}.{proxyPid.Value}.{agentId}.*";
        else if (proxyPid.HasValue)
            filterPattern = $"{PipePrefix}.{proxyPid.Value}.*";
        else
            filterPattern = $"{PipePrefix}*";

        IEnumerable<string> paths;
        try
        {
            if (isWindows)
            {
                paths = Directory.EnumerateFiles(@"\\.\pipe\", filterPattern);
            }
            else
            {
                var directories = new List<string> { "/tmp" };
                var tmpDir = Environment.GetEnvironmentVariable("TMPDIR");
                if (!string.IsNullOrEmpty(tmpDir) && tmpDir != "/tmp" && tmpDir != "/tmp/")
                    directories.Add(tmpDir.TrimEnd('/'));

                paths = directories
                    .Where(Directory.Exists)
                    .SelectMany(dir =>
                    {
                        try { return Directory.EnumerateFiles(dir, $"CoreFxPipe_{filterPattern}"); }
                        catch { return Enumerable.Empty<string>(); }
                    });
            }
        }
        catch
        {
            yield break;
        }

        foreach (var path in paths)
        {
            var fileName = Path.GetFileName(path);
            yield return isWindows ? fileName : fileName["CoreFxPipe_".Length..];
        }
    }

    /// <summary>
    /// Enumerate unowned pipes — those whose original proxy has exited.
    /// Format: SP.{consolePid} (2 segments) vs owned SP.{proxyPid}.{agentId}.{consolePid} (4 segments).
    /// </summary>
    public IEnumerable<string> EnumerateUnownedPipes()
    {
        foreach (var pipe in EnumeratePipes())
        {
            var segments = pipe.Split('.');
            if (segments.Length == 2 && int.TryParse(segments[1], out _))
                yield return pipe;
        }
    }

    /// <summary>
    /// Extracts console PID from pipe name (last segment).
    /// </summary>
    public static int? GetPidFromPipeName(string pipeName)
    {
        var parts = pipeName.Split('.');
        if (parts.Length > 0 && int.TryParse(parts[^1], out var pid))
            return pid;
        return null;
    }

    // --- Helpers ---

    private static string GetDefaultShell()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Prefer pwsh (PowerShell 7); fall back to Windows PowerShell 5.1 if absent.
            var pwshResolved = ResolveShellPath("pwsh.exe");
            if (File.Exists(pwshResolved)) return "pwsh.exe";
            return "powershell.exe";
        }
        return Environment.GetEnvironmentVariable("SHELL") ?? "bash";
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return true;
        }
        catch { return false; }
    }

    // --- Category naming ---

    private void RefillNames()
    {
        if (_fixedNameOrder == null)
        {
            _fixedNameOrder = Categories[_categoryIndex].Words.ToArray();
            for (int i = _fixedNameOrder.Length - 1; i > 0; i--)
            {
                int j = Random.Shared.Next(i + 1);
                (_fixedNameOrder[i], _fixedNameOrder[j]) = (_fixedNameOrder[j], _fixedNameOrder[i]);
            }
        }
        foreach (var n in _fixedNameOrder) _nameQueue.Enqueue(n);
    }

    private int InitializeCategory()
    {
        using var mutex = new Mutex(false, MutexName, out _);
        if (!mutex.WaitOne(TimeSpan.FromSeconds(10)))
            throw new TimeoutException("Could not acquire shared memory mutex for category initialization");

        try
        {
            using var mmf = MemoryMappedFile.CreateFromFile(SharedMemoryFile, FileMode.OpenOrCreate, null, SharedMemorySize);
            using var accessor = mmf.CreateViewAccessor();

            int magic = accessor.ReadInt32(0);
            int count = accessor.ReadInt32(4);

            if (magic != MagicNumber)
            {
                accessor.Write(0, MagicNumber);
                count = 0;
            }

            var validEntries = new List<(int pid, int category)>();
            var usedIndices = new HashSet<int>();

            for (int i = 0; i < count && i < MaxEntries; i++)
            {
                int offset = HeaderSize + (i * EntrySize);
                int pid = accessor.ReadInt32(offset);
                int category = accessor.ReadInt32(offset + 4);

                if (IsProcessAlive(pid))
                {
                    usedIndices.Add(category);
                    validEntries.Add((pid, category));
                }
            }

            var available = Enumerable.Range(0, Categories.Length).Where(i => !usedIndices.Contains(i)).ToList();
            int categoryIndex = available.Count > 0
                ? available[Random.Shared.Next(available.Count)]
                : Random.Shared.Next(Categories.Length);

            validEntries.Add((ProxyPid, categoryIndex));

            accessor.Write(4, validEntries.Count);
            for (int i = 0; i < validEntries.Count; i++)
            {
                int offset = HeaderSize + (i * EntrySize);
                accessor.Write(offset, validEntries[i].pid);
                accessor.Write(offset + 4, validEntries[i].category);
            }

            return categoryIndex;
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    private void CleanupCategory()
    {
        try
        {
            using var mutex = new Mutex(false, MutexName, out _);
            if (!mutex.WaitOne(TimeSpan.FromSeconds(5))) return;

            try
            {
                using var mmf = MemoryMappedFile.CreateFromFile(SharedMemoryFile, FileMode.OpenOrCreate, null, SharedMemorySize);
                using var accessor = mmf.CreateViewAccessor();

                int magic = accessor.ReadInt32(0);
                int count = accessor.ReadInt32(4);
                if (magic != MagicNumber) return;

                var validEntries = new List<(int pid, int category)>();
                for (int i = 0; i < count && i < MaxEntries; i++)
                {
                    int offset = HeaderSize + (i * EntrySize);
                    int pid = accessor.ReadInt32(offset);
                    int category = accessor.ReadInt32(offset + 4);
                    if (pid != ProxyPid && IsProcessAlive(pid))
                        validEntries.Add((pid, category));
                }

                accessor.Write(4, validEntries.Count);
                for (int i = 0; i < validEntries.Count; i++)
                {
                    int offset = HeaderSize + (i * EntrySize);
                    accessor.Write(offset, validEntries[i].pid);
                    accessor.Write(offset + 4, validEntries[i].category);
                }
            }
            finally
            {
                try { mutex.ReleaseMutex(); } catch { }
            }
        }
        catch { }
    }

    // --- Types ---

    private record ConsoleInfo(string PipePath, string DisplayName, string ShellFamily, string ShellPath)
    {
        // Cwd as of the most recent AI command. Used to detect manual user cd
        // and to skip the "NOT executed" warning when cwd is consistent.
        public string? LastAiCwd { get; set; }
    }

    /// <summary>
    /// Normalize a shell path/name to a canonical family name (for display only).
    /// "bash", "/usr/bin/bash", "C:\Windows\System32\bash.exe" → "bash"
    /// "pwsh", "pwsh.exe" → "pwsh"
    /// </summary>
    internal static string NormalizeShellFamily(string shell)
        => Path.GetFileNameWithoutExtension(shell).ToLowerInvariant();

    /// <summary>
    /// Resolve a shell name to its full path. If already rooted, returns as-is.
    /// Otherwise searches PATH directories (with PATHEXT on Windows).
    /// Returns the original string if resolution fails (let CreateProcess handle it).
    /// </summary>
    internal static string ResolveShellPath(string shell)
    {
        if (Path.IsPathRooted(shell))
            return Path.GetFullPath(shell);

        // Use the system-registered PATH (registry), not the current process PATH.
        // The worker is launched with CreateEnvironmentBlock(bInherit=false) which
        // constructs PATH from registry, so we must resolve against the same source.
        var pathDirs = GetRegistryPath();

        // On Windows, try extensions from PATHEXT registry value
        var extensions = OperatingSystem.IsWindows()
            ? GetRegistryPathExt()
            : [""];

        var hasExtension = Path.HasExtension(shell);

        foreach (var dir in pathDirs)
        {
            if (hasExtension)
            {
                var fullPath = Path.Combine(dir, shell);
                if (File.Exists(fullPath))
                    return Path.GetFullPath(fullPath);
            }

            foreach (var ext in extensions)
            {
                var fullPath = Path.Combine(dir, shell + ext);
                if (File.Exists(fullPath))
                    return Path.GetFullPath(fullPath);
            }
        }

        // Resolution failed — return as-is
        return shell;
    }

    /// <summary>
    /// Read PATH from Windows registry (System + User), matching what
    /// CreateEnvironmentBlock produces for child processes.
    /// Falls back to Environment.GetEnvironmentVariable on non-Windows.
    /// </summary>
    private static string[] GetRegistryPath()
    {
        if (!OperatingSystem.IsWindows())
            return Environment.GetEnvironmentVariable("PATH")
                ?.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries) ?? [];

        try
        {
            var systemPath = Microsoft.Win32.Registry.GetValue(
                @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Environment",
                "Path", "") as string ?? "";
            var userPath = Microsoft.Win32.Registry.GetValue(
                @"HKEY_CURRENT_USER\Environment",
                "Path", "") as string ?? "";

            return $"{systemPath};{userPath}"
                .Split(';', StringSplitOptions.RemoveEmptyEntries);
        }
        catch
        {
            return Environment.GetEnvironmentVariable("PATH")
                ?.Split(';', StringSplitOptions.RemoveEmptyEntries) ?? [];
        }
    }

    /// <summary>
    /// Read PATHEXT from registry (System environment).
    /// </summary>
    private static string[] GetRegistryPathExt()
    {
        try
        {
            var pathExt = Microsoft.Win32.Registry.GetValue(
                @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Environment",
                "PATHEXT", "") as string ?? "";
            var result = pathExt.Split(';', StringSplitOptions.RemoveEmptyEntries);
            return result.Length > 0 ? result : [".exe", ".cmd", ".bat"];
        }
        catch
        {
            return [".exe", ".cmd", ".bat"];
        }
    }

    public record StartConsoleResult(string Status, int Pid, string DisplayName, string? ShellFamily = null, string? Cwd = null);

    public class ExecuteResult
    {
        public string Output { get; set; } = "";
        public int ExitCode { get; set; }
        public string Duration { get; set; } = "0";
        public string? Command { get; set; }
        public string? DisplayName { get; set; }
        public string? ShellFamily { get; set; }
        public string? Cwd { get; set; }
        public bool Switched { get; set; }
        public bool TimedOut { get; set; }
    }
}
