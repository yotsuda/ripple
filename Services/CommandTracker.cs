using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace SplashShell.Services;

/// <summary>
/// Tracks command lifecycle using OSC 633 events.
/// Runs in the console worker process.
///
/// Flow:
///   RegisterCommand() → command written to PTY →
///   OSC C (CommandExecuted) → output captured →
///   OSC D;{exitCode} (CommandFinished) → OSC P;Cwd=... → OSC A (PromptStart) →
///   settle timer (150ms) → resolve with { output, exitCode, cwd }
///
/// On timeout: caller's Task is cancelled, but output capture continues.
/// When the shell eventually completes, the result is cached for WaitForCompletion.
/// </summary>
public class CommandTracker
{
    private const int MaxOutputBytes = 1024 * 1024; // 1MB
    private const int PostPrimaryMaxBytes = 64 * 1024; // 64 KB for trailing-output delta

    // Non-SGR ANSI escape sequence pattern.
    // Strips cursor movement, erase, and other control sequences, but preserves
    // SGR (Select Graphic Rendition, ending in 'm') so color information is
    // passed through to the AI for context (e.g. red errors, green success).
    // OSC sequences (window title etc.) are also stripped.
    private static readonly Regex AnsiRegex = new(
        @"\x1b\[[0-9;?]*[a-ln-zA-Z]|\x1b\][^\x07]*\x07|\x1b\][^\x1b]*\x1b\\|\x1b[()][0-9A-B]",
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
    private bool _shellReady; // flipped on first PromptStart; gates user-busy tracking
    private string _output = "";
    private bool _truncated;
    private int _exitCode;
    private string? _cwd;
    private string _commandSent = "";
    private bool _captureEnabled = true;
    private bool _stripAcceptLineNoise = false; // true for pwsh: strip AcceptLine rendering before first \r\n
    private Stopwatch? _stopwatch;

    // Trailing output that arrives AFTER Resolve() has returned the primary
    // CommandResult — e.g. the pwsh prompt repaint or pwsh Format-Table rows
    // that finish streaming after the OSC A marker. The proxy drains this via
    // the drain_post_output pipe message after each successful execute.
    private readonly StringBuilder _postPrimaryOutput = new();

    // Cached result from timed-out commands
    private CommandResult? _cachedResult;

    // Last known cwd from any prompt (AI command or user command)
    private string? _lastKnownCwd;
    public string? LastKnownCwd { get { lock (_lock) return _lastKnownCwd; } }

    public bool Busy => _isAiCommand || _userCommandBusy;
    public bool HasCachedOutput => _cachedResult != null;

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

    public record CommandResult(string Output, int ExitCode, string? Cwd, string? Command, string Duration);

    /// <summary>
    /// Register an AI-initiated command. Returns a Task that completes
    /// when the shell signals command completion via OSC markers.
    /// </summary>
    public Task<CommandResult> RegisterCommand(string commandText, int timeoutMs = 170_000)
    {
        // Minimum 1 second to avoid CancellationTokenSource(0) race conditions
        timeoutMs = Math.Max(timeoutMs, 1000);

        lock (_lock)
        {
            if (_isAiCommand)
                throw new InvalidOperationException("Another command is already executing.");

            _tcs = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            _isAiCommand = true;
            _output = "";
            _truncated = false;
            _exitCode = 0;
            _cwd = null;
            _commandSent = commandText;
            _cachedResult = null;
            _postPrimaryOutput.Clear();
            _stopwatch = Stopwatch.StartNew();

            // Setup timeout
            var timeoutCts = new CancellationTokenSource(timeoutMs);
            _timeoutReg = timeoutCts.Token.Register(() =>
            {
                lock (_lock)
                {
                    if (_tcs != null && !_tcs.Task.IsCompleted)
                    {
                        var tcs = _tcs;
                        _tcs = null; // Detach — output capture continues
                        tcs.TrySetException(new TimeoutException($"Command timed out after {timeoutMs}ms"));
                    }
                }
            });

            return _tcs.Task;
        }
    }

    /// <summary>
    /// Suppress output capture until CommandInputStart (OSC B) arrives.
    /// Called for pwsh to discard PSReadLine prediction rendering noise.
    /// Safety timer re-enables capture after 2s if OSC B never arrives.
    /// </summary>
    public void SuppressUntilCommandStart()
    {
        lock (_lock)
        {
            _captureEnabled = false;
            _stripAcceptLineNoise = true;
            _ = Task.Delay(2000).ContinueWith(_ =>
            {
                lock (_lock) { if (!_captureEnabled) _captureEnabled = true; }
            });
        }
    }

    /// <summary>
    /// Feed an OSC event from the parser.
    /// </summary>
    public void HandleEvent(OscParser.OscEvent evt)
    {
        lock (_lock)
        {
            // Always track cwd, even outside AI commands (for user manual cd)
            if (evt.Type == OscParser.OscEventType.Cwd)
                _lastKnownCwd = evt.Cwd;

            // Mark the shell as "ready" on the first PromptStart. Until then,
            // ignore user-command busy transitions — the initial OSC B that
            // integration scripts emit at startup (and the subsequent prompt
            // setup) would otherwise leave the new console looking busy and
            // cause HandleExecuteAsync to reject the first incoming command.
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
                case OscParser.OscEventType.CommandInputStart:
                    _captureEnabled = true;
                    _output = "";  // discard PSReadLine prediction noise before OSC B
                    break;

                case OscParser.OscEventType.CommandFinished:
                    _exitCode = evt.ExitCode;
                    break;

                case OscParser.OscEventType.Cwd:
                    _cwd = evt.Cwd;
                    break;

                case OscParser.OscEventType.PromptStart:
                    ScheduleResolve();
                    break;
            }
        }
    }

