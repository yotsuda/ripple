using System.Text;
using System.Text.RegularExpressions;

namespace Ripple.Services;

/// <summary>
/// Slice-reader-driven cleaner that turns a raw
/// <see cref="CommandOutputCapture"/> window into the string the MCP
/// client sees. Moved out of the old <c>CommandTracker.CleanOutput</c>
/// to:
///   - operate on the capture's bounded slice reader instead of one
///     unbounded "_aiOutput" string, so arbitrarily large command
///     output never materializes as a single allocation here.
///   - run in the worker's finalize-once path, so inline
///     <c>execute_command</c> and deferred
///     <c>wait_for_completion</c> always go through the same code.
///
/// Not responsible for: truncation (that's
/// <see cref="OutputTruncationHelper"/>), echo stripping (that's
/// <see cref="EchoStripper"/>), or shell-specific post-prompt
/// settling (the worker handles that before calling
/// <see cref="Clean"/>).
/// </summary>
internal static class CommandOutputFinalizer
{
    /// <summary>
    /// Read the command window out of <paramref name="capture"/>, strip
    /// ANSI escapes (except SGR), normalize CRLF to LF, and drop pwsh
    /// continuation-prompt lines (">>", ">> ..."). The result is the
    /// cleaned finalized string that the worker hands to
    /// <see cref="OutputTruncationHelper"/> — at or under
    /// <see cref="CommandOutputCapture.MaxInlineSliceChars"/> the slice
    /// is read as one <c>string</c>, otherwise it streams through the
    /// capture's <see cref="System.IO.TextReader"/> so large captures
    /// never force a single big allocation here.
    /// </summary>
    public static string Clean(
        CommandOutputCapture capture,
        long commandStart,
        long commandEnd,
        VtLiteSnapshot? vtBaseline = null)
    {
        ArgumentNullException.ThrowIfNull(capture);

        if (commandEnd < commandStart) commandEnd = commandStart;
        long length = commandEnd - commandStart;
        if (length <= 0) return "";

        string raw;
        if (length <= CommandOutputCapture.MaxInlineSliceChars)
        {
            raw = capture.ReadSlice(commandStart, length);
        }
        else
        {
            // Stream the slice in page-sized chunks. The StringBuilder
            // still holds the whole cleaned result, but that's
            // intentional — this is the pre-truncation path; the
            // OutputTruncationHelper will immediately spill if the
            // cleaned stream is over threshold, so callers shouldn't
            // rely on us staying under MaxInlineSliceChars here.
            //
            // No initial capacity hint: `length` is the caller-provided
            // upper bound and only gets clamped inside OpenSliceReader,
            // so a mismatched commandEnd against a tiny capture would
            // otherwise pre-allocate up to int.MaxValue chars (~4 GB)
            // before the reader reports the real smaller size. Letting
            // the StringBuilder grow on demand costs a few reallocs on
            // the large-slice path and nothing at all when the slice is
            // bogus.
            using var reader = capture.OpenSliceReader(commandStart, length);
            var sb = new StringBuilder();
            var buf = new char[8 * 1024];
            int n;
            while ((n = reader.Read(buf, 0, buf.Length)) > 0)
                sb.Append(buf, 0, n);
            raw = sb.ToString();
        }

        return CleanString(raw, vtBaseline);
    }

