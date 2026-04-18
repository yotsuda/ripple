namespace Ripple.Services;

/// <summary>
/// Bounded, per-command raw output store used by CommandTracker and
/// consumed by ConsoleWorker's finalize-once path.
///
/// Why this exists (the "why", per AGENTS.md):
/// CommandTracker used to accumulate the entire command capture in a
/// single <c>string _aiOutput</c> field and slice it at finalize time.
/// That forced an unbounded allocation inside the tracker lock and
/// required CleanOutput to rebuild the whole capture as one string
/// before truncation could run. This type keeps the raw capture off
/// the managed heap once it exceeds a small hot window: older chars
/// spill to a worker-private scratch file, but the capture still
/// exposes offset-based reads so the finalizer can walk just the
/// [_commandStart, _commandEnd) window plus any trailing settled
/// bytes without materializing the rest.
///
/// Offset model:
/// Offsets are char counts from the start of the capture — the same
/// unit CommandTracker's existing <c>_commandStart</c> /
/// <c>_commandEnd</c> markers already use (<c>_aiOutput.Length</c>
/// was a char length). Callers that port from <c>_aiOutput</c> do
/// not need to convert units.
///
/// Scratch storage:
/// Spill files live under a ripple-private subdirectory
/// (<c>ripple.capture</c>) that is intentionally distinct from the
/// public spill directory <see cref="OutputTruncationHelper"/> uses
/// for saving oversized finalized results (<c>ripple.output</c>).
/// These scratch files are an implementation detail of in-flight
/// capture and are deleted on <see cref="Complete"/> /
/// <see cref="Dispose"/>; they must never be surfaced to the AI.
///
/// Threading:
/// Appends (tracker thread) can race with reads (worker finalize
/// thread). All mutations and reads serialize through
/// <see cref="_lock"/>. The scratch file is only appended to
/// (never rewritten in place), so readers that have already
/// snapshotted the logical length can safely read their slice
/// while more appends land past the end.
/// </summary>
public sealed class CommandOutputCapture : IDisposable
{
    /// <summary>
    /// Chars kept in memory before older content is spilled to disk.
    /// Sized to cover the common case (commands producing a few KB of
    /// output finalize entirely from RAM) while capping the tracker's
    /// working set when a runaway command streams megabytes.
    /// </summary>
    public const int DefaultHotBufferChars = 64 * 1024;

    /// <summary>
    /// Default cap on chars returned by
    /// <see cref="GetCurrentCommandSnapshot"/>. Matches today's
    /// timeout <c>partialOutput</c> semantics: enough tail for the AI
    /// to diagnose a stuck command, small enough to keep the MCP
    /// response within its token budget.
    /// </summary>
    public const int DefaultSnapshotCharCap = 16 * 1024;

    /// <summary>
    /// Upper bound on the string length any single
    /// <see cref="ReadSlice"/> call is willing to materialize.
    /// Slices larger than this must use <see cref="OpenSliceReader"/>
    /// so the finalizer never rebuilds the full capture as one
    /// string.
    /// </summary>
    public const int MaxInlineSliceChars = 256 * 1024;

    /// <summary>
    /// Directory name used for worker-private scratch spill files.
    /// Separate from <c>ripple.output</c> (the public spill directory
    /// used by the truncation helper) so cleanup routines that walk
    /// one never touch the other.
    /// </summary>
    public const string ScratchDirectoryName = "ripple.capture";

    private const string ScratchFilePrefix = "ripple_capture_";
    private const string ScratchFileSuffix = ".bin";

    private readonly object _lock = new();
    private readonly ICaptureScratchStore _scratchStore;
    private readonly int _hotBufferChars;
    private readonly int _defaultSnapshotChars;

    // Hot ring: most recent _hotBufferChars of the capture. Older content
    // lives in _scratchStream at absolute char offset [0, _spilledChars).
    // Anything at absolute offset >= _spilledChars lives in _hot.
    private readonly char[] _hot;
    private int _hotLen;           // chars currently in _hot, 0.._hot.Length
    private long _spilledChars;    // chars flushed to scratch
    private long _totalChars;      // total chars appended (spilled + hot)

