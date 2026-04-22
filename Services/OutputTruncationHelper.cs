using System.Diagnostics;
using System.Text;

namespace Ripple.Services;

/// <summary>
/// Truncates oversized finalized command output to protect the AI context
/// window. When the cleaned finalized stream exceeds the threshold, the
/// full content is spilled to a public temp file and a head+tail preview
/// is returned inline. Modeled on PowerShell.MCP's OutputTruncationHelper,
/// but shaped for ripple's worker:
///
///   - The worker-owned cache holds a lease on each spill path, so this
///     helper accepts a "live paths" predicate during cleanup to protect
///     spill files still referenced by undrained cached results.
///   - The main entrypoint returns both the display text and the spill
///     path so <see cref="ConsoleWorker"/> can record the path on its
///     cache entry without re-parsing preview text.
///   - Filesystem and clock are injected as thin abstractions so unit
///     tests can drive the threshold, newline alignment, save failure,
///     and cleanup-window branches deterministically.
/// </summary>
public class OutputTruncationHelper
{
    // Any cleaned finalized output at or below this size is returned
    // inline verbatim. Chosen to match PowerShell.MCP so the two servers
    // apply equivalent pressure on the AI's context budget.
    internal const int TruncationThreshold = 15_000;

    // Head / tail preview character counts. Their sum must stay strictly
    // below TruncationThreshold or the head and tail slices of a
    // just-over-threshold output would overlap and duplicate content.
    internal const int PreviewHeadSize = 1_000;
    internal const int PreviewTailSize = 2_000;

    // When aligning a preview boundary to a newline, we only scan this
    // many characters around the nominal cut. Large enough to land on a
    // line boundary for typical log output, small enough that a blob
    // without newlines still truncates at roughly the nominal size.
    internal const int NewlineScanLimit = 200;

    // Public spill directory name under %TEMP% / $TMPDIR. Kept short and
    // ripple-branded so users can spot it when housekeeping temp.
    internal const string OutputDirectoryName = "ripple.output";

    // Opportunistic cleanup window. Files older than this in the spill
    // directory are deleted on each save, unless the caller reports them
    // as still leased by an undrained cache entry.
    internal const int MaxFileAgeMinutes = 120;

    // Shared prefix for spill files. The cleanup pass matches on this
    // prefix so unrelated files a user drops in the folder survive.
    internal const string SpillFilePrefix = "ripple_output_";
    private const string SpillFileExtension = ".txt";
    private const string SpillFileGlob = SpillFilePrefix + "*" + SpillFileExtension;

    // Wire-format strings — kept as constants so tests can assert on the
    // exact separators and so any future tweak stays in one place.
    private const string HeadSeparator = "--- Preview (first ~1000 chars) ---";
    private const string TailSeparator = "--- Preview (last ~2000 chars) ---";
    private const string TruncatedSeparatorFormat = "--- truncated ({0} chars omitted) ---";
    private const string OversizeHeaderFormat = "Output too large ({0} characters). Full output saved to: {1}";
    // Tells the AI how to drill into the spill file using ripple's own
    // MCP tools instead of shell commands. Names are deliberately bare
    // (search_files / read_file) so the hint stays valid no matter which
    // namespace prefix the MCP client applied (ripple / ripple-stable /
    // ripple-dev / etc.) — the AI resolves that from its tool list.
    private const string InspectHintFormat = "Inspect with ripple's search_files (pattern=\"<regex>\", path=\"{0}\") or read_file (path=\"{0}\", offset=<line>, limit=<count>).";
    private const string SaveFailedHeaderFormat = "Output too large ({0} characters). Could not save full output to file.";

    // The threshold must exceed the combined preview sizes; otherwise the
    // head and tail slices overlap, producing duplicated content in the
    // preview. Runs once on first static access of the type.
    private static readonly bool _validated = Validate();
    private static bool Validate()
    {
        Debug.Assert(
            TruncationThreshold > PreviewHeadSize + PreviewTailSize,
            $"TruncationThreshold ({TruncationThreshold}) must be greater than " +
            $"PreviewHeadSize + PreviewTailSize ({PreviewHeadSize + PreviewTailSize}) " +
            "to avoid overlapping head/tail previews.");
        return true;
    }

    private readonly IOutputSpillFileSystem _fs;
    private readonly IClock _clock;
    private readonly string _directory;