    /// <summary>
    /// Feed cleaned output from the PTY (OSC stripped).
    /// </summary>
    public void FeedOutput(string text)
    {
        lock (_lock)
        {
            if (_isAiCommand)
            {
                if (!_captureEnabled) return;

                if (_output.Length < MaxOutputBytes)
                {
                    _output += text;
                    if (_output.Length > MaxOutputBytes)
                    {
                        _output = _output[..MaxOutputBytes];
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

    /// <summary>
    /// Consume cached output from a timed-out command that has since completed.
    /// </summary>
    public CommandResult? ConsumeCachedOutput()
    {
        lock (_lock)
        {
            var result = _cachedResult;
            _cachedResult = null;
            return result;
        }
    }

    private void ScheduleResolve()
    {
        // Resolve immediately on OSC PromptStart — no fixed settle delay.
        // Trailing output that arrives after this (pwsh prompt repaint,
        // Format-Table rows still streaming, etc.) lands in _postPrimaryOutput
        // and the proxy drains it via the drain_post_output pipe message.
        Resolve();
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
            var result = new CommandResult(output, _exitCode, _cwd, _commandSent, duration);

            if (_tcs != null && !_tcs.Task.IsCompleted)
            {
                var tcs = _tcs;
                _timeoutReg.Dispose();
                Cleanup();
                tcs.TrySetResult(result);
            }
            else
            {
                // Timed out earlier — cache result for wait_for_completion
                _cachedResult = result;
                Cleanup();
            }
        }
    }

    private string CleanOutput()
    {
        var raw = _output;

        // For pwsh: everything before the first hard \r\n is AcceptLine rendering noise.
        // (PSReadLine uses soft \r to overwrite ghost text; the \r\n marks end of input line.)
        // Not applied to bash/zsh where OSC B is not emitted per-command.
        if (_stripAcceptLineNoise)
        {
            int firstHardNewline = raw.IndexOf("\r\n");
            if (firstHardNewline >= 0)
                raw = raw[(firstHardNewline + 2)..];
        }

        var output = StripAnsi(raw);
        var lines = output.Split('\n');
        var cleaned = new List<string>();

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');

            // Skip pwsh continuation prompt ">>" (with or without trailing space)
            var trimmed = line.TrimEnd();
            if (trimmed == ">>" || trimmed.StartsWith(">> "))
                continue;

            cleaned.Add(line);
        }

        // Remove trailing prompt/empty lines
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

        var result = string.Join('\n', cleaned).Trim();
        if (_truncated)
            result += "\n\n[Output truncated at 1MB]";
        return result;
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
        if (line.EndsWith('$') || line.EndsWith('#') || line.EndsWith('%') ||
            line.EndsWith('>') || line.EndsWith('❯') || line.EndsWith('λ'))
            return true;
        return false;
    }

    private void Cleanup()
    {
        _tcs = null;
        _isAiCommand = false;
        _output = "";
        _commandSent = "";
        _stopwatch = null;
        _captureEnabled = true;
        _stripAcceptLineNoise = false;
    }

    private static string StripAnsi(string text)
    {
        text = AnsiRegex.Replace(text, "");  // strip non-SGR sequences, keep colors
        text = text.Replace("\r\n", "\n");   // CRLF → LF
        text = text.Replace("\r", "");       // remove any remaining standalone CR
        return text;
    }
}
