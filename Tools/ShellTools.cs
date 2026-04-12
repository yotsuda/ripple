using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text;
using SplashShell.Services;

namespace SplashShell.Tools;

[McpServerToolType]
public class ShellTools
{
    [McpServerTool]
    [Description("Open a visible terminal window. The user can see and type in this terminal; AI commands sent via execute_command will also appear here in real time. If a standby console of the requested shell already exists it is reused unless `reason` is provided. Multiple shell types can be active simultaneously. Every response also reports the busy / finished / closed state of any other consoles you have open so background work stays visible.")]
    public static async Task<string> StartConsole(
        ConsoleManager consoleManager,
        [Description("Shell to use. Name (bash, pwsh, zsh, cmd) or full path. Default: platform default.")]
        string? shell = null,
        [Description("Working directory. Default: home directory.")]
        string? cwd = null,
        [Description("Banner message displayed in the console (green text). Shown on both new and reused consoles.")]
        string? banner = null,
        [Description("Do NOT specify this parameter unless explicitly needed. Forces a new console launch instead of reusing an existing standby. The reason text is displayed in the console as yellow text.")]
        string? reason = null,
        [Description("Set true for sub-agent isolation. Returns an agent_id for subsequent calls.")]
        bool is_subagent = false,
        [Description("Agent ID for sub-agent console isolation.")]
        string? agent_id = null,
        CancellationToken cancellationToken = default)
    {
        var agentId = agent_id ?? "default";

        if (is_subagent && string.IsNullOrEmpty(agent_id))
        {
            var newId = consoleManager.AllocateSubAgentId();
            return $"Sub-agent allocated: {newId}. Use this agent_id in subsequent calls.";
        }

        var result = await consoleManager.StartConsoleAsync(shell, cwd, reason, agentId, banner);
        var shellInfo = result.ShellFamily != null ? $" ({result.ShellFamily})" : "";
        var cwdInfo = !string.IsNullOrEmpty(result.Cwd) ? $" at {result.Cwd}" : "";
        var response = result.Status == "reused"
            ? $"Reusing standby {result.DisplayName}{shellInfo}{cwdInfo}."
            : $"Console {result.DisplayName}{shellInfo} opened{cwdInfo}.";

        return await AppendCachedOutputs(consoleManager, agentId, response);
    }

    [McpServerTool]
    [Description("Execute a command in the shared terminal. The command and its output are visible to the user as they stream. Session state (variables, modules, cwd) persists across calls. If the active console is busy with a user-typed command, splash auto-routes to a same-family standby (or auto-starts a fresh one) and preserves your last known cwd via a cd preamble — if the source console was moved by the user since your last command, you'll see a one-line routing notice explaining what splash did. If the active console is idle but the user manually cd'd in it since your last command, the call returns a verify-and-retry warning instead of running. Every response also reports any other consoles' busy / finished / closed state so you stay aware of background activity.")]
    public static async Task<string> ExecuteCommand(
        ConsoleManager consoleManager,
        [Description("The pipeline to execute (supports pipes, e.g. 'ls | grep foo')")]
        string pipeline,
        [Description("Shell type to execute in (bash, pwsh, zsh, or full path). If omitted, uses the current active console. If specified and no matching console exists, one is auto-started.")]
        string? shell = null,
        [Description("Timeout in seconds (0-170, default: 170). On timeout, execution continues and output is cached for wait_for_completion.")]
        int timeout_seconds = 170,
        [Description("Agent ID for sub-agent console isolation.")]
        string? agent_id = null,
        CancellationToken cancellationToken = default)
    {
        var agentId = agent_id ?? "default";
        var result = await consoleManager.ExecuteCommandAsync(pipeline, timeout_seconds, agentId, shell);

        string response;
        if (result.TimedOut)
        {
            var shellInfo = result.ShellFamily != null ? $" ({result.ShellFamily})" : "";
            var header = $"⧗ {result.DisplayName}{shellInfo} | Status: Busy | Pipeline: {result.Command}\nUse wait_for_completion tool to wait and retrieve the result.";
            if (!string.IsNullOrEmpty(result.PartialOutput))
                response = $"{header}\n\n--- partial output (recent window, not the final result) ---\n{result.PartialOutput}";
            else
                response = header;
        }
        else if (result.Switched)
            response = result.Output ?? "";
        else
        {
            var statusLine = FormatStatusLine(result);
            response = $"{statusLine}\n\n{(string.IsNullOrEmpty(result.Output) ? "(no output)" : result.Output)}";
        }

        // A routing notice (e.g. "source console was moved by user, your
        // last known cwd was preserved by routing to a different console")
        // belongs above the status line so the AI sees the context before
        // reading the command's own output.
        if (!string.IsNullOrEmpty(result.Notice))
            response = $"{result.Notice}\n\n{response}";

        return await AppendCachedOutputs(consoleManager, agentId, response, excludePid: result.Pid);
    }

