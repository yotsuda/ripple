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
        if (OperatingSystem.IsWindows())
            return ConPty.Start(commandLine, workingDirectory, cols, rows, inheritEnvironment, envOverrides);

        return UnixPty.Start(commandLine, workingDirectory, cols, rows, envOverrides);
    }
}
