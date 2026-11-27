using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Ripple.Services;

namespace Ripple.Tests;

/// <summary>
/// Regression tests for the shared pipe frame codec and the readiness probe.
/// These use in-memory streams and real in-process named pipes; no worker or
/// second user is required.
/// </summary>
public static class PipeProtocolTests
{
    public static async Task RunAsync()
    {
        Console.WriteLine("=== PipeProtocol Tests ===");
        var pass = 0;
        var fail = 0;
        void Assert(bool condition, string name)
        {
            if (condition) { pass++; Console.WriteLine($"  PASS: {name}"); }
            else { fail++; Console.Error.WriteLine($"  FAIL: {name}"); }
        }

        static async Task<Exception?> Capture(Func<Task> action)
        {
            try
            {
                await action();
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        }

        async Task<Exception?> AwaitProbe(Task probe, string name)
        {
            var finished = await Task.WhenAny(probe, Task.Delay(1500));
            if (finished != probe)
            {
                Assert(false, $"{name}: probe did not finish within the external deadline");
                return new TimeoutException("probe did not finish");
            }
            return await Capture(() => probe);
        }

        // --- roundtrip ---

        {
            using var stream = new MemoryStream();
            var message = PipeJson.BuildObjectElement(w => w.WriteString("type", "ping"));
            await PipeProtocol.WriteMessageAsync(stream, message, CancellationToken.None);
            stream.Position = 0;
            var parsed = await PipeProtocol.ReadMessageAsync(stream, CancellationToken.None);
            Assert(parsed.TryGetProperty("type", out var type) && type.GetString() == "ping",
                "frame roundtrip");
        }

        // --- invalid length prefixes are rejected before allocation ---

        foreach (var (length, label) in new[]
                 {
                     (-1, "negative"),
                     (0, "zero"),
                     (PipeProtocol.MaxFrameBytes + 1, "oversized"),
                 })
        {
            using var stream = new MemoryStream();
            stream.Write(BitConverter.GetBytes(length));
            stream.Position = 0;
            var ex = await Capture(() => PipeProtocol.ReadMessageAsync(stream, CancellationToken.None));
            Assert(ex is IOException, $"length prefix rejected: {label}");
        }

        // --- truncated payload ---

        {
            using var stream = new MemoryStream();
            stream.Write(BitConverter.GetBytes(10));
            stream.Write(Encoding.UTF8.GetBytes("{"));
            stream.Position = 0;
            var ex = await Capture(() => PipeProtocol.ReadMessageAsync(stream, CancellationToken.None));
            Assert(ex is IOException, "truncated payload is an IOException");
        }

        // --- readiness probe: a peer that accepts but never writes must time out ---

        {
            var name = $"ripple-probe-silent-{Guid.NewGuid():N}";
            using var server = new NamedPipeServerStream(
                name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            var accept = server.WaitForConnectionAsync();
            var probe = ConsoleManager.WaitForPipeReadyAsync(name, TimeSpan.FromMilliseconds(300));
            await accept;
            var ex = await AwaitProbe(probe, "silent peer");
            Assert(ex is TimeoutException, "silent peer times out instead of hanging");
        }

        // --- readiness probe: four garbage bytes are not a valid ready signal ---

        {
            var name = $"ripple-probe-garbage-{Guid.NewGuid():N}";
            using var server = new NamedPipeServerStream(
                name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            var accept = server.WaitForConnectionAsync();
            var probe = ConsoleManager.WaitForPipeReadyAsync(name, TimeSpan.FromSeconds(1));
            await accept;
            try
            {
                await server.WriteAsync(BitConverter.GetBytes(1));
                await server.FlushAsync();
            }
            catch (IOException)
            {
                // The probe may have timed out first; the assertion below still
                // checks that it did not report ready.
            }
            var ex = await AwaitProbe(probe, "garbage prefix");
            Assert(ex is TimeoutException, "garbage length/body times out instead of reporting ready");
        }

        // --- readiness probe: a valid ping response succeeds ---

        {
            var name = $"ripple-probe-valid-{Guid.NewGuid():N}";
            using var server = new NamedPipeServerStream(
                name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            var accept = server.WaitForConnectionAsync();
            var probe = ConsoleManager.WaitForPipeReadyAsync(name, TimeSpan.FromSeconds(2));
            await accept;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var ping = await PipeProtocol.ReadMessageAsync(server, cts.Token);
            Assert(ping.TryGetProperty("type", out var type) && type.GetString() == "ping",
                "probe sends a ping frame");
            await PipeProtocol.WriteMessageAsync(
                server,
                PipeJson.BuildObjectElement(w => w.WriteString("status", "ok")),
                cts.Token);
            var ex = await AwaitProbe(probe, "valid ping");
            Assert(ex is null, "valid status=ok response is accepted");
        }

        Console.WriteLine($"\n{pass} passed, {fail} failed");
        if (fail > 0) Environment.Exit(1);
    }
}