    /// <summary>
    /// Default-DI constructor — uses the real filesystem and system clock
    /// with the platform-appropriate spill directory (<c>%TEMP%</c> on
    /// Windows, <c>$TMPDIR</c> or <c>/tmp</c> on Unix, both with the
    /// <see cref="OutputDirectoryName"/> subfolder).
    /// </summary>
    public OutputTruncationHelper()
        : this(DefaultFileSystem.Instance, SystemClock.Instance, directory: null)
    {
    }

    /// <summary>
    /// Testing constructor — injects a filesystem, a clock, and an
    /// optional override for the spill directory. Pass <c>null</c> for
    /// <paramref name="directory"/> to resolve from the filesystem
    /// abstraction's temp root.
    /// </summary>
    public OutputTruncationHelper(IOutputSpillFileSystem fs, IClock clock, string? directory)
    {
        _fs = fs ?? throw new ArgumentNullException(nameof(fs));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _directory = directory ?? Path.Combine(_fs.GetTempPath(), OutputDirectoryName);
    }

    /// <summary>
    /// Decides whether the cleaned finalized <paramref name="output"/>
    /// fits inline or must spill. Under threshold, the returned
    /// <see cref="DisplayOutput"/> is the input verbatim and
    /// <see cref="OutputTruncationResult.SpillFilePath"/> is <c>null</c>.
    /// Over threshold, the full content is written to
    /// <c>ripple_output_*.txt</c> and a head+tail preview is returned.
    /// If the disk write fails, the preview is still returned but the
    /// header line reports the save failure instead of a path, matching
    /// PowerShell.MCP's fallback.
    /// </summary>
    public virtual OutputTruncationResult Process(string output)
    {
        ArgumentNullException.ThrowIfNull(output);

        if (output.Length <= TruncationThreshold)
            return new OutputTruncationResult(DisplayOutput: output, SpillFilePath: null);

        var headEnd = FindHeadBoundary(output, PreviewHeadSize);
        var head = output[..headEnd];

        var tailStart = FindTailBoundary(output, PreviewTailSize);
        var tail = output[tailStart..];

        var omitted = output.Length - head.Length - tail.Length;

        // Save first so the header can reference the concrete path (or
        // flag the failure). Cleanup of aged spill files is the
        // worker's responsibility — it owns the lease set that
        // distinguishes undrained-cache files from orphans. The helper
        // never sweeps on its own, so save calls can't race a leased
        // file into deletion.
        var filePath = TrySaveFullOutput(output);

        var sb = new StringBuilder();
        if (filePath != null)
        {
            sb.AppendLine(string.Format(OversizeHeaderFormat, output.Length, filePath));
            sb.AppendLine(string.Format(InspectHintFormat, filePath));
        }
        else
            sb.AppendLine(string.Format(SaveFailedHeaderFormat, output.Length));

        sb.AppendLine();
        sb.AppendLine(HeadSeparator);
        sb.AppendLine(head);
        sb.AppendLine(string.Format(TruncatedSeparatorFormat, omitted));
        sb.AppendLine(TailSeparator);
        sb.Append(tail);

        return new OutputTruncationResult(DisplayOutput: sb.ToString(), SpillFilePath: filePath);
    }

    /// <summary>
    /// Deletes <c>ripple_output_*.txt</c> files older than
    /// <see cref="MaxFileAgeMinutes"/>, except for any path for which
    /// <paramref name="isLive"/> returns true. The worker passes a
    /// predicate backed by its undrained-cache lease set so spill files
    /// that a pending <c>wait_for_completion</c> still needs survive
    /// even if they pass the age cutoff. Per-file errors are swallowed
    /// so a locked file does not block other deletions.
    /// </summary>
    public void CleanupOldSpillFiles(Func<string, bool> isLive)
    {
        ArgumentNullException.ThrowIfNull(isLive);

        try
        {
            if (!_fs.DirectoryExists(_directory))
                return;

            var cutoff = _clock.UtcNow.AddMinutes(-MaxFileAgeMinutes);

            foreach (var file in _fs.EnumerateFiles(_directory, SpillFileGlob))
            {
                if (isLive(file))
                    continue;

                try
                {
                    if (_fs.GetLastWriteTimeUtc(file) < cutoff)
                        _fs.DeleteFile(file);
                }
                catch (IOException)
                {
                    // Another thread may be writing — safe to skip this
                    // file and try the rest.
                }
                catch (UnauthorizedAccessException)
                {
                    // Transient ACL contention on Windows — same rationale.
                }
            }
        }
        catch
        {
            // Directory enumeration itself failed — nothing to clean up,
            // and cleanup is opportunistic so we never surface this.
        }
    }