    [McpServerTool]
    [Description("Wait for AI-initiated commands that previously timed out to finish and retrieve their cached output. Returns one of three states: 'no commands pending' (nothing to wait for, stop calling), 'completed' (one or more drained results included in the response), or 'still busy' (call again to keep waiting). Use this after execute_command returned a Busy/timed-out result.")]
    public static async Task<string> WaitForCompletion(
        ConsoleManager consoleManager,
        [Description("Maximum seconds to wait (default: 30)")]
        int timeout_seconds = 30,
        [Description("Agent ID for sub-agent console isolation.")]
        string? agent_id = null,
        CancellationToken cancellationToken = default)
    {
        var agentId = agent_id ?? "default";
        var result = await consoleManager.WaitForCompletionAsync(timeout_seconds, agentId);

        if (result.HadNoBusyPids)
            return await AppendCachedOutputs(consoleManager, agentId,
                "No AI-initiated commands are currently running. Nothing to wait for.");

        var sb = new StringBuilder();
        foreach (var r in result.Completed)
        {
            sb.AppendLine(FormatStatusLine(r));
            sb.AppendLine();
            sb.AppendLine(string.IsNullOrEmpty(r.Output) ? "(no output)" : r.Output);
            sb.AppendLine();
        }

        if (result.StillBusy.Count > 0)
        {
            sb.AppendLine($"Still busy after {timeout_seconds}s. Call wait_for_completion again to keep waiting:");
            foreach (var b in result.StillBusy)
                sb.AppendLine(FormatBusyLine(b));
        }

        return await AppendCachedOutputs(consoleManager, agentId, sb.ToString().TrimEnd());
    }

    [McpServerTool]
    [Description("Send raw keystrokes to a busy console's PTY input. ONLY works when the target console is busy (idle consoles are rejected — use execute_command instead). Use this to: respond to an interactive prompt (Read-Host, password, y/n confirmation); send Ctrl+C (\\x03) to interrupt a stuck or runaway command; exit a watch-mode TUI (q, Ctrl+C); send Enter (\\r) to dismiss a 'Press Enter to continue' pause; send arrow keys (\\x1b[A/B/C/D) to navigate a TUI menu. Always peek_console first to verify what the console is waiting for, then send_input with the appropriate response. Input is sent as-is — include \\r for Enter, \\x03 for Ctrl+C, \\x1b[A for arrow-up, etc. Max 256 chars per call.")]
    public static async Task<string> SendInput(
        ConsoleManager consoleManager,
        [Description("Which console to send input to. Accepts a PID number or a display-name substring (e.g. \"Poseidon\" matches \"#10612 Poseidon\"). Required — you must specify the target.")]
        string console,
        [Description("The raw input to send to the PTY. Sent as-is. Use \\r for Enter, \\x03 for Ctrl+C, \\x1b[A for arrow up, etc. Max 256 chars.")]
        string input,
        [Description("Agent ID for sub-agent console isolation.")]
        string? agent_id = null,
        CancellationToken cancellationToken = default)
    {
        var agentId = agent_id ?? "default";

        if (string.IsNullOrEmpty(console))
            return "Error: 'console' parameter is required. Specify the target console by display name or PID.";
        if (string.IsNullOrEmpty(input))
            return "Error: 'input' parameter is required.";

        var result = await consoleManager.SendInputAsync(agentId, console, input);
        if (result == null)
            return $"No console matches \"{console}\". Use the display name (e.g. \"Poseidon\") or PID shown in previous tool responses.";

        if (result.Status == "ok")
            return $"✓ Sent {input.Length} char(s) to {result.DisplayName}.";
        if (result.Status == "rejected")
            return $"✗ {result.DisplayName} is not busy. Use execute_command to run commands on idle consoles.";
        return $"✗ {result.DisplayName}: {result.Error}";
    }

