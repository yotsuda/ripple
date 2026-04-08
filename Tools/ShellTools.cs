using ModelContextProtocol.Server;
using System.ComponentModel;
using ShellPilot.Services;

namespace ShellPilot.Tools;

[McpServerToolType]
public class ShellTools
{
    [McpServerTool]
    [Description("Open a visible terminal window. The user can see and type in this terminal. AI commands sent via execute_command will also appear here. If a standby console exists, it will be reused unless reason is provided. Shell type is locked per session.")]
    public static async Task<string> StartConsole(
        ConsoleManager consoleManager,
        [Description("Shell to use. Name (bash, pwsh, zsh) or full path. Default: platform default. Locked after first use.")]
        string? shell = null,
        [Description("Working directory. Default: home directory.")]
        string? cwd = null,
        [Description("Forces a new console launch instead of reusing an existing standby. Omit to reuse.")]
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

        var result = await consoleManager.StartConsoleAsync(shell, cwd, reason, agentId);
        var status = result.Status == "reused"
            ? $"Reusing standby console {result.DisplayName} (PID {result.Pid})."
            : $"Console {result.DisplayName} opened (PID {result.Pid}).";

        return status;
    }

    [McpServerTool]
    [Description("Execute a command in the shared terminal. The command and its output are visible to the user in real time. Session state persists across calls. Call start_console first if no console is open.")]
    public static async Task<string> ExecuteCommand(
        ConsoleManager consoleManager,
        [Description("The command to execute")]
        string command,
        [Description("Timeout in seconds (0-170, default: 170). On timeout, execution continues and output is cached for wait_for_completion.")]
        int timeout_seconds = 170,
        [Description("Agent ID for sub-agent console isolation.")]
        string? agent_id = null,
        CancellationToken cancellationToken = default)
    {
        var agentId = agent_id ?? "default";
        var result = await consoleManager.ExecuteCommandAsync(command, timeout_seconds, agentId);

        if (result.TimedOut)
            return $"⧗ {result.DisplayName} | Status: Busy | Pipeline: {result.Command}\nUse wait_for_completion tool to wait and retrieve the result.";

        if (result.Switched)
            return result.Output ?? "";

        var cwdInfo = result.Cwd != null ? $" | Location: {result.Cwd}" : "";
        var statusLine = result.ExitCode == 0
            ? $"✓ {result.DisplayName} | Status: Completed | Pipeline: {result.Command} | Duration: {result.Duration}s{cwdInfo}"
            : $"✗ {result.DisplayName} | Status: Failed (exit {result.ExitCode}) | Pipeline: {result.Command} | Duration: {result.Duration}s{cwdInfo}";

        return $"{statusLine}\n\n{(string.IsNullOrEmpty(result.Output) ? "(no output)" : result.Output)}";
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

        var sb = new System.Text.StringBuilder();
        foreach (var r in results)
        {
            var cwdInfo = r.Cwd != null ? $" | Location: {r.Cwd}" : "";
            var statusLine = r.ExitCode == 0
                ? $"✓ {r.DisplayName} | Status: Completed | Pipeline: {r.Command} | Duration: {r.Duration}s{cwdInfo}"
                : $"✗ {r.DisplayName} | Status: Failed (exit {r.ExitCode}) | Pipeline: {r.Command} | Duration: {r.Duration}s{cwdInfo}";
            sb.AppendLine(statusLine);
            sb.AppendLine();
            sb.AppendLine(string.IsNullOrEmpty(r.Output) ? "(no output)" : r.Output);
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }
}
