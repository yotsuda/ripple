using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using static Splash.Services.Win32Native;

namespace Splash.Services;

/// <summary>
/// Launches splash console worker processes with clean environment.
/// Uses Win32 CreateProcessW + CreateEnvironmentBlock (bInherit=false) to ensure
/// the child process does NOT inherit the MCP server's environment variables.
/// Equivalent to PowerShell.MCP's PwshLauncherWindows pattern.
/// </summary>
public class ProcessLauncher
{
    // Shared P/Invoke: see Win32Native.cs

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

    // CloseHandle is in Win32Native.cs

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

    // TOKEN_QUERY is in Win32Native.cs
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const uint CREATE_NEW_CONSOLE = 0x00000010;

    // STARTUPINFOW dwFlags / wShowWindow values used to spawn the
    // worker console visible-but-inactive so a new splash shell
    // doesn't steal keyboard focus from the editor or terminal the
    // user is currently working in. Without these, Windows spawns
    // CREATE_NEW_CONSOLE children as active foreground windows and
    // any keystrokes the user types land in splash's shell until
    // they notice and re-focus their original window.
    private const uint STARTF_USESHOWWINDOW = 0x00000001;
    private const ushort SW_HIDE = 0;
    private const ushort SW_SHOWNOACTIVATE = 4;