    // Command window markers. -1 == not yet set. Tracker sets these
    // from its OSC C / OSC D handlers using the current Length.
    private long _commandStart = -1;
    private long _commandEnd = -1;

    private IDisposableStream? _scratchStream;
    private string? _scratchPath;
    private bool _disposed;

    public CommandOutputCapture()
        : this(DefaultCaptureScratchStore.Instance, DefaultHotBufferChars, DefaultSnapshotCharCap)
    {
    }

    public CommandOutputCapture(
        ICaptureScratchStore scratchStore,
        int hotBufferChars = DefaultHotBufferChars,
        int defaultSnapshotChars = DefaultSnapshotCharCap)
    {
        if (scratchStore is null) throw new ArgumentNullException(nameof(scratchStore));
        if (hotBufferChars <= 0) throw new ArgumentOutOfRangeException(nameof(hotBufferChars));
        if (defaultSnapshotChars <= 0) throw new ArgumentOutOfRangeException(nameof(defaultSnapshotChars));

        _scratchStore = scratchStore;
        _hotBufferChars = hotBufferChars;
        _defaultSnapshotChars = defaultSnapshotChars;
        _hot = new char[hotBufferChars];
    }

    /// <summary>
    /// Total chars appended to this capture since creation. Monotonically
    /// increasing — this is the value tracker code should snapshot when
    /// it needs to record a command-boundary offset.
    /// </summary>
    public long Length
    {
        get { lock (_lock) return _totalChars; }
    }

    /// <summary>
    /// Current [start, end) command window, or <c>(null, null)</c> if
    /// the corresponding OSC marker has not fired yet. Exposed so the
    /// finalizer can pick the right slice without reaching into
    /// tracker internals.
    /// </summary>
    public (long? Start, long? End) CommandWindow
    {
        get
        {
            lock (_lock)
            {
                return (
                    _commandStart >= 0 ? _commandStart : null,
                    _commandEnd >= 0 ? _commandEnd : null);
            }
        }
    }

    /// <summary>
    /// Path of the scratch spill file, or null if nothing has spilled yet.
    /// Exposed for diagnostics / tests only; production code must not
    /// read it directly — use <see cref="ReadSlice"/> /
    /// <see cref="OpenSliceReader"/>.
    /// </summary>
    public string? ScratchPathForDiagnostics
    {
        get { lock (_lock) return _scratchPath; }
    }

