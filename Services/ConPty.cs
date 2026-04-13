using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using static Splash.Services.Win32Native;

namespace Splash.Services;

/// <summary>
/// Windows ConPTY (Pseudo Console) P/Invoke wrapper.
/// Uses Named Pipes (not anonymous pipes) following node-pty's proven pattern.
/// The pseudoconsole reads from the input pipe and writes to the output pipe.
/// After CreateProcess, the server-side pipe handles and hpc are closed,
/// and reading/writing is done through client-side connections.
/// </summary>
public static class ConPty
{
    // --- ConPTY APIs ---

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int CreatePseudoConsole(
        COORD size,
        SafeFileHandle hInput,
        SafeFileHandle hOutput,
        uint dwFlags,
        out IntPtr phPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int ResizePseudoConsole(IntPtr hPC, COORD size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void ClosePseudoConsole(IntPtr hPC);

    // --- Named Pipe APIs ---

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateNamedPipeW(
        string lpName,
        uint dwOpenMode,
        uint dwPipeMode,
        uint nMaxInstances,
        uint nOutBufferSize,
        uint nInBufferSize,
        uint nDefaultTimeOut,
        IntPtr lpSecurityAttributes);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ConnectNamedPipe(SafeFileHandle hNamedPipe, IntPtr lpOverlapped);

    // --- Process APIs ---

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(
        IntPtr lpAttributeList, uint dwAttributeCount, uint dwFlags, ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(
        IntPtr lpAttributeList, uint dwFlags, IntPtr attribute,
        IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(
        string? lpApplicationName,
        [In, Out] StringBuilder lpCommandLine,
        IntPtr lpProcessAttributes, IntPtr lpThreadAttributes,
        bool bInheritHandles, uint dwCreationFlags,
        IntPtr lpEnvironment, string? lpCurrentDirectory,
        IntPtr lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    // Shared P/Invoke: see Win32Native.cs for GetCurrentProcess, CloseHandle,
    // OpenProcessToken, CreateEnvironmentBlock, DestroyEnvironmentBlock

    // --- Constants ---

    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    // TOKEN_QUERY is in Win32Native.cs
    private static readonly IntPtr PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = (IntPtr)0x00020016;

    // Named Pipe constants
    private const uint PIPE_ACCESS_INBOUND = 0x00000001;
    private const uint PIPE_ACCESS_OUTBOUND = 0x00000002;
    private const uint FILE_FLAG_FIRST_PIPE_INSTANCE = 0x00080000;
    private const uint PIPE_TYPE_BYTE = 0x00000000;
    private const uint PIPE_READMODE_BYTE = 0x00000000;
    private const uint PIPE_WAIT = 0x00000000;
    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint OPEN_EXISTING = 3;
    private const uint PIPE_BUFFER_SIZE = 128 * 1024; // 128KB, same as node-pty

    // --- Structs ---

    [StructLayout(LayoutKind.Sequential)]
    private struct COORD
    {
        public short X;
        public short Y;
        public COORD(short x, short y) { X = x; Y = y; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }

    // --- Public API ---

    public sealed class ConPtySession : IPtySession
    {
        public IntPtr PseudoConsole { get; init; }
        /// <summary>Client-side handle for writing input to the shell.</summary>
        public SafeFileHandle InputClient { get; init; } = null!;
        /// <summary>Client-side handle for reading output from the shell.</summary>
        public SafeFileHandle OutputClient { get; init; } = null!;
        public IntPtr ProcessHandle { get; init; }
        public int ProcessId { get; init; }

        private IntPtr _attrList;
        private Stream? _inputStream;
        private Stream? _outputStream;

        internal void SetAttrList(IntPtr attrList) => _attrList = attrList;

        public Stream InputStream => _inputStream ??= new Win32PipeStream(InputClient, FileAccess.Write);
        public Stream OutputStream => _outputStream ??= new Win32PipeStream(OutputClient, FileAccess.Read);

        public void Resize(int cols, int rows)
        {
            if (PseudoConsole != IntPtr.Zero)
                ResizePseudoConsole(PseudoConsole, new COORD((short)cols, (short)rows));
        }

        public void Dispose()
        {
            if (PseudoConsole != IntPtr.Zero)
                ClosePseudoConsole(PseudoConsole);

            _inputStream?.Dispose();
            _outputStream?.Dispose();
            InputClient?.Dispose();
            OutputClient?.Dispose();

            if (_attrList != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(_attrList);
                Marshal.FreeHGlobal(_attrList);
            }
            if (ProcessHandle != IntPtr.Zero) CloseHandle(ProcessHandle);
        }
    }

    /// <summary>
    /// Create a ConPTY pseudoconsole and launch a process attached to it.
    /// Uses Named Pipes following node-pty's architecture.
    /// </summary>
    public static ConPtySession Start(
        string commandLine,
        string? workingDirectory = null,
        int cols = 120,
        int rows = 30,
        bool inheritEnvironment = false,
        IReadOnlyDictionary<string, string>? envOverrides = null)
        => StartImpl(commandLine, workingDirectory, cols, rows, inheritEnvironment, envOverrides);

    private static ConPtySession StartImpl(
        string commandLine,
        string? workingDirectory,
        int cols,
        int rows,
        bool inheritEnvironment,
        IReadOnlyDictionary<string, string>? envOverrides)
    {
        var pipeName = $@"\\.\pipe\splash-{Environment.ProcessId}-{Guid.NewGuid():N}";

        // Create server-side Named Pipes
        var inPipeName = pipeName + "-in";
        var outPipeName = pipeName + "-out";

        // Both pipes are created DUPLEX (INBOUND | OUTBOUND), matching node-pty's pattern.
        // Using unidirectional pipes causes MSYS2/Git Bash output to not flow through ConPTY.
        const uint PIPE_ACCESS_DUPLEX = PIPE_ACCESS_INBOUND | PIPE_ACCESS_OUTBOUND;

        var hInServer = CreateNamedPipeW(
            inPipeName,
            PIPE_ACCESS_DUPLEX | FILE_FLAG_FIRST_PIPE_INSTANCE,
            PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT,
            1, PIPE_BUFFER_SIZE, PIPE_BUFFER_SIZE, 30000, IntPtr.Zero);
        if (hInServer.IsInvalid)
            throw new InvalidOperationException($"CreateNamedPipe (in) failed: {Marshal.GetLastWin32Error()}");

        var hOutServer = CreateNamedPipeW(
            outPipeName,
            PIPE_ACCESS_DUPLEX | FILE_FLAG_FIRST_PIPE_INSTANCE,
            PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT,
            1, PIPE_BUFFER_SIZE, PIPE_BUFFER_SIZE, 30000, IntPtr.Zero);
        if (hOutServer.IsInvalid)
        {
            hInServer.Dispose();
            throw new InvalidOperationException($"CreateNamedPipe (out) failed: {Marshal.GetLastWin32Error()}");
        }

        // Create pseudoconsole with server-side pipe handles
        var size = new COORD((short)cols, (short)rows);
        int hr = CreatePseudoConsole(size, hInServer, hOutServer, 0, out var hPC);
        if (hr != 0)
        {
            hInServer.Dispose(); hOutServer.Dispose();
            throw new InvalidOperationException($"CreatePseudoConsole failed: HRESULT 0x{hr:X8}");
        }

        // Create client-side connections to the Named Pipes.
        // Use GENERIC_READ|GENERIC_WRITE (duplex) to match node-pty's pattern.
        const uint GENERIC_READWRITE = GENERIC_READ | GENERIC_WRITE;
        var hInClient = CreateFileW(inPipeName, GENERIC_READWRITE, 0, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (hInClient.IsInvalid)
        {
            ClosePseudoConsole(hPC); hInServer.Dispose(); hOutServer.Dispose();
            throw new InvalidOperationException($"CreateFile (in client) failed: {Marshal.GetLastWin32Error()}");
        }

        var hOutClient = CreateFileW(outPipeName, GENERIC_READWRITE, 0, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (hOutClient.IsInvalid)
        {
            ClosePseudoConsole(hPC); hInServer.Dispose(); hOutServer.Dispose(); hInClient.Dispose();
            throw new InvalidOperationException($"CreateFile (out client) failed: {Marshal.GetLastWin32Error()}");
        }

        // Connect server-side pipes (required before CreateProcess)
        ConnectNamedPipe(hInServer, IntPtr.Zero);
        ConnectNamedPipe(hOutServer, IntPtr.Zero);

        // Prepare attribute list
        var attrList = CreateAttributeList(hPC);

        // Build environment block.
        //
        // Four cases, controlled by (inheritEnvironment, envOverrides):
        //
        // (inherit=true,  overrides=null): envBlock=NULL →
        //     CreateProcessW inherits the parent's env verbatim. Used
        //     by the bash/zsh adapters that need MSYSTEM, HOME, PATH
        //     with Git paths preserved.
        //
        // (inherit=false, overrides=null): envBlock from
        //     CreateEnvironmentBlock(bInherit=false). The unmanaged
        //     memory is owned by Win32 and freed via
        //     DestroyEnvironmentBlock. Used by pwsh when the adapter
        //     has no env: block — give pwsh a clean registry-default
        //     env so it doesn't see the MCP server's variables.
        //
        // (inherit=true,  overrides=X) OR
        // (inherit=false, overrides=X): manually build a merged env
        //     block. Seed from GetEnvironmentVariables (inherit) or
        //     from the parsed CreateEnvironmentBlock output (clean),
        //     apply overrides, sort case-insensitive (Win32 requires
        //     sorted blocks), and serialise to AllocHGlobal'd memory.
        //     Freed via Marshal.FreeHGlobal.
        //
        // Milestone Phase C(c): the fourth case replaces the earlier
        // SetEnvironmentVariable-with-a-lock hack — no more mutating
        // the parent process, no racy cleanup on concurrent launches,
        // and pwsh's POWERSHELL_TELEMETRY_OPTOUT (and any future
        // clean-env adapter's overrides) finally takes effect.
        IntPtr envBlock = IntPtr.Zero;
        bool envBlockIsOwnedByUs = false;
        if (envOverrides != null && envOverrides.Count > 0)
        {
            var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (inheritEnvironment)
            {
                foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
                {
                    var key = e.Key as string;
                    if (!string.IsNullOrEmpty(key))
                        merged[key] = e.Value as string ?? "";
                }
            }
            else
            {
                IntPtr userBlock = IntPtr.Zero, hToken = IntPtr.Zero;
                try
                {
                    if (OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, out hToken) &&
                        CreateEnvironmentBlock(out userBlock, hToken, false))
                    {
                        ParseEnvBlockInto(userBlock, merged);
                    }
                }
                finally
                {
                    if (userBlock != IntPtr.Zero) DestroyEnvironmentBlock(userBlock);
                    if (hToken != IntPtr.Zero) CloseHandle(hToken);
                }
            }

            foreach (var kv in envOverrides)
                merged[kv.Key] = kv.Value;

            envBlock = SerializeEnvBlock(merged);
            envBlockIsOwnedByUs = true;
        }
        else if (!inheritEnvironment)
        {
            IntPtr hToken = IntPtr.Zero;
            try
            {
                if (OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, out hToken))
                    CreateEnvironmentBlock(out envBlock, hToken, false);
            }
            finally
            {
                if (hToken != IntPtr.Zero) CloseHandle(hToken);
            }
        }

        // Build STARTUPINFOEX manually in unmanaged memory.
        // CRITICAL: STARTF_USESTDHANDLES with NULL handles forces the child process
        // to use the pseudoconsole for all I/O instead of inheriting parent's handles.
        // Without this, MSYS2/Git Bash writes to the parent's stdout, bypassing ConPTY.
        // STARTUPINFOEXW layout on x64: sizeof(STARTUPINFOW)=104 + lpAttributeList pointer=8 = 112
        // Cannot use Marshal.SizeOf because STARTUPINFOEXW is manually constructed in unmanaged memory.
        const int startupInfoExSize = 112;  // sizeof(STARTUPINFOEXW) on x64
        const int dwFlagsOffset = 60;       // offsetof(STARTUPINFOW, dwFlags)
        const int lpAttributeListOffset = 104; // offsetof(STARTUPINFOEXW, lpAttributeList)
        const int STARTF_USESTDHANDLES = 0x00000100;

        var siPtr = Marshal.AllocHGlobal(startupInfoExSize);
        var zeros = new byte[startupInfoExSize];
        Marshal.Copy(zeros, 0, siPtr, startupInfoExSize);
        Marshal.WriteInt32(siPtr, 0, startupInfoExSize); // cb
        Marshal.WriteInt32(siPtr, dwFlagsOffset, STARTF_USESTDHANDLES); // dwFlags
        // hStdInput/hStdOutput/hStdError are already zero (NULL) from zero-init
        Marshal.WriteIntPtr(siPtr, lpAttributeListOffset, attrList);

        var cwd = workingDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        uint flags = EXTENDED_STARTUPINFO_PRESENT | (envBlock != IntPtr.Zero ? CREATE_UNICODE_ENVIRONMENT : 0);

        var cmdLine = new StringBuilder(commandLine);
        bool ok = CreateProcessW(
            null, cmdLine, IntPtr.Zero, IntPtr.Zero, false,
            flags, envBlock, cwd, siPtr, out var pi);

        Marshal.FreeHGlobal(siPtr);
        if (envBlock != IntPtr.Zero)
        {
            if (envBlockIsOwnedByUs)
                Marshal.FreeHGlobal(envBlock);
            else
                DestroyEnvironmentBlock(envBlock);
        }

        if (!ok)
        {
            var err = Marshal.GetLastWin32Error();
            ClosePseudoConsole(hPC);
            DeleteProcThreadAttributeList(attrList);
            Marshal.FreeHGlobal(attrList);
            hInServer.Dispose(); hOutServer.Dispose();
            hInClient.Dispose(); hOutClient.Dispose();
            throw new InvalidOperationException($"CreateProcessW failed: {err}");
        }

        // Following node-pty: close server-side pipes and thread handle after CreateProcess.
        // The pseudoconsole owns the server sides; client sides are used for I/O.
        CloseHandle(pi.hThread);
        hInServer.Dispose();
        hOutServer.Dispose();

        var session = new ConPtySession
        {
            PseudoConsole = hPC,
            InputClient = hInClient,
            OutputClient = hOutClient,
            ProcessHandle = pi.hProcess,
            ProcessId = (int)pi.dwProcessId,
        };
        session.SetAttrList(attrList);

        return session;
    }

    /// <summary>
    /// Parse a Win32 Unicode environment block (VAR=VAL\0VAR=VAL\0...\0\0)
    /// into a case-insensitive dictionary. Skips cmd.exe drive-letter
    /// state entries like "=C:=C:\\cwd" which start with '=' and would
    /// otherwise corrupt the serialised block downstream.
    /// </summary>
    private static void ParseEnvBlockInto(IntPtr block, Dictionary<string, string> dict)
    {
        if (block == IntPtr.Zero) return;

        int offset = 0;
        while (true)
        {
            var sb = new StringBuilder();
            while (true)
            {
                char c = (char)Marshal.ReadInt16(block, offset);
                offset += 2;
                if (c == '\0') break;
                sb.Append(c);
            }
            if (sb.Length == 0) break; // double-null terminator reached

            var entry = sb.ToString();
            var eq = entry.IndexOf('=');
            if (eq <= 0) continue; // "=C:=..." entries or malformed
            dict[entry[..eq]] = entry[(eq + 1)..];
        }
    }

    /// <summary>
    /// Serialise a dictionary to a Win32 Unicode environment block in
    /// unmanaged memory. The returned pointer must be freed via
    /// Marshal.FreeHGlobal. Keys are sorted case-insensitively because
    /// CreateProcessW requires a sorted environment block under
    /// CREATE_UNICODE_ENVIRONMENT.
    /// </summary>
    private static IntPtr SerializeEnvBlock(IDictionary<string, string> vars)
    {
        var sb = new StringBuilder();
        foreach (var kv in vars.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(kv.Key)) continue;
            sb.Append(kv.Key).Append('=').Append(kv.Value ?? "").Append('\0');
        }
        sb.Append('\0'); // double-null terminator

        var bytes = Encoding.Unicode.GetBytes(sb.ToString());
        var ptr = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, ptr, bytes.Length);
        return ptr;
    }

    private static IntPtr CreateAttributeList(IntPtr hPC)
    {
        var size = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);

        var attrList = Marshal.AllocHGlobal(size);
        if (!InitializeProcThreadAttributeList(attrList, 1, 0, ref size))
        {
            Marshal.FreeHGlobal(attrList);
            throw new InvalidOperationException($"InitializeProcThreadAttributeList failed: {Marshal.GetLastWin32Error()}");
        }

        if (!UpdateProcThreadAttribute(
                attrList, 0,
                PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                hPC,
                (IntPtr)IntPtr.Size,
                IntPtr.Zero, IntPtr.Zero))
        {
            DeleteProcThreadAttributeList(attrList);
            Marshal.FreeHGlobal(attrList);
            throw new InvalidOperationException($"UpdateProcThreadAttribute failed: {Marshal.GetLastWin32Error()}");
        }

        return attrList;
    }
}
