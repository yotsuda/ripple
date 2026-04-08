using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ShellPilot.Services;

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
    /// Create a Unix PTY and fork/exec a process.
    /// </summary>
    public static UnixPtySession Start(string commandLine, string? workingDirectory = null, int cols = 120, int rows = 30)
    {
        // Open master PTY
        int masterFd = posix_openpt(O_RDWR | O_NOCTTY);
        if (masterFd < 0)
            throw new InvalidOperationException($"posix_openpt failed: {Marshal.GetLastPInvokeError()}");

        if (grantpt(masterFd) != 0 || unlockpt(masterFd) != 0)
        {
            close(masterFd);
            throw new InvalidOperationException($"grantpt/unlockpt failed: {Marshal.GetLastPInvokeError()}");
        }

        // Get slave name
        var slaveNamePtr = ptsname(masterFd);
        if (slaveNamePtr == IntPtr.Zero)
        {
            close(masterFd);
            throw new InvalidOperationException("ptsname returned null");
        }
        var slaveName = Marshal.PtrToStringAnsi(slaveNamePtr)!;

        // Set window size
        var ws = new WinSize { ws_col = (ushort)cols, ws_row = (ushort)rows };
        ioctl(masterFd, TIOCSWINSZ, ref ws);

        // Fork
        int pid = fork();
        if (pid < 0)
        {
            close(masterFd);
            throw new InvalidOperationException($"fork failed: {Marshal.GetLastPInvokeError()}");
        }

        if (pid == 0)
        {
            // Child process
            close(masterFd);
            setsid();

            int slaveFd = open(slaveName, O_RDWR);
            if (slaveFd < 0) Environment.Exit(1);

            dup2(slaveFd, 0); // stdin
            dup2(slaveFd, 1); // stdout
            dup2(slaveFd, 2); // stderr
            if (slaveFd > 2) close(slaveFd);

            if (workingDirectory != null)
                chdir(workingDirectory);

            // Parse command line (simple split, handles quoted paths)
            var args = ParseCommandLine(commandLine);
            var argv = new string?[args.Length + 1];
            Array.Copy(args, argv, args.Length);
            argv[args.Length] = null;

            execvp(args[0], argv);
            Environment.Exit(1); // exec failed
        }

        // Parent process
        return new UnixPtySession(masterFd, pid);
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