    [McpServerTool]
    [Description("Snapshot what a console has been printing recently — the output from the currently running command (since it started executing), plus the next prompt once it finishes. Use this to: check a busy console's progress without waiting for wait_for_completion (the peek pipe works during a running command); diagnose a timed-out execute_command (watch mode, interactive prompt, stalled progress); look in on a console that's busy with a user-typed command the AI can see is blocked. Read-only — does not interrupt or change anything. Returns the live rolling window (ANSI-stripped) plus busy / running-command / elapsed metadata. Works on any console — if you need to observe one other than your active one, pass its display name or PID in `console`.")]
    public static async Task<string> PeekConsole(
        ConsoleManager consoleManager,
        [Description("Which console to peek at. Accepts a PID number or a display-name substring (e.g. \"Reggae\" matches \"#43060 Reggae\"). Omit to peek at your current active console. Crucial when you need to observe a different console than the one you'd normally send commands to — for example, a console that just returned busy for execute_command.")]
        string? console = null,
        [Description("Agent ID for sub-agent console isolation.")]
        string? agent_id = null,
        [Description("Debug: include raw ring buffer bytes as an escaped hex preview. Off by default.")]
        bool raw = false,
        CancellationToken cancellationToken = default)
    {
        var agentId = agent_id ?? "default";
        var peek = await consoleManager.PeekConsoleAsync(agentId, console, raw);
        if (peek == null)
        {
            var msg = !string.IsNullOrEmpty(console)
                ? $"No console matches \"{console}\". Use the display name (e.g. \"Reggae\") or PID shown in previous tool responses."
                : "No console to peek at. Start one with start_console first.";
            return await AppendCachedOutputs(consoleManager, agentId, msg);
        }

        var shellInfo = peek.ShellFamily != null ? $" ({peek.ShellFamily})" : "";
        var busyMark = peek.Busy ? "⧗ Busy" : "✓ Idle";
        var sb = new StringBuilder();
        sb.AppendLine($"{busyMark} {peek.DisplayName}{shellInfo} | Status: {peek.Status}");
        if (peek.Busy && !string.IsNullOrEmpty(peek.RunningCommand))
        {
            var elapsedPart = peek.RunningElapsedSeconds.HasValue
                ? $" ({peek.RunningElapsedSeconds.Value:F1}s elapsed)"
                : "";
            sb.AppendLine($"Running: {peek.RunningCommand}{elapsedPart}");
        }
        else if (peek.Busy)
        {
            var elapsedPart = peek.RunningElapsedSeconds.HasValue
                ? $" ({peek.RunningElapsedSeconds.Value:F1}s elapsed)"
                : "";
            sb.AppendLine($"Running: (user-typed command, unknown){elapsedPart}");
        }
        sb.AppendLine();
        sb.AppendLine("--- recent output ---");
        sb.Append(string.IsNullOrEmpty(peek.RecentOutput) ? "(empty)" : peek.RecentOutput);

        if (raw && !string.IsNullOrEmpty(peek.RawBase64))
        {
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine("--- raw ring bytes (hex escaped) ---");
            var rawBytes = Convert.FromBase64String(peek.RawBase64!);
            var rawText = System.Text.Encoding.UTF8.GetString(rawBytes);
            var hex = new StringBuilder();
            foreach (var c in rawText)
            {
                if (c == '\x1b') hex.Append("\\e");
                else if (c == '\r') hex.Append("\\r");
                else if (c == '\n') hex.Append("\\n");
                else if (c == '\t') hex.Append("\\t");
                else if (c == '\a') hex.Append("\\a");
                else if (c < 0x20) hex.Append($"\\x{(int)c:x2}");
                else if (c == '\\') hex.Append("\\\\");
                else hex.Append(c);
            }
            sb.Append(hex.ToString());
        }

        return await AppendCachedOutputs(consoleManager, agentId, sb.ToString(), excludePid: peek.Pid);
    }

