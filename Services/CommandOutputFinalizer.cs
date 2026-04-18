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
    // Non-SGR ANSI escape sequence pattern — same regex the old
    // CommandTracker.StripAnsi used. Strips cursor movement, erase,
    // and other control sequences, but preserves SGR (Select Graphic
    // Rendition, ending in 'm') so color information is passed
    // through to the AI for context (e.g. red errors, green success).
    // OSC sequences (window title etc.) are also stripped.
    //
    // Alternatives covered:
    //   \x1b[...letter       — CSI sequences (cursor, erase, SGR "m" preserved)
    //   \x1b]...(\x07|\x1b\) — OSC, non-greedy to the first BEL or ST so an
    //                          OSC never swallows characters past a prior
    //                          terminator (two separate BEL- vs. ST-terminated
    //                          branches would let the BEL branch eat through
    //                          an earlier ST-terminated OSC and the real
    //                          content between them).
    //   \x1b[()][0-9A-B]     — Character set designation (G0/G1)
    //   \x1b[=>]             — DECKPAM / DECKPNM keypad mode (PSReadLine emits
    //                          these around prompt redraws; leaving them through
    //                          makes the ESC byte invisible and the trailing
    //                          '=' or '>' look like a stray char at the start
    //                          or end of captured output).
    private static readonly Regex AnsiRegex = new(
        @"\x1b\[[0-9;?]*[a-ln-zA-Z]|\x1b\][\s\S]*?(?:\x07|\x1b\\)|\x1b[()][0-9A-B]|\x1b[=>]",
        RegexOptions.Compiled);

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
        long commandEnd)
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
            using var reader = capture.OpenSliceReader(commandStart, length);
            var sb = new StringBuilder(checked((int)Math.Min(length, int.MaxValue)));
            var buf = new char[8 * 1024];
            int n;
            while ((n = reader.Read(buf, 0, buf.Length)) > 0)
                sb.Append(buf, 0, n);
            raw = sb.ToString();
        }

        return CleanString(raw);
    }

    /// <summary>
    /// Clean a finalized command-window string. Separated from
    /// <see cref="Clean"/> so callers (echo stripping) that already
    /// hold a string don't pay a capture round-trip.
    /// </summary>
    public static string CleanString(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";

        var stripped = StripAnsi(raw);
        var lines = stripped.Split('\n');
        var cleaned = new List<string>(lines.Length);
        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');
            var trimmed = line.TrimEnd();
            // pwsh continuation prompt lines from multi-line input aren't
            // command output and look jarring in the result.
            if (trimmed == ">>" || trimmed.StartsWith(">> ")) continue;
            cleaned.Add(line);
        }

        return string.Join('\n', cleaned).Trim();
    }

    private static string StripAnsi(string text)
    {
        text = AnsiRegex.Replace(text, "");  // strip non-SGR sequences, keep colors
        text = text.Replace("\r\n", "\n");   // CRLF → LF
        text = text.Replace("\r", "");       // remove any remaining standalone CR
        return text;
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
