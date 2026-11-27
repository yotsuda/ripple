using System.Text.Json;

namespace Ripple.Services;

/// <summary>
/// Shared length-prefixed JSON frame codec for the proxy/worker named pipes.
/// Both ends must enforce the same ceiling: an unbounded length prefix is an
/// allocation primitive, not a message.
/// </summary>
internal static class PipeProtocol
{
    internal const int MaxFrameBytes = 64 * 1024 * 1024;

    internal static async Task WriteMessageAsync(Stream stream, JsonElement message, CancellationToken ct)
    {
        var msgBytes = PipeJson.ElementToBytes(message);
        var lenBytes = BitConverter.GetBytes(msgBytes.Length);
        await stream.WriteAsync(lenBytes, ct);
        await stream.WriteAsync(msgBytes, ct);
        await stream.FlushAsync(ct);
    }

    internal static async Task<JsonElement> ReadMessageAsync(Stream stream, CancellationToken ct)
    {
        var lenBuf = new byte[4];
        await ReadExactAsync(stream, lenBuf, ct);
        var len = BitConverter.ToInt32(lenBuf);
        if (len <= 0 || (uint)len > MaxFrameBytes)
            throw new IOException($"Pipe frame length {len} out of range (1..{MaxFrameBytes})");
        var msgBuf = new byte[len];
        await ReadExactAsync(stream, msgBuf, ct);
        return PipeJson.ParseElement(msgBuf);
    }

    internal static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), ct);
            if (read == 0) throw new IOException("Pipe closed");
            offset += read;
        }
    }
}
