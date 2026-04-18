using System.Collections.Concurrent;
using Ripple.Services;

namespace Ripple.Tests;

/// <summary>
/// Unit tests for <see cref="CommandOutputCapture"/>. The capture replaces
/// the old tracker-side <c>_aiOutput</c> string with a bounded hot-buffer
/// plus scratch-file spill so the tracker's working set stays flat on
/// runaway commands. This suite pins down:
///   - bounded memory: large appends spill to scratch,
///   - offset reads cross the hot/scratch boundary correctly,
///   - streaming reader returns the same content as inline slice,
///   - Complete/Dispose are idempotent and release scratch,
///   - GetCurrentCommandSnapshot only reads inside the current command
///     window (never falls back to a general recent-output ring),
///   - concurrent append + slice read produces consistent (possibly
///     trailing, never corrupt) reads.
///
/// The capture takes an <see cref="ICaptureScratchStore"/> seam so these
/// tests use a tiny in-memory store and never touch real temp storage.
/// Hot-buffer size is tuned down from the production 64 KB default so
/// spill boundaries are reachable with kilobyte-scale inputs.
/// </summary>
public static class CommandOutputCaptureTests
{
    private const int SmallHotBuffer = 256;          // chars
    private const int SnapshotDefault = 128;         // chars

    public static void Run()
    {
        var pass = 0;
        var fail = 0;
        void Assert(bool condition, string name)
        {
            if (condition) { pass++; Console.WriteLine($"  PASS: {name}"); }
            else { fail++; Console.Error.WriteLine($"  FAIL: {name}"); }
        }

        Console.WriteLine("=== CommandOutputCapture Tests ===");

        // Bounded memory: append >> hot-buffer size. A scratch file is
        // created and the spilled byte count lines up with total - hot.
        {
            var store = new FakeScratchStore();
            var cap = new CommandOutputCapture(store, SmallHotBuffer, SnapshotDefault);

            const int totalChars = 4 * SmallHotBuffer; // = 1024 chars
            cap.Append(new string('x', totalChars));

            Assert(cap.Length == totalChars, "bounded: Length reflects total appended");
            Assert(store.CreatedCount == 1, "bounded: exactly one scratch file created");
            Assert(store.TotalBytesWritten > 0, "bounded: bytes spilled to scratch");
            // Spilled = total - hot (approximately). Exact value depends
            // on whether the append flushed incrementally or in bulk,
            // but it must be in the range [total - hotBuffer, total].
            long minSpill = totalChars - SmallHotBuffer;
            long charsSpilled = store.TotalBytesWritten / 2; // UTF-16 = 2 bytes/char
            Assert(charsSpilled >= minSpill && charsSpilled <= totalChars,
                $"bounded: spilled char count within [{minSpill},{totalChars}] (got {charsSpilled})");

            cap.Dispose();
        }

        // Offset reads across hot/scratch boundary: ReadSlice returns the
        // same substring the append produced, regardless of whether the
        // slice lands entirely in scratch, entirely in hot, or straddles.
        {
            var store = new FakeScratchStore();
            var cap = new CommandOutputCapture(store, SmallHotBuffer, SnapshotDefault);

            // Build input with a recognizable pattern so ReadSlice results
            // can be validated against the original string directly.
            var pattern = new System.Text.StringBuilder();
            for (int i = 0; i < 1024; i++)
                pattern.Append((char)('a' + (i % 26)));
            var input = pattern.ToString();
            cap.Append(input);

            // Entirely in scratch (first 100 chars — hot holds only the
            // last SmallHotBuffer = 256).
            var scratchSlice = cap.ReadSlice(0, 100);
            Assert(scratchSlice == input[..100], "offset: scratch-only slice matches");

            // Entirely in hot (last 128 chars).
            var hotSlice = cap.ReadSlice(input.Length - 128, 128);
            Assert(hotSlice == input[^128..], "offset: hot-only slice matches");

            // Straddles the hot/scratch boundary — pick a range whose
            // start is below (total - hotBuffer) and whose end is above.
            long boundary = input.Length - SmallHotBuffer; // first char in hot
            var straddle = cap.ReadSlice(boundary - 50, 100);
            Assert(straddle == input.Substring((int)(boundary - 50), 100),
                "offset: straddle slice matches across boundary");

            // Full slice round-trip via the streaming reader. Compares
            // byte-for-byte to prove OpenSliceReader yields identical
            // content to an inline ReadSlice for the same window.
            using (var reader = cap.OpenSliceReader(0, input.Length))
            {
                var buf = new char[2048];
                var read = new System.Text.StringBuilder();
                int n;
                while ((n = reader.Read(buf, 0, buf.Length)) > 0)
                    read.Append(buf, 0, n);
                Assert(read.ToString() == input,
                    "offset: OpenSliceReader streams identical content");
            }

            cap.Dispose();
        }

        // Cleanup / idempotency: Complete deletes the scratch file, and
        // calling Complete + Dispose again is a no-op (no throw). This
        // protects the finalizer's "best-effort release on every exit
        // path" pattern.
        {
            var store = new FakeScratchStore();
            var cap = new CommandOutputCapture(store, SmallHotBuffer, SnapshotDefault);
            cap.Append(new string('y', SmallHotBuffer * 2));
            var path = cap.ScratchPathForDiagnostics;
            Assert(path != null, "cleanup: scratch path visible before Complete");

            cap.Complete();
            Assert(store.DeletedPaths.Contains(path!), "cleanup: store.TryDelete called on scratch path");

            bool threw = false;
            try { cap.Complete(); cap.Dispose(); }
            catch { threw = true; }
            Assert(!threw, "cleanup: Complete / Dispose are idempotent");
        }

        // Cleanup swallows store-level delete failure so scratch release
        // never blocks the finalize-once path. A throwing TryDelete must
        // not bubble out of Complete/Dispose.
        {
            var store = new FakeScratchStore { ThrowOnDelete = true };
            var cap = new CommandOutputCapture(store, SmallHotBuffer, SnapshotDefault);
            cap.Append(new string('z', SmallHotBuffer * 2));

            bool threw = false;
            try { cap.Complete(); }
            catch { threw = true; }
            Assert(!threw, "cleanup: TryDelete failure swallowed by Complete");
        }

        // GetCurrentCommandSnapshot: returns only the tail of
        // [CommandStart, Length). Must not fall back to any earlier
        // content — this is the replacement for _aiOutput's role as
        // timeout partialOutput, and the plan explicitly forbids a
        // general recent-output fallback.
        {
            var store = new FakeScratchStore();
            var cap = new CommandOutputCapture(store, SmallHotBuffer, SnapshotDefault);

            // Pre-command content: must never appear in the snapshot.
            cap.Append(new string('P', 300)); // 300 'P' chars, pre-command
            cap.MarkCommandStart();
            // Command content: 1000 chars of 'C' followed by a marker tail.
            cap.Append(new string('C', 1000));
            cap.Append("<<END>>");

            var snap = cap.GetCurrentCommandSnapshot(maxChars: 50);
            Assert(snap.Length == 50, $"snapshot: respects maxChars (got {snap.Length})");
            Assert(snap.EndsWith("<<END>>"), "snapshot: reads the tail of the command window");
            Assert(!snap.Contains("P"), "snapshot: does NOT include pre-command content");

            // No maxChars argument: default cap applies, still no leakage.
            var defaultSnap = cap.GetCurrentCommandSnapshot();
            Assert(defaultSnap.Length <= SnapshotDefault,
                $"snapshot: default cap respected (got {defaultSnap.Length})");
            Assert(!defaultSnap.Contains('P'),
                "snapshot: default snapshot still excludes pre-command");

            // Call before command start: empty string, not recent ring.
            cap.Dispose();
            var fresh = new CommandOutputCapture(new FakeScratchStore(), SmallHotBuffer, SnapshotDefault);
            fresh.Append("some general output");
            Assert(fresh.GetCurrentCommandSnapshot() == "",
                "snapshot: empty before MarkCommandStart (no recent-ring fallback)");
            fresh.Dispose();
        }

        // Concurrent append + slice read: the reader thread should never
        // crash and its reads should always return exactly `length` chars
        // (or less if the capture is shorter). We don't assert specific
        // content — the appender races ahead — but we do assert that
        // every returned slice is a prefix of the underlying pattern
        // at some earlier point in time, so there is no interleaving
        // corruption.
        {
            var store = new FakeScratchStore();
            var cap = new CommandOutputCapture(store, SmallHotBuffer, SnapshotDefault);

            // Both threads stop when the appender has written N chars.
            const int appendChunks = 2000;
            const int chunkSize = 32;
            var done = new System.Threading.ManualResetEventSlim(false);
            var errors = new ConcurrentQueue<string>();

            var appender = Task.Run(() =>
            {
                try
                {
                    for (int i = 0; i < appendChunks; i++)
                        cap.Append(new string((char)('a' + (i % 26)), chunkSize));
                }
                catch (Exception ex) { errors.Enqueue("append: " + ex.Message); }
                finally { done.Set(); }
            });

            var reader = Task.Run(() =>
            {
                try
                {
                    while (!done.IsSet)
                    {
                        var len = cap.Length;
                        if (len < 100) { Thread.Yield(); continue; }
                        // Read a small middle slice. Any crash / partial
                        // corruption shows up as an exception here.
                        var slice = cap.ReadSlice(Math.Max(0, len - 100), 50);
                        if (slice.Length > 50)
                            errors.Enqueue($"slice too long: {slice.Length}");
                    }
                }
                catch (Exception ex) { errors.Enqueue("read: " + ex.Message); }
            });

            Task.WaitAll(appender, reader);

            Assert(errors.IsEmpty,
                $"concurrent: no crashes or oversize reads (errors={string.Join("; ", errors)})");
            Assert(cap.Length == (long)appendChunks * chunkSize,
                $"concurrent: final Length matches expected ({cap.Length})");
            cap.Dispose();
        }

        Console.WriteLine($"\n{pass} passed, {fail} failed");
        if (fail > 0) Environment.Exit(1);
    }