    /// <summary>
    /// Writes the full cleaned output to the spill directory and
    /// returns the created path. Returns <c>null</c> on any failure,
    /// so the caller can fall back to a save-failed preview header.
    /// Does NOT sweep the directory: cleanup is lease-aware and owned
    /// by the worker (<see cref="CleanupOldSpillFiles"/> called with
    /// a live-path predicate). A sweep from the save path would lack
    /// that context and could delete files still referenced by
    /// undrained cached results.
    /// </summary>
    private string? TrySaveFullOutput(string output)
    {
        try
        {
            _fs.CreateDirectory(_directory);

            var fileName = BuildSpillFileName(_clock.UtcNow);
            var filePath = Path.Combine(_directory, fileName);

            _fs.WriteAllText(filePath, output);

            return filePath;
        }
        catch
        {
            return null;
        }
    }

    private static string BuildSpillFileName(DateTimeOffset timestamp)
    {
        // yyyyMMdd_HHmmss_fff lines up with PowerShell.MCP's name so
        // files from either server sort sensibly side-by-side when both
        // happen to live under %TEMP%. The GetRandomFileName() suffix
        // protects against collisions inside the same millisecond.
        var stamp = timestamp.ToLocalTime().ToString("yyyyMMdd_HHmmss_fff");
        var random = Path.GetRandomFileName();
        return $"{SpillFilePrefix}{stamp}_{random}{SpillFileExtension}";
    }

    /// <summary>
    /// Finds a head cut position aligned to the nearest preceding
    /// newline within <see cref="NewlineScanLimit"/>. If no newline is
    /// found inside the scan window, falls back to the nominal cut so
    /// the preview size stays bounded on blobs without newlines.
    /// </summary>
    private static int FindHeadBoundary(string output, int nominalSize)
    {
        if (nominalSize >= output.Length)
            return output.Length;

        var searchStart = Math.Max(0, nominalSize - NewlineScanLimit);
        var lastNewline = output.LastIndexOf('\n', nominalSize - 1, nominalSize - searchStart);

        // Cut after the newline so the head preview ends on a complete line.
        return lastNewline >= 0 ? lastNewline + 1 : nominalSize;
    }

    /// <summary>
    /// Finds a tail start position aligned to the nearest following
    /// newline within <see cref="NewlineScanLimit"/>. If none is found
    /// inside the scan window, falls back to the nominal start.
    /// </summary>
    private static int FindTailBoundary(string output, int nominalSize)
    {
        var nominalStart = output.Length - nominalSize;
        if (nominalStart <= 0)
            return 0;

        var searchEnd = Math.Min(output.Length, nominalStart + NewlineScanLimit);
        var nextNewline = output.IndexOf('\n', nominalStart, searchEnd - nominalStart);

        // Start after the newline so the tail preview begins on a fresh line.
        return nextNewline >= 0 ? nextNewline + 1 : nominalStart;
    }
}

/// <summary>
/// Outcome of <see cref="OutputTruncationHelper.Process(string)"/>.
/// <see cref="DisplayOutput"/> is what the worker sends back to the
/// MCP client (inline content under threshold, head+tail preview over
/// threshold). <see cref="SpillFilePath"/> is the absolute path of the
/// public spill file when one was created, or <c>null</c> when the
/// output fit inline or the save failed. The worker records it on the
/// cache entry so cleanup can protect it while the entry is undrained.
/// </summary>
public readonly record struct OutputTruncationResult(string DisplayOutput, string? SpillFilePath);

/// <summary>
/// Injection seam for the filesystem operations the helper performs.
/// Keeps the production path a thin pass-through to
/// <see cref="System.IO"/> while letting tests assert on writes,
/// simulate save failures, and control enumerated results without
/// touching real temp storage.
/// </summary>
public interface IOutputSpillFileSystem
{
    string GetTempPath();
    bool DirectoryExists(string path);
    void CreateDirectory(string path);
    void WriteAllText(string path, string contents);
    IEnumerable<string> EnumerateFiles(string directory, string searchPattern);
    DateTimeOffset GetLastWriteTimeUtc(string path);
    void DeleteFile(string path);
}

