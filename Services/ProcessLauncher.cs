using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ShellPilot.Services;

/// <summary>
/// Launches shellpilot console worker processes with clean environment.
/// Uses Win32 CreateProcessW + CreateEnvironmentBlock (bInherit=false) to ensure
/// the child process does NOT inherit the MCP server's environment variables.
/// Equivalent to PowerShell.MCP's PwshLauncherWindows pattern.
/// </summary>
public class ProcessLauncher
{
    // Win32 API declarations
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(out IntPtr lpEnvironment, IntPtr hToken, bool bInherit);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool DestroyEnvironmentBlock(IntPtr lpEnvironment);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(
        string? lpApplicationName,
        string lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFOW lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFOW
    {
        public uint cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public uint dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public ushort wShowWindow;
        public ushort cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }

    private const uint TOKEN_QUERY = 0x0008;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const uint CREATE_NEW_CONSOLE = 0x00000010;

    /// <summary>
    /// Launch a shellpilot console worker (--console mode) with clean environment.
    /// The worker creates a PTY (ConPTY on Windows, forkpty on Linux/macOS),
    /// launches the shell, and serves commands via Named Pipe.
    ///
    /// The worker constructs its pipe name as SP.{proxyPid}.{agentId}.{ownPid},
    /// matching the name the proxy constructs from the returned PID (same as PowerShell.MCP pattern).
    /// </summary>
    public int LaunchConsoleWorker(int proxyPid, string agentId, string shell, string? workingDirectory = null, string? banner = null, string? reason = null)
    {
        var exePath = Process.GetCurrentProcess().MainModule?.FileName
            ?? throw new InvalidOperationException("Cannot determine shellpilot executable path");

        var cwd = workingDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var args = $"--console --proxy-pid {proxyPid} --agent-id {agentId} --shell \"{shell}\" --cwd \"{cwd}\"";
        if (!string.IsNullOrEmpty(banner))
            args += $" --banner \"{banner.Replace("\"", "\\\"")}\"";
        if (!string.IsNullOrEmpty(reason))
            args += $" --reason \"{reason.Replace("\"", "\\\"")}\"";

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return LaunchWithCleanEnvironment($"\"{exePath}\" {args}", cwd);
        }
        else
        {
            return LaunchConsoleWorkerUnix(exePath, proxyPid, agentId, shell, cwd);
        }
    }

    /// <summary>
    /// Launch console worker on Linux/macOS with clean environment.
    /// Uses env -i to strip inherited environment, then login shell to restore user defaults.
    /// </summary>
    private static int LaunchConsoleWorkerUnix(string exePath, int proxyPid, string agentId, string shell, string cwd)
    {
        // Use setsid to detach from parent's terminal session
        var psi = new ProcessStartInfo
        {
            FileName = "setsid",
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = cwd,
        };

        // Get user's login shell for clean environment setup
        var loginShell = Environment.GetEnvironmentVariable("SHELL") ?? "/bin/bash";

        // setsid <loginShell> -l -c 'exec <exePath> --console ...'
        // Login shell (-l) loads user's profile for clean PATH etc.
        var workerCmd = $"exec \"{exePath}\" --console --proxy-pid {proxyPid} --agent-id {agentId} --shell \"{shell}\" --cwd \"{cwd}\"";

        psi.ArgumentList.Add(loginShell);
        psi.ArgumentList.Add("-l");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(workerCmd);

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to launch console worker process");

        var pid = process.Id;
        process.Dispose();
        return pid;
    }

    /// <summary>
    /// Launch a process with a clean environment block from the registry.
    /// The process gets its own console window and does NOT inherit
    /// the MCP server's environment variables.
    /// </summary>
    private int LaunchWithCleanEnvironment(string commandLine, string? workingDirectory = null)
    {
        IntPtr hToken = IntPtr.Zero;
        IntPtr env = IntPtr.Zero;
        IntPtr hProcess = IntPtr.Zero;
        IntPtr hThread = IntPtr.Zero;

        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, out hToken))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            // false = do not inherit current process environment
            // Uses only system/user default environment variables from registry
            if (!CreateEnvironmentBlock(out env, hToken, false))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            var si = new STARTUPINFOW { cb = (uint)Marshal.SizeOf<STARTUPINFOW>() };
            var pi = new PROCESS_INFORMATION();

            bool ok = CreateProcessW(
                null,
                commandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                false,  // Do not inherit handles
                CREATE_UNICODE_ENVIRONMENT | CREATE_NEW_CONSOLE,
                env,
                workingDirectory ?? userProfile,
                ref si,
                out pi);

            if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error());

            hProcess = pi.hProcess;
            hThread = pi.hThread;
            return (int)pi.dwProcessId;
        }
        finally
        {
            if (env != IntPtr.Zero) DestroyEnvironmentBlock(env);
            if (hToken != IntPtr.Zero) CloseHandle(hToken);
            if (hProcess != IntPtr.Zero) CloseHandle(hProcess);
            if (hThread != IntPtr.Zero) CloseHandle(hThread);
        }
    }
}