    /// <summary>
    /// Mark the current <see cref="Length"/> as the command-start offset.
    /// Safe to call more than once — last write wins, but tracker
    /// callers only set this when they see the first OSC C of a
    /// command.
    /// </summary>
    public void MarkCommandStart()
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            _commandStart = _totalChars;
        }
    }

    /// <summary>
    /// Mark the current <see cref="Length"/> as the command-end offset
    /// (observed when OSC D fires).
    /// </summary>
    public void MarkCommandEnd()
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            _commandEnd = _totalChars;
        }
    }

    /// <summary>
    /// Force command-start to zero for shells that cannot emit OSC C
    /// at the right moment (cmd.exe). Matches
    /// <c>CommandTracker.SkipCommandStartMarker</c>.
    /// </summary>
    public void ForceCommandStartAtZero()
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            if (_commandStart < 0) _commandStart = 0;
        }
    }

    /// <summary>
    /// Append raw captured chars. Text may span the hot-buffer / scratch
    /// boundary; the capture spills older content transparently.
    /// </summary>
    public void Append(ReadOnlySpan<char> text)
    {
        if (text.Length == 0) return;
        lock (_lock)
        {
            ThrowIfDisposed();

            // If this one append alone is larger than the hot buffer,
            // everything except its last _hotBufferChars can go
            // straight to scratch without touching _hot twice.
            if (text.Length >= _hotBufferChars)
            {
                // Flush whatever is currently in _hot to scratch first —
                // it's logically older than any byte of this append.
                FlushHotToScratch(_hotLen);

                int tailStart = text.Length - _hotBufferChars;
                if (tailStart > 0)
                    SpillCharsToScratch(text[..tailStart]);

                // Now refill _hot with the last _hotBufferChars of text.
                text[tailStart..].CopyTo(_hot);
                _hotLen = _hotBufferChars;
                _totalChars += text.Length;
                return;
            }

            int remaining = _hotBufferChars - _hotLen;
            if (text.Length <= remaining)
            {
                text.CopyTo(_hot.AsSpan(_hotLen));
                _hotLen += text.Length;
                _totalChars += text.Length;
                return;
            }

            // Need to spill the oldest (text.Length - remaining) chars of
            // _hot before we can fit `text` in its entirety.
            int overflow = text.Length - remaining;
            FlushHotToScratch(overflow);

            // _hot now has `remaining + overflow` = text.Length free
            // slots at the tail.
            text.CopyTo(_hot.AsSpan(_hotLen));
            _hotLen += text.Length;
            _totalChars += text.Length;
        }
    }

    /// <summary>
    /// Convenience overload. Callers holding a <see cref="string"/> avoid
    /// an AsSpan() site at every append.
    /// </summary>
    public void Append(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        Append(text.AsSpan());
    }

    /// <summary>
    /// Read a bounded slice <c>[start, start + length)</c> as a single
    /// <see cref="string"/>. Intended for the cleaning window — small
    /// enough that assembling it as one string is fine. Throws if the
    /// slice is larger than <see cref="MaxInlineSliceChars"/>; use
    /// <see cref="OpenSliceReader"/> in that case.
    /// </summary>
    public string ReadSlice(long start, long length)
    {
        if (start < 0) throw new ArgumentOutOfRangeException(nameof(start));
        if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
        if (length == 0) return "";
        if (length > MaxInlineSliceChars)
            throw new ArgumentOutOfRangeException(
                nameof(length),
                $"Slice of {length} chars exceeds inline limit {MaxInlineSliceChars}; use OpenSliceReader.");

        lock (_lock)
        {
            ThrowIfDisposed();
            var (clampedStart, clampedLen) = ClampSlice(start, length);
            if (clampedLen == 0) return "";

            var buffer = new char[clampedLen];
            int written = CopySliceLocked(clampedStart, buffer.AsSpan());
            return new string(buffer, 0, written);
        }
    }

    /// <summary>
    /// Stream-oriented reader for slices that may exceed
    /// <see cref="MaxInlineSliceChars"/>. The returned
    /// <see cref="TextReader"/> reads a snapshot of the slice as it
    /// existed at call time — further appends do not extend the
    /// reader's view. Caller owns disposal.
    /// </summary>
    public TextReader OpenSliceReader(long start, long length)
    {
        if (start < 0) throw new ArgumentOutOfRangeException(nameof(start));
        if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));

        lock (_lock)
        {
            ThrowIfDisposed();
            var (clampedStart, clampedLen) = ClampSlice(start, length);
            return new CaptureSliceReader(this, clampedStart, clampedLen);
        }
    }

    /// <summary>
    /// Tail-bounded snapshot of the CURRENT IN-FLIGHT COMMAND ONLY. This
    /// is the replacement for <c>_aiOutput</c>'s role as the source of
    /// timeout <c>partialOutput</c>. It reads only from
    /// <c>[_commandStart, Length)</c> and returns at most
    /// <paramref name="maxChars"/> chars from the tail of that
    /// region — NEVER the general recent-output ring (that belongs
    /// to peek_console / the tracker's <c>_recentBuf</c> and is out
    /// of scope for this type).
    ///
    /// Returns empty string when no command start has been observed.
    /// </summary>
    public string GetCurrentCommandSnapshot(int? maxChars = null)
    {
        int cap = maxChars ?? _defaultSnapshotChars;
        if (cap <= 0) return "";

        lock (_lock)
        {
            ThrowIfDisposed();
            if (_commandStart < 0) return "";

            long start = _commandStart;
            long end = _totalChars;
            if (end <= start) return "";

            long available = end - start;
            long take = Math.Min(available, cap);
            long readFrom = end - take;

            var buffer = new char[take];
            int written = CopySliceLocked(readFrom, buffer.AsSpan());
            return new string(buffer, 0, written);
        }
    }

    /// <summary>
    /// Explicit finalization hook. Release scratch-file resources once
    /// the worker has read every slice it needs. Idempotent; further
    /// reads throw <see cref="ObjectDisposedException"/>.
    /// </summary>
    public void Complete()
    {
        Dispose();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;

            var stream = _scratchStream;
            var path = _scratchPath;
            _scratchStream = null;

            if (stream is not null)
            {
                try { stream.Dispose(); } catch { /* best effort */ }
            }
            if (path is not null)
            {
                try { _scratchStore.TryDelete(path); } catch { /* best effort */ }
            }
        }
    }

    // ---- internals ----

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(CommandOutputCapture));
    }

    private (long Start, long Length) ClampSlice(long start, long length)
    {
        long total = _totalChars;
        if (start >= total) return (total, 0);
        long end = start + length;
        if (end > total) end = total;
        if (end <= start) return (start, 0);
        return (start, end - start);
    }

    /// <summary>
    /// Copy chars from absolute offset <paramref name="absStart"/> into
    /// <paramref name="dest"/>. Caller must hold <see cref="_lock"/>.
    /// Returns the number of chars written (never greater than
    /// dest.Length).
    /// </summary>
    private int CopySliceLocked(long absStart, Span<char> dest)
    {
        if (dest.Length == 0) return 0;

        long totalWanted = Math.Min(dest.Length, _totalChars - absStart);
        if (totalWanted <= 0) return 0;

        int written = 0;

        // Portion that lives in the scratch file.
        if (absStart < _spilledChars)
        {
            long scratchEnd = Math.Min(_spilledChars, absStart + totalWanted);
            int scratchCount = (int)(scratchEnd - absStart);
            ReadScratchLocked(absStart, dest[..scratchCount]);
            written += scratchCount;
        }

        // Portion that lives in the hot ring.
        long hotStart = Math.Max(absStart, _spilledChars);
        long hotEnd = absStart + totalWanted;
        if (hotEnd > hotStart)
        {
            int hotOffset = (int)(hotStart - _spilledChars);
            int hotCount = (int)(hotEnd - hotStart);
            _hot.AsSpan(hotOffset, hotCount).CopyTo(dest[written..]);
            written += hotCount;
        }

        return written;
    }

    /// <summary>
    /// Move the oldest <paramref name="flushChars"/> chars out of the hot
    /// ring and into the scratch stream. Caller must hold the lock.
    /// </summary>
    private void FlushHotToScratch(int flushChars)
    {
        if (flushChars <= 0) return;
        if (flushChars > _hotLen) flushChars = _hotLen;

        EnsureScratchOpenLocked();
        _scratchStream!.WriteChars(_hot.AsSpan(0, flushChars));
        _spilledChars += flushChars;

        int retained = _hotLen - flushChars;
        if (retained > 0)
            Array.Copy(_hot, flushChars, _hot, 0, retained);
        _hotLen = retained;
    }

    private void SpillCharsToScratch(ReadOnlySpan<char> chars)
    {
        if (chars.Length == 0) return;
        EnsureScratchOpenLocked();
        _scratchStream!.WriteChars(chars);
        _spilledChars += chars.Length;
    }

    private void ReadScratchLocked(long absStart, Span<char> dest)
    {
        if (dest.Length == 0) return;
        if (_scratchStream is null)
            throw new InvalidOperationException("Scratch stream missing while spilled chars > 0.");

        _scratchStream.ReadChars(absStart, dest);
    }

    private void EnsureScratchOpenLocked()
    {
        if (_scratchStream is not null) return;
        var (path, stream) = _scratchStore.CreateScratch(ScratchFilePrefix, ScratchFileSuffix);
        _scratchPath = path;
        _scratchStream = stream;
    }

    /// <summary>
    /// Snapshot-based TextReader over a fixed capture slice. Copies out
    /// of the capture in small chunks so large slices never allocate
    /// the whole slice as one string.
    /// </summary>
    private sealed class CaptureSliceReader : TextReader
    {
        // Page size is small enough to stay in L1 but large enough that
        // the finalizer's line-scan pass doesn't pay per-char locking
        // overhead.
        private const int PageChars = 8 * 1024;

        private readonly CommandOutputCapture _owner;
        private readonly long _absStart;
        private readonly long _length;
        private long _position;
        private bool _disposed;

        public CaptureSliceReader(CommandOutputCapture owner, long absStart, long length)
        {
            _owner = owner;
            _absStart = absStart;
            _length = length;
        }

        public override int Read(char[] buffer, int index, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            if (index < 0 || count < 0 || index + count > buffer.Length)
                throw new ArgumentOutOfRangeException(nameof(count));
            if (_disposed) throw new ObjectDisposedException(nameof(CaptureSliceReader));
            if (count == 0) return 0;

            long remaining = _length - _position;
            if (remaining <= 0) return 0;

            int toRead = (int)Math.Min(count, Math.Min(remaining, PageChars));
            int written;
            lock (_owner._lock)
            {
                _owner.ThrowIfDisposed();
                written = _owner.CopySliceLocked(_absStart + _position, buffer.AsSpan(index, toRead));
            }
            _position += written;
            return written;
        }

        public override int Read()
        {
            Span<char> one = stackalloc char[1];
            int n;
            if (_disposed) throw new ObjectDisposedException(nameof(CaptureSliceReader));
            long remaining = _length - _position;
            if (remaining <= 0) return -1;
            lock (_owner._lock)
            {
                _owner.ThrowIfDisposed();
                n = _owner.CopySliceLocked(_absStart + _position, one);
            }
            if (n == 0) return -1;
            _position++;
            return one[0];
        }

        public override int Peek()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(CaptureSliceReader));
            long remaining = _length - _position;
            if (remaining <= 0) return -1;
            Span<char> one = stackalloc char[1];
            int n;
            lock (_owner._lock)
            {
                _owner.ThrowIfDisposed();
                n = _owner.CopySliceLocked(_absStart + _position, one);
            }
            return n == 0 ? -1 : one[0];
        }

        protected override void Dispose(bool disposing)
        {
            _disposed = true;
            base.Dispose(disposing);
        }
    }
}