/// <summary>
/// Minimal clock abstraction so the cleanup cutoff is deterministic in
/// tests. Production uses <see cref="SystemClock"/>.
/// </summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

internal sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

internal sealed class DefaultFileSystem : IOutputSpillFileSystem
{
    public static readonly DefaultFileSystem Instance = new();

    // Owner-only directory mode (0700). Applied on Unix so sibling users
    // on a shared host (where $TMPDIR falls back to /tmp) can't enumerate
    // ripple's spill directory. Windows keeps default ACLs — %TEMP% is
    // per-user and already restricted.
    internal const UnixFileMode SpillDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    // Owner-only file mode (0600). Spill files can contain command output
    // with tokens, paths, or secrets, so they must not be world-readable.
    internal const UnixFileMode SpillFileMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public string GetTempPath() => Path.GetTempPath();
    public bool DirectoryExists(string path) => Directory.Exists(path);

    public void CreateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
            return;
        }

        // On Unix, create with 0700 in a single syscall so there's no
        // umask-dependent window where a wider mode (e.g. 0755) is
        // briefly observable to another local user. The .NET 8+ overload
        // applies the mode atomically when the directory is created; for
        // a pre-existing directory it returns a DirectoryInfo but does
        // NOT change the mode, so we tighten permissions defensively
        // below in case a prior run (or the user) left them open.
        Directory.CreateDirectory(path, SpillDirectoryMode);
        TrySetUnixFileMode(path, SpillDirectoryMode);
    }

    public void WriteAllText(string path, string contents)
    {
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(path, contents);
            return;
        }

        // On Unix, open the file with 0600 at creation time so there's no
        // 0644-window between WriteAllText's default open and a follow-up
        // chmod. FileStream(string, FileStreamOptions) with UnixCreateMode
        // set on FileStreamOptions gives an atomic O_CREAT|O_EXCL-like
        // creation with the requested mode (net7+). UTF-8 without BOM
        // mirrors File.WriteAllText's default encoding.
        var options = new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.None,
            UnixCreateMode = SpillFileMode,
        };

        using (var stream = new FileStream(path, options))
        using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
        {
            writer.Write(contents);
        }

        // Defensive chmod in case the file already existed (FileMode.Create
        // truncates but preserves the old mode on some filesystems) or the
        // UnixCreateMode wasn't honored (e.g. FAT/NFS mounts that don't
        // track POSIX perms). Failures are logged and swallowed so the
        // spill still lands — otherwise we'd break the feature on mounts
        // that can't enforce perms, where the data is already local to
        // the user anyway.
        TrySetUnixFileMode(path, SpillFileMode);
    }

    public IEnumerable<string> EnumerateFiles(string directory, string searchPattern)
        => Directory.EnumerateFiles(directory, searchPattern);

    public DateTimeOffset GetLastWriteTimeUtc(string path)
        => new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);

    public void DeleteFile(string path) => File.Delete(path);

    // Sets owner-only permissions where possible. Swallows the three
    // well-known failure modes so the spill path stays robust:
    //   - IOException: filesystem doesn't support POSIX perms (FAT, some
    //     NFS mounts, 9p) — data still lands, just at the mount's mode.
    //   - UnauthorizedAccessException: caller doesn't own the path — rare
    //     (helper only writes to paths it just created), but safe to
    //     skip rather than block the spill.
    //   - PlatformNotSupportedException: belt-and-suspenders guard for
    //     callers that invoke this on Windows by mistake.
    private static void TrySetUnixFileMode(string path, UnixFileMode mode)
    {
        if (OperatingSystem.IsWindows())
            return;

        try
        {
            File.SetUnixFileMode(path, mode);
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine(
                $"[ripple] warn: could not set permissions on '{path}': {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.Error.WriteLine(
                $"[ripple] warn: not permitted to set permissions on '{path}': {ex.Message}");
        }
        catch (PlatformNotSupportedException)
        {
            // Unreachable in practice (guarded by OperatingSystem.IsWindows
            // above), but keeps the contract honest on exotic runtimes.
        }
    }
}