    /// <summary>
    /// Clean a finalized command-window string. Separated from
    /// <see cref="Clean"/> so callers (echo stripping) that already
    /// hold a string don't pay a capture round-trip.
    ///
    /// Drives <see cref="CommandOutputRenderer"/> — a cell-based
    /// terminal emulator that handles cursor positioning, line erase /
    /// display erase, ECH / DCH / ICH / IL / DL, and alt-screen entry
    /// (vim / less / htop) as a placeholder. Replaces the legacy
    /// inline grid-less collapser. The renderer's
    /// <see cref="CommandOutputRenderer.Render"/> already trims trailing
    /// blank rows, drops pwsh <c>&gt;&gt;</c> continuation lines, and
    /// emits SGR (color) bytes verbatim attached to the cells they
    /// applied to.
    /// </summary>
    public static string CleanString(string raw, VtLiteSnapshot? vtBaseline = null)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        var renderer = new CommandOutputRenderer(vtBaseline);
        renderer.Feed(raw.AsSpan());
        var rendered = renderer.Render();
        // Strip any line that references a ripple-created tempfile
        // (ripple-exec-<pid>-<guid>.ps1/.cmd/.sh). These paths only appear
        // when the multi-line AI command delivery wraps the user's body
        // into a temp file and dot-sources it; PowerShell's ConciseView
        // error renderer prefixes the summary line with the path + line
        // number, exposing ripple's implementation detail. Single-line AI
        // commands never wrap so they never produce this leakage.
        // Stripping the whole offending line keeps the `Line | N | <source>`
        // block below, which carries the real diagnostic content.
        rendered = TempfileReferenceLine.Replace(rendered, "");
        // Strip leading SGR resets — they're no-ops at the MCP boundary
        // (the consumer hasn't applied any SGR before this output, so
        // resetting to default state changes nothing). Common source:
        // PowerShell's Write-Progress cleanup writes a reset right where
        // the bar lived; that reset attaches as the SGR prefix of the
        // first non-bar character that ends up at that column, producing
        // visible noise like `[mafter` for a bare `"after"` write.
        rendered = LeadingResetSgr.Replace(rendered, "");
        // Trim whitespace BEFORE deciding about the trailing reset —
        // the reset is appended after the last visible character, not
        // after stray newline padding from the renderer's row trimming.
        rendered = rendered.Trim();
        // Append `\e[0m` if the cumulative SGR state at end-of-output is
        // non-default. Otherwise the consumer's next output (their next
        // prompt, the AI's next tool result, etc.) inherits whatever
        // colour / weight / reverse-video the last cell left active —
        // observed when a command ends with `Write-Error` (red) or a
        // colourised diagnostic that doesn't emit its own trailing reset.
        if (NeedsTrailingReset(rendered))
            rendered += "\x1b[0m";
        return rendered;
    }

    // Windows: `C:\path\to\ripple-exec-<pid>-<guid>.ps1` (optionally `:N`).
    // POSIX:   `/path/to/ripple-exec-<pid>-<guid>.sh`     (optionally `:N`).
    // Match the whole line that contains such a reference (any prefix /
    // suffix on the same line — typically `<Cmdlet>: <path>:<N>`).
    // Multiline + non-greedy body keep the match scoped to a single line.
    private static readonly Regex TempfileReferenceLine = new(
        @"^[^\n]*(?:[A-Za-z]:[\\/]|/)[^\s:]*?ripple-exec-\d+-[0-9a-fA-F]+\.(?:ps1|cmd|sh)[^\n]*(?:\n|$)",
        RegexOptions.Multiline | RegexOptions.Compiled);

    // SGR reset variants: `\e[m` (omitted parameter), `\e[0m`,
    // `\e[0;0m`, `\e[00m`, `\e[0;0;0m` — anything whose parameters
    // are empty or all zeros means reset-all-attributes. The leading
    // strip matches one or more contiguous resets.
    private static readonly Regex LeadingResetSgr = new(
        @"^(?:\x1b\[(?:0?(?:;0?)*)m)+",
        RegexOptions.Compiled);

    // Match every `\e[<params>m` SGR sequence so NeedsTrailingReset can
    // inspect the LAST one. SGR is `CSI <params> m`; params are
    // semicolon-separated decimal numbers (or empty).
    private static readonly Regex AnySgr = new(
        @"\x1b\[([\d;]*)m",
        RegexOptions.Compiled);

    // True when the parameter blob represents a reset-all sequence —
    // empty, or all components are zero (or empty).
    private static bool IsResetSgrParam(string param)
    {
        if (string.IsNullOrEmpty(param)) return true;
        foreach (var part in param.Split(';'))
        {
            if (string.IsNullOrEmpty(part)) continue;
            if (!int.TryParse(part, out var n) || n != 0) return false;
        }
        return true;
    }

    // True when the rendered output's cumulative SGR state at end is
    // non-default — i.e. the LAST SGR sequence in the output is a setter
    // rather than a reset. Trim trailing whitespace first because
    // newlines don't change SGR state.
    private static bool NeedsTrailingReset(string text)
    {
        var trimmed = text.TrimEnd();
        Match? last = null;
        foreach (Match m in AnySgr.Matches(trimmed))
            last = m;
        if (last is null) return false;
        return !IsResetSgrParam(last.Groups[1].Value);
    }
}

/// <summary>
/// Deterministic echo stripping for adapters that declare
/// <c>input_echo_strategy: deterministic_byte_match</c> (cmd, python,
/// any REPL without a stdlib pre-input hook that can emit OSC C). The
/// adapter's PTY stream interleaves the worker's PTY-write bytes with
/// the command's real output; there is no marker separating the two,
/// so the finalizer strips exactly the payload bytes the worker wrote
/// (minus the Enter keystroke) from the head of the cleaned window.
///
/// Moved out of <c>ConsoleWorker.StripCmdInputEcho</c> unchanged in
/// behaviour — the matching rules (skip CR/LF injected by ConPTY's
/// soft-wrap, fail closed when bytes diverge) are preserved verbatim.
/// </summary>
internal static class EchoStripper
{
    /// <summary>
    /// Strip the PTY payload (with line-ending removed) off the head
    /// of <paramref name="output"/>. Returns the original string
    /// unchanged when the head does not match the expected echo,
    /// matching the old tracker behaviour of failing open rather
    /// than silently mangling output.
    /// </summary>
    public static string Strip(string output, string ptyPayload, string lineEnding)
    {
        if (string.IsNullOrEmpty(output) || string.IsNullOrEmpty(ptyPayload))
            return output;

        var sentInput = ptyPayload;
        if (!string.IsNullOrEmpty(lineEnding) && sentInput.EndsWith(lineEnding))
            sentInput = sentInput[..^lineEnding.Length];

        if (sentInput.Length == 0) return output;

        int oi = 0;
        int ci = 0;
        while (ci < sentInput.Length && oi < output.Length)
        {
            var oc = output[oi];
            // ConPTY wraps long input echo at terminal width by injecting
            // CR/LF into the output stream — those bytes were never in the
            // typed command, so skip them while continuing to match.
            if (oc is '\r' or '\n')
            {
                oi++;
                continue;
            }

            if (oc != sentInput[ci])
                return output;

            oi++;
            ci++;
        }

        if (ci < sentInput.Length)
            return output;

        while (oi < output.Length && output[oi] is '\r' or '\n')
            oi++;

        return output[oi..];
    }
}
