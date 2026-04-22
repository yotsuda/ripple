using System.Diagnostics;
using System.Text;

namespace Ripple.Services;

/// <summary>
/// Tracks command lifecycle using OSC 633 events.
/// Runs in the console worker process.
///
/// Scope (post ripple issue #1 refactor):
///   - detect command boundaries (OSC C / OSC D / OSC A plus the cmd.exe
///     and regex-prompt fallbacks) and translate them into raw-capture
///     offsets on a <see cref="CommandOutputCapture"/>.
///   - route every PTY-produced char into either that capture (while an
///     AI command is in flight) or the "not my command" ring used by
///     peek_console / timeout partialOutput.
///   - expose a tail-bounded snapshot of the in-flight AI command so the
///     worker's timeout response has something meaningful to surface in
///     <c>partialOutput</c>.
///
/// Explicitly NOT tracker responsibilities (these all moved to
/// <see cref="ConsoleWorker"/>'s finalize-once path):
///   - cleaning ANSI / prompt noise off the command output window.
///   - applying truncation or writing spill files.
///   - assembling <see cref="CommandResult"/> objects.
///   - owning a completed-result cache and status-line formatting.
///
/// Flow:
///   RegisterCommand(registration) → command written to PTY →
///   OSC C (CommandExecuted) → MarkCommandStart on the capture →
///   OSC D;{exitCode} (CommandFinished) → MarkCommandEnd on the capture →
///   OSC A (PromptStart) → emit <see cref="CompletedCommandSnapshot"/> →
///   worker finalizes once.
///
/// On timeout: the worker's caller side cancels, but the tracker keeps
/// capturing. When the shell eventually resolves the command the
/// snapshot is still emitted; the worker decides whether to deliver it
/// inline or cache it for the next <c>wait_for_completion</c>.
/// </summary>
public class CommandTracker
{
    // Rolling window of everything the PTY has emitted recently, regardless
    // of whether an AI command was in flight or the user typed something
    // themselves. Used by (a) execute_command's timeout response, which
    // returns the tail of this buffer as `partialOutput` so the AI can
    // diagnose stuck commands, and (b) the peek_console MCP tool, which
    // lets the AI inspect a busy console on demand.
    // Small and fixed-size so the token cost of returning it stays bounded.
    private const int RecentOutputCapacity = 4096;

    private readonly object _lock = new();
    private TaskCompletionSource<CompletedCommandSnapshot>? _tcs;
    private CancellationTokenRegistration _timeoutReg;
    private bool _isAiCommand;
    private bool _userCommandBusy;
    private bool _userBusyByPolling; // set by ConsoleWorker's CPU/child polling for cmd
    private bool _shellReady; // flipped on first PromptStart; gates user-busy tracking

    // Monotonic token stamped onto every CompletedCommandSnapshot at
    // emission time so the worker's finalize-once path can safely
    // release the tracker's AI-command busy flag without clobbering a
    // newer command that registered while the previous finalize was
    // still running. See ReleaseAiCommand.
    private long _commandGeneration;

    // Absolute char offset (in _capture) at which the current AI
    // command's OSC A (PromptStart) fired. Set by HandleEvent when
    // PromptStart closes a matched C/D cycle and propagated into the
    // emitted snapshot. Reset on every RegisterCommand so a back-to-
    // back command doesn't inherit the previous command's value. Null
    // when OSC A hasn't fired yet for the in-flight command — in
    // practice the snapshot is never emitted before OSC A, but callers
    // treat it as nullable so early-exit / shell-exit paths that
    // bypass OSC A still produce a usable snapshot.
    private long? _promptStartOffset;

    // Per-command capture store. Replaces the old _aiOutput string field
    // and its 1MB truncation behaviour — the capture keeps a small hot
    // window and spills the rest to a worker-private scratch file, with
    // offset-based reads for the finalizer. Null when no AI command is
    // tracked.
    private CommandOutputCapture? _capture;

    // Snapshot of the worker's session-wide VtLiteState taken at OSC C
    // by ConsoleWorker.SetCapturedBaselineForCurrentCommand. Carries
    // the screen state that was visible when the command started so
    // CommandOutputRenderer can initialise itself with the correct
    // baseline — necessary for ConPTY repaint bursts to be detected
    // as idempotent overwrites instead of fresh content. Sourced from
    // ConsoleWorker._vtState (always-on, fed raw bytes, NOT reset at
    // OSC C) and not the tracker's own _vtState (which resets per
    // command for peek_console scoping). Null until set; cleared on
    // RegisterCommand.
    private VtLiteSnapshot? _capturedBaselineSnapshot;

    /// <summary>
    /// Set by ConsoleWorker at the moment OSC C fires for the current
    /// AI command. The snapshot must come from the worker's
    /// session-wide VtLiteState so it captures the full visible
    /// viewport (the tracker's own VtLiteState is reset at every OSC C
    /// and would only contain the most recent slice).
    /// </summary>
    public void SetCapturedBaseline(VtLiteSnapshot snapshot)
    {
        lock (_lock)
        {
            if (_isAiCommand) _capturedBaselineSnapshot = snapshot;
        }
    }

