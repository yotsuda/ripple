using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ShellPilot.Services;

/// <summary>
/// Stream wrapper that uses raw Win32 ReadFile/WriteFile for pipe handles.
/// </summary>
public sealed class Win32PipeStream : Stream
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(SafeFileHandle hFile, byte[] lpBuffer, int nNumberOfBytesToRead, out int lpNumberOfBytesRead, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(SafeFileHandle hFile, byte[] lpBuffer, int nNumberOfBytesToWrite, out int lpNumberOfBytesWritten, IntPtr lpOverlapped);

    private readonly SafeFileHandle _handle;
    private readonly FileAccess _access;

    public Win32PipeStream(SafeFileHandle handle, FileAccess access)
    {
        _handle = handle;
        _access = access;
    }

    public override bool CanRead => _access.HasFlag(FileAccess.Read);
    public override bool CanWrite => _access.HasFlag(FileAccess.Write);
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count)
    {
        byte[] buf = offset == 0 ? buffer : new byte[count];

        if (!ReadFile(_handle, buf, count, out int bytesRead, IntPtr.Zero))
        {
            var err = Marshal.GetLastWin32Error();
            if (err == 109) return 0; // ERROR_BROKEN_PIPE = EOF
            throw new IOException($"ReadFile failed: Win32 error {err}");
        }

        if (offset != 0 && bytesRead > 0)
            Array.Copy(buf, 0, buffer, offset, bytesRead);

        return bytesRead;
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        byte[] buf;
        if (offset == 0 && count == buffer.Length)
            buf = buffer;
        else
        {
            buf = new byte[count];
            Array.Copy(buffer, offset, buf, 0, count);
        }

        if (!WriteFile(_handle, buf, count, out _, IntPtr.Zero))
            throw new IOException($"WriteFile failed: Win32 error {Marshal.GetLastWin32Error()}");
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
