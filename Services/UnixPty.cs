using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Ripple.Services;

/// <summary>
/// Unix PTY (forkpty) wrapper for Linux and macOS.
/// Uses posix_openpt/grantpt/unlockpt/ptsname + fork/exec to create a PTY session.
/// </summary>
public static class UnixPty
{
    // --- libc imports ---

    [DllImport("libc", SetLastError = true)]
    private static extern int posix_openpt(int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int grantpt(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern int unlockpt(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern IntPtr ptsname(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern int open(string pathname, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern int dup2(int oldfd, int newfd);

    [DllImport("libc", SetLastError = true)]
    private static extern int setsid();

    [DllImport("libc", SetLastError = true)]
    private static extern int ioctl(int fd, ulong request, ref WinSize ws);

    [DllImport("libc", SetLastError = true)]
    private static extern int fork();

    [DllImport("libc", SetLastError = true)]
    private static extern int execvp(string file, string?[] argv);

    [DllImport("libc", SetLastError = true)]
    private static extern int chdir(string path);

    [DllImport("libc", SetLastError = true)]
    private static extern int read(int fd, byte[] buf, int count);

    [DllImport("libc", SetLastError = true)]
    private static extern int write(int fd, byte[] buf, int count);

    [DllImport("libc", SetLastError = true)]
    private static extern int waitpid(int pid, out int status, int options);

    // --- termios (used by the worker's stdin raw-mode toggle) ---
    //
    // The struct layout differs between macOS and Linux (and even between
    // 32/64-bit), so we never marshal the fields by name — we allocate a
    // fixed buffer big enough for any platform, let tcgetattr populate it,
    // call cfmakeraw on the pointer, and hand the same bytes back to
    // tcsetattr. 256 bytes covers every Unix this project targets with
    // generous headroom (actual termios sizes: macOS 72, Linux 60).
    public const int TermiosBufSize = 256;
    public const int TCSANOW = 0;

    [DllImport("libc", EntryPoint = "tcgetattr", SetLastError = true)]
    public static extern int TcGetAttr(int fd, IntPtr termios);

    [DllImport("libc", EntryPoint = "tcsetattr", SetLastError = true)]
    public static extern int TcSetAttr(int fd, int optionalActions, IntPtr termios);

    [DllImport("libc", EntryPoint = "cfmakeraw", SetLastError = true)]
    public static extern void CfMakeRaw(IntPtr termios);

    [DllImport("libc", EntryPoint = "isatty", SetLastError = true)]
    public static extern int IsATty(int fd);

    [DllImport("libc", EntryPoint = "read", SetLastError = true)]
    public static extern int ReadFd(int fd, byte[] buf, int count);

    // --- posix_spawn: the safe replacement for fork/execvp on multi-threaded
    //     .NET processes.
    //
    // Why not fork+execvp: .NET runs GC / JIT / thread-pool threads that
    // duplicate into the child on fork() with inconsistent lock and signal
    // state. Ubuntu 24.04 + .NET 9 SIGSEGVs the child immediately after
    // clone(SIGCHLD), before execvp has a chance to replace the image.
    // macOS has the same vulnerability in principle but its tiered JIT
    // happens to fire less aggressively so the race wasn't exposed in
    // earlier ad-hoc testing. posix_spawn avoids the problem entirely —
    // it's the POSIX-standard async-signal-safe primitive for starting a
    // new process, and glibc implements it via vfork() which only copies
    // the calling thread. Both Linux (glibc 2.26+) and macOS 10.15+ support
    // POSIX_SPAWN_SETSID, which combined with opening the slave PTY
    // without O_NOCTTY gives us a child that's a new session leader with
    // the slave as its controlling terminal — exactly the setup
    // forkpty(3) would give us, but via a safe-for-.NET code path.
    //
    // posix_spawn_file_actions_t / posix_spawnattr_t are opaque structs.
    // Their actual sizes are ~80 bytes on Linux glibc, smaller on macOS,
    // and may grow in future libc versions — so we allocate a generous
    // 512-byte buffer instead of hard-coding the exact size.
    private const int PosixSpawnFileActionsSize = 512;
    private const int PosixSpawnAttrSize = 512;

    // POSIX_SPAWN_SETSID: make the child a new session leader, equivalent
    // to calling setsid() before exec. Flag value differs between macOS
    // (<spawn.h>: 0x400) and glibc (<bits/spawn.h>: 0x80) — both stable
    // values, unchanged in decades.
    private static short POSIX_SPAWN_SETSID => OperatingSystem.IsMacOS() ? (short)0x400 : (short)0x80;

    [DllImport("libc", SetLastError = true)]
    private static extern int posix_spawn_file_actions_init(IntPtr actions);

    [DllImport("libc", SetLastError = true)]
    private static extern int posix_spawn_file_actions_destroy(IntPtr actions);

    [DllImport("libc", SetLastError = true)]
    private static extern int posix_spawn_file_actions_addopen(IntPtr actions, int fd, string path, int oflag, uint mode);

    [DllImport("libc", SetLastError = true)]
    private static extern int posix_spawn_file_actions_adddup2(IntPtr actions, int fromFd, int toFd);

    [DllImport("libc", SetLastError = true)]
    private static extern int posix_spawn_file_actions_addclose(IntPtr actions, int fd);

    // _np suffix: non-portable but available on both Linux (glibc 2.29+)
    // and macOS 10.15+. Runtime requirements are already higher than that
    // (Ubuntu 24.04 ships glibc 2.39; we test on macOS Sequoia).
    [DllImport("libc", EntryPoint = "posix_spawn_file_actions_addchdir_np", SetLastError = true)]
    private static extern int posix_spawn_file_actions_addchdir_np(IntPtr actions, string path);

    [DllImport("libc", SetLastError = true)]
    private static extern int posix_spawnattr_init(IntPtr attr);

    [DllImport("libc", SetLastError = true)]
    private static extern int posix_spawnattr_destroy(IntPtr attr);

    [DllImport("libc", SetLastError = true)]
    private static extern int posix_spawnattr_setflags(IntPtr attr, short flags);

    // UnmanagedType.LPArray does not append a NULL terminator, so argv /
    // envp must be constructed with an explicit trailing null element
    // before they're passed in — posix_spawnp would otherwise walk past
    // the end of the managed array looking for the sentinel and segfault
    // or EFAULT. The parameter type is string?[] to make the null slot
    // explicit at the call site.
    [DllImport("libc", SetLastError = true)]
    private static extern int posix_spawnp(
        out int pid,
        string path,
        IntPtr actions,
        IntPtr attr,
        [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPStr)] string?[] argv,
        [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPStr)] string?[]? envp);

    // O_RDWR
    private const int O_RDWR = 2;
    // O_NOCTTY
    private const int O_NOCTTY = 0x100;

    // TIOCSWINSZ - differs by platform
    private static ulong TIOCSWINSZ => OperatingSystem.IsMacOS() ? 0x80087467UL : 0x5414UL;

    [StructLayout(LayoutKind.Sequential)]
    private struct WinSize
    {
        public ushort ws_row;
        public ushort ws_col;
        public ushort ws_xpixel;
        public ushort ws_ypixel;
    }

    /// <summary>
    /// Unix PTY session implementing IPtySession.
    /// </summary>
    public sealed class UnixPtySession : IPtySession
    {
        private readonly int _masterFd;
        private readonly UnixFdStream _inputStream;
        private readonly UnixFdStream _outputStream;
        private bool _disposed;

        public int ProcessId { get; }
        public Stream InputStream => _inputStream;
        public Stream OutputStream => _outputStream;

        internal UnixPtySession(int masterFd, int childPid)
        {
            _masterFd = masterFd;
            ProcessId = childPid;
            // Master fd is bidirectional: write to send input, read to get output
            _inputStream = new UnixFdStream(masterFd, FileAccess.Write);
            _outputStream = new UnixFdStream(masterFd, FileAccess.Read);
        }

        public void Resize(int cols, int rows)
        {
            var ws = new WinSize
            {
                ws_col = (ushort)cols,
                ws_row = (ushort)rows,
            };
            ioctl(_masterFd, TIOCSWINSZ, ref ws);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            // Don't dispose streams — they share the same fd
            close(_masterFd);
        }
    }

    /// <summary>
    /// Stream wrapper around a Unix file descriptor.
    /// </summary>
    private sealed class UnixFdStream : Stream
    {
        private readonly int _fd;
        private readonly FileAccess _access;

        public UnixFdStream(int fd, FileAccess access)
        {
            _fd = fd;
            _access = access;
        }

        public override bool CanRead => _access.HasFlag(FileAccess.Read);
        public override bool CanWrite => _access.HasFlag(FileAccess.Write);
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (offset == 0)
            {
                int n = UnixPty.read(_fd, buffer, count);
                if (n < 0) throw new IOException($"read() failed: {Marshal.GetLastPInvokeError()}");
                return n;
            }
            // Handle offset by using a temp buffer
            var tmp = new byte[count];
            int read = UnixPty.read(_fd, tmp, count);
            if (read < 0) throw new IOException($"read() failed: {Marshal.GetLastPInvokeError()}");
            Array.Copy(tmp, 0, buffer, offset, read);
            return read;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            byte[] data;
            if (offset == 0 && count == buffer.Length)
            {
                data = buffer;
            }
            else
            {
                data = new byte[count];
                Array.Copy(buffer, offset, data, 0, count);
            }
            int written = UnixPty.write(_fd, data, count);
            if (written < 0) throw new IOException($"write() failed: {Marshal.GetLastPInvokeError()}");
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    /// <summary>
    /// Create a Unix PTY and spawn a process via posix_spawn.
    ///
    /// Equivalent to forkpty(3): the spawned child runs as a new session
    /// leader with the slave PTY as its controlling terminal, inheriting
    /// <paramref name="workingDirectory"/> as cwd. The parent retains the
    /// master end of the PTY and returns it wrapped in
    /// <see cref="UnixPtySession"/>.
    ///
    /// posix_spawn is used instead of raw fork+execvp because .NET
    /// processes are multi-threaded (GC, JIT, thread pool) and fork()
    /// duplicates the full virtual memory image but only the calling
    /// thread — every other thread's spinlocks / TLS / signal masks end
    /// up in inconsistent states in the child, which SIGSEGVs in
    /// libcoreclr before execvp runs. posix_spawn sidesteps this by
    /// using vfork() semantics and only applying file_actions /
    /// spawnattrs between the syscall and the exec.
    /// </summary>
    public static UnixPtySession Start(string commandLine, string? workingDirectory = null, int cols = 120, int rows = 30, IReadOnlyDictionary<string, string>? envOverrides = null)
    {
        int masterFd = posix_openpt(O_RDWR | O_NOCTTY);
        if (masterFd < 0)
            throw new InvalidOperationException($"posix_openpt failed: {Marshal.GetLastPInvokeError()}");

        if (grantpt(masterFd) != 0 || unlockpt(masterFd) != 0)
        {
            close(masterFd);
            throw new InvalidOperationException($"grantpt/unlockpt failed: {Marshal.GetLastPInvokeError()}");
        }

        var slaveNamePtr = ptsname(masterFd);
        if (slaveNamePtr == IntPtr.Zero)
        {
            close(masterFd);
            throw new InvalidOperationException("ptsname returned null");
        }
        var slaveName = Marshal.PtrToStringAnsi(slaveNamePtr)!;

        // Window size lives on the master — the slave shares it via the
        // kernel-managed pty pair, so the child shell sees the right
        // dimensions on its first stty / tput(cols) call without any
        // extra action on the slave end.
        var ws = new WinSize { ws_col = (ushort)cols, ws_row = (ushort)rows };
        ioctl(masterFd, TIOCSWINSZ, ref ws);

        var args = ParseCommandLine(commandLine);
        if (args.Length == 0)
        {
            close(masterFd);
            throw new InvalidOperationException($"Empty command line: '{commandLine}'");
        }

        // argv / envp each need an explicit NULL sentinel at the end —
        // LPArray marshalling hands posix_spawnp a char** that's exactly
        // the length of the managed array, with no trailing NULL, so the
        // callee reads garbage off the end if we don't put one there
        // ourselves.
        var argv = new string?[args.Length + 1];
        Array.Copy(args, argv, args.Length);
        argv[args.Length] = null;

        // Build envp. Passing null to posix_spawnp inherits the parent's
        // environment; passing an explicit array replaces it wholesale.
        // To honour envOverrides we have to materialise the full parent
        // env, layer the overrides on top, and hand posix_spawn the
        // merged result.
        string?[]? envp = null;
        if (envOverrides is { Count: > 0 })
        {
            var merged = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (System.Collections.DictionaryEntry kv in Environment.GetEnvironmentVariables())
            {
                merged[(string)kv.Key] = (string?)kv.Value ?? "";
            }
            foreach (var kv in envOverrides)
            {
                merged[kv.Key] = kv.Value;
            }
            envp = merged.Select(kv => (string?)$"{kv.Key}={kv.Value}")
                         .Append(null)
                         .ToArray();
        }

        IntPtr fileActions = Marshal.AllocHGlobal(PosixSpawnFileActionsSize);
        IntPtr attr = Marshal.AllocHGlobal(PosixSpawnAttrSize);
        bool fileActionsInit = false, attrInit = false;
        int childPid;
        try
        {
            if (posix_spawn_file_actions_init(fileActions) != 0)
                throw new InvalidOperationException($"posix_spawn_file_actions_init failed: errno={Marshal.GetLastPInvokeError()}");
            fileActionsInit = true;

            if (posix_spawnattr_init(attr) != 0)
                throw new InvalidOperationException($"posix_spawnattr_init failed: errno={Marshal.GetLastPInvokeError()}");
            attrInit = true;

            // POSIX_SPAWN_SETSID runs setsid() between the spawn syscall
            // and exec. After setsid the child has no controlling tty;
            // the subsequent addopen for the slave path WITHOUT O_NOCTTY
            // attaches the slave as the new controlling terminal (the
            // standard POSIX behaviour for a session leader that opens
            // a TTY).
            if (posix_spawnattr_setflags(attr, POSIX_SPAWN_SETSID) != 0)
                throw new InvalidOperationException($"posix_spawnattr_setflags failed: errno={Marshal.GetLastPInvokeError()}");

            // Open the slave on fd 3 first, then dup2 it onto 0/1/2 and
            // close 3. Going through an explicit fd instead of addopen-
            // ing each of 0/1/2 directly avoids the edge case where the
            // spawn engine tries to close fd 0 before opening on 0.
            if (posix_spawn_file_actions_addopen(fileActions, 3, slaveName, O_RDWR, 0) != 0)
                throw new InvalidOperationException($"file_actions addopen slave failed: errno={Marshal.GetLastPInvokeError()}");

            if (posix_spawn_file_actions_adddup2(fileActions, 3, 0) != 0 ||
                posix_spawn_file_actions_adddup2(fileActions, 3, 1) != 0 ||
                posix_spawn_file_actions_adddup2(fileActions, 3, 2) != 0)
                throw new InvalidOperationException($"file_actions dup2 failed: errno={Marshal.GetLastPInvokeError()}");

            if (posix_spawn_file_actions_addclose(fileActions, 3) != 0)
                throw new InvalidOperationException($"file_actions addclose failed: errno={Marshal.GetLastPInvokeError()}");

            if (!string.IsNullOrEmpty(workingDirectory))
            {
                if (posix_spawn_file_actions_addchdir_np(fileActions, workingDirectory) != 0)
                    throw new InvalidOperationException($"file_actions addchdir_np failed: errno={Marshal.GetLastPInvokeError()}");
            }

            int rc = posix_spawnp(out childPid, args[0], fileActions, attr, argv, envp);
            if (rc != 0)
                throw new InvalidOperationException($"posix_spawnp failed: errno={rc}");
        }
        catch
        {
            close(masterFd);
            throw;
        }
        finally
        {
            if (fileActionsInit) posix_spawn_file_actions_destroy(fileActions);
            if (attrInit) posix_spawnattr_destroy(attr);
            Marshal.FreeHGlobal(fileActions);
            Marshal.FreeHGlobal(attr);
        }

        return new UnixPtySession(masterFd, childPid);
    }

    private static string[] ParseCommandLine(string cmdLine)
    {
        var args = new List<string>();
        var current = new System.Text.StringBuilder();
        bool inQuote = false;

        foreach (char c in cmdLine)
        {
            if (c == '"')
            {
                inQuote = !inQuote;
            }
            else if (c == ' ' && !inQuote)
            {
                if (current.Length > 0)
                {
                    args.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
        }
        if (current.Length > 0) args.Add(current.ToString());

        return args.ToArray();
    }
}
