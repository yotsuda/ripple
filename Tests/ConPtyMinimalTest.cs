using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Ripple.Tests;

/// <summary>
/// Test: does ConPTY pass OSC 633 sequences through the output pipe?
/// Creates ConPTY with cmd.exe, sends an echo command that outputs OSC 633.
/// </summary>
public static class ConPtyMinimalTest
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int CreatePseudoConsole(COORD size, SafeFileHandle hInput, SafeFileHandle hOutput, uint dwFlags, out IntPtr phPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void ClosePseudoConsole(IntPtr hPC);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateNamedPipeW(string lpName, uint dwOpenMode, uint dwPipeMode, uint nMaxInstances, uint nOutBufferSize, uint nInBufferSize, uint nDefaultTimeOut, IntPtr lpSecurityAttributes);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ConnectNamedPipe(SafeFileHandle hNamedPipe, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, uint dwAttributeCount, uint dwFlags, ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr attribute, IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(string? lpApplicationName, StringBuilder lpCommandLine, IntPtr lpProcessAttributes, IntPtr lpThreadAttributes, bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment, string? lpCurrentDirectory, IntPtr lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(SafeFileHandle hFile, byte[] lpBuffer, uint nNumberOfBytesToRead, out uint lpNumberOfBytesRead, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(SafeFileHandle hFile, byte[] lpBuffer, uint nNumberOfBytesToWrite, out uint lpNumberOfBytesWritten, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(out IntPtr lpEnvironment, IntPtr hToken, bool bInherit);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool DestroyEnvironmentBlock(IntPtr lpEnvironment);

    [StructLayout(LayoutKind.Sequential)]
    private struct COORD { public short X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public uint dwProcessId, dwThreadId; }

    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    private static readonly IntPtr PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = (IntPtr)0x00020016;
    private const uint PIPE_ACCESS_INBOUND = 0x00000001;
    private const uint PIPE_ACCESS_OUTBOUND = 0x00000002;
    private const uint FILE_FLAG_FIRST_PIPE_INSTANCE = 0x00080000;
    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint OPEN_EXISTING = 3;

    public static void Run()
    {
        Console.Error.WriteLine("=== ConPTY OSC Passthrough Test ===");

        var basePipe = $@"\\.\pipe\sp-test-{Environment.ProcessId}";

        // Create Named Pipes (server side)
        var hInServer = CreateNamedPipeW(basePipe + "-in", PIPE_ACCESS_INBOUND | FILE_FLAG_FIRST_PIPE_INSTANCE, 0, 1, 131072, 131072, 30000, IntPtr.Zero);
        var hOutServer = CreateNamedPipeW(basePipe + "-out", PIPE_ACCESS_OUTBOUND | FILE_FLAG_FIRST_PIPE_INSTANCE, 0, 1, 131072, 131072, 30000, IntPtr.Zero);

        // Create pseudoconsole
        int hr = CreatePseudoConsole(new COORD { X = 80, Y = 25 }, hInServer, hOutServer, 0, out var hPC);
        Console.Error.WriteLine($"  CreatePseudoConsole: hr=0x{hr:X8}");
        if (hr != 0) return;

        // Client connections
        var hInClient = CreateFileW(basePipe + "-in", GENERIC_WRITE, 0, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        var hOutClient = CreateFileW(basePipe + "-out", GENERIC_READ, 0, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        ConnectNamedPipe(hInServer, IntPtr.Zero);
        ConnectNamedPipe(hOutServer, IntPtr.Zero);

        // Attr list
        var attrSize = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attrSize);
        var attrList = Marshal.AllocHGlobal(attrSize);
        InitializeProcThreadAttributeList(attrList, 1, 0, ref attrSize);
        UpdateProcThreadAttribute(attrList, 0, PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, hPC, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero);

        // STARTUPINFOEX
        var siPtr = Marshal.AllocHGlobal(112);
        var zeros = new byte[112];
        Marshal.Copy(zeros, 0, siPtr, 112);
        Marshal.WriteInt32(siPtr, 112);
        Marshal.WriteIntPtr(siPtr, 104, attrList);

        // Build clean environment block (same as ConPty.cs)
        IntPtr envBlock = IntPtr.Zero;
        IntPtr hToken = IntPtr.Zero;
        OpenProcessToken(GetCurrentProcess(), 0x0008, out hToken);
        CreateEnvironmentBlock(out envBlock, hToken, false);
        if (hToken != IntPtr.Zero) CloseHandle(hToken);

        uint createFlags = EXTENDED_STARTUPINFO_PRESENT | (envBlock != IntPtr.Zero ? 0x00000400u : 0u); // CREATE_UNICODE_ENVIRONMENT

        var cmdLine = new StringBuilder("\"pwsh.exe\"");
        bool ok = CreateProcessW(null, cmdLine, IntPtr.Zero, IntPtr.Zero, false, createFlags, envBlock, null, siPtr, out var pi);
        if (envBlock != IntPtr.Zero) DestroyEnvironmentBlock(envBlock);
        Console.Error.WriteLine($"  CreateProcessW: ok={ok}, pid={pi.dwProcessId}");
        if (!ok) return;

        CloseHandle(pi.hThread);
        hInServer.Dispose();
        hOutServer.Dispose();
        Marshal.FreeHGlobal(siPtr);

        // Start reading output on background thread
        var buf = new byte[4096];
        var allOutput = new StringBuilder();
        var readThread = new Thread(() =>
        {
            while (true)
            {
                if (!ReadFile(hOutClient, buf, (uint)buf.Length, out var bytesRead, IntPtr.Zero) || bytesRead == 0)
                    break;
                allOutput.Append(Encoding.UTF8.GetString(buf, 0, (int)bytesRead));
            }
        });
        readThread.IsBackground = true;
        readThread.Start();

        // Wait for shell to start (profile may take 2-4 seconds)
        Thread.Sleep(5000);
        Console.Error.WriteLine($"  After 3s wait: {allOutput.Length} chars captured");

        // Test multiple methods of outputting OSC 633
        var oscCmd = "[Console]::Write([char]0x1B + ']633;A' + [char]7); Write-Host 'METHOD1_CONSOLE'; Write-Host -NoNewline \"$([char]0x1B)]633;B$([char]7)\"; Write-Host 'METHOD2_WRITEHOST'; $Host.UI.Write(\"$([char]0x1B)]633;C$([char]7)\"); Write-Host 'METHOD3_HOSTUI'\r\n";
        var cmdBytes = Encoding.UTF8.GetBytes(oscCmd);
        WriteFile(hInClient, cmdBytes, (uint)cmdBytes.Length, out _, IntPtr.Zero);
        Console.Error.WriteLine("  Sent OSC test command to PTY");

        // Wait for output
        Thread.Sleep(3000);
        Console.Error.WriteLine($"  After command: {allOutput.Length} chars captured");

        // Send exit
        var exitCmd = Encoding.UTF8.GetBytes("exit\r\n");
        WriteFile(hInClient, exitCmd, (uint)exitCmd.Length, out _, IntPtr.Zero);
        readThread.Join(5000);

        // Analyze output
        var output = allOutput.ToString();
        Console.Error.WriteLine($"  Total output: {output.Length} chars");

        // Check for OSC 633 sequence
        bool hasOsc633 = output.Contains("\x1b]633;");
        Console.Error.WriteLine($"  Contains \\x1b]633;: {hasOsc633}");

        // Check for visible marker
        bool hasMarker = output.Contains("MARKER_VISIBLE");
        Console.Error.WriteLine($"  Contains MARKER_VISIBLE: {hasMarker}");

        // Hex dump first 500 chars
        var hex = new StringBuilder();
        for (int i = 0; i < Math.Min(output.Length, 500); i++)
        {
            var c = output[i];
            if (c < 0x20 || c > 0x7e) hex.Append($"\\x{(int)c:x2}");
            else hex.Append(c);
        }
        Console.Error.WriteLine($"  Output: {hex}");

        // Cleanup
        ClosePseudoConsole(hPC);
        DeleteProcThreadAttributeList(attrList);
        Marshal.FreeHGlobal(attrList);
        CloseHandle(pi.hProcess);
        hInClient.Dispose();
        hOutClient.Dispose();
    }
}
