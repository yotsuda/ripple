using System.Collections.Concurrent;
using Ripple.Services;

namespace Ripple.Tests;

/// <summary>
/// Unit tests for <see cref="OutputTruncationHelper"/>. The helper is the
/// single choke-point for the 15_000-char spill / preview behaviour — the
/// worker calls <c>Process</c> on every finalized command result and then
/// <c>CleanupOldSpillFiles</c> with its live-lease predicate. Coverage
/// here pins down:
///   - boundary math (exactly at threshold stays inline; one over spills),
///   - newline-aligned head/tail cuts within ±NewlineScanLimit,
///   - save-failure fallback header (preview still returned, no path),
///   - age-based cleanup window,
///   - live-lease protection for files still referenced by undrained
///     cache entries.
///
/// Filesystem and clock are injected via the public DI seams
/// (<see cref="IOutputSpillFileSystem"/>, <see cref="IClock"/>) so tests
/// never touch real %TEMP%. A fake in-memory filesystem mirrors the
/// minimal surface the helper calls; a tunable clock drives the cleanup
/// window branches deterministically.
/// </summary>
public static class OutputTruncationHelperTests
{
    // Named constants for repeated values so the "why" stays readable:
    // these mirror the helper's own thresholds without reaching into its
    // internals (those are intentionally `internal const`, not public).
    private const int Threshold = 15_000;
    private const int PreviewHead = 1_000;
    private const int PreviewTail = 2_000;
    private const int NewlineScanLimit = 200;
    private const int MaxFileAgeMinutes = 120;

    private const string SpillDir = @"C:\fake-temp\ripple.output";
    private const string TempRoot = @"C:\fake-temp";

    public static void Run()
    {
        var pass = 0;
        var fail = 0;
        void Assert(bool condition, string name)
        {
            if (condition) { pass++; Console.WriteLine($"  PASS: {name}"); }
            else { fail++; Console.Error.WriteLine($"  FAIL: {name}"); }
        }

        Console.WriteLine("=== OutputTruncationHelper Tests ===");

        // Under threshold (14_999 chars): returned verbatim, no spill written.
        // Picked one below the boundary so any off-by-one in the guard
        // would either spill (wrong) or truncate (wrong).
        {
            var fs = new FakeFileSystem();
            var clock = new FakeClock(DateTimeOffset.UtcNow);
            var helper = new OutputTruncationHelper(fs, clock, SpillDir);

            var input = new string('a', Threshold - 1);
            var result = helper.Process(input);

            Assert(result.DisplayOutput == input, "under: returns input verbatim");
            Assert(result.SpillFilePath == null, "under: no spill path");
            Assert(fs.WriteCount == 0, "under: no disk write");
        }

        // Exactly at threshold (15_000 chars): inline. The helper's check
        // is `output.Length <= TruncationThreshold`, so 15_000 falls on
        // the inline side of the boundary. Regression guard for a
        // mistaken `<` that would flip the behaviour.
        {
            var fs = new FakeFileSystem();
            var clock = new FakeClock(DateTimeOffset.UtcNow);
            var helper = new OutputTruncationHelper(fs, clock, SpillDir);

            var input = new string('b', Threshold);
            var result = helper.Process(input);

            Assert(result.DisplayOutput == input, "exact: returns input verbatim at threshold");
            Assert(result.SpillFilePath == null, "exact: no spill at threshold");
            Assert(fs.WriteCount == 0, "exact: no disk write at threshold");
        }

        // Over threshold (20_000 chars, no newlines): preview has header,
        // head separator, head, truncated separator, tail separator, tail.
        // The full cleaned input is written to disk; the returned path
        // is non-null. Without newlines, head/tail cuts fall at the
        // nominal sizes (1000 / 18000).
        {
            var fs = new FakeFileSystem();
            var clock = new FakeClock(new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero));
            var helper = new OutputTruncationHelper(fs, clock, SpillDir);

            var input = new string('c', 20_000);
            var result = helper.Process(input);

            Assert(result.SpillFilePath != null, "over: spill path returned");
            Assert(fs.WriteCount == 1, "over: exactly one file written");
            Assert(fs.TryReadAllText(result.SpillFilePath!, out var saved) && saved == input,
                "over: spill file content equals full input");
            Assert(result.DisplayOutput.Contains("Output too large (20000 characters)"),
                "over: header reports character count");
            Assert(result.DisplayOutput.Contains("Full output saved to: " + result.SpillFilePath),
                "over: header references the saved path");
            Assert(result.DisplayOutput.Contains("--- Preview (first ~1000 chars) ---"),
                "over: head separator present");
            // Omitted = 20000 - 1000 - 2000 = 17000
            Assert(result.DisplayOutput.Contains("--- truncated (17000 chars omitted) ---"),
                "over: truncated separator reports omitted count");
            Assert(result.DisplayOutput.Contains("--- Preview (last ~2000 chars) ---"),
                "over: tail separator present");
        }

