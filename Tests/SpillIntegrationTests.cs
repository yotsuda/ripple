using System.Diagnostics;
using System.Text.Json;

namespace Ripple.Tests;

/// <summary>
/// End-to-end integration tests for the issue #1 spill path. These run a
/// real <c>ripple --console</c> worker, send <c>execute_command</c>
/// requests that produce &gt; 15_000 chars, and assert the truncation
/// helper's behaviour as observed on the wire. Covers:
///   - inline execute_command returns preview + readable spill file,
///   - timed-out execute_command's deferred wait_for_completion path
///     returns equivalent preview + spill file,
///   - inline and cached paths return equivalent content for the same
///     command shape,
///   - timed-out execute_command's partialOutput is bounded and does
///     NOT include a spill-file preview before final completion,
///   - pwsh prompt / prediction noise is excluded from preview and
///     spill file.
///
/// Tests launch their own worker process, use pwsh (Windows-only paths
/// for now; the same command shape works on bash but the test matrix
/// keeps complexity low until the issue #1 plan ships cross-shell).
/// </summary>
public static class SpillIntegrationTests
{
    // Mirror OutputTruncationHelper's wire format so assertions stay
    // grounded in observable string output rather than internal state.
    private const int SpillThreshold = 15_000;
    private const string HeadSeparator = "--- Preview (first ~1000 chars) ---";
    private const string TailSeparator = "--- Preview (last ~2000 chars) ---";
    private const string SpillFileNameHint = "ripple_output_";
    private const string OversizePrefix = "Output too large";
    // Distinctive marker used by the trailing-bytes scenario. Emitted
    // as the very LAST line of a large command so any loss of bytes in
    // the OSC D → OSC A / post-prompt settle window would drop this
    // specific line and fail the assertion deterministically.
    private const string TrailingTailSentinel = "TRAILING-TAIL-SENTINEL-XXYYZZ";

