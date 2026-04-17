using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Ripple.Services.Adapters;

namespace Ripple.Services;

/// <summary>
/// Manages shell console processes via Named Pipe discovery.
/// Pipe naming: RP.{proxyPid}.{agentId}.{consolePid} (owned) / RP.{consolePid} (unowned)
/// Category naming: each proxy instance gets a unique category (Animals, Gems, etc.)
/// and assigns names from that category to consoles.
/// </summary>
public class ConsoleManager
{
    public const string PipePrefix = "RP";

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
    private static readonly string SharedMemoryFile = Path.Combine(Path.GetTempPath(), "Ripple.AllocatedConsoleCategories.dat");
    private const string MutexName = "Ripple.AllocatedConsoleCategories";
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

        // Snapshot of the most recently-active console's state, captured
        // the moment the console stopped being available (shell exited,
        // pipe broken, window closed). Lets the next execute_command spin
        // up a fresh same-family console and cd to where the AI was last
        // working, instead of making the AI verify-and-retry from scratch.
        // Consumed by PlanExecutionAsync on the following call and then
        // cleared.
        public string? LastActiveCwd;
        public string? LastActiveShellPath;
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

    /// <summary>
    /// If the given pid is the agent's currently-active console, snapshot
    /// its last-known cwd and shell path into the agent session so the
    /// next execute_command can seamlessly auto-start a same-family
    /// replacement at the same cwd. No-op for non-active consoles and for
    /// consoles not in the tracking table. Call this BEFORE ClearDeadConsole
    /// so the ConsoleInfo is still readable.
    /// </summary>
    private void RememberClosedActive(string agentId, int pid)
    {
        lock (_lock)
        {
            var state = GetOrCreateAgentState(agentId);
            if (state.ActivePid != pid) return;
            var info = _consoles.GetValueOrDefault(pid);
            if (info == null) return;
            state.LastActiveCwd = info.LastAiCwd;
            state.LastActiveShellPath = info.ShellPath;
        }
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

                // Reposition the reused standby to the target cwd. An
                // explicit cwd wins; otherwise default to the user's home
                // directory so an unspecified start_console acts like a
                // fresh session. Skip the cd if the console is already at
                // the target directory — avoids unnecessary noise in the
                // terminal when the standby happens to be at home already.
                var targetCwd = !string.IsNullOrEmpty(cwd)
                    ? cwd
                    : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (reusePipe != null)
                {
                    // Query current cwd to decide whether cd is needed.
                    var currentCwd = await QueryConsoleCwdAsync(reusePipe);
                    var needsCd = currentCwd == null
                        || !string.Equals(
                            Path.GetFullPath(currentCwd), Path.GetFullPath(targetCwd),
                            StringComparison.OrdinalIgnoreCase);

                    if (needsCd)
                    {
                        var cdPreamble = BuildCdPreamble(shellFamily, targetCwd);
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
                            }
                            catch { /* best-effort */ }
                        }
                    }
                    UpdateConsoleInfo(standby.Value.Pid, info => info.LastAiCwd = targetCwd);
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

        // Launch ripple.exe --console mode with ConPTY.
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
            UpdateConsoleInfo(pid, info => info.LastAiCwd = initialCwd);

        try { await SendPipeRequestAsync(pipeName, w => { w.WriteString("type", "set_title"); w.WriteString("title", displayName); }, TimeSpan.FromSeconds(3)); }
        catch { /* best-effort */ }