        // Newline alignment: a '\n' sitting within ±NewlineScanLimit of
        // the nominal head cut causes the preview to end exactly after
        // that newline, so the head preview never stops mid-line.
        {
            var fs = new FakeFileSystem();
            var clock = new FakeClock(DateTimeOffset.UtcNow);
            var helper = new OutputTruncationHelper(fs, clock, SpillDir);

            // Place a newline at position 950 — 50 chars before the
            // nominal 1000 cut, i.e. inside the NewlineScanLimit window.
            // Everything else is 'x' so there's no other newline to
            // confuse the cut logic.
            var before = new string('x', 950);
            var after = new string('x', 20_000 - 951);
            var input = before + "\n" + after;

            var result = helper.Process(input);

            // Head should be everything up to and INCLUDING the newline
            // (cut after the '\n'), so length == 951.
            // We can't read the head slice out of DisplayOutput directly
            // (the header prepends extra lines), so we extract the segment
            // between the head separator and the truncated separator.
            var display = result.DisplayOutput;
            var headStart = display.IndexOf("--- Preview (first ~1000 chars) ---") + "--- Preview (first ~1000 chars) ---".Length;
            var headEnd = display.IndexOf("--- truncated");
            var headSegment = display.Substring(headStart, headEnd - headStart).Trim('\r', '\n');

            Assert(headSegment.Length == 950,
                $"newline-align head: head preview ends just before the newline (got {headSegment.Length}, expected 950)");
            Assert(headSegment.EndsWith("x"), "newline-align head: head content is pre-newline");

            // Omitted should reflect the line-aligned cut: full length -
            // head (951, includes trailing '\n') - tail (2000, nominal).
            // Tail alignment may also move; we just verify the omitted
            // count is within ±NewlineScanLimit of the naive value.
            var naiveOmitted = input.Length - 951 - 2000;
            var omittedMarker = "--- truncated (";
            var omittedAt = display.IndexOf(omittedMarker) + omittedMarker.Length;
            var omittedEnd = display.IndexOf(' ', omittedAt);
            var omitted = int.Parse(display[omittedAt..omittedEnd]);
            Assert(Math.Abs(omitted - naiveOmitted) <= NewlineScanLimit,
                $"newline-align head: omitted count within ±{NewlineScanLimit} (got {omitted}, naive {naiveOmitted})");
        }

        // Save failure: filesystem throws on WriteAllText. The helper must
        // still return the preview, but with the save-failed header and
        // no SpillFilePath. Mirrors PowerShell.MCP's fallback.
        {
            var fs = new FakeFileSystem { ThrowOnWrite = true };
            var clock = new FakeClock(DateTimeOffset.UtcNow);
            var helper = new OutputTruncationHelper(fs, clock, SpillDir);

            var input = new string('d', 20_000);
            var result = helper.Process(input);

            Assert(result.SpillFilePath == null, "save-fail: no spill path");
            Assert(result.DisplayOutput.Contains("Could not save full output"),
                "save-fail: header reports save failure");
            Assert(result.DisplayOutput.Contains("(20000 characters)"),
                "save-fail: header still reports char count");
            Assert(result.DisplayOutput.Contains("--- Preview (first ~1000 chars) ---"),
                "save-fail: preview head separator present");
            Assert(result.DisplayOutput.Contains("--- Preview (last ~2000 chars) ---"),
                "save-fail: preview tail separator present");
        }