    // Capture handed off to the worker's finalize-once path but still
    // accepting PTY bytes that arrive AFTER OSC A has fired. Non-pwsh
    // shells can emit trailing rows (Format-Table tails, cmd PROMPT
    // repaint, progress-bar final frames) between OSC A and the
    // worker's WaitCaptureStable settle deadline; those bytes must
    // land in the same capture the finalizer slices so the cleaned
    // output matches what the user saw. The worker takes ownership
    // via DetachSettlingCapture once it has read its final slice; the
    // tracker no-ops the detach if a newer command already replaced
    // this reference. Guarded by _lock on every read/write.
    private CommandOutputCapture? _settlingCapture;

    // Circular buffer storing the last RecentOutputCapacity chars of the
    // PTY output stream (OSC-stripped but still with SGR colors/cursor
    // escapes in it — the read side does final cleanup). Written to from
    // FeedOutput unconditionally; snapshotted via GetRecentOutputSnapshot.
    private readonly char[] _recentBuf = new char[RecentOutputCapacity];
    private int _recentPos;     // next write index
    private int _recentLen;     // count of valid chars, capped at capacity
    private int _exitCode;
    // Latest OSC 633;E;{N} value reported between RegisterCommand and the
    // snapshot emission. PowerShell's prompt fn fires this once per
    // command with the $Error.Count delta over the pipeline. Other shells
    // don't emit OSC E so the value stays at 0 — meaning "Errors: N"
    // never appears in the proxy status line for non-pwsh adapters,
    // which is correct: $Error.Count has no equivalent in bash / cmd.
    private int _errorCount;
    private string? _cwd;
    private string _commandSent = "";
    private Stopwatch? _stopwatch;

    // Registration metadata captured at RegisterCommand time. The
    // finalizer-once path in ConsoleWorker uses these fields (not
    // whatever the worker's adapter currently says) so a cached
    // snapshot stays consistent with the configuration that was in
    // effect when the command ran.
    private string? _registeredShellFamily;
    private string? _registeredDisplayName;
    private int _registeredPostPromptSettleMs;
    private string? _registeredInputEchoStrategy;
    private string _registeredInputEchoLineEnding = "\n";
    private string _registeredPtyPayload = "";
    // Per-registration inline-delivery routing id. The worker allocates
    // a monotonic id before calling RegisterCommand and stores it in
    // its _inlineDeliveriesById dictionary alongside the inline TCS.
    // The tracker stamps it onto the emitted snapshot so the worker's
    // finalize-once path can deliver to the correct awaiter even when
    // two concurrent execute requests sneak past the Busy gate and
    // reach the finalize step on separate capture instances. Null when
    // the worker chose not to wire an inline delivery (no ordinary
    // flow does this today; tests that invoke the tracker directly
    // without the worker can also leave it unset).
    private long? _registeredInlineDeliveryId;

    // Last known cwd from any prompt (AI command or user command)
    private string? _lastKnownCwd;
    public string? LastKnownCwd { get { lock (_lock) return _lastKnownCwd; } }

    // Provenance counter: number of user-typed commands that have completed
    // since the last AI RegisterCommand. The proxy's drift detector reads
    // this via get_status to decide whether the shell's live cwd still
    // reflects AI's intended state (counter == 0) or may have been moved by
    // the human (counter > 0). Incremented exactly once per user command at
    // OSC A (PromptStart) when it closes a user-busy cycle, and reset to 0
    // on every RegisterCommand. Replaces the old "compare LastAiCwd vs live
    // cwd snapshot" heuristic, which misattributed internal state lag
    // (standby rotation, race between RecordShellCwd and the next
    // get_status) as user-initiated drift.
    private int _userCmdsSinceLastAi;

    /// <summary>
    /// Number of user-typed commands completed since the last
    /// <see cref="RegisterCommand"/>. Used by the proxy to decide whether
    /// the live cwd can be trusted as AI's intended state. Reset to 0 on
    /// each RegisterCommand.
    /// </summary>
    public int UserCmdsSinceLastAi { get { lock (_lock) return _userCmdsSinceLastAi; } }

    // Terminal dimensions for VT-medium viewport. Set by ConsoleWorker
    // at PTY creation and on each resize.
    private int _terminalCols = 120;
    private int _terminalRows = 30;

    // Live VT-100 interpreter — advanced per FeedOutput, reset on the
    // same triggers as the ring buffer (first OSC A, every OSC C,
    // ClearRecentOutput). GetRecentOutputSnapshot returns Render()
    // directly so peek_console no longer pays the cost of re-parsing the
    // 4 KB ring through VtLite on every call. Fed from cleaned (OSC-
    // stripped) output; ConsoleWorker keeps a separate _vtState fed
    // from the raw PTY chunk for DSR-reply cursor tracking on Unix.
    private VtLiteState _vtState = new(30, 120);

    public void SetTerminalSize(int cols, int rows)
    {
        lock (_lock)
        {
            _terminalCols = cols;
            _terminalRows = rows;
            // Re-create the VT interpreter with the new geometry. Grid
            // cells are fixed-size; existing cursor coordinates would be
            // invalid after a resize anyway, and the shell repaints.
            _vtState = new VtLiteState(rows, cols);
        }
    }

    public bool Busy => _isAiCommand || _userCommandBusy || _userBusyByPolling;

