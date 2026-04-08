using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace ShellPilot.Services;

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
    private readonly HashSet<int> _busyPids = new();
    private readonly HashSet<string> _allocatedSubAgentIds = new();

    // Category naming
    private readonly int _categoryIndex;
    private readonly Queue<string> _nameQueue = new();
    private string[]? _fixedNameOrder;
    private readonly Dictionary<int, string> _pidToTitle = new();

    public int ProxyPid { get; } = Environment.ProcessId;

    // Shared memory for category allocation (same pattern as PowerShell.MCP)
    private static readonly string SharedMemoryFile = Path.Combine(Path.GetTempPath(), "ShellPilot.AllocatedConsoleCategories.dat");
    private const string MutexName = "ShellPilot.AllocatedConsoleCategories";
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

    public string AllocateSubAgentId()
    {
        lock (_lock)
        {
            string id;
            do
            {
                id = $"sa-{Guid.NewGuid():N}"[..11];
            } while (!_allocatedSubAgentIds.Add(id));
            return id;
        }
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
        var resolvedShell = shell ?? GetDefaultShell();
        var shellFamily = NormalizeShellFamily(resolvedShell);
        bool forceNew = !string.IsNullOrEmpty(reason);

        if (!forceNew)
        {
            var standby = await FindStandbyConsoleAsync(agentId, shellFamily);
            if (standby != null)
            {
                lock (_lock) GetOrCreateAgentState(agentId).ActivePid = standby.Value.Pid;

                // Display banner on reused console via pipe
                if (!string.IsNullOrEmpty(banner) || !string.IsNullOrEmpty(reason))
                {
                    var reusePipe = _consoles.GetValueOrDefault(standby.Value.Pid)?.PipePath;
                    if (reusePipe != null)
                        try { await SendPipeRequestAsync(reusePipe, new { type = "display_banner", banner, reason }, TimeSpan.FromSeconds(3)); } catch { }
                }

                return new StartConsoleResult("reused", standby.Value.Pid, standby.Value.DisplayName);
            }
        }

        // Launch shellpilot.exe --console mode with ConPTY.
        // Banner/reason passed as CLI args so the worker can display them before the first prompt.
        int pid = _launcher.LaunchConsoleWorker(ProxyPid, agentId, resolvedShell, cwd, banner, reason);

        var displayName = AssignConsoleName(pid);
        var pipeName = GetPipeName(agentId, pid);
        lock (_lock)
        {
            _consoles[pid] = new ConsoleInfo(pipeName, displayName, shellFamily);
            GetOrCreateAgentState(agentId).ActivePid = pid;
        }

        await WaitForPipeReadyAsync(pipeName, TimeSpan.FromSeconds(30));

        try { await SendPipeRequestAsync(pipeName, new { type = "set_title", title = displayName }, TimeSpan.FromSeconds(3)); }
        catch { /* best-effort */ }

        return new StartConsoleResult("started", pid, displayName);
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
        var requestedFamily = shell != null ? NormalizeShellFamily(shell) : null;

        int consolePid;
        string pipeName;

        lock (_lock)
        {
            consolePid = GetOrCreateAgentState(agentId).ActivePid;

            // Check if active console matches the requested shell
            if (consolePid != 0 && requestedFamily != null)
            {
                var info = _consoles.GetValueOrDefault(consolePid);
                if (info != null && !info.ShellFamily.Equals(requestedFamily, StringComparison.OrdinalIgnoreCase))
                    consolePid = 0; // Force finding/starting a matching console
            }

            pipeName = consolePid != 0
                ? _consoles.GetValueOrDefault(consolePid)?.PipePath ?? GetPipeName(agentId, consolePid)
                : "";
        }

        // No active console, or active console is wrong shell type → switch
        if (consolePid == 0 || !IsProcessAlive(consolePid))
        {
            string switchedDisplayName;

            // Try to find a standby of the right shell type
            var standby = await FindStandbyConsoleAsync(agentId, requestedFamily);
            if (standby != null)
            {
                lock (_lock) GetOrCreateAgentState(agentId).ActivePid = standby.Value.Pid;
                switchedDisplayName = standby.Value.DisplayName;
            }
            else
            {
                // No standby — auto-start a new console
                var resolvedShell = shell ?? GetDefaultShell();
                var startResult = await StartConsoleInnerAsync(resolvedShell, null, null, agentId);
                switchedDisplayName = startResult.DisplayName;
            }

            // Don't execute — cwd may differ. Let the caller re-execute.
            return new ExecuteResult
            {
                Switched = true,
                DisplayName = switchedDisplayName,
                Output = $"Switched to console {switchedDisplayName}. Pipeline NOT executed — cd to the correct directory and re-execute.",
            };
        }

        try
        {
            var response = await SendPipeRequestAsync(pipeName, new
            {
                type = "execute",
                id = Guid.NewGuid().ToString(),
                command,
                timeout = timeoutSeconds * 1000,
            }, TimeSpan.FromSeconds(timeoutSeconds + 5));

            var output = response.TryGetProperty("output", out var outputProp) ? outputProp.GetString() ?? "" : "";
            var exitCode = response.TryGetProperty("exitCode", out var exitProp) ? exitProp.GetInt32() : 0;
            var duration = response.TryGetProperty("duration", out var durProp) ? durProp.GetString() ?? "0" : "0";
            var cwdResult = response.TryGetProperty("cwd", out var cwdProp) ? cwdProp.GetString() : null;
            var displayName = _consoles.GetValueOrDefault(consolePid)?.DisplayName ?? $"#{consolePid}";

            return new ExecuteResult
            {
                Output = output,
                ExitCode = exitCode,
                Duration = duration,
                Command = command,
                DisplayName = displayName,
                Cwd = cwdResult,
            };
        }
        catch (TimeoutException)
        {
            var displayName = _consoles.GetValueOrDefault(consolePid)?.DisplayName ?? $"#{consolePid}";
            return new ExecuteResult { TimedOut = true, DisplayName = displayName, Command = command };
        }
        catch (IOException)
        {
            ClearDeadConsole(agentId, consolePid);
            var resolvedShell = shell ?? GetDefaultShell();
            var startResult = await StartConsoleInnerAsync(resolvedShell, null, null, agentId);
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
            _busyPids.Remove(consolePid);
            var state = GetOrCreateAgentState(agentId);
            if (state.ActivePid == consolePid)
                state.ActivePid = 0;
        }
    }

    // --- Pipe communication ---

    public static string GetPipeName(string agentId, int consolePid)
        => $"{PipePrefix}.{Environment.ProcessId}.{agentId}.{consolePid}";

    public static string GetPipePath(string agentId, int consolePid)
        => GetPipeName(agentId, consolePid);

    private async Task<JsonElement> SendPipeRequestAsync(string pipeName, object message, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

        await client.ConnectAsync(cts.Token);

        var json = JsonSerializer.Serialize(message);
        var msgBytes = Encoding.UTF8.GetBytes(json);
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

        return JsonSerializer.Deserialize<JsonElement>(recvBytes);
    }

    /// <summary>
    /// Display banner and/or reason text in a console's visible window.
    /// Writes directly to the worker's stdout via pipe command (shell-agnostic).
    /// </summary>
    public async Task DisplayBannerAsync(int consolePid, string? banner, string? reason)
    {
        string? pipeName;
        lock (_lock) pipeName = _consoles.GetValueOrDefault(consolePid)?.PipePath;
        if (pipeName == null) return;

        try
        {
            await SendPipeRequestAsync(pipeName, new
            {
                type = "display_banner",
                banner,
                reason
            }, TimeSpan.FromSeconds(3));
        }
        catch { /* best-effort */ }
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
                var json = JsonSerializer.Serialize(new { type = "ping" });
                var msgBytes = Encoding.UTF8.GetBytes(json);
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
    private async Task<(int Pid, string DisplayName)?> FindStandbyConsoleAsync(string agentId, string? shellFamily = null)
    {
        // 1. Try owned pipes for this proxy + agent
        var found = await TryFindInPipesAsync(EnumeratePipes(ProxyPid, agentId), shellFamily);
        if (found.HasValue) return found;

        // 2. Try unowned pipes (workers whose original proxy died)
        found = await TryFindInPipesAsync(EnumerateUnownedPipes(), shellFamily);
        return found;
    }

    private async Task<(int Pid, string DisplayName)?> TryFindInPipesAsync(IEnumerable<string> pipes, string? shellFamily = null)
    {
        foreach (var pipe in pipes)
        {
            var pid = GetPidFromPipeName(pipe);
            if (!pid.HasValue || !IsProcessAlive(pid.Value)) continue;

            try
            {
                var response = await SendPipeRequestAsync(pipe,
                    new { type = "get_status" },
                    TimeSpan.FromSeconds(3));

                var status = response.TryGetProperty("status", out var sp) ? sp.GetString() : null;
                if (status is not ("standby" or "completed")) continue;

                // Shell family filter: skip consoles that don't match the requested shell
                var workerShell = response.TryGetProperty("shellFamily", out var sf) ? sf.GetString() : null;
                if (shellFamily != null && workerShell != null &&
                    !workerShell.Equals(shellFamily, StringComparison.OrdinalIgnoreCase))
                    continue;

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
                    await SendPipeRequestAsync(pipe, new
                    {
                        type = "claim",
                        proxy_pid = ProxyPid,
                        agent_id = "default",
                        title = displayNameNew
                    }, TimeSpan.FromSeconds(3));

                    await WaitForPipeReadyAsync(newPipeName, TimeSpan.FromSeconds(5));
                }
                catch { newPipeName = pipe; }

                lock (_lock)
                {
                    _consoles[pid.Value] = new ConsoleInfo(newPipeName, displayNameNew, workerShell ?? "unknown");
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

    // --- Wait for completion ---

    /// <summary>
    /// Poll busy consoles for cached output (timed-out commands that have since completed).
    /// Called from the WaitForCompletion MCP tool (proxy side).
    /// </summary>
    public async Task<List<ExecuteResult>> WaitForCompletionAsync(int timeoutSeconds, string agentId)
    {
        var results = new List<ExecuteResult>();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(timeoutSeconds);

        while (DateTime.UtcNow < deadline)
        {
            foreach (var pipe in EnumeratePipes(ProxyPid, agentId))
            {
                var pid = GetPidFromPipeName(pipe);
                if (!pid.HasValue) continue;

                try
                {
                    // Check status
                    var statusResp = await SendPipeRequestAsync(pipe,
                        new { type = "get_status" },
                        TimeSpan.FromSeconds(3));

                    var hasCached = statusResp.TryGetProperty("hasCachedOutput", out var hc) && hc.GetBoolean();
                    if (!hasCached) continue;

                    // Retrieve cached output
                    var cachedResp = await SendPipeRequestAsync(pipe,
                        new { type = "get_cached_output" },
                        TimeSpan.FromSeconds(5));

                    var cacheStatus = cachedResp.TryGetProperty("status", out var cs) ? cs.GetString() : null;
                    if (cacheStatus != "ok") continue;

                    var displayName = _consoles.GetValueOrDefault(pid.Value)?.DisplayName ?? $"#{pid.Value}";
                    results.Add(new ExecuteResult
                    {
                        Output = cachedResp.TryGetProperty("output", out var o) ? o.GetString() ?? "" : "",
                        ExitCode = cachedResp.TryGetProperty("exitCode", out var e) ? e.GetInt32() : 0,
                        Duration = cachedResp.TryGetProperty("duration", out var d) ? d.GetString() ?? "0" : "0",
                        Command = cachedResp.TryGetProperty("command", out var c) ? c.GetString() : null,
                        DisplayName = displayName,
                        Cwd = cachedResp.TryGetProperty("cwd", out var w) ? w.GetString() : null,
                    });
                }
                catch
                {
                    // Skip unresponsive pipes
                }
            }

            if (results.Count > 0) break;
            await Task.Delay(500);
        }

        return results;
    }

    // --- Pipe enumeration ---

    /// <summary>
    /// Enumerates shellpilot Named Pipes.
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
            var envShell = Environment.GetEnvironmentVariable("SHELLPILOT_SHELL");
            if (!string.IsNullOrEmpty(envShell)) return envShell;
            return "pwsh.exe";
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

    private record ConsoleInfo(string PipePath, string DisplayName, string ShellFamily);

    /// <summary>
    /// Normalize a shell path/name to a canonical family name.
    /// "bash", "/usr/bin/bash", "C:\Windows\System32\bash.exe" → "bash"
    /// "pwsh", "pwsh.exe" → "pwsh"
    /// </summary>
    internal static string NormalizeShellFamily(string shell)
        => Path.GetFileNameWithoutExtension(shell).ToLowerInvariant();

    public record StartConsoleResult(string Status, int Pid, string DisplayName);

    public class ExecuteResult
    {
        public string Output { get; set; } = "";
        public int ExitCode { get; set; }
        public string Duration { get; set; } = "0";
        public string? Command { get; set; }
        public string? DisplayName { get; set; }
        public string? Cwd { get; set; }
        public bool Switched { get; set; }
        public bool TimedOut { get; set; }
    }
}