        // Cleanup window: three files at different ages. The clock drives
        // "now"; files older than MaxFileAgeMinutes are deleted, newer
        // ones are kept. Live-set predicate returns false for all three
        // so age is the only filter.
        {
            var now = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);
            var clock = new FakeClock(now);
            var fs = new FakeFileSystem();
            var helper = new OutputTruncationHelper(fs, clock, SpillDir);

            // 30 min old — fresh; should survive.
            var fresh = Path.Combine(SpillDir, "ripple_output_fresh.txt");
            fs.PutFile(fresh, "fresh", now.AddMinutes(-30));
            // 119 min old — just under the cutoff; should survive.
            var edge = Path.Combine(SpillDir, "ripple_output_edge.txt");
            fs.PutFile(edge, "edge", now.AddMinutes(-(MaxFileAgeMinutes - 1)));
            // 200 min old — past the cutoff; should be deleted.
            var stale = Path.Combine(SpillDir, "ripple_output_stale.txt");
            fs.PutFile(stale, "stale", now.AddMinutes(-200));
            // Non-matching prefix ("other") — cleanup's SpillFileGlob
            // only matches ripple_output_*.txt, so this file survives
            // regardless of age. Regression guard against deleting
            // unrelated temp files in the same directory.
            var alien = Path.Combine(SpillDir, "other_file.txt");
            fs.PutFile(alien, "alien", now.AddMinutes(-999));

            helper.CleanupOldSpillFiles(_ => false);