    /// <summary>
    /// True while an AI command is in flight. Distinct from
    /// <see cref="Busy"/> (which also covers user-typed commands the
    /// tracker never registered). The worker uses this to gate echo /
    /// hold-gate behaviour that only makes sense for AI-dispatched
    /// work.
    /// </summary>
    public bool IsAiCommand { get { lock (_lock) return _isAiCommand; } }

    /// <summary>
    /// Update the polling-based user-busy hint. Used by ConsoleWorker for
    /// shells (cmd) that have no preexec hook and therefore can't fire OSC
    /// 633 C/B markers when the user starts a command. Skipped during AI
    /// command execution because the AI tracker has its own busy state.
    /// </summary>
    public void SetUserBusyHint(bool busy)
    {
        lock (_lock)
        {
            if (_isAiCommand) return;
            _userBusyByPolling = busy;
        }
    }

    /// <summary>
    /// Text of the AI command currently executing, or null when idle / the
    /// active command is user-initiated (we don't know what the human typed).
    /// </summary>
    public string? RunningCommand
    {
        get { lock (_lock) return _isAiCommand ? _commandSent : null; }
    }

    /// <summary>
    /// Elapsed seconds since the current AI command was registered, or null
    /// when no AI command is tracked.
    /// </summary>
    public double? RunningElapsedSeconds
    {
        get { lock (_lock) return _isAiCommand ? _stopwatch?.Elapsed.TotalSeconds : null; }
    }

    // MCP protocol imposes a 3-minute (180s) ceiling on tool-call
    // response latency. Past that the client stops listening and
    // anything we try to write is discarded. To guarantee that a
    // busy execute_command always gets a valid response back before
    // that ceiling, the tracker caps its own timeout at 170s and
    // fails the awaiting task at the cap — the execute_command
    // handler sees the TimeoutException, returns a "timed out,
    // cached for next tool call" response, and the still-running
    // command's eventual snapshot flows to the worker's cache via
    // the same finalize-once path so the next drain picks it up.
    public const int PreemptiveTimeoutMs = 170_000;

    /// <summary>
    /// All per-command input the worker needs to persist in the tracker
    /// at registration time. Keeping this as a single parameter object
    /// (rather than a growing method signature) makes it trivial to
    /// extend without churning every caller, and documents which
    /// metadata is consumed on the finalize-once path.
    /// </summary>
    public sealed record CommandRegistration(
        string CommandText,
        string PtyPayload,
        string InputEchoLineEnding,
        string? InputEchoStrategy,
        string? ShellFamily,
        string? DisplayName,
        int PostPromptSettleMs,
        int TimeoutMs = PreemptiveTimeoutMs,
        long? InlineDeliveryId = null);

    /// <summary>
    /// Register an AI-initiated command. Returns a Task that completes
    /// with a <see cref="CompletedCommandSnapshot"/> once the shell
    /// signals command completion via OSC markers. The worker is
    /// responsible for finalization — see <see cref="CompletedCommandSnapshot"/>.
    ///
    /// The timeout is capped at <see cref="PreemptiveTimeoutMs"/> (170s)
    /// so the execute_command handler can always return a meaningful
    /// response within the MCP protocol's 3-minute window, even when
    /// the underlying command keeps running in the background.
    /// </summary>
    public Task<CompletedCommandSnapshot> RegisterCommand(CommandRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);

        // 0 is the "interactive" sentinel — caller wants ripple to flip
        // to cache mode as soon as possible so the MCP response comes
        // back without blocking on a shell waiting for user input.
        // Any other value is clamped to the MCP 170s ceiling so the
        // response path stays live.
        var timeoutMs = registration.TimeoutMs;
        if (timeoutMs < 0) timeoutMs = 0;
        timeoutMs = Math.Min(timeoutMs, PreemptiveTimeoutMs);