/// <summary>
/// Filesystem abstraction for capture scratch files. Injected so unit
/// tests can run without touching real temp storage, and so the
/// worker can be configured with a non-default scratch root if needed
/// (e.g. tests, alternate TMPDIR).
/// </summary>
public interface ICaptureScratchStore
{
    /// <summary>
    /// Create a fresh scratch file and return its absolute path plus a
    /// stream that supports append-at-end writes and random-offset
    /// reads (both measured in chars, i.e. UTF-16 code units). The
    /// stream must be exclusively owned by the caller until disposed.
    /// </summary>
    (string Path, IDisposableStream Stream) CreateScratch(string prefix, string suffix);

    /// <summary>
    /// Best-effort delete of a path previously returned by
    /// <see cref="CreateScratch"/>. Must not throw on missing file —
    /// cleanup runs from Dispose paths that should never observe
    /// finalization errors.
    /// </summary>
    void TryDelete(string path);
}

/// <summary>
/// Minimal stream surface that <see cref="CommandOutputCapture"/>
/// needs from its scratch store. Keeping this narrow (chars only,
/// not byte streams) lets tests back the capture with an in-memory
/// implementation without pulling in <see cref="FileStream"/>.
/// </summary>
public interface IDisposableStream : IDisposable
{
    void WriteChars(ReadOnlySpan<char> chars);
    void ReadChars(long absCharOffset, Span<char> dest);
}

