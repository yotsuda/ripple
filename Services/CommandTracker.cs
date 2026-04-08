using System.Diagnostics;
using System.Text.RegularExpressions;

namespace ShellPilot.Services;

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

    // ANSI escape sequence pattern
    private static readonly Regex AnsiRegex = new(
        @"\x1b\[[0-9;?]*[a-zA-Z]|\x1b\][^\x07]*\x07|\x1b\][^\x1b]*\x1b\\|\x1b[()][0-9A-B]|\r",
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
    private string _output = "";
    private bool _truncated;
    private int _exitCode;
    private string? _cwd;
    private string _commandSent = "";
    private Stopwatch? _stopwatch;
    private CancellationTokenSource? _settleCts;

    // Cached result from timed-out commands
    private CommandResult? _cachedResult;

    public bool Busy => _isAiCommand;
    public bool HasCachedOutput => _cachedResult != null;

    public record CommandResult(string Output, int ExitCode, string? Cwd, string? Command, string Duration);

    /// <summary>
    /// Register an AI-initiated command. Returns a Task that completes
    /// when the shell signals command completion via OSC markers.
    /// </summary>
    public Task<CommandResult> RegisterCommand(string commandText, int timeoutMs = 170_000)
    {
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
    /// Feed an OSC event from the parser.
    /// </summary>
    public void HandleEvent(OscParser.OscEvent evt)
    {
        lock (_lock)
        {
            if (!_isAiCommand) return;

            switch (evt.Type)
            {
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
            if (!_isAiCommand) return;

            if (_output.Length < MaxOutputBytes)
            {
                _output += text;
                if (_output.Length > MaxOutputBytes)
                {
                    _output = _output[..MaxOutputBytes];
                    _truncated = true;
                }
            }

            // If settle timer is running, restart it (more output arriving)
            if (_settleCts != null)
                ScheduleResolve();
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
        // Cancel previous settle timer
        _settleCts?.Cancel();
        _settleCts = new CancellationTokenSource();
        var token = _settleCts.Token;

        // 500ms settle — wait for any trailing output after PromptStart.
        // pwsh Format-Table output may arrive in chunks after the prompt is drawn,
        // so we need a generous settle window.
        _ = Task.Delay(500, token).ContinueWith(_ =>
        {
            if (!token.IsCancellationRequested)
                Resolve();
        }, TaskContinuationOptions.OnlyOnRanToCompletion);
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
        var output = StripAnsi(_output);
        // Strip pwsh prompts that ConPTY glued onto previous lines via cursor positioning
        output = PwshPromptInline.Replace(output, "");
        var lines = output.Split('\n');
        var cleaned = new List<string>();
        bool echoSkipped = false;

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');

            // Skip pwsh continuation prompt ">>" (with or without trailing space)
            var trimmed = line.TrimEnd();
            if (trimmed == ">>" || trimmed.StartsWith(">> "))
                continue;

            if (!echoSkipped)
            {
                // Skip the command echo line and any blank lines before it
                if (line.Contains(_commandSent) || string.IsNullOrWhiteSpace(line))
                {
                    echoSkipped = true;
                    continue;
                }
            }
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
    /// Detect common shell prompt formats:
    ///   bash/zsh: "user@host:~$" or "user@host /path #"
    ///   pwsh: "PS C:\path>"
    ///   cmd: "C:\path>"
    /// </summary>
    private static bool IsShellPrompt(string line)
    {
        // pwsh: "PS <path>>"
        if (line.StartsWith("PS ") && line.EndsWith(">")) return true;
        // cmd: "<drive>:\...>" or any line ending with ">"
        if (line.Length >= 2 && line[1] == ':' && line.EndsWith(">")) return true;
        // bash/zsh: ends with $, #, %
        if (line.EndsWith('$') || line.EndsWith('#') || line.EndsWith('%')) return true;
        return false;
    }

    private void Cleanup()
    {
        _settleCts?.Cancel();
        _settleCts = null;
        _tcs = null;
        _isAiCommand = false;
        _output = "";
        _commandSent = "";
        _stopwatch = null;
    }

    private static string StripAnsi(string text) => AnsiRegex.Replace(text, "");
}