        Task<CompletedCommandSnapshot> task;
        lock (_lock)
        {
            // A live _tcs means a command is still awaiting its OSC
            // cycle (snapshot has NOT been emitted yet). That's the
            // "genuinely overlapping" case and must refuse.
            //
            // _isAiCommand alone is not enough to refuse: the finalize-
            // once path now keeps _isAiCommand set across the settle +
            // cache-insert window AFTER BuildAndReleaseSnapshot has
            // cleared _tcs (so Busy stays true and get_status doesn't
            // flicker to "standby"). If a back-to-back RegisterCommand
            // lands during that window the tracker must accept it; the
            // stale finalize's ReleaseAiCommand will no-op via the
            // generation check below.
            if (_tcs != null)
                throw new InvalidOperationException("Another command is already executing.");

            // A still-present _settlingCapture here means the previous
            // finalize hasn't called DetachSettlingCapture yet (a
            // back-to-back command arrived before the worker's slice
            // read completed). Cut the tracker's write path so the new
            // command's PTY bytes route to _capture exclusively and
            // don't bleed into the old capture's tail. Disposal of the
            // detached capture is owned by the worker's finalize-once
            // path (ConsoleWorker calls snapshot.Capture.Complete()
            // after reading its slice); the tracker must not dispose
            // here or an off-thread slice read would hit
            // ObjectDisposedException.
            _settlingCapture = null;

            _tcs = new TaskCompletionSource<CompletedCommandSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            // Capture the Task reference locally BEFORE wiring up the
            // CancellationTokenSource. When timeoutMs is 0 (or very
            // small), `new CancellationTokenSource(timeoutMs)` can be
            // already-cancelled by the time we call Register, which
            // invokes FlipToCacheMode synchronously on this thread.
            // FlipToCacheMode sets `_tcs = null` as part of detaching
            // the broken response channel, so `return _tcs.Task` below
            // would NullReferenceException. Capturing `task` locally
            // means the null-out is harmless — we return the captured
            // (already-faulted) reference.
            task = _tcs.Task;
            _isAiCommand = true;
            // A fresh AI command resets the provenance counter: anything
            // the human did before this point is no longer a source of
            // "potential drift" from the AI's perspective — AI is about
            // to define a new "last known" state via this command.
            _userCmdsSinceLastAi = 0;
            // Bump the generation so any in-flight ReleaseAiCommand
            // from a previous command's finalize will no-op against the
            // new command. See ReleaseAiCommand for the token check.
            _commandGeneration++;
            _promptStartOffset = null;
            _capturedBaselineSnapshot = null;
            _capture = new CommandOutputCapture();
            _exitCode = 0;
            _errorCount = 0;
            _cwd = null;
            _commandSent = registration.CommandText;
            _registeredShellFamily = registration.ShellFamily;
            _registeredDisplayName = registration.DisplayName;
            _registeredPostPromptSettleMs = Math.Max(0, registration.PostPromptSettleMs);
            _registeredInputEchoStrategy = registration.InputEchoStrategy;
            _registeredInputEchoLineEnding = registration.InputEchoLineEnding ?? "\n";
            _registeredPtyPayload = registration.PtyPayload ?? "";
            _registeredInlineDeliveryId = registration.InlineDeliveryId;
            _stopwatch = Stopwatch.StartNew();

            // Setup preemptive timeout. When the 170s cap (or the
            // caller's shorter request) fires, we surface a
            // TimeoutException on the awaiting task; the worker turns
            // that into a "cached for next tool call" response, and
            // the in-flight snapshot eventually routes through the
            // same finalize-once path (but the worker cache, not the
            // inline channel). See FlipToCacheMode below.
            var timeoutCts = new CancellationTokenSource(timeoutMs);
            _timeoutReg = timeoutCts.Token.Register(FlipToCacheMode);
        }

