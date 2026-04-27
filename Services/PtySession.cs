namespace Ripple.Services;

/// <summary>
/// Platform-independent PTY session interface.
/// Windows: ConPTY, Linux/macOS: forkpty.
/// </summary>
public interface IPtySession : IDisposable
{
    int ProcessId { get; }
    Stream InputStream { get; }
    Stream OutputStream { get; }
    void Resize(int cols, int rows);
}

/// <summary>
/// Factory for creating platform-specific PTY sessions.
/// </summary>
public static class PtyFactory
{
    public static IPtySession Start(
        string commandLine,
        string? workingDirectory = null,
        int cols = 120,
        int rows = 30,
        bool inheritEnvironment = false,
        IReadOnlyDictionary<string, string>? envOverrides = null)
    {
        var merged = MergePagerDefaults(envOverrides);

        if (OperatingSystem.IsWindows())
            return ConPty.Start(commandLine, workingDirectory, cols, rows, inheritEnvironment, merged);

        return UnixPty.Start(commandLine, workingDirectory, cols, rows, merged);
    }

    // Pager-suppression env defaults for every console ripple launches.
    // External CLIs that probe isatty (git, less, man, kubectl, etc.) start
    // an interactive pager when stdout is a real TTY — which ripple's
    // ConPTY / forkpty always is. The pager freezes the visible console
    // until someone sends `q`, which produces no human-UX win on a
    // shared AI+human console where output already streams freely. A few
    // tools recognize different env names (GIT_PAGER, MANPAGER), so cover
    // the common ones at once. Adapter YAML `process.env` still wins —
    // an adapter that genuinely needs a pager can re-declare PAGER.
    private static IReadOnlyDictionary<string, string> MergePagerDefaults(
        IReadOnlyDictionary<string, string>? envOverrides)
    {
        var merged = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PAGER"] = "cat",
            ["GIT_PAGER"] = "cat",
            ["MANPAGER"] = "cat",
        };
        if (envOverrides != null)
        {
            foreach (var kv in envOverrides)
                merged[kv.Key] = kv.Value;
        }
        return merged;
    }
}