    public static async Task Run()
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("=== Spill Integration Tests === SKIP (Windows-only)");
            return;
        }

        Console.WriteLine("=== Spill Integration Tests ===");
        var pass = 0;
        var fail = 0;
        void Assert(bool condition, string name)
        {
            if (condition) { pass++; Console.WriteLine($"  PASS: {name}"); }
            else { fail++; Console.Error.WriteLine($"  FAIL: {name}"); }
        }

        // Every scenario runs on a fresh worker so a previous scenario's
        // cache state can't bleed into the next assertion set.

        // 1. Inline execute_command with output > 15_000 chars returns
        //    a preview and a readable spill file on disk whose content
        //    equals the cleaned finalized output.
        //
        //    We emit 400 lines of a predictable marker so the spill file
        //    is readable and easy to assert on, and so we're not relying
        //    on pty-layer line-wrapping behaviour for a single giant line
        //    (ConPTY soft-wraps at window width, inflating the byte
        //    count and making exact-length assertions brittle).
        {
            await RunWithFreshWorker(async (pipeName, workerPid) =>
            {
                // 400 × 40-char lines ≈ 16_400 chars — above threshold,
                // well under any reasonable pty buffer limit, and each
                // line stays well below terminal width so wraps are
                // avoided.
                const string marker = "SPILL-LINE-PATTERN-0123456789-abcdefgh";
                var cmd = $"1..400 | ForEach-Object {{ '{marker}' }}";
                var resp = await ConsoleWorkerTests.SendRequest(pipeName,
                    w => { w.WriteString("type", "execute"); w.WriteString("command", cmd); w.WriteNumber("timeout", 30000); },
                    TimeSpan.FromSeconds(45));

                var output = resp.TryGetProperty("output", out var o) ? o.GetString() ?? "" : "";
                var timedOut = resp.TryGetProperty("timedOut", out var t) && t.GetBoolean();
                Assert(!timedOut, "inline-spill: did not time out");
                Assert(output.StartsWith(OversizePrefix),
                    $"inline-spill: preview header present (got head: {output[..Math.Min(100, output.Length)].Replace("\n", "\\n")})");
                Assert(output.Contains(HeadSeparator), "inline-spill: head separator present");
                Assert(output.Contains(TailSeparator), "inline-spill: tail separator present");
                Assert(output.Contains(SpillFileNameHint),
                    "inline-spill: header references spill path with ripple_output_ prefix");

                // Inline response must carry a structured spillFilePath
                // JSON field, matching the cached-path contract. Clients
                // (the proxy) should not have to string-parse the preview
                // header to discover the saved-output location; that's a
                // DTO field. Regression guard for the Codex-flagged bug
                // where the save path was in the header only and
                // ExecuteResult had no SpillFilePath column.
                var structuredSpill = resp.TryGetProperty("spillFilePath", out var sp)
                    ? sp.GetString() : null;
                Assert(!string.IsNullOrEmpty(structuredSpill),
                    "inline-spill: inline response exposes structured spillFilePath field");

                // Extract the path from the header line for disk-read
                // assertions. The structured field and the header text
                // must agree — the header is derived from the same
                // saved-path value the DTO carries.
                var pathMarker = "Full output saved to: ";
                var idx = output.IndexOf(pathMarker);
                string? spillPath = null;
                if (idx >= 0)
                {
                    var start = idx + pathMarker.Length;
                    var end = output.IndexOf('\n', start);
                    if (end < 0) end = output.Length;
                    spillPath = output[start..end].Trim();
                }
                Assert(spillPath != null && File.Exists(spillPath),
                    $"inline-spill: spill file exists on disk (path={spillPath})");
                Assert(string.Equals(spillPath, structuredSpill, StringComparison.OrdinalIgnoreCase),
                    $"inline-spill: header path matches structured field (header={spillPath}, structured={structuredSpill})");

                if (spillPath != null && File.Exists(spillPath))
                {
                    var onDisk = await File.ReadAllTextAsync(spillPath);
                    // Count marker occurrences — every one of the 400 loop
                    // iterations should land in the spill file.
                    var markerCount = CountOccurrences(onDisk, marker);
                    Assert(markerCount == 400,
                        $"inline-spill: disk content contains all 400 marker lines (got {markerCount})");
                    Assert(!onDisk.StartsWith(OversizePrefix),
                        "inline-spill: disk content is the raw cleaned output, not the preview");
                    // pwsh's own prompt / prediction noise must NOT leak
                    // into the spill file. These are ANSI-heavy sequences
                    // that the finalizer strips before the truncation
                    // helper runs.
                    Assert(!onDisk.Contains("\x1b["),
                        "inline-spill: no ANSI escape sequences in spill content");
                    Assert(!onDisk.Contains("PS "),
                        "inline-spill: no PS> prompt noise in spill content");

                    // Clean up so subsequent cleanup-window tests aren't
                    // confused by leftover fresh files.
                    try { File.Delete(spillPath); } catch { }
                }
            });
        }

        // 2. Timed-out execute_command later fetched by
        //    wait_for_completion returns preview + spill file. The
        //    deferred path and the inline path must be observably
        //    equivalent (same preview shape, same spill-file contract).
        {
            await RunWithFreshWorker(async (pipeName, workerPid) =>
            {
                // Slow command producing > 15_000 chars AFTER a delay
                // longer than the timeout. Start-Sleep keeps the
                // execute_command on the wire long enough to timeout
                // preemptively, then the big output lands afterward
                // and routes into the cache via FinalizeSnapshotAsync.
                const string marker = "CACHE-LINE-PATTERN-0123456789-abcdefgh";
                var cmd = $"Start-Sleep -Milliseconds 1500; 1..400 | ForEach-Object {{ '{marker}' }}";
                var resp = await ConsoleWorkerTests.SendRequest(pipeName,
                    w => { w.WriteString("type", "execute"); w.WriteString("command", cmd); w.WriteNumber("timeout", 400); },
                    TimeSpan.FromSeconds(10));

                var timedOut = resp.TryGetProperty("timedOut", out var t) && t.GetBoolean();
                Assert(timedOut, "cache-spill: initial execute timed out");

                // partialOutput at timeout must NOT carry the spill
                // preview — the plan states timeout returns a bounded
                // diagnostic partialOutput sourced only from the
                // in-flight capture, not the final truncation product.
                var partial = resp.TryGetProperty("partialOutput", out var po) ? po.GetString() ?? "" : "";
                Assert(!partial.StartsWith(OversizePrefix),
                    "cache-spill: timeout partialOutput is NOT a preview header");
                Assert(!partial.Contains(SpillFileNameHint),
                    "cache-spill: timeout partialOutput does NOT reference a spill path");

                // Poll until the cached result is ready. Generous
                // window so a slow pwsh start / ForEach runtime doesn't
                // flake the assertion.
                string cachedOutput = "";
                string? spillPath = null;
                var deadline = DateTime.UtcNow.AddSeconds(20);
                while (DateTime.UtcNow < deadline)
                {
                    var drain = await ConsoleWorkerTests.SendRequest(pipeName,
                        w => w.WriteString("type", "get_cached_output"));
                    var status = drain.TryGetProperty("status", out var s) ? s.GetString() : null;
                    if (status == "ok"
                        && drain.TryGetProperty("results", out var results)
                        && results.ValueKind == JsonValueKind.Array
                        && results.GetArrayLength() > 0)
                    {
                        var first = results[0];
                        cachedOutput = first.TryGetProperty("output", out var co) ? co.GetString() ?? "" : "";
                        spillPath = first.TryGetProperty("spillFilePath", out var sp) ? sp.GetString() : null;
                        break;
                    }
                    await Task.Delay(200);
                }

                Assert(cachedOutput.StartsWith(OversizePrefix),
                    $"cache-spill: cached output is a preview (got head: {cachedOutput[..Math.Min(100, cachedOutput.Length)].Replace("\n", "\\n")})");
                Assert(cachedOutput.Contains(HeadSeparator), "cache-spill: cached head separator present");
                Assert(cachedOutput.Contains(TailSeparator), "cache-spill: cached tail separator present");
                Assert(!string.IsNullOrEmpty(spillPath),
                    "cache-spill: cached result exposes spillFilePath field");
                Assert(spillPath != null && File.Exists(spillPath),
                    $"cache-spill: cached spill file exists on disk (path={spillPath})");

                if (spillPath != null && File.Exists(spillPath))
                {
                    var onDisk = await File.ReadAllTextAsync(spillPath);
                    var markerCount = CountOccurrences(onDisk, marker);
                    // The ForEach loop emits exactly 400 marker lines.
                    // The command-echo slice at the head of the capture
                    // window can include one more occurrence of the
                    // literal marker string (it appears verbatim inside
                    // the ForEach-Object body). Cache mode uses a wider
                    // capture window than inline (effectiveEnd extends
                    // past OSC D to include trailing prompt repaint), so
                    // we tolerate that one extra match rather than
                    // depending on exact echo-strip shape.
                    Assert(markerCount >= 400,
                        $"cache-spill: disk content contains at least 400 marker lines (got {markerCount})");
                    try { File.Delete(spillPath); } catch { }
                }
            });
        }

        // 3. Inline and cached paths return EQUIVALENT finalized content
        //    for the same command. Running the same command twice on
        //    separate workers — once with a generous timeout (inline)
        //    and once with a short timeout (cached) — should yield the
        //    same preview shape, same spill content, same statusLine
        //    pattern, and same exitCode. The plan's §2 guarantee is
        //    "inline == cached" — this test exercises both halves.
        //
        //    As in tests 1 and 2, we use a line-based marker pattern so
        //    pty soft-wrap doesn't change the byte count between runs.
        {
            const string equivMarker = "EQUIV-LINE-PATTERN-0123456789-abcdefgh";
            var cmd = $"1..400 | ForEach-Object {{ '{equivMarker}' }}";
            string inlineOutput = "", cachedOutput = "";
            string inlineStatus = "", cachedStatus = "";
            int inlineExit = -1, cachedExit = -1;
            string? inlineSpillContent = null, cachedSpillContent = null;

            // Inline: big timeout, result comes back in the execute
            // response directly.
            await RunWithFreshWorker(async (pipeName, _) =>
            {
                var resp = await ConsoleWorkerTests.SendRequest(pipeName,
                    w => { w.WriteString("type", "execute"); w.WriteString("command", cmd); w.WriteNumber("timeout", 30000); },
                    TimeSpan.FromSeconds(45));
                inlineOutput = resp.TryGetProperty("output", out var o) ? o.GetString() ?? "" : "";
                inlineExit = resp.TryGetProperty("exitCode", out var e) ? e.GetInt32() : -1;
                // Inline responses don't serialize statusLine today — the
                // proxy builds that from other fields — but they DO carry
                // the output preview. We compare preview shape here and
                // leave statusLine to the cached-path check below.
                inlineStatus = resp.TryGetProperty("statusLine", out var sl) ? sl.GetString() ?? "" : "";

                var marker = "Full output saved to: ";
                var idx = inlineOutput.IndexOf(marker);
                if (idx >= 0)
                {
                    var start = idx + marker.Length;
                    var end = inlineOutput.IndexOf('\n', start);
                    if (end < 0) end = inlineOutput.Length;
                    var path = inlineOutput[start..end].Trim();
                    if (File.Exists(path))
                    {
                        inlineSpillContent = await File.ReadAllTextAsync(path);
                        try { File.Delete(path); } catch { }
                    }
                }
            });

            // Cached: short timeout forces a preemptive flip, then the
            // cached result lands via wait_for_completion. Use sleep
            // before the big output to guarantee a timeout.
            await RunWithFreshWorker(async (pipeName, _) =>
            {
                var slowCmd = "Start-Sleep -Milliseconds 1200; " + cmd;
                var execResp = await ConsoleWorkerTests.SendRequest(pipeName,
                    w => { w.WriteString("type", "execute"); w.WriteString("command", slowCmd); w.WriteNumber("timeout", 400); },
                    TimeSpan.FromSeconds(10));
                var timedOut = execResp.TryGetProperty("timedOut", out var t) && t.GetBoolean();
                Assert(timedOut, "equiv: cached path initial execute timed out");

                var deadline = DateTime.UtcNow.AddSeconds(10);
                string? spillPath = null;
                while (DateTime.UtcNow < deadline)
                {
                    var drain = await ConsoleWorkerTests.SendRequest(pipeName,
                        w => w.WriteString("type", "get_cached_output"));
                    var status = drain.TryGetProperty("status", out var s) ? s.GetString() : null;
                    if (status == "ok"
                        && drain.TryGetProperty("results", out var results)
                        && results.ValueKind == JsonValueKind.Array
                        && results.GetArrayLength() > 0)
                    {
                        var first = results[0];
                        cachedOutput = first.TryGetProperty("output", out var o) ? o.GetString() ?? "" : "";
                        cachedExit = first.TryGetProperty("exitCode", out var e) ? e.GetInt32() : -1;
                        cachedStatus = first.TryGetProperty("statusLine", out var sl) ? sl.GetString() ?? "" : "";
                        spillPath = first.TryGetProperty("spillFilePath", out var sp) ? sp.GetString() : null;
                        break;
                    }
                    await Task.Delay(200);
                }

                if (spillPath != null && File.Exists(spillPath))
                {
                    cachedSpillContent = await File.ReadAllTextAsync(spillPath);
                    try { File.Delete(spillPath); } catch { }
                }
            });

            // Both paths must produce the same observable truncation
            // shape: oversize header, head + tail preview separators,
            // a readable spill file, and exit 0 for the same loop
            // command. A byte-for-byte comparison isn't possible here
            // because the cached path is driven by a slightly different
            // shell command (a leading Start-Sleep forces the timeout
            // → cache flip), so the two spills intentionally differ by
            // a few bytes at the head. Structural equivalence is what
            // the plan §2 guarantee actually promises.
            Assert(inlineOutput.Contains(HeadSeparator) && cachedOutput.Contains(HeadSeparator),
                "equiv: both paths produce head separator");
            Assert(inlineOutput.Contains(TailSeparator) && cachedOutput.Contains(TailSeparator),
                "equiv: both paths produce tail separator");
            Assert(inlineOutput.StartsWith(OversizePrefix) && cachedOutput.StartsWith(OversizePrefix),
                "equiv: both paths produce the oversize header");
            Assert(inlineExit == 0 && cachedExit == 0,
                $"equiv: both paths report exit 0 (inline={inlineExit}, cached={cachedExit})");

            // Cached path always bakes a statusLine; inline path doesn't
            // expose one (proxy builds it). Assert the cached line has
            // the expected shape (completed / failed marker + command
            // fragment + duration + location).
            Assert(cachedStatus.Contains("Status:") && cachedStatus.Contains("Duration:"),
                $"equiv: cached statusLine carries baked status + duration (got: {cachedStatus})");

            // Spill contents must both contain all 400 loop iterations
            // — the cleaned output written to disk is the raw command
            // output, independent of which delivery path routed it.
            Assert(inlineSpillContent != null && cachedSpillContent != null,
                "equiv: both paths produced a spill file");
            if (inlineSpillContent != null && cachedSpillContent != null)
            {
                var inlineCount = CountOccurrences(inlineSpillContent, equivMarker);
                var cachedCount = CountOccurrences(cachedSpillContent, equivMarker);
                Assert(inlineCount >= 400,
                    $"equiv: inline spill contains >= 400 marker lines (got {inlineCount})");
                Assert(cachedCount >= 400,
                    $"equiv: cached spill contains >= 400 marker lines (got {cachedCount})");
            }
        }

        // 4. Delayed trailing output that belongs to the command result
        //    is preserved before spill / truncate runs. Ripple's plan
        //    §3 step 2 extends the finalize window past OSC D to
        //    Capture.Length so trailing bytes arriving between OSC D
        //    (primary completion marker) and OSC A (prompt ready) are
        //    still part of the command result. This test forces
        //    trailing bytes into that window by emitting a distinctive
        //    sentinel as the VERY LAST line after 400 bulk lines, then
        //    asserts the sentinel appears in the spill file content.
        //
        //    Regression guard for the "OSC D closes the window" bug
        //    shape: if effectiveEnd fell back to snapshot.CommandEnd
        //    alone, the tail sentinel would be dropped silently and
        //    the spill file would end mid-loop.
        //
        //    Uses bash.exe — its adapter declares
        //    output.post_prompt_settle_ms: 50, so WaitCaptureStable
        //    runs and any bytes still flushing through the PTY buffer
        //    after OSC D are observed by the capture before finalize
        //    slices the window. pwsh is a weaker target here because
        //    its post_prompt_settle_ms is 0 and OSC A is emitted from
        //    the prompt function itself, so for pwsh the entire
        //    captured stream is already terminal by the time the
        //    snapshot fires. bash exercises the actual settle path.
        {
            await RunWithFreshWorker(async (pipeName, workerPid) =>
            {
                // Body: 400 bulk lines, then the distinctive sentinel.
                // The bulk drives the output over the 15_000-char
                // threshold so truncation / spill runs; the sentinel
                // lands last so any trailing-bytes regression drops it.
                const string bulkMarker = "TRAIL-BULK-PATTERN-0123456789-abcdefgh";
                var cmd = $"for i in $(seq 1 400); do echo '{bulkMarker}'; done; echo '{TrailingTailSentinel}'";
                var resp = await ConsoleWorkerTests.SendRequest(pipeName,
                    w => { w.WriteString("type", "execute"); w.WriteString("command", cmd); w.WriteNumber("timeout", 30000); },
                    TimeSpan.FromSeconds(45));

                var output = resp.TryGetProperty("output", out var o) ? o.GetString() ?? "" : "";
                var timedOut = resp.TryGetProperty("timedOut", out var t) && t.GetBoolean();
                Assert(!timedOut, "trailing: did not time out");
                Assert(output.StartsWith(OversizePrefix),
                    "trailing: output is a spill preview (above threshold)");

                // The tail sentinel is the LAST line of the command
                // output, so it must land in the spill preview's tail
                // block (--- Preview (last ~2000 chars) ---).
                var tailSepIndex = output.IndexOf(TailSeparator);
                Assert(tailSepIndex >= 0, "trailing: tail separator present");
                if (tailSepIndex >= 0)
                {
                    var tailSegment = output[(tailSepIndex + TailSeparator.Length)..];
                    Assert(tailSegment.Contains(TrailingTailSentinel),
                        $"trailing: sentinel appears in tail preview (last {Math.Min(120, tailSegment.Length)} chars: {tailSegment[^Math.Min(120, tailSegment.Length)..].Replace("\n", "\\n")})");
                }

                // Extract the spill path and assert the sentinel also
                // appears in the on-disk content — the plan's §3 step
                // 2 guarantee is that the cleaned finalized stream
                // (not just the preview) carries trailing bytes.
                var pathMarker = "Full output saved to: ";
                var idx = output.IndexOf(pathMarker);
                string? spillPath = null;
                if (idx >= 0)
                {
                    var start = idx + pathMarker.Length;
                    var end = output.IndexOf('\n', start);
                    if (end < 0) end = output.Length;
                    spillPath = output[start..end].Trim();
                }
                Assert(spillPath != null && File.Exists(spillPath),
                    $"trailing: spill file exists on disk (path={spillPath})");

                if (spillPath != null && File.Exists(spillPath))
                {
                    var onDisk = await File.ReadAllTextAsync(spillPath);
                    Assert(onDisk.Contains(TrailingTailSentinel),
                        "trailing: sentinel present in on-disk spill content");

                    var bulkCount = CountOccurrences(onDisk, bulkMarker);
                    Assert(bulkCount == 400,
                        $"trailing: all 400 bulk lines present on disk (got {bulkCount})");

                    // The sentinel must be the LAST marker-bearing line
                    // — confirms nothing after OSC D was truncated away.
                    var lastBulkIdx = onDisk.LastIndexOf(bulkMarker, StringComparison.Ordinal);
                    var sentinelIdx = onDisk.LastIndexOf(TrailingTailSentinel, StringComparison.Ordinal);
                    Assert(sentinelIdx > lastBulkIdx,
                        $"trailing: sentinel is after the last bulk marker on disk (sentinel={sentinelIdx}, lastBulk={lastBulkIdx})");

                    // Regression guard for Codex Bug 2 (non-pwsh shells
                    // leak prompt text after OSC A). The finalizer caps
                    // effectiveEnd at PromptStartOffset so any bytes bash
                    // emits AFTER OSC A — namely the next prompt string
                    // ("$ ", "bash-5.1$ ", etc.) — must not bleed into
                    // either the cleaned output on disk or the tail
                    // preview. The sentinel is the LAST command byte, so
                    // everything after it on disk should be at most a
                    // final newline. We check the slice from the byte
                    // immediately after the sentinel to end-of-file.
                    var tailSlice = onDisk[(sentinelIdx + TrailingTailSentinel.Length)..];
                    var tailTrimmed = tailSlice.TrimEnd('\r', '\n', ' ', '\t');
                    Assert(tailTrimmed.Length == 0,
                        $"trailing: no bytes follow the sentinel on disk (trailing slice: '{tailSlice.Replace("\n", "\\n").Replace("\r", "\\r")}')");
                    // Defence-in-depth: even if the trim rule above ever
                    // tolerates whitespace, explicit prompt-shape checks
                    // prevent a silent regression where bash prints a
                    // literal prompt after OSC A. "$ " is the default
                    // bash prompt; "bash-" prefix matches MSYS2 fallback
                    // prompts ("bash-5.1$ ", etc.).
                    Assert(!tailSlice.Contains("$ "),
                        "trailing: on-disk spill does not end with bash '$ ' prompt");
                    Assert(!tailSlice.Contains("bash-"),
                        "trailing: on-disk spill does not end with bash-*$ prompt");

                    try { File.Delete(spillPath); } catch { }
                }
            }, shell: "bash.exe");
        }

        Console.WriteLine($"\n{pass} passed, {fail} failed");
        if (fail > 0) Environment.Exit(1);
    }

    /// <summary>
    /// Launches a fresh worker, waits for its pipe, runs the scenario
    /// body, and kills the worker on exit. Keeps each integration
    /// scenario isolated so a previous scenario's cache / spill files
    /// can't bleed into the next.
    /// </summary>
    private static async Task RunWithFreshWorker(Func<string, int, Task> body, string shell = "pwsh.exe")
    {
        var proxyPid = Environment.ProcessId;
        var agentId = "spill-" + Guid.NewGuid().ToString("N")[..8];
        var cwd = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var launcher = new Services.ProcessLauncher();
        int workerPid = launcher.LaunchConsoleWorker(proxyPid, agentId, shell, cwd);
        var pipeName = $"RP.{proxyPid}.{agentId}.{workerPid}";

        try
        {
            var ready = await ConsoleWorkerTests.WaitForPipeAsync(pipeName, TimeSpan.FromSeconds(30));
            if (!ready)
            {
                Console.Error.WriteLine($"  FAIL: worker pipe did not become ready for {pipeName}");
                return;
            }

            await body(pipeName, workerPid);
        }
        finally
        {
            try
            {
                var proc = Process.GetProcessById(workerPid);
                proc.Kill();
                await proc.WaitForExitAsync();
            }
            catch { }
        }
    }

    /// <summary>
    /// Counts non-overlapping occurrences of <paramref name="needle"/> in
    /// <paramref name="haystack"/>. Used by spill assertions to verify
    /// the emitted loop body landed in the spill file the expected
    /// number of times, independent of pty line-wrap choices.
    /// </summary>
    private static int CountOccurrences(string haystack, string needle)
    {
        if (string.IsNullOrEmpty(needle)) return 0;
        int count = 0;
        int i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0)
        {
            count++;
            i += needle.Length;
        }
        return count;
    }
}
