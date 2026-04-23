namespace Ripple.Services;

/// <summary>
/// Pure utility for resolving a shell / REPL launch argument
/// ("pwsh", "python", "C:\\tools\\duckdb.exe", ...) to an absolute
/// executable path. Used by:
///
///   * <see cref="ConsoleManager"/> when spawning consoles.
///   * <see cref="Adapters.Adapter.ResolveLaunchExecutable"/> when
///     computing the effective launch path for the <c>list_shells</c>
///     MCP tool response.
///   * <see cref="ConsoleWorker"/> when printing the resolved
///     executable path to the visible console for the user's
///     benefit at console start.
///
/// Lives in its own class (not on ConsoleManager) because the
/// Adapters layer — which is a data model, not a runtime — needs
/// to call it. Routing that call through ConsoleManager would be a
/// reverse dependency from model to runtime. Here the dependency is
/// correct: both the runtime (ConsoleManager / ConsoleWorker) and
/// the data model (Adapters) depend on this small utility.
///
/// PATH resolution uses the Windows <em>registry</em> PATH (System +
/// User) rather than the current process's PATH environment
/// variable. The worker is launched with
/// <c>CreateEnvironmentBlock(bInherit=false)</c>, which rebuilds
/// PATH from the registry; resolving the same way makes the
/// worker's own understanding of "where is pwsh" match what the
/// child processes actually see.
/// </summary>
internal static class ShellPathResolver
{
    /// <summary>
    /// Resolve a shell argument to an absolute executable path.
    /// Rooted paths are returned normalized but otherwise unchanged.
    /// Bare names are searched in PATH (registry-backed on Windows,
    /// environment-backed on Unix) with PATHEXT extension matching on
    /// Windows. Returns the input verbatim when nothing resolves so
    /// the caller can decide whether to fail loudly or let the OS
    /// produce its own "command not found".
    /// </summary>
    public static string Resolve(string shell)
    {
        if (Path.IsPathRooted(shell))
            return Path.GetFullPath(shell);

        // Use the system-registered PATH (registry), not the current
        // process PATH. The worker is launched with
        // CreateEnvironmentBlock(bInherit=false) which constructs
        // PATH from registry, so we must resolve against the same
        // source.
        var pathDirs = GetRegistryPath();

        // On Windows, try extensions from PATHEXT registry value.
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
    /// CreateEnvironmentBlock produces for child processes. Falls
    /// back to <c>Environment.GetEnvironmentVariable("PATH")</c> on
    /// non-Windows and on registry read failure.
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
    /// by construction — the caller (<see cref="Resolve"/>) branches
    /// on <c>OperatingSystem.IsWindows()</c> before invoking this, but
    /// the early-return keeps the CA1416 analyzer happy without
    /// relying on inter-method flow analysis.
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
}
