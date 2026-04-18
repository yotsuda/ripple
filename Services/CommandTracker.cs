using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Ripple.Services;

/// <summary>
/// Tracks command lifecycle using OSC 633 events.
/// Runs in the console worker process.
///
/// Flow:
///   RegisterCommand() → command written to PTY →
///   OSC C (CommandExecuted) → output captured →
///   OSC D;{exitCode} (CommandFinished) → OSC P;Cwd=... → OSC A (PromptStart) →
///   Resolve() with { output, exitCode, cwd } → proxy drains post-primary
///   buffer via WaitAndDrainPostOutputAsync (stable_ms comes from the
///   adapter's output.post_prompt_settle_ms)
///
/// On timeout: caller's Task is cancelled, but output capture continues.
/// When the shell eventually completes, the result is cached for WaitForCompletion.
/// </summary>
public class CommandTracker
{
    private const int MaxAiOutputBytes = 1024 * 1024; // 1MB
    private const int PostPrimaryMaxBytes = 64 * 1024; // 64 KB for trailing-output delta

    // Rolling window of everything the PTY has emitted recently, regardless
    // of whether an AI command was in flight or the user typed something
    // themselves. Used by (a) execute_command's timeout response, which
    // returns the tail of this buffer as `partialOutput` so the AI can
    // diagnose stuck commands, and (b) the peek_console MCP tool, which
    // lets the AI inspect a busy console on demand.
    // Small and fixed-size so the token cost of returning it stays bounded.
    private const int RecentOutputCapacity = 4096;

    // Non-SGR ANSI escape sequence pattern.
    // Strips cursor movement, erase, and other control sequences, but preserves
    // SGR (Select Graphic Rendition, ending in 'm') so color information is
    // passed through to the AI for context (e.g. red errors, green success).
    // OSC sequences (window title etc.) are also stripped.
    //
    // Alternatives covered:
    //   \x1b[...letter       — CSI sequences (cursor, erase, SGR "m" preserved)
    //   \x1b]...\x07         — OSC with BEL terminator
    //   \x1b]...\x1b\        — OSC with ST terminator
    //   \x1b[()][0-9A-B]     — Character set designation (G0/G1)
    //   \x1b[=>]             — DECKPAM / DECKPNM keypad mode (PSReadLine emits
    //                          these around prompt redraws; leaving them through
    //                          makes the ESC byte invisible and the trailing
    //                          '=' or '>' look like a stray char at the start
    //                          or end of captured output).
    private static readonly Regex AnsiRegex = new(
        @"\x1b\[[0-9;?]*[a-ln-zA-Z]|\x1b\][^\x07]*\x07|\x1b\][^\x1b]*\x1b\\|\x1b[()][0-9A-B]|\x1b[=>]",
        RegexOptions.Compiled);


    // pwsh prompt pattern: "PS <drive>:\<path>> "
    // ConPTY may emit this glued to the previous line via cursor positioning,
    // so we strip it inline before splitting on \n.
    private static readonly Regex PwshPromptInline = new(
        @"PS [A-Z]:\\[^\n>]*> ?",
        RegexOptions.Compiled);

    private readonly object _lock = new();
    private TaskCompletionSource<CommandResult>? _tcs;
    private CancellationTokenRegistration _timeoutReg;
    private bool _isAiCommand;
    private bool _userCommandBusy;
    private bool _userBusyByPolling; // set by ConsoleWorker's CPU/child polling for cmd
    private bool _shellReady; // flipped on first PromptStart; gates user-busy tracking
    private string _aiOutput = "";
    private bool _truncated;

    // Circular buffer storing the last RecentOutputCapacity chars of the
    // PTY output stream (OSC-stripped but still with SGR colors/cursor
    // escapes in it — the read side does final cleanup). Written to from
    // FeedOutput unconditionally; snapshotted via GetRecentOutputSnapshot.
    private readonly char[] _recentBuf = new char[RecentOutputCapacity];
    private int _recentPos;     // next write index
    private int _recentLen;     // count of valid chars, capped at capacity
    private int _exitCode;
    private string? _cwd;
    private string _commandSent = "";
    private Stopwatch? _stopwatch;

    // Slice markers: _aiOutput position at OSC C (command about to run) and
    // OSC D (command finished). CleanOutput slices [commandStart..commandEnd)
    // from _aiOutput to produce the result, which cleanly excludes both the
    // AcceptLine finalize rendering that PSReadLine writes between OSC B and
    // OSC C and the prompt text that comes after OSC D / OSC A.
    private int _commandStart = -1;
    private int _commandEnd = -1;

