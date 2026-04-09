using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text;
using ShellPilot.Services;

namespace ShellPilot.Tools;

[McpServerToolType]
public class ShellTools
{
    [McpServerTool]
    [Description("Open a visible terminal window. The user can see and type in this terminal. AI commands sent via execute_command will also appear here. If a standby console of the requested shell exists, it will be reused unless reason is provided. Multiple shell types can be active simultaneously.")]
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
        var response = result.Status == "reused"
            ? $"Reusing standby console {result.DisplayName}. Did not launch a new console. To force a new console, provide the reason parameter."
            : $"Console {result.DisplayName} opened.";

        return await AppendCachedOutputs(consoleManager, agentId, response);
    }

    [McpServerTool]
    [Description("Execute a command in the shared terminal. The command and its output are visible to the user in real time. Session state persists across calls. Call start_console first if no console is open. Optionally specify shell to target a specific shell type.")]
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
            response = $"⧗ {result.DisplayName}{shellInfo} | Status: Busy | Pipeline: {result.Command}\nUse wait_for_completion tool to wait and retrieve the result.";
        }
        else if (result.Switched)
            response = result.Output ?? "";
        else
        {
            var statusLine = FormatStatusLine(result);
            response = $"{statusLine}\n\n{(string.IsNullOrEmpty(result.Output) ? "(no output)" : result.Output)}";
        }

        return await AppendCachedOutputs(consoleManager, agentId, response);
    }

    [McpServerTool]
    [Description("Wait for busy console(s) to complete and retrieve cached output. Use this after a command times out.")]
    public static async Task<string> WaitForCompletion(
        ConsoleManager consoleManager,
        [Description("Maximum seconds to wait (default: 30)")]
        int timeout_seconds = 30,
        [Description("Agent ID for sub-agent console isolation.")]
        string? agent_id = null,
        CancellationToken cancellationToken = default)
    {
        var agentId = agent_id ?? "default";
        var results = await consoleManager.WaitForCompletionAsync(timeout_seconds, agentId);

        if (results.Count == 0)
            return "No completed results. Consoles may still be busy — try again later.";

        var sb = new StringBuilder();
        foreach (var r in results)
        {
            sb.AppendLine(FormatStatusLine(r));
            sb.AppendLine();
            sb.AppendLine(string.IsNullOrEmpty(r.Output) ? "(no output)" : r.Output);
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
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
    /// Detect closed consoles and collect cached outputs, prepending notifications
    /// to the response. Called at the very end of each tool, just before returning,
    /// so no cached results or close events are missed due to processing delays.
    /// </summary>
    private static async Task<string> AppendCachedOutputs(ConsoleManager consoleManager, string agentId, string response)
    {
        var closed = consoleManager.DetectClosedConsoles(agentId);
        var cached = await consoleManager.CollectCachedOutputsAsync(agentId);

        if (closed.Count == 0 && cached.Count == 0)
            return response;

        var sb = new StringBuilder();

        // Closed console notifications
        foreach (var (displayName, shellFamily) in closed)
        {
            var shellInfo = !string.IsNullOrEmpty(shellFamily) ? $" ({shellFamily})" : "";
            sb.AppendLine($"Console {displayName}{shellInfo} closed.");
            sb.AppendLine();
        }

        // Cached command results
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
}