    /// <summary>
    /// Launch a splash console worker (--console mode) with clean environment.
    /// The worker creates a PTY (ConPTY on Windows, forkpty on Linux/macOS),
    /// launches the shell, and serves commands via Named Pipe.
    ///
    /// The worker constructs its pipe name as SP.{proxyPid}.{agentId}.{ownPid},
    /// matching the name the proxy constructs from the returned PID (same as PowerShell.MCP pattern).
    /// </summary>
    public int LaunchConsoleWorker(int proxyPid, string agentId, string shell, string? workingDirectory = null, string? banner = null, string? reason = null, bool noUserInput = false)
    {
        var exePath = Process.GetCurrentProcess().MainModule?.FileName
            ?? throw new InvalidOperationException("Cannot determine splash executable path");

        var cwd = workingDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var args = $"--console --proxy-pid {proxyPid} --agent-id {agentId} --shell \"{shell}\" --cwd \"{cwd}\"";
        if (!string.IsNullOrEmpty(banner))
            args += $" --banner \"{banner.Replace("\"", "\\\"")}\"";
        if (!string.IsNullOrEmpty(reason))
            args += $" --reason \"{reason.Replace("\"", "\\\"")}\"";
        if (noUserInput)
            args += " --no-user-input";

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Hide the console window entirely for test workers. Normal
            // splash usage shows the worker console (SW_SHOWNOACTIVATE)
            // because the user needs to see and interact with the shared
            // shell. During --adapter-tests there's no human in the loop:
            // hiding the window prevents the rapid window creation / focus
            // churn that disrupts the user's other windows. `noUserInput`
            // is already the signal "this is a test worker" so we reuse it
            // rather than threading a separate `hideWindow` parameter.
            return LaunchWithCleanEnvironment($"\"{exePath}\" {args}", cwd, hideWindow: noUserInput);
        }
        else
        {
            return LaunchConsoleWorkerUnix(exePath, proxyPid, agentId, shell, cwd);
        }
    }

    /// <summary>
    /// Launch console worker on Linux/macOS inside a visible terminal emulator
    /// (Terminal.app on macOS, $TERMINAL / gnome-terminal / konsole / xterm /
    /// alacritty / kitty / foot on Linux).
    ///
    /// The worker PID must equal the value baked into the pipe name
    /// SP.{proxyPid}.{agentId}.{workerPid}, but Process.Start on a terminal
    /// emulator returns the emulator's PID, not the splash worker's.
    ///
    /// Resolution: write a per-launch wrapper script that records its own
    /// $$ to a handshake PID file before exec'ing splash. Because exec(3)
    /// preserves the PID, the value in the file is the splash worker's
    /// actual PID. The proxy polls the file, reads the PID, and builds the
    /// same pipe name it would have on Windows — no protocol changes.
    /// </summary>
    [UnsupportedOSPlatform("windows")]
    private static int LaunchConsoleWorkerUnix(string exePath, int proxyPid, string agentId, string shell, string cwd)
    {
        SweepStaleHandshakeFiles();

        var handshake = Guid.NewGuid().ToString("N");
        var wrapperPath = $"/tmp/splash-launch-{handshake}.sh";
        var pidFilePath = $"/tmp/splash-launch-{handshake}.pid";

        // /tmp/splash-launch-<guid>.pid is written atomically via a .tmp +
        // rename so the proxy never observes a half-written PID. exec
        // replaces the shell with splash while preserving the PID already
        // written — that's the load-bearing guarantee of this design.
        var workerArgs = BuildWorkerArgs(exePath, proxyPid, agentId, shell, cwd);
        var wrapperContent =
            "#!/bin/bash\n" +
            $"echo $$ > \"{pidFilePath}.tmp\"\n" +
            $"mv \"{pidFilePath}.tmp\" \"{pidFilePath}\"\n" +
            $"exec {workerArgs}\n";

        File.WriteAllText(wrapperPath, wrapperContent);
        File.SetUnixFileMode(wrapperPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        var termPsi = BuildTerminalEmulatorPsi(wrapperPath, cwd)
            ?? throw new InvalidOperationException(
                "No supported terminal emulator found. Install gnome-terminal, konsole, xterm, " +
                "alacritty, kitty, or foot, or set $TERMINAL to an emulator that accepts '-e <command>'.");

        using (var launcher = Process.Start(termPsi)
            ?? throw new InvalidOperationException($"Failed to start terminal emulator: {termPsi.FileName}"))
        {
            // Don't wait on launcher.WaitForExit() — osascript returns
            // immediately, and Linux emulators may fork-detach. We only need
            // the PID written by the wrapper.
        }

        // 15 s covers macOS Terminal.app first-launch TCC prompt latency
        // (Accessibility / Automation permission dialogs) without blocking
        // the MCP server forever if the wrapper never runs.
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(pidFilePath))
            {
                try
                {
                    var content = File.ReadAllText(pidFilePath).Trim();
                    if (int.TryParse(content, out var workerPid) && workerPid > 0)
                    {
                        try { File.Delete(pidFilePath); } catch { }
                        try { File.Delete(wrapperPath); } catch { }
                        return workerPid;
                    }
                }
                catch { /* file racing with mv — retry next tick */ }
            }
            Thread.Sleep(50);
        }

        try { File.Delete(wrapperPath); } catch { }
        throw new InvalidOperationException(
            $"Timed out waiting for splash worker handshake ({pidFilePath}). " +
            "The terminal emulator may have failed to start the wrapper script.");
    }

    private static string BuildWorkerArgs(string exePath, int proxyPid, string agentId, string shell, string cwd)
    {
        // Each arg is single-quoted to survive bash re-parsing. Any ' inside
        // a value is escaped by closing the quote, adding an escaped ', and
        // reopening: foo'bar -> 'foo'\''bar'. agentId is a GUID and proxyPid
        // is an int, so only shell / cwd realistically need this.
        return $"{SingleQuote(exePath)} --console --proxy-pid {proxyPid} --agent-id {SingleQuote(agentId)} --shell {SingleQuote(shell)} --cwd {SingleQuote(cwd)}";
    }

    private static string SingleQuote(string s) => "'" + s.Replace("'", "'\\''") + "'";

    private static ProcessStartInfo? BuildTerminalEmulatorPsi(string wrapperPath, string cwd)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            // Terminal.app opens a new window and runs the argument as a
            // shell command. We pass the wrapper's path (short, no special
            // chars — only /tmp/splash-launch-<hex>.sh), which is safe to
            // embed in AppleScript. `activate` brings the window forward so
            // the user sees their new shell.
            var psi = new ProcessStartInfo
            {
                FileName = "osascript",
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = cwd,
            };
            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add($"tell application \"Terminal\" to do script \"{wrapperPath}\"");
            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add("tell application \"Terminal\" to activate");
            return psi;
        }

        // Linux: probe $TERMINAL first, then well-known emulators in order
        // of ubiquity. Invocation convention differs per emulator — most
        // accept `-e <cmd>`, but gnome-terminal deprecated `-e` and uses
        // `-- <cmd>` instead, kitty takes the command as positional args.
        var userTerm = Environment.GetEnvironmentVariable("TERMINAL");
        if (!string.IsNullOrEmpty(userTerm) && TryFindInPath(userTerm) is string userPath)
            return BuildLinuxTermPsi(userPath, userTerm, wrapperPath, cwd);

        foreach (var term in new[] { "gnome-terminal", "konsole", "xfce4-terminal",
                                     "mate-terminal", "alacritty", "kitty",
                                     "foot", "xterm" })
        {
            if (TryFindInPath(term) is string path)
                return BuildLinuxTermPsi(path, term, wrapperPath, cwd);
        }

        return null;
    }

    private static ProcessStartInfo BuildLinuxTermPsi(string termPath, string termName, string wrapperPath, string cwd)
    {
        var psi = new ProcessStartInfo
        {
            FileName = termPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = cwd,
        };

        // gnome-terminal: `-- <cmd> [args]` is the modern replacement for
        // the deprecated `-e "<cmd>"`. Everything after `--` is treated as
        // the command and its argv.
        if (termName == "gnome-terminal")
        {
            psi.ArgumentList.Add("--");
            psi.ArgumentList.Add(wrapperPath);
            return psi;
        }

        // kitty takes the command as positional args after its own flags.
        if (termName == "kitty")
        {
            psi.ArgumentList.Add(wrapperPath);
            return psi;
        }

        // foot: `foot <cmd> [args]` — positional like kitty.
        if (termName == "foot")
        {
            psi.ArgumentList.Add(wrapperPath);
            return psi;
        }

        // konsole, xfce4-terminal, mate-terminal, alacritty, xterm: all
        // accept `-e <cmd>` (single-argument form). For the wrapper path
        // this is unambiguous because the path has no whitespace.
        psi.ArgumentList.Add("-e");
        psi.ArgumentList.Add(wrapperPath);
        return psi;
    }

    private static string? TryFindInPath(string exeName)
    {
        // Absolute path short-circuit — $TERMINAL may be "/usr/bin/kitty".
        if (Path.IsPathRooted(exeName))
            return File.Exists(exeName) ? exeName : null;

        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return null;

        foreach (var dir in path.Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir, exeName);
                if (File.Exists(candidate)) return candidate;
            }
            catch { /* malformed PATH entry — skip */ }
        }
        return null;
    }

    private static void SweepStaleHandshakeFiles()
    {
        // Handshake files from crashed launches leak into /tmp forever
        // without this — ConsoleWorker does the same for its *.log and
        // .splash-exec-*.ps1 files. Best-effort, silent on error.
        try
        {
            var cutoff = DateTime.UtcNow.AddHours(-1);
            foreach (var pattern in new[] { "splash-launch-*.sh", "splash-launch-*.pid", "splash-launch-*.pid.tmp" })
            {
                foreach (var p in Directory.EnumerateFiles("/tmp", pattern))
                {
                    try
                    {
                        if (File.GetLastWriteTimeUtc(p) < cutoff) File.Delete(p);
                    }
                    catch { }
                }
            }
        }
        catch { }
    }

    /// <summary>
    /// Launch a process with a clean environment block from the registry.
    /// The process gets its own console window and does NOT inherit
    /// the MCP server's environment variables.
    /// </summary>
    private int LaunchWithCleanEnvironment(string commandLine, string? workingDirectory = null, bool hideWindow = false)
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

            // STARTF_USESHOWWINDOW + SW_SHOWNOACTIVATE: display the new
            // console window without activating it. Windows will put
            // the worker behind whatever the user is currently focused
            // on, so their editor / other terminal keeps keyboard focus
            // and the keystrokes they're already typing don't land in
            // splash's shell. The console is still fully visible and
            // the user can click into it deliberately whenever they
            // want to inspect or interact with the shell.
            //
            // When hideWindow=true (test workers, --no-user-input), the
            // console is suppressed entirely with SW_HIDE. This prevents
            // the rapid window creation / focus churn that disrupts the
            // user during full --adapter-tests runs — 15+ windows opening
            // in succession otherwise steals focus and renders the user's
            // typing into other apps unusable. Test workers have no
            // human in the loop, so there's nothing to show.
            var si = new STARTUPINFOW
            {
                cb = (uint)Marshal.SizeOf<STARTUPINFOW>(),
                dwFlags = STARTF_USESHOWWINDOW,
                wShowWindow = hideWindow ? SW_HIDE : SW_SHOWNOACTIVATE,
            };
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