    // Trailing output that arrives AFTER Resolve() has returned the primary
    // CommandResult — e.g. the pwsh prompt repaint or pwsh Format-Table rows
    // that finish streaming after the OSC A marker. The proxy drains this via
    // the drain_post_output pipe message after each successful execute.
    private readonly StringBuilder _postPrimaryOutput = new();

    // Cached results from commands whose response channel was lost —
    // either a preemptive 170s timeout fired and returned a timeout
    // response while the command kept running, or a new pipe request
    // arrived while busy (proving the prior response channel broke)
    // and FlipToCacheMode flipped the in-flight command. List, not
    // single, because multiple such events can stack up on one console
    // before any tool call drains the cache.
    private readonly List<CommandResult> _cachedResults = new();

    // When true, the in-flight command's Resolve() will append its
    // result to _cachedResults instead of handing it to _tcs. Set by
    // FlipToCacheMode when we decide the caller won't receive the
    // result via the normal channel. Cleared on RegisterCommand for
    // the next command's lifecycle.
    private bool _shouldCacheOnComplete;

    // Display identity set by ConsoleWorker at claim time. Used by
    // Resolve() to bake a self-describing status line into the
    // CommandResult before it is cached or delivered, so drain
    // consumers don't have to reformat with proxy-side metadata.
    private string? _displayName;
    private string? _shellFamily;

    // Last known cwd from any prompt (AI command or user command)
    private string? _lastKnownCwd;
    public string? LastKnownCwd { get { lock (_lock) return _lastKnownCwd; } }

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
    public bool HasCachedOutput => _cachedResults.Count > 0;
    public int CachedOutputCount { get { lock (_lock) return _cachedResults.Count; } }

    /// <summary>
    /// Set the display identity used for cached result status lines.
    /// Called by ConsoleWorker when a proxy claims this console, so
    /// any command that later gets cached already carries a status
    /// line that matches the display name / shell family the proxy
    /// uses elsewhere. No-op if called with nulls — the tracker will
    /// fall back to the pid-only form in BuildStatusLine.
    /// </summary>
    public void SetDisplayContext(string? displayName, string? shellFamily)
    {
        lock (_lock)
        {
            _displayName = displayName;
            _shellFamily = shellFamily;
        }
    }

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

    public record CommandResult(
        string Output,
        int ExitCode,
        string? Cwd,
        string? Command,
        string Duration,
        string StatusLine);

    // MCP protocol imposes a 3-minute (180s) ceiling on tool-call
    // response latency. Past that the client stops listening and
    // anything we try to write is discarded. To guarantee that a
    // busy execute_command always gets a valid response back before
    // that ceiling, the tracker caps its own timeout at 170s and
    // fires FlipToCacheMode at the cap — the execute_command handler
    // sees the TimeoutException, returns a "timed out, cached for
    // next tool call" response, and the still-running command's
    // eventual result is appended to _cachedResults for the next
    // drain to pick up.
    public const int PreemptiveTimeoutMs = 170_000;

