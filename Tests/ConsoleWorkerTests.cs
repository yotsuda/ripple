using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace ShellPilot.Tests;

/// <summary>
/// E2E test: launch ConsoleWorker in --console mode, send commands via Named Pipe.
/// Tests ConPTY + shell integration + OSC parsing + command tracking.
/// </summary>
public class ConsoleWorkerTests
{
    public static async Task Run()
    {
        var pass = 0;
        var fail = 0;

        void Assert(bool condition, string name)
        {
            if (condition) { pass++; Console.WriteLine($"  PASS: {name}"); }
            else { fail++; Console.Error.WriteLine($"  FAIL: {name}"); }
        }

        Console.WriteLine("=== ConsoleWorker E2E Tests ===");

        // Find shellpilot executable
        var exePath = Process.GetCurrentProcess().MainModule?.FileName;
        if (exePath == null)
        {
            Console.Error.WriteLine("  SKIP: Cannot determine exe path");
            return;
        }

        var proxyPid = Environment.ProcessId;
        var agentId = "test";
        var shell = OperatingSystem.IsWindows() ? "pwsh.exe" : "bash";
        var cwd = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // Launch worker via ProcessLauncher (uses CREATE_NEW_CONSOLE on Windows,
        // required so the worker process has a console for ConPTY to attach to)
        var launcher = new Services.ProcessLauncher();
        int workerPid = launcher.LaunchConsoleWorker(proxyPid, agentId, shell, cwd);

        var pipeName = $"SP.{proxyPid}.{agentId}.{workerPid}";
        Console.WriteLine($"  Pipe: {pipeName}, Worker PID: {workerPid}");

        // Wait for pipe to be ready (up to 30s)
        Console.WriteLine("  Waiting for pipe...");
        var ready = await WaitForPipeAsync(pipeName, TimeSpan.FromSeconds(30));
        Assert(ready, "Worker pipe became ready");

        if (!ready)
        {
            try { Process.GetProcessById(workerPid).Kill(); } catch { }
            Console.WriteLine($"\n{pass} passed, {fail} failed");
            if (fail > 0) Environment.Exit(1);
            return;
        }

        // Test 1: ping
        {
            var resp = await SendRequest(pipeName, new { type = "ping" });
            var status = resp.TryGetProperty("status", out var s) ? s.GetString() : null;
            Assert(status == "ok", "Ping returns ok");
        }

        // Test 2: get_status (should be standby)
        {
            var resp = await SendRequest(pipeName, new { type = "get_status" });
            var status = resp.TryGetProperty("status", out var s) ? s.GetString() : null;
            Assert(status == "standby", $"Status is standby (got: {status})");
        }

        // Test 3: execute simple command
        {
            var command = OperatingSystem.IsWindows() ? "Write-Output 'hello shellpilot'" : "echo 'hello shellpilot'";
            Console.WriteLine($"  Executing: {command}");
            var resp = await SendRequest(pipeName, new { type = "execute", command, timeout = 10000 }, TimeSpan.FromSeconds(15));

            var output = resp.TryGetProperty("output", out var o) ? o.GetString() ?? "" : "";
            var timedOut = resp.TryGetProperty("timedOut", out var t) && t.GetBoolean();
            var exitCode = resp.TryGetProperty("exitCode", out var e) ? e.GetInt32() : -1;
            var cwdResult = resp.TryGetProperty("cwd", out var c) ? c.GetString() : null;

            Console.WriteLine($"  Output: '{output.Replace("\n", "\\n")}'");
            Console.WriteLine($"  ExitCode: {exitCode}, TimedOut: {timedOut}, Cwd: {cwdResult}");

            Assert(!timedOut, "Command did not time out");
            Assert(output.Contains("hello shellpilot"), "Output contains expected text");
            Assert(exitCode == 0, "Exit code is 0");
            Assert(cwdResult != null, "Cwd is reported");
        }

        // Test 4: execute command with non-zero exit (native command)
        {
            var command = OperatingSystem.IsWindows()
                ? "cmd /c exit 42"
                : "bash -c 'exit 42'";
            Console.WriteLine($"  Executing: {command}");
            var resp = await SendRequest(pipeName, new { type = "execute", command, timeout = 10000 }, TimeSpan.FromSeconds(15));

            var exitCode = resp.TryGetProperty("exitCode", out var e) ? e.GetInt32() : -1;
            var timedOut = resp.TryGetProperty("timedOut", out var t) && t.GetBoolean();
            Console.WriteLine($"  ExitCode: {exitCode}, TimedOut: {timedOut}");

            Assert(!timedOut, "Non-zero exit: did not time out");
            Assert(exitCode == 42, $"Non-zero exit: code is 42 (got: {exitCode})");
        }

        // Test 5: get_status after commands (should be standby again)
        {
            var resp = await SendRequest(pipeName, new { type = "get_status" });
            var status = resp.TryGetProperty("status", out var s) ? s.GetString() : null;
            Assert(status == "standby", $"Status back to standby (got: {status})");
        }

        // Cleanup
        try
        {
            var proc = Process.GetProcessById(workerPid);
            proc.Kill();
            await proc.WaitForExitAsync();
        }
        catch { }

        Console.WriteLine($"\n{pass} passed, {fail} failed");
        if (fail > 0) Environment.Exit(1);
    }

    private static async Task<bool> WaitForPipeAsync(string pipeName, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var resp = await SendRequest(pipeName, new { type = "ping" }, TimeSpan.FromSeconds(2));
                return true;
            }
            catch
            {
                await Task.Delay(500);
            }
        }
        return false;
    }

    private static async Task<JsonElement> SendRequest(string pipeName, object request, TimeSpan? timeout = null)
    {
        timeout ??= TimeSpan.FromSeconds(5);
        using var cts = new CancellationTokenSource(timeout.Value);
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

        await client.ConnectAsync(cts.Token);

        var json = JsonSerializer.Serialize(request);
        var msgBytes = Encoding.UTF8.GetBytes(json);
        var lenBytes = BitConverter.GetBytes(msgBytes.Length);

        await client.WriteAsync(lenBytes, cts.Token);
        await client.WriteAsync(msgBytes, cts.Token);
        await client.FlushAsync(cts.Token);

        var recvLenBytes = new byte[4];
        await ReadExactAsync(client, recvLenBytes, cts.Token);
        var recvLen = BitConverter.ToInt32(recvLenBytes);

        var recvBytes = new byte[recvLen];
        await ReadExactAsync(client, recvBytes, cts.Token);

        return JsonSerializer.Deserialize<JsonElement>(recvBytes);
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
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
