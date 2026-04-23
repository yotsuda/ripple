namespace Ripple.Services;

/// <summary>
/// Single source of truth for the status-line format that ripple
/// prepends to every execute_command / wait_for_completion /
/// cached-drain response. Invoked from two sites:
///
///   * <c>ConsoleWorker.BuildStatusLine</c> — bakes the line into
///     <c>CommandResult.StatusLine</c> at finalize-once time so
///     cached entries are self-contained (the proxy can print the
///     line verbatim without re-joining ConsoleInfo metadata that
///     may have drifted since the command was registered).
///   * <c>ShellTools.FormatStatusLine</c> — formats the line for
///     live inline execute responses that haven't been cached.
///
/// Keeping both paths on the same formatter prevents format drift
/// between the two sites. Before this helper existed, each status-
/// line extension (e.g. the `LastExit: N` tag) had to be added in
/// both places; missing one was a silent regression that only
/// surfaced when a command timed out and drained via the cached
/// path. Now the rendering logic lives here and both callers are
/// thin unpackers.
/// </summary>
internal static class StatusLineFormatter
{
    /// <summary>
    /// Render the status line from its constituent fields. Callers
    /// unpack their own metadata (snapshot, ExecuteResult, etc.) into
    /// this flat parameter list so the formatter has zero knowledge
    /// of the caller's record shape.
    /// </summary>
    /// <param name="command">Pipeline text the AI asked to run.</param>
    /// <param name="exitCode">Resolved exit code (0 on success, non-zero on failure).</param>
    /// <param name="duration">Pre-formatted duration (e.g. "1.2").</param>
    /// <param name="cwd">Cwd at command end (null → section omitted).</param>
    /// <param name="shellFamily">Normalized shell family ("pwsh", "bash", "cmd", ...); drives
    /// the badge and whether the line advertises exit-code semantics.</param>
    /// <param name="displayName">Console display identity (e.g. "#12345 Dolphin").</param>
    /// <param name="errorCount">PowerShell $Error delta (OSC 633;E). 0 for non-pwsh.</param>
    /// <param name="lastExitCode">Raw $LASTEXITCODE when a native exe returned non-zero
    /// mid-pipeline while the pipeline overall succeeded (OSC 633;L). 0 = no report.</param>
    public static string Format(
        string? command, int exitCode, string duration, string? cwd,
        string? shellFamily, string? displayName,
        int errorCount, int lastExitCode)
    {
        var identity = string.IsNullOrEmpty(displayName) ? "" : displayName;
        var shell = string.IsNullOrEmpty(shellFamily) ? "" : $" ({shellFamily})";
        var cwdInfo = string.IsNullOrEmpty(cwd) ? "" : $" | Location: {cwd}";
        var cmd = CommandTracker.TruncateForStatusLine(command);
        // Only PowerShell currently emits OSC 633;E. For other adapters
        // errorCount stays at 0 and "Errors: N" is omitted, so the line
        // shape remains identical to the pre-OSC-E world. Zero is also
        // omitted for pwsh — the happy path doesn't need a "Errors: 0"
        // tag.
        var errInfo = errorCount > 0 ? $" | Errors: {errorCount}" : "";
        // "LastExit: N" surfaces a native exe that returned non-zero
        // mid-pipeline when the pipeline overall succeeded. Only
        // populated for pwsh (OSC L emission is pwsh-only) and only
        // when the integration script judged it worth reporting
        // (lecChanged & non-zero & pipeline overall ok — see
        // integration.ps1 prompt fn). Rendered only on the ✓ / ⚠
        // branches; omitted on ✗ Failed because `exit N` already
        // carries the non-zero signal there.
        var lastExitInfo = lastExitCode > 0 ? $" | LastExit: {lastExitCode}" : "";

        // cmd.exe can't expose real %ERRORLEVEL% through its PROMPT,
        // so the worker always reports ExitCode=0 for cmd. Render a
        // neutral "Finished" line with no success marker so the AI
        // doesn't assume every cmd command succeeded.
        if (shellFamily == "cmd")
            return $"○ {identity}{shell} | Status: Finished (exit code unavailable) | Pipeline: {cmd} | Duration: {duration}s{cwdInfo}";

        // Three outcomes for shells that report exit codes:
        //   exit != 0           → ✗ Failed
        //   exit 0 + errCount>0 → ⚠ Completed with errors (pwsh non-terminating
        //                         errors: the command RAN but $Error grew, so a
        //                         green ✓ would understate what the AI needs to
        //                         look at)
        //   exit 0 + errCount=0 → ✓ Completed (happy path)
        if (exitCode != 0)
            return $"✗ {identity}{shell} | Status: Failed (exit {exitCode}){errInfo} | Pipeline: {cmd} | Duration: {duration}s{cwdInfo}";
        if (errorCount > 0)
            return $"⚠  {identity}{shell} | Status: Completed with errors{errInfo}{lastExitInfo} | Pipeline: {cmd} | Duration: {duration}s{cwdInfo}";
        return $"✓ {identity}{shell} | Status: Completed{lastExitInfo} | Pipeline: {cmd} | Duration: {duration}s{cwdInfo}";
    }
}