    /// <summary>
    /// Format a status line for a completed/failed command result.
    /// Includes console name, shell type, status, pipeline, duration, and location.
    /// </summary>
    private static string FormatStatusLine(ConsoleManager.ExecuteResult r)
    {
        var shell = r.ShellFamily != null ? $" ({r.ShellFamily})" : "";
        var cwdInfo = r.Cwd != null ? $" | Location: {r.Cwd}" : "";
        return r.ExitCode == 0
            ? $"✓ {r.DisplayName}{shell} | Status: Completed | Pipeline: {r.Command} | Duration: {r.Duration}s{cwdInfo}"
            : $"✗ {r.DisplayName}{shell} | Status: Failed (exit {r.ExitCode}) | Pipeline: {r.Command} | Duration: {r.Duration}s{cwdInfo}";
    }

    /// <summary>
    /// Detect closed consoles, collect cached outputs, and report busy
    /// consoles other than the one this tool just used. Everything gets
    /// prepended to the response so the AI stays aware of background work
    /// on consoles it isn't currently acting on. Pass excludePid to keep
    /// the response free of a duplicate busy line for the current console.
    /// </summary>
    private static async Task<string> AppendCachedOutputs(ConsoleManager consoleManager, string agentId, string response, int excludePid = 0)
    {
        var closed = consoleManager.DetectClosedConsoles(agentId);
        var cached = await consoleManager.CollectCachedOutputsAsync(agentId);
        var report = await consoleManager.CollectBusyStatusesAsync(agentId, excludePid);

        if (closed.Count == 0 && cached.Count == 0 && report.Busy.Count == 0 && report.Finished.Count == 0)
            return response;

        var sb = new StringBuilder();

        // Closed console notifications
        foreach (var (displayName, shellFamily) in closed)
        {
            var shellInfo = !string.IsNullOrEmpty(shellFamily) ? $" ({shellFamily})" : "";
            sb.AppendLine($"Console {displayName}{shellInfo} closed.");
            sb.AppendLine();
        }

        // Other consoles still running a previously-started command
        foreach (var b in report.Busy)
        {
            sb.AppendLine(FormatBusyLine(b));
        }
        if (report.Busy.Count > 0) sb.AppendLine();

        // Consoles whose previously-reported busy command has now finished.
        // Currently only fires for user-typed commands; AI commands with
        // cached output are drained above and never reach this branch.
        foreach (var f in report.Finished)
        {
            sb.AppendLine(FormatFinishedLine(f));
        }
        if (report.Finished.Count > 0) sb.AppendLine();

        // Cached command results (timed-out AI commands that have since completed)
        foreach (var r in cached)
        {
            sb.AppendLine(FormatStatusLine(r));
            sb.AppendLine();
            sb.AppendLine(string.IsNullOrEmpty(r.Output) ? "(no output)" : r.Output);
            sb.AppendLine();
        }

        sb.Append(response);
        return sb.ToString();
    }

    /// <summary>
    /// One-line summary of a background busy console. Shown at the top of
    /// unrelated tool responses so the AI doesn't forget long-running work.
    /// </summary>
    private static string FormatBusyLine(ConsoleManager.BusyStatus b)
    {
        var shell = b.ShellFamily != null ? $" ({b.ShellFamily})" : "";
        var elapsed = b.ElapsedSeconds.HasValue ? $" ({b.ElapsedSeconds.Value:F0}s)" : "";
        var cmd = string.IsNullOrEmpty(b.RunningCommand) ? "(user command)" : b.RunningCommand;
        return $"⧗ {b.DisplayName}{shell} | Status: Busy{elapsed} | Pipeline: {cmd}";
    }

    /// <summary>
    /// One-line summary of a console whose previously-busy command has just
    /// finished. Mirrors FormatBusyLine's shape so the two lines read
    /// consistently when they appear together.
    /// </summary>
    private static string FormatFinishedLine(ConsoleManager.FinishedStatus f)
    {
        var shell = f.ShellFamily != null ? $" ({f.ShellFamily})" : "";
        return $"✓ {f.DisplayName}{shell} | Status: User command finished";
    }
}