            Assert(fs.Exists(fresh), "cleanup-age: 30-min-old file kept");
            Assert(fs.Exists(edge), "cleanup-age: 119-min-old file kept (inside window)");
            Assert(!fs.Exists(stale), "cleanup-age: 200-min-old file deleted");
            Assert(fs.Exists(alien), "cleanup-age: non-matching prefix ignored");
        }

        // Live-set: a specific path is NOT deleted even if past the age
        // cutoff. This is how the worker's undrained cache entries keep
        // their spill files alive past the opportunistic sweep.
        {
            var now = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);
            var clock = new FakeClock(now);
            var fs = new FakeFileSystem();
            var helper = new OutputTruncationHelper(fs, clock, SpillDir);

            var live = Path.Combine(SpillDir, "ripple_output_live.txt");
            var dead = Path.Combine(SpillDir, "ripple_output_dead.txt");
            fs.PutFile(live, "live", now.AddMinutes(-500));  // older than cutoff
            fs.PutFile(dead, "dead", now.AddMinutes(-500));  // older than cutoff

            helper.CleanupOldSpillFiles(path => string.Equals(path, live, StringComparison.OrdinalIgnoreCase));

            Assert(fs.Exists(live), "cleanup-live: leased path survives despite age");
            Assert(!fs.Exists(dead), "cleanup-live: non-leased aged path deleted");
        }

        // Save path must NOT perform its own opportunistic cleanup.
        // Regression guard for the Codex-flagged bug where
        // TrySaveFullOutput called CleanupOldSpillFiles(static _ => false)
        // — that predicate claimed "no file is live" and would therefore
        // delete ANY aged file, including spill files still referenced by
        // undrained cache entries held by the worker. The fix: strip the
        // sweep out of the save path entirely; only ConsoleWorker's
        // TriggerSpillCleanup (with its lease-aware predicate) is
        // authorised to delete files.
        {
            var now = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);
            var clock = new FakeClock(now);
            var fs = new FakeFileSystem();
            var helper = new OutputTruncationHelper(fs, clock, SpillDir);

            // Seed an aged spill file that a real worker would have leased
            // via _liveSpillPaths because an undrained cache entry still
            // references it. The old save-path sweep would have deleted
            // this file the moment the next Process() ran.
            var leasedAged = Path.Combine(SpillDir, "ripple_output_leased_aged.txt");
            fs.PutFile(leasedAged, "leased content", now.AddMinutes(-500));

            // Drive a second oversized Process to trigger the save path.
            // In the broken code this would fire CleanupOldSpillFiles
            // with a "nothing is live" predicate and delete leasedAged.
            var input = new string('e', 20_000);
            var result = helper.Process(input);

            Assert(result.SpillFilePath != null, "save-no-cleanup: save succeeded");
            Assert(fs.Exists(leasedAged),
                "save-no-cleanup: aged leased file survives the save path");
            Assert(fs.Exists(result.SpillFilePath!),
                "save-no-cleanup: newly-written spill exists");
        }

        // Missing directory: cleanup must not throw when the spill
        // directory doesn't exist yet (fresh worker before any spill
        // has been created).
        {
            var fs = new FakeFileSystem();
            var clock = new FakeClock(DateTimeOffset.UtcNow);
            var helper = new OutputTruncationHelper(fs, clock, SpillDir);

            bool threw = false;
            try { helper.CleanupOldSpillFiles(_ => false); }
            catch { threw = true; }
            Assert(!threw, "cleanup-empty: no throw when directory missing");
        }

        Console.WriteLine($"\n{pass} passed, {fail} failed");
        if (fail > 0) Environment.Exit(1);
    }

    /// <summary>
    /// Minimal in-memory <see cref="IOutputSpillFileSystem"/>. Mirrors the
    /// production surface (create dir, write text, enumerate, read
    /// mtime, delete) with a concurrent dictionary so the same fake can
    /// back age-based cleanup tests. Throws on demand via
    /// <see cref="ThrowOnWrite"/> to exercise the save-failure branch.
    /// </summary>
    private sealed class FakeFileSystem : IOutputSpillFileSystem
    {
        private readonly ConcurrentDictionary<string, (string Contents, DateTimeOffset Mtime)> _files =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _dirs = new(StringComparer.OrdinalIgnoreCase);

        public bool ThrowOnWrite { get; set; }
        public int WriteCount { get; private set; }

        public string GetTempPath() => TempRoot;

        public bool DirectoryExists(string path) => _dirs.Contains(path);

        public void CreateDirectory(string path) => _dirs.Add(path);

        public void WriteAllText(string path, string contents)
        {
            if (ThrowOnWrite) throw new IOException("Simulated write failure.");
            WriteCount++;
            _files[path] = (contents, DateTimeOffset.UtcNow);
        }

        public IEnumerable<string> EnumerateFiles(string directory, string searchPattern)
        {
            // Very small glob support — enough for "ripple_output_*.txt".
            // Prefix and suffix are separated by '*', so split and match
            // boundary substrings. If the pattern is something else the
            // test would show it; keep the impl obvious so a regression
            // surfaces there rather than in the helper.
            var star = searchPattern.IndexOf('*');
            if (star < 0)
            {
                foreach (var kv in _files)
                    if (Path.GetDirectoryName(kv.Key) == directory && Path.GetFileName(kv.Key) == searchPattern)
                        yield return kv.Key;
                yield break;
            }
            var prefix = searchPattern[..star];
            var suffix = searchPattern[(star + 1)..];
            foreach (var kv in _files)
            {
                if (Path.GetDirectoryName(kv.Key) != directory) continue;
                var name = Path.GetFileName(kv.Key);
                if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    && name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    yield return kv.Key;
            }
        }

        public DateTimeOffset GetLastWriteTimeUtc(string path)
        {
            if (_files.TryGetValue(path, out var entry)) return entry.Mtime;
            throw new FileNotFoundException(path);
        }

        public void DeleteFile(string path) => _files.TryRemove(path, out _);

        // Test-only helpers:
        public void PutFile(string path, string contents, DateTimeOffset mtime)
        {
            _dirs.Add(Path.GetDirectoryName(path)!);
            _files[path] = (contents, mtime);
        }

        public bool Exists(string path) => _files.ContainsKey(path);

        public bool TryReadAllText(string path, out string contents)
        {
            if (_files.TryGetValue(path, out var entry))
            {
                contents = entry.Contents;
                return true;
            }
            contents = "";
            return false;
        }
    }

    private sealed class FakeClock : IClock
    {
        public FakeClock(DateTimeOffset now) { UtcNow = now; }
        public DateTimeOffset UtcNow { get; set; }
    }
}