        return task;
    }

    /// <summary>
    /// Detach the in-flight AI command from its awaiting caller. Called
    /// when the pipe handler decides the response channel to the
    /// original caller has been lost — either because a preemptive
    /// 170s timer fired, or because a new pipe request arrived while
    /// this console was still busy (proving the caller is no longer
    /// listening for the previous response). The blocked
    /// <c>_tcs</c> is failed with a <see cref="TimeoutException"/> so
    /// HandleExecuteAsync unwinds and returns a "cached for next tool
    /// call" response; the real <see cref="CompletedCommandSnapshot"/>
    /// arrives later via <see cref="SnapshotProduced"/> and the worker
    /// caches it there. Safe no-op if there is no in-flight AI command
    /// or if the TCS is already completed.
    /// </summary>
    public void FlipToCacheMode()
    {
        lock (_lock)
        {
            if (!_isAiCommand) return;
            if (_tcs != null && !_tcs.Task.IsCompleted)
            {
                var tcs = _tcs;
                _tcs = null; // Detach — output capture continues
                // Do NOT dispose _timeoutReg here — FlipToCacheMode can be
                // invoked FROM the registration callback (RegisterCommand
                // wires the 170s preemptive timeout to this method), and
                // CancellationTokenRegistration.Dispose blocks until the
                // callback finishes, which would deadlock against the
                // thread currently inside the callback. The registration
                // is cleaned up when Resolve() / AbortPending() fires.
                tcs.TrySetException(new TimeoutException("Response channel lost, command flipped to cache mode"));
            }
        }
    }

    /// <summary>
    /// Event raised on primary command completion, regardless of whether
    /// an inline caller is still listening. The worker subscribes once
    /// and routes every snapshot through its shared finalize-once path
    /// — inline vs. cache choice happens there based on whether the
    /// registration's awaited task is still attached.
    /// </summary>
    public event Action<CompletedCommandSnapshot>? SnapshotProduced;

    /// <summary>
    /// Mark the command-start position as the start of the capture for
    /// shells that cannot emit OSC 633 C at the right moment. cmd.exe
    /// has no preexec hook, so there is no way for its prompt
    /// integration to fire OSC C before the command output begins —
    /// this method lets the worker paper over that gap so AI commands
    /// can still resolve when cmd's PROMPT fires OSC D + OSC A.
    ///
    /// Safe no-op when no AI command is active or when OSC C already fired.
    /// </summary>
    public void SkipCommandStartMarker()
    {
        lock (_lock)
        {
            if (!_isAiCommand) return;
            _capture?.ForceCommandStartAtZero();
        }
    }

    /// <summary>
    /// Fail any in-flight RegisterCommand with a "shell exited" error so the
    /// HandleExecuteAsync call blocked on it unwinds promptly. Called from
    /// the worker's read loop when the child shell process goes away.
    /// </summary>
    public void AbortPending()
    {
        CommandOutputCapture? orphaned = null;
        CancellationTokenRegistration? timeoutToDispose = null;
        lock (_lock)
        {
            if (_tcs != null && !_tcs.Task.IsCompleted)
            {
                var tcs = _tcs;
                _tcs = null;
                // Dispose outside the lock: CancellationTokenRegistration.Dispose
                // blocks until any in-flight callback (FlipToCacheMode)
                // finishes, and that callback reacquires _lock — classic
                // AB-BA deadlock. Detaching _tcs above makes the callback
                // a no-op when it re-enters, so releasing the Dispose
                // outside the lock is safe.
                timeoutToDispose = _timeoutReg;
                _timeoutReg = default;
                // The capture was never handed to a snapshot (no
                // OSC cycle completed), so its scratch file would
                // leak if ResetPerCommandState just nulled the
                // reference. Dispose outside the lock below.
                orphaned = _capture;
                // Shell exit: nobody will finalize this command and
                // therefore nobody will call ReleaseAiCommand. Clear
                // the AI-busy flag here so the worker doesn't stay
                // in "busy" forever on a dead shell.
                _isAiCommand = false;
                ResetPerCommandState();
                tcs.TrySetException(new InvalidOperationException("Shell process exited before the command completed."));
            }
        }

        timeoutToDispose?.Dispose();
        if (orphaned is not null)
        {
            try { orphaned.Complete(); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Feed an OSC event from the parser. The caller must pass events in
    /// source order, interleaved with matching FeedOutput calls, so that
    /// the capture's Length at event-dispatch time is the offset at
    /// which the event fired in the original byte stream.
    /// </summary>
    public void HandleEvent(OscParser.OscEvent evt)
    {
        CompletedCommandSnapshot? emit = null;
        CancellationTokenRegistration? timeoutToDispose = null;
        lock (_lock)
        {
            // Always track cwd, even outside AI commands (for user manual cd)
            if (evt.Type == OscParser.OscEventType.Cwd)
                _lastKnownCwd = evt.Cwd;

            // OSC C (CommandExecuted) is the cleanest boundary to reset the
            // recent-output ring for peek_console / timeout partialOutput.
            // Everything before OSC C is PSReadLine typing noise — per-
            // keystroke re-rendering, inline history prediction, cursor
            // dancing via absolute CUP — which no amount of VT-lite
            // interpretation can sanitise perfectly because PSReadLine
            // uses terminal-absolute coordinates that don't line up with
            // our ring's start. Clearing on OSC C gives peek a clean
            // "everything since the current command started running"
            // view, which is exactly the question execute_command's
            // timeout asks ("what is this stuck command doing right now?").
            // The command text itself is still reported via the
            // runningCommand metadata field, so peek callers never lose
            // context about what's running.
            if (evt.Type == OscParser.OscEventType.CommandExecuted)
                ResetRecentBuffer();

            // Mark the shell as "ready" on the first PromptStart. Until then,
            // ignore user-command busy transitions — the initial OSC B that
            // integration scripts emit at startup (and the subsequent prompt
            // setup) would otherwise leave the new console looking busy and
            // cause HandleExecuteAsync to reject the first incoming command.
            // The FIRST PromptStart is also when we clear the recent-output
            // ring: anything before it is pre-shell boot noise (or prior-
            // session residue on a reused standby console) that isn't part
            // of what the user would see on a fresh terminal.
            if (evt.Type == OscParser.OscEventType.PromptStart && !_shellReady)
                ResetRecentBuffer();
            if (evt.Type == OscParser.OscEventType.PromptStart)
                _shellReady = true;

            // When no AI command is active, track whether the human user is
            // mid-command in the terminal. OSC B / OSC C both mean "a command
            // is about to start / is starting" (pwsh fires B on Enter, bash
            // and zsh fire C from preexec); OSC A means the shell is back at
            // a prompt. This lets get_status report "busy" for user commands
            // too, so execute_command won't shove an AI command into a PTY
            // that the human is actively using.
            if (!_isAiCommand)
            {
                if (!_shellReady) return;

                switch (evt.Type)
                {
                    case OscParser.OscEventType.CommandInputStart:
                    case OscParser.OscEventType.CommandExecuted:
                        _userCommandBusy = true;
                        break;
                    case OscParser.OscEventType.PromptStart:
                        // Closing a user-busy cycle = one completed user
                        // command. Increment the provenance counter so the
                        // proxy's drift detector (ConsoleManager.PlanExecutionAsync)
                        // can see, on the next AI call's get_status, that the
                        // human has touched the terminal since the last AI
                        // command and the live cwd is therefore not
                        // necessarily AI's intended state. Gate on
                        // _userCommandBusy so a bare OSC A (shell startup,
                        // back-to-back prompt repaint) doesn't inflate the
                        // count without an actual command cycle.
                        if (_userCommandBusy) _userCmdsSinceLastAi++;
                        _userCommandBusy = false;
                        break;
                }
                return;
            }

            switch (evt.Type)
            {
                case OscParser.OscEventType.CommandExecuted:
                    // OSC C: PreCommandLookupAction has fired, everything
                    // preceding this point in the capture is AcceptLine
                    // finalize noise. Record the position so the finalizer
                    // knows where the real command output begins.
                    _capture?.MarkCommandStart();
                    break;

                case OscParser.OscEventType.CommandFinished:
                    // OSC D: command is done, the prompt function is about to
                    // print the prompt. Snapshot the position here so the
                    // prompt text is excluded from the result.
                    _exitCode = evt.ExitCode;
                    _capture?.MarkCommandEnd();
                    break;

                case OscParser.OscEventType.Cwd:
                    _cwd = evt.Cwd;
                    break;

                case OscParser.OscEventType.ErrorCount:
                    // OSC E: PowerShell prompt fn reports the $Error.Count
                    // delta over the just-finished pipeline. Carry it
                    // through to the snapshot so the proxy can format
                    // "Errors: N" in the status line.
                    _errorCount = evt.ErrorCount;
                    break;

                case OscParser.OscEventType.PromptStart:
                    // Only treat OSC A as the end of an AI command when we
                    // actually saw a command cycle — both OSC C (command
                    // started) and OSC D (command finished) must have
                    // fired since RegisterCommand. A bare OSC A without
                    // that framing means the shell just printed a prompt
                    // unrelated to our AI command — most commonly the
                    // very first prompt after pwsh startup, which can
                    // arrive AFTER RegisterCommand if the shell was slow
                    // to initialize and WaitForReady's timeout fell
                    // through. Resolving here would hand the AI the
                    // reason banner / PSReadLine prediction rendering as
                    // "command output" and leave the real command
                    // unanswered. Ignore this OSC A and wait for the
                    // real one.
                    var window = _capture?.CommandWindow ?? (null, null);
                    if (window.Start.HasValue && window.End.HasValue)
                    {
                        // Snapshot the capture's length AT the moment
                        // OSC A fired — events are dispatched in source
                        // order interleaved with FeedOutput, so
                        // _capture.Length here is the byte offset
                        // separating trailing command output (arrived
                        // before OSC A) from prompt text (arrives after).
                        // The finalizer uses this as a hard cap on
                        // effectiveEnd so shells that stream a prompt
                        // immediately after OSC A (bash `$ `, cmd
                        // PROMPT) never leak prompt chars into the
                        // command's cleaned output.
                        _promptStartOffset = _capture?.Length;
                        emit = BuildAndReleaseSnapshot(window.Start.Value, window.End.Value, out timeoutToDispose);
                    }
                    break;
            }
        }

        // Dispose the preemptive-timeout registration outside the lock:
        // Dispose blocks until any in-flight FlipToCacheMode callback
        // finishes, and that callback reacquires _lock, so disposing
        // under the lock would deadlock.
        timeoutToDispose?.Dispose();

        // Raise the snapshot event OUTSIDE the tracker lock so subscribers
        // (the worker's finalize-once path) can read from _capture without
        // racing our next FeedOutput append or HandleEvent call. The
        // snapshot itself owns the capture reference, and Resolve has
        // already detached it from _capture so further appends can't
        // corrupt the finalization window.
        if (emit != null)
            RaiseSnapshot(emit);
    }

    /// <summary>
    /// Feed cleaned output from the PTY (OSC stripped). While an AI
    /// command is running the bytes land in the active capture;
    /// after OSC A has fired and the snapshot has been handed off,
    /// trailing bytes still belonging to the command result keep
    /// flowing into <c>_settlingCapture</c> until the worker detaches
    /// it. Outside any AI command capture context the bytes are
    /// ignored by the tracker — the worker's finalize-once path owns
    /// the post-prompt settle window for AI commands, and nothing
    /// reads user-output bytes after a prompt anymore. In every
    /// branch the text is also mirrored into <c>_recentBuf</c>, a
    /// small rolling window that peek_console and execute timeout
    /// responses return as "what's on screen right now" context.
    /// </summary>
    public void FeedOutput(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        lock (_lock)
        {
            AppendRecent(text);
            _vtState.Feed(text.AsSpan());

            // Prefer the in-flight capture while a command is being
            // tracked; fall through to _settlingCapture so bytes that
            // arrive after OSC A (but before the worker's settle
            // deadline) still reach the same capture object the
            // finalizer will slice.
            var target = _capture ?? _settlingCapture;
            target?.Append(text);
        }
    }

    private void ResetRecentBuffer()
    {
        _recentPos = 0;
        _recentLen = 0;
        // Keep the VT interpreter in lockstep with the ring so
        // peek_console after a clear doesn't leak state from before
        // the OSC A / OSC C boundary.
        _vtState = new VtLiteState(_terminalRows, _terminalCols);
    }

    /// <summary>
    /// Public hook to drop everything currently sitting in the
    /// recent-output ring. Called by the ConsoleWorker when a new
    /// proxy claims this console, so peek_console / timeout
    /// partialOutput don't start out leaking bytes from whatever
    /// the previous owner was running.
    /// </summary>
    public void ClearRecentOutput()
    {
        lock (_lock) ResetRecentBuffer();
    }

    /// <summary>
    /// Diagnostic: return the raw bytes currently in the recent-output
    /// ring, in the order they were received, without any ANSI or
    /// VT processing. Used to debug VtLite interpretation issues
    /// (when peek_console shows content that isn't in the byte
    /// stream).
    /// </summary>
    public string GetRawRecentBytes()
    {
        lock (_lock)
        {
            if (_recentLen == 0) return "";
            if (_recentLen < RecentOutputCapacity)
                return new string(_recentBuf, 0, _recentLen);

            var tmp = new char[RecentOutputCapacity];
            var firstPart = RecentOutputCapacity - _recentPos;
            Array.Copy(_recentBuf, _recentPos, tmp, 0, firstPart);
            Array.Copy(_recentBuf, 0, tmp, firstPart, _recentPos);
            return new string(tmp);
        }
    }

    private void AppendRecent(string text)
    {
        if (text.Length == 0) return;

        // If this single write is larger than the ring, only the tail of
        // it can ever survive — fast-path by copying the last N chars
        // straight into buf[0..N] and setting pos/len appropriately.
        if (text.Length >= RecentOutputCapacity)
        {
            text.AsSpan(text.Length - RecentOutputCapacity).CopyTo(_recentBuf);
            _recentPos = 0;
            _recentLen = RecentOutputCapacity;
            return;
        }

        foreach (var ch in text)
        {
            _recentBuf[_recentPos] = ch;
            _recentPos = (_recentPos + 1) % RecentOutputCapacity;
            if (_recentLen < RecentOutputCapacity) _recentLen++;
        }
    }

    /// <summary>
    /// Tail-bounded snapshot of what the current in-flight AI command has
    /// produced so far. Returns an empty string outside an AI command.
    /// Used by execute_command's timeout response as the
    /// <c>partialOutput</c> payload so the AI sees only the bytes
    /// produced by the command it just launched — NOT whatever the
    /// shell had on screen from previous commands whose results have
    /// already been drained, and NOT a preview of any oversize spill
    /// file (those only exist after finalization, which by definition
    /// has not happened yet when this is called).
    /// </summary>
    public string GetCurrentCommandSnapshot(int? maxChars = null)
    {
        lock (_lock)
        {
            if (!_isAiCommand || _capture is null) return "";
            return _capture.GetCurrentCommandSnapshot(maxChars);
        }
    }

    /// <summary>
    /// Snapshot the rolling recent-output window as a string, processed
    /// through a VT-lite interpreter so in-place redraws from PSReadLine,
    /// progress bars, and cursor-positioning escape sequences collapse to
    /// their final state. Both peek_console and execute timeout responses
    /// use this so the AI sees what the console actually displays, not
    /// the concatenated history of every intermediate redraw.
    /// </summary>
    public string GetRecentOutputSnapshot()
    {
        lock (_lock) return _vtState.Render();
    }

    /// <summary>
    /// Resolve the in-flight AI command into a <see cref="CompletedCommandSnapshot"/>.
    /// Completes the awaiting caller's task (if still attached), hands
    /// the capture handle to the snapshot, and clears the tracker's
    /// per-command capture state. Deliberately leaves
    /// <c>_isAiCommand = true</c> — the worker's finalize-once path
    /// calls <see cref="ReleaseAiCommand"/> after the result has
    /// landed in the cache (or been delivered inline), so the
    /// tracker's <see cref="Busy"/> / <see cref="Status"/> view stays
    /// semantically "busy" across the entire settle + finalize
    /// window. Without that, a fast-polling client can observe
    /// <c>{status: standby, hasCachedOutput: false}</c> between
    /// emission and cache insertion and treat the command as lost.
    /// Caller must hold <see cref="_lock"/>.
    /// </summary>
    private CompletedCommandSnapshot? BuildAndReleaseSnapshot(
        long commandStart,
        long commandEnd,
        out CancellationTokenRegistration? timeoutToDispose)
    {
        timeoutToDispose = null;
        if (_capture is null) return null;

        var duration = _stopwatch?.Elapsed.TotalSeconds.ToString("F1") ?? "0.0";

        // Move ownership of the active capture into _settlingCapture
        // BEFORE ResetPerCommandState nulls _capture. FeedOutput
        // continues routing post-OSC-A bytes into the same object
        // via this reference so the worker's settle window sees real
        // length growth instead of polling a frozen capture. The
        // worker owns the disposal lifetime from here on and detaches
        // by calling DetachSettlingCapture once it has read its final
        // slice.
        var capture = _capture;
        _settlingCapture = capture;

        var snapshot = new CompletedCommandSnapshot(
            Capture: capture,
            CommandStart: commandStart,
            CommandEnd: commandEnd,
            ExitCode: _exitCode,
            Duration: duration,
            Cwd: _cwd,
            Command: _commandSent,
            ShellFamily: _registeredShellFamily,
            DisplayName: _registeredDisplayName,
            PostPromptSettleMs: _registeredPostPromptSettleMs,
            InputEchoStrategy: _registeredInputEchoStrategy,
            InputEchoLineEnding: _registeredInputEchoLineEnding,
            PtyPayloadBaseline: _registeredPtyPayload,
            PromptStartOffset: _promptStartOffset,
            Generation: _commandGeneration,
            InlineDeliveryId: _registeredInlineDeliveryId,
            VtBaseline: _capturedBaselineSnapshot,
            ErrorCount: _errorCount);

        // Hand the inline caller (if still attached) the snapshot
        // directly; the worker's shared finalize-once path consumes
        // it via the same code either way. If the caller is gone
        // (FlipToCacheMode already detached _tcs or the timeout
        // fired), we still raise the event so the worker's cache
        // branch picks the snapshot up.
        var tcs = _tcs;
        _tcs = null;
        // Dispose outside the lock: CancellationTokenRegistration.Dispose
        // blocks until any in-flight callback (FlipToCacheMode)
        // finishes, and that callback reacquires _lock — classic
        // AB-BA deadlock. Detaching _tcs above makes the callback
        // a no-op when it re-enters, so the caller releases the
        // Dispose after exiting the lock.
        timeoutToDispose = _timeoutReg;
        _timeoutReg = default;
        ResetPerCommandState();
        tcs?.TrySetResult(snapshot);

        return snapshot;
    }

    /// <summary>
    /// Clear the tracker's AI-busy flag after the worker's finalize-
    /// once path has delivered (inline) or cached the snapshot. The
    /// <paramref name="generation"/> check prevents a late release
    /// from a stale finalize from clobbering a newer command that
    /// registered while the previous finalize was still running:
    /// <see cref="RegisterCommand"/> bumps <see cref="_commandGeneration"/>
    /// on every new command, so the stale release's token will no
    /// longer match and this method becomes a no-op. Safe to call
    /// from any thread; the tracker reacquires <see cref="_lock"/>.
    /// </summary>
    public void ReleaseAiCommand(long generation)
    {
        lock (_lock)
        {
            if (generation != _commandGeneration) return;
            _isAiCommand = false;
        }
    }

    /// <summary>
    /// Worker handoff: the finalize-once path calls this after reading
    /// its final slice from the capture. Clears the tracker's
    /// settling-capture reference so no further PTY bytes route into
    /// the soon-to-be-disposed capture. No-op if a newer
    /// RegisterCommand has already replaced the reference (guards
    /// against a late finalize racing the next command's start).
    /// </summary>
    internal void DetachSettlingCapture(CommandOutputCapture capture)
    {
        if (capture is null) return;
        lock (_lock)
        {
            if (ReferenceEquals(_settlingCapture, capture))
                _settlingCapture = null;
        }
    }

    /// <summary>
    /// Collapse a command into a single line for use in a status line:
    /// strip leading/trailing whitespace + newlines, take only the first
    /// remaining line, mark with "..." if any content was dropped (either
    /// extra lines or the 60-char overflow tail). Returning a guaranteed
    /// single line keeps the status line "1 line = 1 result" invariant
    /// even when the user sent a multi-line script.
    /// </summary>
    public static string TruncateForStatusLine(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return "";

        var trimmed = command.Trim();
        var newlineIndex = trimmed.IndexOfAny(['\r', '\n']);
        var firstLine = newlineIndex >= 0 ? trimmed[..newlineIndex].TrimEnd() : trimmed;
        var hadExtraLines = firstLine.Length < trimmed.Length;

        if (firstLine.Length > 60)
            return firstLine[..60] + "...";
        if (hadExtraLines)
            return firstLine + "...";
        return firstLine;
    }

    private void RaiseSnapshot(CompletedCommandSnapshot snapshot)
    {
        try
        {
            SnapshotProduced?.Invoke(snapshot);
        }
        catch
        {
            // Subscribers (the worker) already log their own errors;
            // the tracker must not let a broken listener break the
            // next command's registration. Swallowing here keeps the
            // tracker's invariants intact even when the finalize-once
            // path throws.
        }
    }

    /// <summary>
    /// Test hook — force-seed the ring buffer. Only used by
    /// CommandTrackerTests to verify GetRecentOutputSnapshot without
    /// having to plumb a full PTY.
    /// </summary>
    internal void SeedRecentForTests(string text)
    {
        lock (_lock) AppendRecent(text);
    }

    /// <summary>
    /// Clear the per-command bookkeeping after a snapshot has been
    /// emitted (normal path) or a pending command has been aborted
    /// (shell-exit path). Intentionally does NOT clear
    /// <see cref="_isAiCommand"/>: on the normal path the worker's
    /// finalize-once handler keeps the AI-busy flag alive until
    /// <see cref="ReleaseAiCommand"/> fires, so the tracker stays
    /// <see cref="Busy"/> across the settle + cache-insert window.
    /// The abort path flips the flag explicitly before calling this
    /// helper because nobody will ever finalize the orphaned
    /// capture. Caller must hold <see cref="_lock"/>.
    /// </summary>
    private void ResetPerCommandState()
    {
        // The capture reference is handed off to the snapshot; the
        // finalizer owns its lifetime from here. Null out the tracker
        // field so the next RegisterCommand starts fresh.
        _capture = null;
        _promptStartOffset = null;
        _commandSent = "";
        _stopwatch = null;
        _registeredShellFamily = null;
        _registeredDisplayName = null;
        _registeredPostPromptSettleMs = 0;
        _registeredInputEchoStrategy = null;
        _registeredInputEchoLineEnding = "\n";
        _registeredPtyPayload = "";
        _registeredInlineDeliveryId = null;
        // _recentBuf survives deliberately — it's a rolling window
        // spanning command boundaries so peek_console can still show
        // what was on screen just before/during the next call.
    }
}