        return new StartConsoleResult("started", pid, displayName, shellFamily, initialCwd);
    }

    /// The hard ceiling a single execute_command can spend inside the
    /// MCP tool call, in seconds. Just under the 180s (3-minute) ceiling
    /// the MCP protocol imposes on tool-call response latency. Kept in
    /// sync with CommandTracker.PreemptiveTimeoutMs so the worker's
    /// internal timer always fires before the pipe wait gives up.
    public const int MaxExecuteTimeoutSeconds = 170;

    /// <summary>
    /// Execute a command on the active console via Named Pipe.
    /// Serialized via _toolLock.
    /// </summary>
    public async Task<ExecuteResult> ExecuteCommandAsync(string command, int timeoutSeconds, string agentId = "default", string? shell = null)
    {
        // Cap the caller-supplied timeout at the MCP ceiling so the
        // pipe wait + worker timer both unwind within the 3-minute
        // tool-call window. Callers that ask for longer get a clean
        // preemptive-timeout response at 170s and can keep polling
        // via wait_for_completion. 0 is the "interactive" sentinel —
        // ripple flips to cache mode as soon as the pipeline is on the
        // PTY so execute_command returns immediately and the drain
        // wrapper salvages the result on the next tool call.
        timeoutSeconds = Math.Clamp(timeoutSeconds, 0, MaxExecuteTimeoutSeconds);
        await _toolLock.WaitAsync();
        try { return await ExecuteCommandInnerAsync(command, timeoutSeconds, agentId, shell); }
        finally { _toolLock.Release(); }
    }

    private async Task<ExecuteResult> ExecuteCommandInnerAsync(string command, int timeoutSeconds, string agentId, string? shell)
    {
        var plan = await PlanExecutionAsync(command, agentId, shell);
        if (plan.EarlyResult != null) return plan.EarlyResult;

        return await ExecutePlannedCommandAsync(
            consolePid: plan.ConsolePid,
            pipeName: plan.PipeName,
            command: command,
            cdCommand: plan.CdCommand,
            expectedCwdAfterCd: plan.PreambleCwd,
            timeoutSeconds: timeoutSeconds,
            agentId: agentId,
            shell: shell,
            routingNotice: plan.RoutingNotice);
    }

    /// <summary>
    /// Output of the routing phase. Either a concrete target console to run
    /// the command on (ConsolePid + PipeName + optional CdCommand that must
    /// be executed first, possibly with a RoutingNotice that should be
    /// surfaced to the AI) or an EarlyResult that short-circuits the execute
    /// entirely — used for the "switched, re-execute" and "cwd drifted,
    /// verify" paths where the planner refuses to run the command on the
    /// AI's behalf. CdCommand is a standalone shell command (e.g.
    /// `Set-Location 'C:\...'`) that gets run as its own execute so the
    /// AI's command stays pure and the status line can show what the AI
    /// asked for rather than the proxy-injected cd preamble.
    /// </summary>
    private sealed record ExecutionPlan(
        int ConsolePid,
        string PipeName,
        string? CdCommand,
        string? PreambleCwd,
        string? RoutingNotice,
        ExecuteResult? EarlyResult);

    /// <summary>
    /// Decide which console the command should run on, whether a cd preamble
    /// is needed, and whether any drift / cross-shell-switch warning should
    /// be surfaced. This is all side-effect-aware (MarkPipeBusy, LastAiCwd
    /// updates, auto-starts via StartConsoleInnerAsync) because the decisions
    /// and the state updates are entangled — separating them would just mean
    /// the caller has to replay the decisions.
    /// </summary>
    private async Task<ExecutionPlan> PlanExecutionAsync(string command, string agentId, string? shell)
    {
        // Resolve shell to full path for consistent matching
        var resolvedShell = shell != null ? ResolveShellPath(shell) : null;

        int initialActivePid;
        int consolePid;
        string pipeName;
        string? sourceShellFamily;
        // Snapshot of a recently-closed active console (shell exited,
        // pipe broken, window closed by user). If present, it seeds
        // sourceShellFamily + sourceCwd + resolvedShell so the auto-start
        // path below can spin up a same-family replacement at the AI's
        // last known cwd and run the command there in one shot, instead
        // of a "switched to Foo, please re-execute" warning.
        string? cachedDeadCwd = null;

        lock (_lock)
        {
            var state = GetOrCreateAgentState(agentId);
            initialActivePid = state.ActivePid;
            consolePid = initialActivePid;
            sourceShellFamily = _consoles.GetValueOrDefault(consolePid)?.ShellFamily;

            if (initialActivePid == 0 && state.LastActiveCwd != null)
            {
                cachedDeadCwd = state.LastActiveCwd;
                resolvedShell ??= state.LastActiveShellPath;
                if (state.LastActiveShellPath != null)
                    sourceShellFamily = NormalizeShellFamily(state.LastActiveShellPath);
                // Consume once so subsequent calls don't keep re-applying.
                state.LastActiveCwd = null;
                state.LastActiveShellPath = null;
            }

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
        string? sourceCwd = cachedDeadCwd;
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
                    // "completed" = the worker has one or more cached results from
                    // earlier flipped commands that haven't been drained yet. Routing
                    // away here isn't strictly required — RegisterCommand no longer
                    // clears _cachedResults and AppendCachedOutputs drains it at the
                    // tail of this tool call — but surfacing those results in the
                    // same response as a brand-new inline execute would conflate
                    // two unrelated command histories. Stay conservative: treat
                    // completed as busy for routing purposes so the fresh execute
                    // lands on a sibling console and the cached results drain
                    // cleanly on their own line.
                    activeBusy = statusStr == "busy" || statusStr == "completed";
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
            // Remember that this console is running something so the tool
            // response can include a background-busy line for it. Without
            // this, `KnownBusyPids` is only populated from AI-command
            // timeouts, so user-initiated activity like `pause` silently
            // disappears from the background busy report even though we
            // just detected and routed away from it. CollectBusyStatuses
            // will self-heal the entry once the console returns to idle.
            MarkPipeBusy(agentId, initialActivePid);

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
                // Auto-start a new console. When we're in the lazy-recovery
                // path (cachedDeadCwd populated from a freshly-dead active
                // console) and the target shell is Windows-native (pwsh,
                // powershell, cmd) we can hand the cached Windows path
                // straight to CreateProcess as workingDirectory — the
                // shell comes up in the right place from its very first
                // prompt, so no cd preamble or Set-Location injection is
                // needed at all. Bypass the rest of the planning phase
                // in that case. For bash/zsh on Windows we still pass
                // null and fall through to the cd preamble branch below,
                // because those shells report POSIX paths
                // (/mnt/c/... or /c/...) that CreateProcess can't use as
                // a working directory.
                var targetShellPath = resolvedShell ?? GetDefaultShell();
                // Only shells that report cwd in Windows-native form
                // (pwsh/powershell/cmd, and REPLs on Windows that use
                // process.cwd() / os.getcwd()) can have their cached
                // dead cwd handed straight to CreateProcess's
                // lpCurrentDirectory. For posix-cwd shells (bash/zsh
                // via WSL or MSYS2) the path would be /mnt/c/... which
                // Win32 rejects — those fall through to the preamble
                // branch below. Phase C(postscript): this is the last
                // hardcoded shell-family helper in ConsoleManager; the
                // adapter capability takes over.
                var targetAdapter = AdapterRegistry.Default?.Find(NormalizeShellFamily(targetShellPath));
                var targetCwdFormat = targetAdapter?.Capabilities.CwdFormat ?? "none";
                if (cachedDeadCwd != null && targetCwdFormat == "windows_native")
                {
                    var startResult = await StartConsoleInnerAsync(targetShellPath, cachedDeadCwd, null, agentId);
                    consolePid = startResult.Pid;
                    lock (_lock)
                        pipeName = _consoles.GetValueOrDefault(consolePid)?.PipePath ?? GetPipeName(agentId, consolePid);
                    // StartConsoleInnerAsync has already set LastAiCwd to
                    // the freshly-started console's live cwd (which is
                    // cachedDeadCwd). Return a plan that points directly
                    // at the new console — no preamble, no warning.
                    return new ExecutionPlan(consolePid, pipeName, CdCommand: null, PreambleCwd: null, RoutingNotice: null, EarlyResult: null);
                }

                var fallbackStart = await StartConsoleInnerAsync(targetShellPath, null, null, agentId);
                consolePid = fallbackStart.Pid;
                lock (_lock)
                    pipeName = _consoles.GetValueOrDefault(consolePid)?.PipePath ?? GetPipeName(agentId, consolePid);
            }
        }

        // Determine target shell family and check cross-shell compatibility for cd preamble
        string? targetShellFamily;
        lock (_lock) targetShellFamily = _consoles.GetValueOrDefault(consolePid)?.ShellFamily;

        bool sameShellFamily = sourceShellFamily != null && targetShellFamily != null &&
                                sourceShellFamily.Equals(targetShellFamily, StringComparison.OrdinalIgnoreCase);

        // Standalone cd command to run before the AI's command, when
        // routing moved us to a console whose cwd differs from the AI's
        // intended cwd. Running it as a separate execute (instead of
        // prepending `cd '...'; ` to the AI's command) keeps the AI-
        // facing status line honest: the Pipeline field shows what the
        // AI actually asked for, not a proxy-manufactured composite.
        // expectedCwdAfterCd is the cwd we expect to reach; after the
        // cd runs we compare the worker's reported cwd against it and
        // bail if they don't match — catches the pwsh case where
        // `Set-Location` to a non-existent path leaves $LASTEXITCODE
        // unchanged (= 0) so an exit-code-only check would silently
        // let the AI command run in whatever cwd cd got stuck at.
        string? cdCommand = null;
        string? expectedCwdAfterCd = null;

        // Out-of-band notice attached to the success result, used when we
        // silently corrected for a user-initiated cwd change in the source
        // console so the AI sees what ripple did on its behalf.
        string? routingNotice = null;

        if (isSwitching && sourceCwd != null && sameShellFamily)
        {
            // The AI's "intended cwd" is whatever it last saw a successful
            // command complete at on the source console — i.e. source's
            // LastAiCwd. The source's *live* cwd may have drifted from that
            // if the human user manually cd'd before kicking off the busy
            // command we're routing around. Honor the AI's intent: when
            // there's drift, use LastAiCwd as the preamble target instead
            // of the live cwd so the AI keeps working in the directory it
            // thinks it's in. The source's LastAiCwd is intentionally NOT
            // updated, so if the AI later returns to the source console it
            // will either match (user cd'd back) or trigger the same-console
            // mismatch warning, which gives the AI the explicit signal it
            // needs to verify and re-execute.
            string? sourceLastAiCwd = null;
            if (initialActivePid != 0)
                lock (_lock) sourceLastAiCwd = _consoles.GetValueOrDefault(initialActivePid)?.LastAiCwd;

            bool sourceDrifted = IsCwdDrifted(sourceLastAiCwd, sourceCwd);

            // preambleCwd is what the new console will be cd'd to before
            // the AI command runs. When source has drifted we restore the
            // AI's last known cwd; otherwise we propagate the live cwd
            // (which equals LastAiCwd in the no-drift case anyway).
            var preambleCwd = sourceDrifted ? sourceLastAiCwd! : sourceCwd;

            var cdPreamble = BuildCdPreamble(targetShellFamily!, preambleCwd);
            if (cdPreamble != null)
            {
                // Strip trailing `&` / `;` / ` ` so the preamble stands on
                // its own as a standalone shell command. BuildCdPreamble
                // produces `cd '...' && ` / `Set-Location '...'; ` /
                // `cd /d "..." && `; after TrimEnd we get the bare cd.
                cdCommand = cdPreamble.TrimEnd('&', ';', ' ');
                expectedCwdAfterCd = preambleCwd;
                UpdateConsoleInfo(consolePid, info => info.LastAiCwd = preambleCwd);
            }

            if (sourceDrifted)
            {
                var sourceDisplay = _consoles.GetValueOrDefault(initialActivePid)?.DisplayName ?? $"#{initialActivePid}";
                var targetDisplay = _consoles.GetValueOrDefault(consolePid)?.DisplayName ?? $"#{consolePid}";
                routingNotice =
                    $"Note: source {sourceDisplay} was moved by user to '{sourceCwd}'; " +
                    $"ran in {targetDisplay} at your last known cwd '{sourceLastAiCwd}'.";
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
            return new ExecutionPlan(consolePid, pipeName, cdCommand, expectedCwdAfterCd, routingNotice,
                EarlyResult: new ExecuteResult
                {
                    Pid = consolePid,
                    Switched = true,
                    DisplayName = displayName,
                    Output = $"Switched to console {displayName}. Pipeline NOT executed — cd to the correct directory and re-execute.",
                });
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

            if (IsCwdDrifted(lastAiCwd, currentCwd))
            {
                UpdateConsoleInfo(consolePid, info => info.LastAiCwd = currentCwd);
                var displayName = consoleInfo?.DisplayName ?? $"#{consolePid}";
                return new ExecutionPlan(consolePid, pipeName, cdCommand, expectedCwdAfterCd, routingNotice,
                    EarlyResult: new ExecuteResult
                    {
                        Pid = consolePid,
                        Switched = true,
                        DisplayName = displayName,
                        Output = $"Console {displayName} cwd is now '{currentCwd}' (was '{lastAiCwd}'). Pipeline NOT executed — verify and re-execute.",
                    });
            }
        }

        return new ExecutionPlan(consolePid, pipeName, cdCommand, expectedCwdAfterCd, routingNotice, EarlyResult: null);
    }

    /// <summary>
    /// Run the pipe-level execute call after routing is done. Writes the
    /// (possibly preamble-augmented) command to the worker, waits for the
    /// response, drains any trailing post-prompt output, updates
    /// LastAiCwd, and translates pipe-level exceptions (timeout, cancel,
    /// I/O error) into an appropriate ExecuteResult. The routing notice
    /// chosen by the planner is carried through every success / busy /
    /// timeout path so the AI sees source-drift context even when the
    /// command itself fails.
    /// </summary>
    private async Task<ExecuteResult> ExecutePlannedCommandAsync(
        int consolePid,
        string pipeName,
        string command,
        string? cdCommand,
        string? expectedCwdAfterCd,
        int timeoutSeconds,
        string agentId,
        string? shell,
        string? routingNotice)
    {
        try
        {
            // Record the AI-visible command for this console so background
            // busy reports can show what the AI asked for, not the proxy-
            // injected cd. The cd itself is handled as a separate execute
            // below and never shown in the AI-facing status line.
            UpdateConsoleInfo(consolePid, ci => ci.LastAiCommand = command);

            // Phase 1: optional cd. Run as its own execute so the AI's
            // command stays pure in the status line and the cache entry.
            // Splitting also makes error handling cleaner: if the cd
            // fails (target directory doesn't exist, etc.) we report the
            // failure explicitly instead of running the AI command in
            // the wrong place. We use a short fixed timeout here since
            // cd is always a near-instant shell builtin.
            if (!string.IsNullOrEmpty(cdCommand))
            {
                try
                {
                    var cdResp = await SendPipeRequestAsync(pipeName, w =>
                    {
                        w.WriteString("type", "execute");
                        w.WriteString("id", Guid.NewGuid().ToString());
                        w.WriteString("command", cdCommand);
                        w.WriteNumber("timeout", 5000);
                    }, TimeSpan.FromSeconds(8));

                    var cdActualCwd = cdResp.TryGetProperty("cwd", out var ccProp) ? ccProp.GetString() : null;
                    var cdOutput = cdResp.TryGetProperty("output", out var coProp) ? coProp.GetString() : null;
                    // Verify the cd actually reached the expected
                    // directory. Exit-code checking is unreliable here
                    // because pwsh's `Set-Location` surfaces path-not-
                    // found failures as a cmdlet error record, not a
                    // non-zero $LASTEXITCODE — and cmd's `cd /d`
                    // doesn't expose %ERRORLEVEL% through the PROMPT
                    // shim at all. The worker's post-command cwd (sent
                    // on every execute response via OSC P) is the
                    // shell-agnostic source of truth: if the cwd
                    // returned from cdResp doesn't match what we
                    // expected, the cd didn't land where we wanted,
                    // even if the shell reported exit code 0.
                    if (!string.IsNullOrEmpty(expectedCwdAfterCd)
                        && !string.IsNullOrEmpty(cdActualCwd)
                        && !CwdEquals(cdActualCwd!, expectedCwdAfterCd!))
                    {
                        var displayName = _consoles.GetValueOrDefault(consolePid)?.DisplayName ?? $"#{consolePid}";
                        var shellFamily = _consoles.GetValueOrDefault(consolePid)?.ShellFamily;
                        // Roll LastAiCwd forward to whatever cwd the
                        // shell actually ended up at so subsequent
                        // execute_commands from the AI don't keep
                        // trying to return to the unreachable path.
                        UpdateConsoleInfo(consolePid, info => info.LastAiCwd = cdActualCwd);
                        return new ExecuteResult
                        {
                            Pid = consolePid,
                            DisplayName = displayName,
                            ShellFamily = shellFamily,
                            Command = command,
                            Output = string.IsNullOrEmpty(cdOutput)
                                ? $"Failed to change directory to '{expectedCwdAfterCd}' before running command. Shell stayed at '{cdActualCwd}'."
                                : cdOutput + $"\n(Shell stayed at '{cdActualCwd}'.)",
                            ExitCode = 1,
                            Cwd = cdActualCwd,
                            Notice = routingNotice,
                        };
                    }
                }
                catch
                {
                    // Pipe error on the cd phase — fall through to the
                    // main execute. The status line will still report
                    // whatever cwd the main command ends up at, so the
                    // AI has visible signal if the cd didn't happen.
                }
            }

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
                var partial = response.TryGetProperty("partialOutput", out var poProp) ? poProp.GetString() : null;
                return new ExecuteResult { Pid = consolePid, TimedOut = true, DisplayName = displayName, ShellFamily = shellFamily, Command = command, Notice = routingNotice, PartialOutput = partial };
            }

            // Worker reported that its shell process exited before the
            // command could complete (user typed `exit`, shell crashed,
            // etc). Clear our tracking of the dead console and return a
            // "died" notification — deliberately NOT auto-starting a
            // replacement. If the AI meant to close the console, auto-
            // spawning a new one would resurrect it against the AI's
            // intent. The next execute_command will go through the
            // normal no-active-console path in PlanExecutionAsync and
            // spin up a fresh same-family console lazily.
            var shellExited = response.TryGetProperty("shellExited", out var seProp) && seProp.GetBoolean();
            if (shellExited)
            {
                var deadName = _consoles.GetValueOrDefault(consolePid)?.DisplayName ?? $"#{consolePid}";
                RememberClosedActive(agentId, consolePid);
                ClearDeadConsole(agentId, consolePid);
                return new ExecuteResult
                {
                    Pid = consolePid,
                    Switched = true,
                    DisplayName = deadName,
                    Output = $"Console {deadName} exited (shell process gone). Pipeline NOT executed — re-execute and ripple will spin up a fresh console if needed.",
                };
            }

            var output = response.TryGetProperty("output", out var outputProp) ? outputProp.GetString() ?? "" : "";
            var exitCode = response.TryGetProperty("exitCode", out var exitProp) ? exitProp.GetInt32() : 0;
            var duration = response.TryGetProperty("duration", out var durProp) ? durProp.GetString() ?? "0" : "0";
            var cwdResult = response.TryGetProperty("cwd", out var cwdProp) ? cwdProp.GetString() : null;
            var displayName2 = _consoles.GetValueOrDefault(consolePid)?.DisplayName ?? $"#{consolePid}";

            // Drain any trailing bytes the shell streamed after OSC PromptStart
            // (pwsh prompt repaint, Format-Table rows still finishing, etc.).
            // With the worker's fixed 500ms settle removed, this call
            // adaptively waits until the post-primary buffer has been stable
            // for stable_ms, capped at max_ms. Fast commands return in ~100ms,
            // slow streaming waits as long as needed up to max_ms. Runs on
            // the second pipe instance since the execute call freed the first.
            //
            // Milestone 2g: stable_ms is sourced from the adapter's
            // output.post_prompt_settle_ms when a YAML adapter is loaded for
            // the console's shell family, else 100. max_ms is scaled so it
            // always has at least 200ms of headroom above stable_ms, so
            // settle windows wider than the old 500ms ceiling (e.g. cmd's
            // 400ms) still get a chance to complete.
            try
            {
                var info = _consoles.GetValueOrDefault(consolePid);
                var drainAdapter = info != null
                    ? AdapterRegistry.Default?.Find(info.ShellFamily)
                    : null;
                int stableMs = drainAdapter?.Output.PostPromptSettleMs ?? 100;
                int maxMs = Math.Max(500, stableMs + 200);
                var drainResp = await SendPipeRequestAsync(pipeName, w =>
                {
                    w.WriteString("type", "drain_post_output");
                    w.WriteNumber("stable_ms", stableMs);
                    w.WriteNumber("max_ms", maxMs);
                }, TimeSpan.FromSeconds(2));

                var delta = drainResp.TryGetProperty("delta", out var dp) ? dp.GetString() : null;
                if (!string.IsNullOrEmpty(delta))
                {
                    output = string.IsNullOrEmpty(output) ? delta : output + "\n" + delta;
                }
            }
            catch
            {
                // Best-effort — if drain fails the primary output is still returned.
            }

            // Update LastAiCwd with the result cwd (the cwd the command ended at)
            if (cwdResult != null)
                UpdateConsoleInfo(consolePid, info => info.LastAiCwd = cwdResult);

            return new ExecuteResult
            {
                Pid = consolePid,
                Output = output,
                ExitCode = exitCode,
                Duration = duration,
                Command = command,
                DisplayName = displayName2,
                ShellFamily = _consoles.GetValueOrDefault(consolePid)?.ShellFamily,
                Cwd = cwdResult,
                Notice = routingNotice,
            };
        }
        catch (TimeoutException)
        {
            // Pipe communication timeout (worker didn't respond in time)
            var displayName = _consoles.GetValueOrDefault(consolePid)?.DisplayName ?? $"#{consolePid}";
            MarkPipeBusy(agentId, consolePid);
            return new ExecuteResult { Pid = consolePid, TimedOut = true, DisplayName = displayName, Command = command, Notice = routingNotice };
        }
        catch (OperationCanceledException)
        {
            // Pipe CancellationToken fired
            var displayName = _consoles.GetValueOrDefault(consolePid)?.DisplayName ?? $"#{consolePid}";
            MarkPipeBusy(agentId, consolePid);
            return new ExecuteResult { Pid = consolePid, TimedOut = true, DisplayName = displayName, Command = command, Notice = routingNotice };
        }
        catch (IOException)
        {
            var deadName = _consoles.GetValueOrDefault(consolePid)?.DisplayName ?? $"#{consolePid}";
            RememberClosedActive(agentId, consolePid);
            ClearDeadConsole(agentId, consolePid);
            return new ExecuteResult
            {
                Pid = consolePid,
                Switched = true,
                DisplayName = deadName,
                Output = $"Console {deadName} died (pipe broken). Pipeline NOT executed — re-execute and ripple will spin up a fresh console.",
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

    /// <summary>
    /// True when a console's live cwd differs from the cwd the AI last saw
    /// it complete a command in (its LastAiCwd), i.e. the human user has
    /// manually cd'd since the last AI command. Null on either side means
    /// the AI has no prior expectation (fresh console, or no cwd reported
    /// by the worker) and so nothing has drifted — the routing code treats
    /// that as "use the live cwd as-is". Uses the same path comparison
    /// policy (Ordinal on POSIX, OrdinalIgnoreCase on Windows) as every
    /// other path match in this file.
    /// </summary>
    internal static bool IsCwdDrifted(string? lastAiCwd, string? liveCwd)
        => lastAiCwd != null && liveCwd != null
           && !liveCwd.Equals(lastAiCwd, PathComparison);

    /// <summary>
    /// Case/separator-normalized cwd comparison used by the cd-failure
    /// detector in ExecutePlannedCommandAsync. Normalizes both paths
    /// via <see cref="Path.GetFullPath(string)"/> so trailing slashes
    /// and short-vs-long 8.3 forms collapse to the same canonical
    /// spelling, then compares under the platform path-comparison
    /// policy (OrdinalIgnoreCase on Windows, Ordinal on POSIX).
    /// Returns false on any normalization exception — a path that
    /// can't even be canonicalized is definitely not equivalent to a
    /// real cwd.
    /// </summary>
    private static bool CwdEquals(string a, string b)
    {
        try
        {
            return Path.GetFullPath(a).Equals(Path.GetFullPath(b), PathComparison);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Apply an update to a tracked console's info under _lock. Consolidates
    /// the "look up _consoles[pid], if non-null mutate" pattern that used to
    /// live inline at every LastAiCwd / LastAiCommand assignment site, so
    /// all writes now go through one well-defined critical section. No-op
    /// if the console has been removed in the meantime.
    /// </summary>
    private void UpdateConsoleInfo(int pid, Action<ConsoleInfo> update)
    {
        lock (_lock)
        {
            var info = _consoles.GetValueOrDefault(pid);
            if (info != null) update(info);
        }
    }

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
                // Reusable states: "standby" (nothing pending) and
                // "completed" (shell is idle but holds a cached result
                // from a previous AI command). Routing onto a
                // "completed" console is safe since RegisterCommand no
                // longer clears _cachedResults — the old entries ride
                // along until the universal drain wrapper picks them
                // up on the way out of the next tool call, which is
                // typically the same tool call that spawned the new
                // command. Excluding "completed" caused auto-routing
                // to spawn a fresh console whenever an earlier
                // timeout-drained console was still holding a cached
                // result, which felt unnatural and wasted standbys.
                if (status != "standby" && status != "completed") continue;

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
    /// Resolve a user-supplied console selector (from peek_console /
    /// diagnostic tools) to a console PID. The selector matches in
    /// this order:
    ///   1. An exact PID number.
    ///   2. An exact display name ("#43060 Reggae").
    ///   3. A case-insensitive substring of the display name
    ///      ("Reggae", "reggae", "43060").
    /// Returns null if nothing matches, or if the match is ambiguous
    /// across multiple consoles. Caller must hold _lock.
    /// </summary>
    private int? ResolveConsoleSelector(string selector)
    {
        // Numeric → PID
        if (int.TryParse(selector, out var pid) && _consoles.ContainsKey(pid))
            return pid;

        // Exact display-name match
        foreach (var kv in _consoles)
        {
            if (string.Equals(kv.Value.DisplayName, selector, StringComparison.OrdinalIgnoreCase))
                return kv.Key;
        }

        // Substring match
        var matches = _consoles
            .Where(kv => kv.Value.DisplayName.Contains(selector, StringComparison.OrdinalIgnoreCase))
            .Select(kv => (int?)kv.Key)
            .ToList();

        return matches.Count == 1 ? matches[0] : null;
    }

    public record PeekResult(
        int Pid,
        string DisplayName,
        string? ShellFamily,
        string Status,
        bool Busy,
        string? RunningCommand,
        double? RunningElapsedSeconds,
        string RecentOutput,
        string? RawBase64 = null);

    /// <summary>
    /// Snapshot what a console has been emitting recently via the peek
    /// pipe command. Lets the AI inspect a busy console (stuck command,
    /// interactive prompt, user-typed command in progress) without
    /// interrupting it or waiting for completion.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <paramref name="console"/> selects which console to peek at.
    /// If it's a number, it's matched against console PIDs. Otherwise
    /// it's matched (case-insensitive, contains-style) against display
    /// names like "#43060 Reggae". When omitted, the agent's current
    /// active console is used.
    /// </para>
    /// <para>
    /// Works regardless of whether the target is idle or busy — the
    /// worker's peek pipe handler is never blocked by the tracker's
    /// busy state, so this is specifically the right tool for
    /// observing a long-running command's progress.
    /// </para>
    /// <para>
    /// Returns null if no console matches.
    /// </para>
    /// </remarks>
    public async Task<PeekResult?> PeekConsoleAsync(string agentId, string? console = null, bool raw = false)
    {
        int? pid;
        string? pipeName;
        string? displayName;
        string? shellFamily;

        lock (_lock)
        {
            var state = GetOrCreateAgentState(agentId);
            if (string.IsNullOrWhiteSpace(console))
            {
                pid = state.ActivePid != 0 ? state.ActivePid : (int?)null;
            }
            else
            {
                pid = ResolveConsoleSelector(console);
            }

            if (pid == null || !_consoles.TryGetValue(pid.Value, out var info))
                return null;

            pipeName = info.PipePath;
            displayName = info.DisplayName;
            shellFamily = info.ShellFamily;
        }

        try
        {
            var resp = await SendPipeRequestAsync(pipeName,
                w => { w.WriteString("type", "peek"); if (raw) w.WriteBoolean("raw", true); },
                TimeSpan.FromSeconds(3));

            var status = resp.TryGetProperty("status", out var stProp) ? stProp.GetString() ?? "" : "";
            var busy = resp.TryGetProperty("busy", out var bProp) && bProp.GetBoolean();
            var runningCmd = resp.TryGetProperty("runningCommand", out var rcProp) && rcProp.ValueKind == JsonValueKind.String ? rcProp.GetString() : null;
            double? elapsed = null;
            if (resp.TryGetProperty("runningElapsedSeconds", out var esProp) && esProp.ValueKind == JsonValueKind.Number)
                elapsed = esProp.GetDouble();
            var recent = resp.TryGetProperty("recentOutput", out var roProp) ? roProp.GetString() ?? "" : "";
            var rawB64 = resp.TryGetProperty("rawBase64", out var rbProp) ? rbProp.GetString() : null;

            return new PeekResult(pid.Value, displayName!, shellFamily, status, busy, runningCmd, elapsed, recent, rawB64);
        }
        catch
        {
            return null;
        }
    }

    public record SendInputResult(int Pid, string DisplayName, string Status, string? Error = null);

    /// <summary>
    /// Send raw input to a busy console's PTY. The console is resolved
    /// via the same selector as PeekConsoleAsync (PID or display-name
    /// substring). Returns a result with status "ok" or an error.
    /// </summary>
    public async Task<SendInputResult?> SendInputAsync(string agentId, string console, string input)
    {
        int? pid;
        string? pipeName;
        string? displayName;

        lock (_lock)
        {
            pid = ResolveConsoleSelector(console);
            if (pid == null || !_consoles.TryGetValue(pid.Value, out var info))
                return null;
            pipeName = info.PipePath;
            displayName = info.DisplayName;
        }

        try
        {
            var resp = await SendPipeRequestAsync(pipeName, w =>
            {
                w.WriteString("type", "send_input");
                w.WriteString("input", input);
            }, TimeSpan.FromSeconds(5));

            var status = resp.TryGetProperty("status", out var stProp) ? stProp.GetString() ?? "" : "";
            var error = resp.TryGetProperty("error", out var errProp) ? errProp.GetString() : null;
            return new SendInputResult(pid.Value, displayName!, status, error);
        }
        catch (Exception ex)
        {
            return new SendInputResult(pid.Value, displayName!, "error", ex.Message);
        }
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
                var state = GetOrCreateAgentState(agentId);
                // If this was the AI's active console, snapshot its cwd /
                // shell path before removing it so the next execute_command
                // can seamlessly spin up a same-family replacement at the
                // same working directory. Inlined rather than calling
                // RememberClosedActive because we're already under _lock.
                if (state.ActivePid == pid)
                {
                    state.LastActiveCwd = info.LastAiCwd;
                    state.LastActiveShellPath = info.ShellPath;
                    state.ActivePid = 0;
                }
                _consoles.Remove(pid);
                _pidToTitle.Remove(pid);
                state.KnownBusyPids.Remove(pid);
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

                // If there's no cached output, leave KnownBusyPids alone —
                // CollectBusyStatusesAsync (called right after this in
                // AppendCachedOutputs) will detect the busy→idle transition
                // and emit a finished notification. Stale entries with a
                // lost cache end up going through the same path and get
                // surfaced as "finished" too, which is a little misleading
                // but beats the old silent cleanup that swallowed real
                // user-command finish events.
                if (!hasCached) continue;

                var cachedResp = await SendPipeRequestAsync(pipe,
                    w => w.WriteString("type", "get_cached_output"),
                    TimeSpan.FromSeconds(5));

                var cacheStatus = cachedResp.TryGetProperty("status", out var cs) ? cs.GetString() : null;
                if (cacheStatus != "ok") continue;
                if (!cachedResp.TryGetProperty("results", out var resultsProp)
                    || resultsProp.ValueKind != JsonValueKind.Array)
                    continue;

                var consoleInfo = _consoles.GetValueOrDefault(pid.Value);
                var displayName = consoleInfo?.DisplayName ?? $"#{pid.Value}";

                // A single console's cache may hold multiple entries if
                // sequential commands each flipped to cache mode without
                // an intervening drain — preserve them all, in order, so
                // the AI sees the full history on the next tool call.
                foreach (var entry in resultsProp.EnumerateArray())
                {
                    results.Add(new ExecuteResult
                    {
                        Pid = pid.Value,
                        Output = entry.TryGetProperty("output", out var o) ? o.GetString() ?? "" : "",
                        ExitCode = entry.TryGetProperty("exitCode", out var e) ? e.GetInt32() : 0,
                        Duration = entry.TryGetProperty("duration", out var d) ? d.GetString() ?? "0" : "0",
                        Command = entry.TryGetProperty("command", out var c) ? c.GetString() : null,
                        DisplayName = displayName,
                        ShellFamily = consoleInfo?.ShellFamily,
                        Cwd = entry.TryGetProperty("cwd", out var w) ? w.GetString() : null,
                        StatusLine = entry.TryGetProperty("statusLine", out var sl) ? sl.GetString() : null,
                    });
                }
                UnmarkPipeBusy(agentId, pid.Value);
            }
            catch { }
        }

        return results;
    }

    /// <summary>
    /// Snapshot of a busy console's running command for reporting to the AI
    /// at the end of unrelated tool calls. Used to keep the AI aware of
    /// long-running work on other consoles instead of silently forgetting them.
    /// </summary>
    public record BusyStatus(
        int Pid,
        string DisplayName,
        string? ShellFamily,
        string? RunningCommand,
        double? ElapsedSeconds,
        string? Cwd);

    public record FinishedStatus(
        int Pid,
        string DisplayName,
        string? ShellFamily,
        string? Cwd);

    public record BusyReport(
        List<BusyStatus> Busy,
        List<FinishedStatus> Finished);

    /// <summary>
    /// Report currently-busy consoles (other than any caller-excluded one)
    /// so the caller can prepend their status to the next response. Walks
    /// every known console — not just KnownBusyPids — so that user-typed
    /// commands which the proxy never explicitly tracked still surface in
    /// the busy report. Newly-discovered busy consoles are added to
    /// KnownBusyPids so the eventual idle transition lands in the Finished
    /// list on a later call. Consoles that were previously reported as
    /// busy but are now idle produce one "finished" notification and are
    /// then unmarked.
    /// </summary>
    public async Task<BusyReport> CollectBusyStatusesAsync(string agentId, int excludePid = 0)
    {
        var busyReport = new List<BusyStatus>();
        var finishedReport = new List<FinishedStatus>();

        var knownBusy = SnapshotBusyPids(agentId).ToHashSet();
        List<int> allConsoles;
        lock (_lock) allConsoles = _consoles.Keys.ToList();

        var toCheck = new HashSet<int>(knownBusy);
        foreach (var pid in allConsoles) toCheck.Add(pid);

        foreach (var pid in toCheck)
        {
            if (pid == excludePid) continue;

            if (!IsProcessAlive(pid))
            {
                UnmarkPipeBusy(agentId, pid);
                continue;
            }

            string? pipeName;
            ConsoleInfo? info;
            lock (_lock)
            {
                info = _consoles.GetValueOrDefault(pid);
                pipeName = info?.PipePath;
            }
            if (pipeName == null)
            {
                UnmarkPipeBusy(agentId, pid);
                continue;
            }

            try
            {
                var statusResp = await SendPipeRequestAsync(pipeName,
                    w => w.WriteString("type", "get_status"),
                    TimeSpan.FromSeconds(3));

                var statusStr = statusResp.TryGetProperty("status", out var st) ? st.GetString() : null;
                var wasKnownBusy = knownBusy.Contains(pid);

                // Both busy and finished lines carry the worker's live
                // cwd so the AI can see where each background console
                // is without a separate peek_console round-trip. The
                // worker reports _tracker.LastKnownCwd which is updated
                // on every OSC P (Cwd) event, including from user-typed
                // cd commands, so this stays in sync with whatever the
                // human is doing on the shared terminal.
                var cwdForLine = statusResp.TryGetProperty("cwd", out var cwdProp)
                    ? cwdProp.GetString()
                    : null;

                if (statusStr != "busy")
                {
                    // Idle now. Only emit a finished line if we'd previously
                    // reported the console as busy — skip consoles that were
                    // idle all along, otherwise every tool call for a user
                    // with multiple standby consoles would spam finished
                    // entries. AI commands that timed out and then completed
                    // are drained by CollectCachedOutputs before this runs,
                    // so they're gone from KnownBusyPids by now.
                    if (wasKnownBusy)
                    {
                        UnmarkPipeBusy(agentId, pid);
                        finishedReport.Add(new FinishedStatus(pid, info?.DisplayName ?? $"#{pid}", info?.ShellFamily, cwdForLine));
                    }
                    continue;
                }

                // Busy now. Mark it so the later transition to idle produces
                // a finished notification even if nothing else tagged it.
                if (!wasKnownBusy) MarkPipeBusy(agentId, pid);

                // Decide what command text to show on the busy line. The
                // worker's runningCommand returns null when its busy state
                // is from a user-typed command (CommandTracker only fills it
                // for AI commands), so a null here means "the human typed
                // this, not the AI" — surface that as "(user command)" by
                // returning null. The proxy's LastAiCommand is the previous
                // AI command's text and would otherwise leak across into
                // a user busy line as a stale label. When the worker IS
                // running an AI command, prefer LastAiCommand (it's the
                // clean original without any cd preamble ripple injected),
                // falling back to the worker's text if the proxy never
                // recorded one.
                var workerRunning = statusResp.TryGetProperty("runningCommand", out var rc)
                    ? rc.GetString()
                    : null;
                var cmd = workerRunning != null
                    ? (info?.LastAiCommand ?? workerRunning)
                    : null;
                double? elapsed = null;
                if (statusResp.TryGetProperty("runningElapsedSeconds", out var esProp)
                    && esProp.ValueKind == JsonValueKind.Number)
                    elapsed = esProp.GetDouble();

                busyReport.Add(new BusyStatus(pid, info?.DisplayName ?? $"#{pid}", info?.ShellFamily, cmd, elapsed, cwdForLine));
            }
            catch
            {
                // Transient pipe error — leave any existing KnownBusy state
                // alone so we retry next tick. Don't emit a partial entry.
            }
        }

        return new BusyReport(busyReport, finishedReport);
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
        List<BusyStatus> StillBusy,
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
                new List<BusyStatus>(),
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

                    var statusStr = statusResp.TryGetProperty("status", out var stProp) ? stProp.GetString() : null;
                    var hasCached = statusResp.TryGetProperty("hasCachedOutput", out var hc) && hc.GetBoolean();

                    // Worker is back at standby with nothing to deliver — the
                    // previous AI command's cache was lost/destroyed. Stop
                    // waiting; there will never be a result to drain.
                    if (!hasCached && statusStr == "standby")
                    {
                        UnmarkPipeBusy(agentId, pid);
                        busyPids.RemoveAt(i);
                        continue;
                    }

                    if (!hasCached) continue;

                    var cachedResp = await SendPipeRequestAsync(pipeName,
                        w => w.WriteString("type", "get_cached_output"),
                        TimeSpan.FromSeconds(5));

                    var cacheStatus = cachedResp.TryGetProperty("status", out var cs) ? cs.GetString() : null;
                    if (cacheStatus != "ok") continue;
                    if (!cachedResp.TryGetProperty("results", out var resultsProp)
                        || resultsProp.ValueKind != JsonValueKind.Array)
                        continue;

                    var info2 = _consoles.GetValueOrDefault(pid);
                    var displayName2 = info2?.DisplayName ?? $"#{pid}";
                    foreach (var entry in resultsProp.EnumerateArray())
                    {
                        completed.Add(new ExecuteResult
                        {
                            Pid = pid,
                            Output = entry.TryGetProperty("output", out var o) ? o.GetString() ?? "" : "",
                            ExitCode = entry.TryGetProperty("exitCode", out var e) ? e.GetInt32() : 0,
                            Duration = entry.TryGetProperty("duration", out var d) ? d.GetString() ?? "0" : "0",
                            Command = entry.TryGetProperty("command", out var c) ? c.GetString() : null,
                            DisplayName = displayName2,
                            ShellFamily = info2?.ShellFamily,
                            Cwd = entry.TryGetProperty("cwd", out var w) ? w.GetString() : null,
                            StatusLine = entry.TryGetProperty("statusLine", out var sl) ? sl.GetString() : null,
                        });
                    }
                    UnmarkPipeBusy(agentId, pid);
                    busyPids.RemoveAt(i);
                }
                catch
                {
                    // Transient pipe error — keep the pid in the busy list and
                    // retry on the next poll tick.
                }
            }

            // Return as soon as ANY busy console produces a result — the
            // caller can call wait_for_completion again to pick up the
            // rest. Waiting for all of them at once made AI sessions
            // block on the slowest command even after a faster one had
            // completed, which defeats the whole point of the
            // cache-on-busy-receive salvage layer. First-drain-wins
            // gives the AI a tight feedback loop: react to whatever
            // finished, issue the next command, and loop.
            if (busyPids.Count == 0 || completed.Count > 0) break;
            await Task.Delay(300);
        }

        // Report the still-busy consoles via the same BusyStatus shape the
        // per-tool background busy report uses, so the AI sees a consistent
        // `⧗ #pid Name (shell) | Status: Busy (Ns) | Pipeline: cmd` format
        // everywhere.
        var stillBusy = (await CollectBusyStatusesAsync(agentId)).Busy;

        return new WaitForCompletionResult(completed, stillBusy, HadNoBusyPids: false);
    }

    // --- Pipe enumeration ---

    /// <summary>
    /// Enumerates ripple Named Pipes.
    /// Windows: \\.\pipe\SP.*
    /// Linux/macOS: /tmp/CoreFxPipe_RP.*
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
    /// Format: RP.{consolePid} (2 segments) vs owned RP.{proxyPid}.{agentId}.{consolePid} (4 segments).
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

        // Original AI-visible command text (without proxy-injected cd preamble).
        // Used by CollectBusyStatusesAsync so background busy lines show what
        // the AI asked for, not the preamble-augmented string.
        public string? LastAiCommand { get; set; }
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
    /// Read PATHEXT from registry (System environment). Windows-only
    /// by construction — the caller (ResolveShellPath) already branches
    /// on OperatingSystem.IsWindows() before invoking this, but the
    /// early-return keeps the CA1416 analyzer happy without relying on
    /// inter-method flow analysis.
    /// </summary>
    private static string[] GetRegistryPathExt()
    {
        if (!OperatingSystem.IsWindows())
            return [".exe", ".cmd", ".bat"];

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
        public int Pid { get; set; }
        public string Output { get; set; } = "";
        public int ExitCode { get; set; }
        public string Duration { get; set; } = "0";
        public string? Command { get; set; }
        public string? DisplayName { get; set; }
        public string? ShellFamily { get; set; }
        public string? Cwd { get; set; }
        public bool Switched { get; set; }
        public bool TimedOut { get; set; }
        // Pre-formatted status line baked in by the worker at Resolve
        // time (or reconstructed by the proxy for inline results). Cached
        // drains return this verbatim instead of reformatting with
        // possibly-stale proxy-side ConsoleInfo metadata.
        public string? StatusLine { get; set; }
        // Populated only on TimedOut — the recent-output ring snapshot the
        // worker captured at timeout. Lets the AI diagnose stuck commands
        // (watch mode, interactive prompts) without waiting for
        // wait_for_completion to drain the full cached result.
        public string? PartialOutput { get; set; }
        // Free-form notice prepended to the response. Used to surface
        // out-of-band events like "the source console you came from has
        // been moved by the user, your last known cwd has been preserved".
        public string? Notice { get; set; }
    }
}