    /// <summary>
    /// Register an AI-initiated command. Returns a Task that completes
    /// when the shell signals command completion via OSC markers.
    /// The timeout is capped at <see cref="PreemptiveTimeoutMs"/> (170s)
    /// so the execute_command handler can always return a meaningful
    /// response within the MCP protocol's 3-minute window, even when
    /// the underlying command keeps running in the background.
    /// </summary>
    public Task<CommandResult> RegisterCommand(string commandText, int timeoutMs = PreemptiveTimeoutMs)
    {
        // 0 is the "interactive" sentinel — caller wants ripple to flip
        // to cache mode as soon as possible so the MCP response comes
        // back without blocking on a shell waiting for user input.
        // Any other value is clamped to the MCP 170s ceiling so the
        // response path stays live.
        if (timeoutMs < 0) timeoutMs = 0;
        timeoutMs = Math.Min(timeoutMs, PreemptiveTimeoutMs);

        lock (_lock)
        {
            if (_isAiCommand)
                throw new InvalidOperationException("Another command is already executing.");

            _tcs = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            // Capture the Task reference locally BEFORE wiring up the
            // CancellationTokenSource. When timeoutMs is 0 (or very
            // small), `new CancellationTokenSource(timeoutMs)` can be
            // already-cancelled by the time we call Register, which
            // invokes FlipToCacheMode synchronously on this thread.
            // FlipToCacheMode sets `_tcs = null` as part of detaching
            // the broken response channel, so `return _tcs.Task` below
            // would NullReferenceException. Capturing `task` locally
            // means the null-out is harmless — we return the captured
            // (already-faulted) reference. See commit 8275846 for the
            // original NRE incident and the clamp-to-1000ms workaround
            // this replaces.
            var task = _tcs.Task;
            _isAiCommand = true;
            _aiOutput = "";
            _truncated = false;
            _exitCode = 0;
            _cwd = null;
            _commandSent = commandText;
            _commandStart = -1;
            _commandEnd = -1;
            _shouldCacheOnComplete = false;
            // _cachedResults is NOT cleared here — stale entries from
            // prior flipped commands stay in the list until a drain
            // (ConsumeCachedOutputs) picks them up. This is what lets
            // the next tool call salvage results that were flipped
            // because the prior response channel broke.
            _postPrimaryOutput.Clear();
            _stopwatch = Stopwatch.StartNew();

            // Setup preemptive timeout. When the 170s cap (or the
            // caller's shorter request) fires, we delegate to
            // FlipToCacheMode so the in-flight command's eventual
            // result ends up in _cachedResults instead of being
            // silently discarded. HandleExecuteAsync catches the
            // TimeoutException and turns it into a "cached for next
            // tool call" response the AI can act on.
            var timeoutCts = new CancellationTokenSource(timeoutMs);
            _timeoutReg = timeoutCts.Token.Register(FlipToCacheMode);

            return task;
        }
    }