/// <summary>
/// Default scratch store: files under <c>%TEMP%\ripple.capture</c>
/// on Windows or <c>${TMPDIR:-/tmp}/ripple.capture</c> on Unix.
/// Intentionally distinct from the public <c>ripple.output</c> directory
/// used by <c>OutputTruncationHelper</c> so directory-level cleanup on
/// either side can never stomp on the other's files.
/// </summary>
public sealed class DefaultCaptureScratchStore : ICaptureScratchStore
{
    public static readonly DefaultCaptureScratchStore Instance = new();

    private readonly string _root;

    public DefaultCaptureScratchStore()
        : this(ResolveDefaultRoot())
    {
    }

    public DefaultCaptureScratchStore(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
            throw new ArgumentException("Scratch root required.", nameof(root));
        _root = root;
    }

    public (string Path, IDisposableStream Stream) CreateScratch(string prefix, string suffix)
    {
        Directory.CreateDirectory(_root);
        // Guid randomises per-capture so two workers racing cleanup on the
        // same directory can never collide on a filename.
        string path = Path.Combine(_root, prefix + Guid.NewGuid().ToString("N") + suffix);
        var fs = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.DeleteOnClose);
        return (path, new FileScratchStream(fs));
    }

    public void TryDelete(string path)
    {
        // FileOptions.DeleteOnClose handles the common case; this is a
        // belt-and-braces sweep in case the stream was never opened
        // (CreateScratch failed mid-way) or the OS left the entry
        // behind after a crash.
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best effort */ }
    }

    private static string ResolveDefaultRoot()
    {
        // Windows: %TEMP% (via Path.GetTempPath). Unix: honour $TMPDIR
        // explicitly since Path.GetTempPath also consults it but the
        // plan calls out the fallback to /tmp.
        string tempRoot;
        if (OperatingSystem.IsWindows())
        {
            tempRoot = Path.GetTempPath();
        }
        else
        {
            string? tmpdir = Environment.GetEnvironmentVariable("TMPDIR");
            tempRoot = string.IsNullOrWhiteSpace(tmpdir) ? "/tmp" : tmpdir;
        }
        return Path.Combine(tempRoot, CommandOutputCapture.ScratchDirectoryName);
    }

    private sealed class FileScratchStream : IDisposableStream
    {
        // UTF-16 LE matches .NET's in-memory char layout; writing raw
        // chars as 2-byte units avoids any encoding / surrogate
        // awareness in the hot path. The capture is an
        // implementation detail and is never read by another process,
        // so the format is free to be this trivial.
        private const int BytesPerChar = 2;

        private readonly FileStream _fs;
        private long _charCount;

        public FileScratchStream(FileStream fs) { _fs = fs; }

        public void WriteChars(ReadOnlySpan<char> chars)
        {
            if (chars.Length == 0) return;
            _fs.Seek(0, SeekOrigin.End);
            var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(chars);
            _fs.Write(bytes);
            _charCount += chars.Length;
        }

        public void ReadChars(long absCharOffset, Span<char> dest)
        {
            if (dest.Length == 0) return;
            if (absCharOffset < 0 || absCharOffset + dest.Length > _charCount)
                throw new ArgumentOutOfRangeException(nameof(absCharOffset));

            _fs.Seek(absCharOffset * BytesPerChar, SeekOrigin.Begin);
            var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(dest);
            int totalRead = 0;
            while (totalRead < bytes.Length)
            {
                int n = _fs.Read(bytes[totalRead..]);
                if (n <= 0) throw new EndOfStreamException("Scratch file shorter than tracked length.");
                totalRead += n;
            }
        }

        public void Dispose()
        {
            try { _fs.Dispose(); } catch { /* best effort */ }
        }
    }
}