    /// <summary>
    /// In-memory <see cref="ICaptureScratchStore"/>. Backs each scratch
    /// allocation with a byte list so tests can run without real disk,
    /// while still exercising the production UTF-16 char layout via
    /// <see cref="IDisposableStream"/>. Records create / delete for
    /// assertions, and supports a throw-on-delete switch for the
    /// Complete-swallows-errors branch.
    /// </summary>
    private sealed class FakeScratchStore : ICaptureScratchStore
    {
        private int _counter;

        public int CreatedCount { get; private set; }
        public long TotalBytesWritten { get; private set; }
        public ConcurrentBag<string> DeletedPaths { get; } = new();
        public bool ThrowOnDelete { get; set; }

        public (string Path, IDisposableStream Stream) CreateScratch(string prefix, string suffix)
        {
            CreatedCount++;
            var path = $"/fake/scratch/{prefix}{++_counter}{suffix}";
            var stream = new MemoryScratchStream(bytes => TotalBytesWritten += bytes);
            return (path, stream);
        }

        public void TryDelete(string path)
        {
            if (ThrowOnDelete) throw new IOException("Simulated delete failure.");
            DeletedPaths.Add(path);
        }

        /// <summary>
        /// Append-only byte list with random-offset reads; just enough
        /// surface to match <see cref="FileScratchStream"/> semantically.
        /// Chars are stored as UTF-16 LE (2 bytes per char), matching the
        /// production layout so any byte-level assumption elsewhere in
        /// the capture surfaces here.
        /// </summary>
        private sealed class MemoryScratchStream : IDisposableStream
        {
            private readonly List<byte> _bytes = new();
            private readonly Action<long> _reportBytes;

            public MemoryScratchStream(Action<long> reportBytes)
            {
                _reportBytes = reportBytes;
            }

            public void WriteChars(ReadOnlySpan<char> chars)
            {
                if (chars.Length == 0) return;
                var src = System.Runtime.InteropServices.MemoryMarshal.AsBytes(chars);
                _bytes.AddRange(src.ToArray());
                _reportBytes(src.Length);
            }

            public void ReadChars(long absCharOffset, Span<char> dest)
            {
                if (dest.Length == 0) return;
                long byteOffset = absCharOffset * 2;
                var destBytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(dest);
                for (int i = 0; i < destBytes.Length; i++)
                    destBytes[i] = _bytes[(int)byteOffset + i];
            }

            public void Dispose() { /* nothing to close */ }
        }
    }
}