    /// <summary>
    /// Flip the in-flight AI command to cache-on-complete mode. Called
    /// when the pipe handler decides the response channel to the
    /// original caller has been lost — either because a preemptive
    /// 170s timer fired, or because a new pipe request arrived while
    /// this console was still busy (proving the caller is no longer
    /// listening for the previous response). The blocked
    /// <c>_tcs</c> is failed with a <see cref="TimeoutException"/> so
    /// HandleExecuteAsync unwinds and returns a "cached for next tool
    /// call" response, and <see cref="Resolve"/> will later append
    /// the real result to <c>_cachedResults</c> instead of delivering
    /// it through the dead channel. Safe no-op if there is no
    /// in-flight AI command or if the TCS is already completed.
    /// </summary>
    public void FlipToCacheMode()
    {
        lock (_lock)
        {
            if (!_isAiCommand) return;
            _shouldCacheOnComplete = true;
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
                // is cleaned up by Resolve() / AbortPending() when the
                // command eventually completes.
                tcs.TrySetException(new TimeoutException("Response channel lost, command flipped to cache mode"));
            }
        }
    }

    /// <summary>
    /// Mark the command-start position as 0 (start of <c>_aiOutput</c>) for shells
    /// that cannot emit OSC 633 C at the right moment. cmd.exe has no preexec hook,
    /// so there is no way for its prompt integration to fire OSC C before the
    /// command output begins — this method lets the worker paper over that gap so
    /// AI commands can still resolve when cmd's PROMPT fires OSC D + OSC A.
    ///
    /// Safe no-op when no AI command is active or when OSC C already fired.
    /// </summary>
    public void SkipCommandStartMarker()
    {
        lock (_lock)
        {
            if (_isAiCommand && _commandStart < 0)
                _commandStart = 0;
        }
    }

    /// <summary>
    /// Fail any in-flight RegisterCommand with a "shell exited" error so the
    /// HandleExecuteAsync call blocked on it unwinds promptly. Called from
    /// the worker's read loop when the child shell process goes away.
    /// </summary>
    public void AbortPending()
    {
        lock (_lock)
        {
            if (_tcs != null && !_tcs.Task.IsCompleted)
            {
                var tcs = _tcs;
                _tcs = null;
                _timeoutReg.Dispose();
                Cleanup();
                tcs.TrySetException(new InvalidOperationException("Shell process exited before the command completed."));
            }
        }
    }

    /// <summary>
    /// Feed an OSC event from the parser. The caller must pass events in
    /// source order, interleaved with matching FeedOutput calls, so that
    /// _aiOutput.Length at event-dispatch time is the offset at which the
    /// event fired in the original byte stream.
    /// </summary>
    public void HandleEvent(OscParser.OscEvent evt)
    {
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
                        _userCommandBusy = false;
                        break;
                }
                return;
            }

            switch (evt.Type)
            {
                case OscParser.OscEventType.CommandExecuted:
                    // OSC C: PreCommandLookupAction has fired, everything
                    // preceding this point in _aiOutput is AcceptLine finalize
                    // noise. Record the position so CleanOutput knows where
                    // the real command output begins.
                    _commandStart = _aiOutput.Length;
                    break;

                case OscParser.OscEventType.CommandFinished:
                    // OSC D: command is done, the prompt function is about to
                    // print the prompt. Snapshot the position here so the
                    // prompt text is excluded from the result.
                    _exitCode = evt.ExitCode;
                    _commandEnd = _aiOutput.Length;
                    break;

                case OscParser.OscEventType.Cwd:
                    _cwd = evt.Cwd;
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
                    // to initialize (Defender first-scan, Import-Module
                    // PSReadLine, sourcing the banner prefix, etc) and
                    // WaitForReady's timeout fell through. Resolving here
                    // would hand the AI the reason banner / PSReadLine
                    // prediction rendering as "command output" and leave
                    // the real command unanswered. Ignore this OSC A and
                    // wait for the real one.
                    if (_commandStart >= 0 && _commandEnd >= 0)
                        Resolve();
                    break;
            }
        }
    }

    /// <summary>
    /// Feed cleaned output from the PTY (OSC stripped). During an AI command
    /// the text is appended to _aiOutput for later OSC C/D slicing; outside
    /// an AI command it goes to the post-primary drain buffer for the
    /// proxy. In BOTH modes the text is also mirrored into _recentBuf, a
    /// small rolling window that peek_console and execute timeout responses
    /// return as "what's on screen right now" context.
    /// </summary>
    public void FeedOutput(string text)
    {
        lock (_lock)
        {
            AppendRecent(text);
            _vtState.Feed(text.AsSpan());

            if (_isAiCommand)
            {
                if (_aiOutput.Length < MaxAiOutputBytes)
                {
                    _aiOutput += text;
                    if (_aiOutput.Length > MaxAiOutputBytes)
                    {
                        _aiOutput = _aiOutput[..MaxAiOutputBytes];
                        _truncated = true;
                    }
                }
                return;
            }

            // No AI command active: this is either pre-first-prompt shell boot
            // noise, a user-typed command's output, or trailing bytes arriving
            // after a primary Resolve() has returned. Capture into the
            // post-primary buffer — the proxy drains it via drain_post_output.
            // Bounded at PostPrimaryMaxBytes so a runaway shell can't grow the
            // buffer forever if the proxy never drains.
            var remaining = PostPrimaryMaxBytes - _postPrimaryOutput.Length;
            if (remaining <= 0) return;
            if (text.Length <= remaining) _postPrimaryOutput.Append(text);
            else _postPrimaryOutput.Append(text, 0, remaining);
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
    /// Snapshot of the AI output accumulated for the current in-flight
    /// command only (i.e. what FeedOutput has written since the OSC C
    /// that started this command). Returns an empty string outside an
    /// AI command. Used by execute_command's timeout response as the
    /// `partialOutput` payload so the AI sees only the bytes produced
    /// by the command it just launched — not whatever the shell had
    /// on screen from previous commands whose results have already
    /// been drained.
    /// </summary>
    public string GetCurrentAiOutputSnapshot()
    {
        lock (_lock)
        {
            return _isAiCommand ? _aiOutput : "";
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
    /// Atomically drain all cached command results and clear the
    /// internal list. Each call returns the complete set of cached
    /// entries accumulated since the last drain — never partial.
    /// Callers render every returned CommandResult (each has its own
    /// baked-in status line) so nothing is silently lost when multiple
    /// flipped commands stacked up before the drain fired.
    /// </summary>
    public List<CommandResult> ConsumeCachedOutputs()
    {
        lock (_lock)
        {
            if (_cachedResults.Count == 0) return new List<CommandResult>();
            var drained = new List<CommandResult>(_cachedResults);
            _cachedResults.Clear();
            return drained;
        }
    }

    /// <summary>
    /// Discard whatever is in the post-primary buffer without waiting. Used
    /// by shells (pwsh/powershell) where nothing useful ever arrives after
    /// OSC PromptStart — the trailing bytes are just the prompt repaint and
    /// PSReadLine prediction animation, which would otherwise leak into the
    /// next command's delta capture.
    /// </summary>
    public void ClearPostPrimary()
    {
        lock (_lock) _postPrimaryOutput.Clear();
    }

    /// <summary>
    /// Wait for the post-primary output buffer to stabilise (no growth for
    /// stableMs), then drain it and return the cleaned delta. Called from
    /// the worker's drain_post_output pipe handler after the primary execute
    /// response has been delivered to the proxy.
    /// </summary>
    public async Task<string> WaitAndDrainPostOutputAsync(int stableMs, int maxMs, CancellationToken ct)
    {
        stableMs = Math.Max(1, stableMs);
        maxMs = Math.Max(stableMs, maxMs);
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(maxMs);

        int lastLen;
        lock (_lock) lastLen = _postPrimaryOutput.Length;
        var lastChange = DateTime.UtcNow;

        while (DateTime.UtcNow < deadline)
        {
            var remaining = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
            var pollMs = Math.Clamp(Math.Min(30, stableMs / 2), 5, remaining);
            try { await Task.Delay(pollMs, ct); }
            catch (OperationCanceledException) { break; }

            int currentLen;
            lock (_lock) currentLen = _postPrimaryOutput.Length;

            if (currentLen != lastLen)
            {
                lastLen = currentLen;
                lastChange = DateTime.UtcNow;
                continue;
            }

            if ((DateTime.UtcNow - lastChange).TotalMilliseconds >= stableMs)
                break;
        }

        string raw;
        lock (_lock)
        {
            raw = _postPrimaryOutput.ToString();
            _postPrimaryOutput.Clear();
        }
        return CleanDelta(raw);
    }

    /// <summary>
    /// Clean a trailing-output delta — strip ANSI, drop trailing prompt and
    /// blank lines, normalise CRLF. Unlike CleanOutput this does NOT strip
    /// AcceptLine noise (no command echo in the delta) and does NOT trim
    /// leading blanks (they might be meaningful Format-Table spacing).
    /// </summary>
    private static string CleanDelta(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";

        var output = StripAnsi(raw);
        var lines = output.Split('\n');
        var cleaned = new List<string>(lines.Length);
        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');
            var trimmed = line.TrimEnd();
            if (trimmed == ">>" || trimmed.StartsWith(">> ")) continue;
            cleaned.Add(line);
        }

        while (cleaned.Count > 0)
        {
            var last = cleaned[^1].Trim();
            if (string.IsNullOrEmpty(last) ||
                last is "$" or "#" or "%" or ">>" ||
                IsShellPrompt(last))
            {
                cleaned.RemoveAt(cleaned.Count - 1);
            }
            else break;
        }

        return string.Join('\n', cleaned).TrimEnd();
    }

    private void Resolve()
    {
        lock (_lock)
        {
            var output = CleanOutput();
            var duration = _stopwatch?.Elapsed.TotalSeconds.ToString("F1") ?? "0.0";
            var statusLine = BuildStatusLine(_commandSent, _exitCode, duration, _cwd, _shellFamily, _displayName);
            var result = new CommandResult(output, _exitCode, _cwd, _commandSent, duration, statusLine);

            var deliverInline = !_shouldCacheOnComplete
                                && _tcs != null
                                && !_tcs.Task.IsCompleted;

            if (deliverInline)
            {
                var tcs = _tcs!;
                _timeoutReg.Dispose();
                Cleanup();
                tcs.TrySetResult(result);
            }
            else
            {
                // Response channel is known broken (either FlipToCacheMode
                // fired, or the timeout CancellationTokenSource already
                // detached _tcs before Resolve got called). Append to the
                // cache list so the next tool call's drain picks it up.
                // The command has finished running by now, so disposing
                // the timer registration is safe — no more callbacks
                // will fire even if we hadn't.
                _timeoutReg.Dispose();
                _cachedResults.Add(result);
                _shouldCacheOnComplete = false;
                Cleanup();
            }
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

    /// <summary>
    /// Build a self-describing status line for a command result, using
    /// what the worker knows at Resolve time: the proxy-supplied
    /// display name, the adapter's shell family, the command text,
    /// exit code, duration and resolved cwd. Baking this into the
    /// CommandResult (rather than formatting at drain time on the
    /// proxy side) keeps cached results self-contained: a cache
    /// drain just reads the status line out and prints it, without
    /// having to re-join metadata from the proxy's ConsoleInfo
    /// which may have drifted since the command was registered.
    /// Mirrors the visual format ShellTools.FormatStatusLine produces
    /// for inline results.
    /// </summary>
    private static string BuildStatusLine(
        string? command, int exitCode, string duration, string? cwd,
        string? shellFamily, string? displayName)
    {
        var identity = string.IsNullOrEmpty(displayName) ? "" : displayName;
        var shell = string.IsNullOrEmpty(shellFamily) ? "" : $" ({shellFamily})";
        var cwdInfo = string.IsNullOrEmpty(cwd) ? "" : $" | Location: {cwd}";
        var cmd = TruncateForStatusLine(command);

        // cmd.exe can't expose real %ERRORLEVEL% through its PROMPT, so the
        // worker always reports ExitCode=0 for cmd. Render a neutral
        // "Finished" line with no success marker so the AI doesn't assume
        // every cmd command succeeded.
        if (shellFamily == "cmd")
            return $"○ {identity}{shell} | Status: Finished (exit code unavailable) | Pipeline: {cmd} | Duration: {duration}s{cwdInfo}";

        return exitCode == 0
            ? $"✓ {identity}{shell} | Status: Completed | Pipeline: {cmd} | Duration: {duration}s{cwdInfo}"
            : $"✗ {identity}{shell} | Status: Failed (exit {exitCode}) | Pipeline: {cmd} | Duration: {duration}s{cwdInfo}";
    }

    /// <summary>
    /// Slice the command-output window out of _aiOutput and clean it up.
    /// The window is [_commandStart, _commandEnd), filled in by OSC C and
    /// OSC D. If OSC C never fired (parse error, OSC markers misconfigured)
    /// we fall back to the whole buffer. If OSC D never fired but OSC A did
    /// (unusual), we take everything up to _aiOutput.Length.
    /// </summary>
    private string CleanOutput()
    {
        var start = _commandStart >= 0 ? _commandStart : 0;
        var end = _commandEnd >= 0 ? _commandEnd : _aiOutput.Length;
        if (end < start) end = start;
        if (end > _aiOutput.Length) end = _aiOutput.Length;

        var raw = _aiOutput.Substring(start, end - start);

        var output = StripAnsi(raw);
        var lines = output.Split('\n');
        var cleaned = new List<string>();
        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');
            var trimmed = line.TrimEnd();
            // pwsh continuation prompt lines from multi-line input aren't
            // command output and look jarring in the result.
            if (trimmed == ">>" || trimmed.StartsWith(">> ")) continue;
            cleaned.Add(line);
        }

        var result = string.Join('\n', cleaned).Trim();
        if (_truncated)
            result += "\n\n[Output truncated at 1MB]";
        return result;
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
    /// Detect shell prompt lines. Used as fallback when OSC markers are unavailable.
    /// Checks common formats + generic trailing prompt characters.
    /// </summary>
    private static bool IsShellPrompt(string line)
    {
        // pwsh: "PS <path>>"
        if (line.StartsWith("PS ") && line.EndsWith(">")) return true;
        // cmd: "<drive>:\...>"
        if (line.Length >= 2 && line[1] == ':' && line.EndsWith(">")) return true;
        // bash/zsh/fish: ends with $, #, %, >, ❯, λ
        // ccl/abcl top-level: ends with ? (other Common Lisp REPL prompts)
        if (line.EndsWith('$') || line.EndsWith('#') || line.EndsWith('%') ||
            line.EndsWith('>') || line.EndsWith('❯') || line.EndsWith('λ') ||
            line.EndsWith('?'))
            return true;
        return false;
    }

    private void Cleanup()
    {
        _tcs = null;
        _isAiCommand = false;
        _aiOutput = "";
        _commandSent = "";
        _stopwatch = null;
        _commandStart = -1;
        _commandEnd = -1;
        // _recentBuf survives deliberately — it's a rolling window
        // spanning command boundaries so peek_console can still show
        // what was on screen just before/during the next call.
    }

    private static string StripAnsi(string text)
    {
        text = AnsiRegex.Replace(text, "");  // strip non-SGR sequences, keep colors
        text = text.Replace("\r\n", "\n");   // CRLF → LF
        text = text.Replace("\r", "");       // remove any remaining standalone CR
        return text;
    }

}
