using System.Text;
using System.Text.RegularExpressions;

namespace Ripple.Services;

/// <summary>
/// Detects REPL prompt boundaries by regex matching on the cleaned PTY
/// output stream. Used for adapters whose prompt strategy is `regex` —
/// shells/REPLs that don't speak OSC 633 (F# Interactive, ghci without
/// integration, ...) and instead surface command-completion state via a
/// visible prompt string like <c>&gt; </c> or <c>jshell&gt; </c>.
///
/// **CSI-aware.** OscParser strips OSC 633 sequences but leaves CSI
/// escapes (cursor movement, colors, screen wipes) intact in the cleaned
/// text. Adapter authors want to write regexes against the text the user
/// would see on the rendered terminal — not against the raw byte stream
/// with cursor positioning escapes interleaved between every token. So
/// the detector strips CSI internally before running the regex, builds
/// a stripped→original index map, and translates match positions back
/// to original coordinates before returning. The caller (ConsoleWorker)
/// gets offsets that line up with the original cleaned text it feeds
/// to the tracker, so synthetic events drop into the right place
/// without the caller having to know stripping happened.
///
/// The detector is intentionally stateless across the OscParser — both
/// run in parallel in ConsoleWorker.ReadOutputLoop, and the synthetic
/// PromptStart events it emits are merged with any real OSC events in
/// TextOffset order so the CommandTracker sees one coherent event
/// stream regardless of which strategy fired.
///
/// Buffering: a small tail of the most recent chunk (in original byte
/// form) is retained so a prompt pattern that lands across a chunk
/// boundary still matches. The buffer is capped to prevent pathological
/// growth.
/// </summary>
public sealed class RegexPromptDetector
{
    private readonly Regex _primary;
    private string _buffer = "";
    private const int MaxBufferLength = 2048;

    public RegexPromptDetector(string primaryPattern)
    {
        _primary = new Regex(primaryPattern, RegexOptions.Compiled | RegexOptions.Multiline);
    }

    /// <summary>
    /// Scan a chunk of cleaned output for prompt matches. Returns the
    /// list of chunk-local offsets (one per match) at which a virtual
    /// PromptStart event should fire. Offsets are into the
    /// <paramref name="chunk"/> passed in (in raw / original
    /// coordinates, including any CSI escapes), so the caller can
    /// merge them with OscParser events that already carry chunk-local
    /// TextOffset values.
    /// </summary>
    public List<int> Scan(string chunk)
    {
        var offsets = new List<int>();
        if (chunk.Length == 0) return offsets;

        var searchIn = _buffer + chunk;
        var bufferLen = _buffer.Length;

        // Strip CSI sequences from searchIn. `stripped` is what the
        // regex actually runs against; `map[i]` is the searchIn-relative
        // position of stripped[i], with map[stripped.Length] being the
        // searchIn-relative position right after the last consumed byte.
        var (stripped, map) = StripCsiWithMap(searchIn);

        int lastReportedOriginalEnd = 0;
        foreach (Match m in _primary.Matches(stripped))
        {
            var strippedEnd = m.Index + m.Length;
            // Translate stripped-coordinate end → original-coordinate
            // end. The map has stripped.Length+1 entries so end-of-match
            // at strippedEnd == stripped.Length resolves to the position
            // right after all consumed CSI tail bytes.
            var originalEnd = map[strippedEnd];

            // Matches that fall entirely inside the previous chunk's
            // trailing buffer were already reported on that scan — skip
            // them. Compare in original coordinates because that's where
            // the buffer boundary lives.
            if (originalEnd <= bufferLen) continue;

            offsets.Add(originalEnd - bufferLen);
            lastReportedOriginalEnd = originalEnd;
        }

        // Buffer carry-over: keep the original-coordinate tail past the
        // farthest reported match end and past the last newline so a
        // partial prompt landing on a chunk boundary is still available
        // on the next scan. Operating in original coordinates (rather
        // than stripped) means a partial CSI escape at end-of-chunk is
        // also retained — next scan re-strips with the full sequence
        // visible.
        var lastNewline = searchIn.LastIndexOf('\n');
        var newBufferStart = Math.Max(lastReportedOriginalEnd, lastNewline + 1);
        if (newBufferStart >= searchIn.Length)
        {
            _buffer = "";
        }
        else
        {
            var tail = searchIn[newBufferStart..];
            _buffer = tail.Length <= MaxBufferLength
                ? tail
                : tail[^MaxBufferLength..];
        }

        return offsets;
    }

    /// <summary>
    /// Strip CSI escape sequences from <paramref name="input"/> and
    /// build a position map. Returns:
    /// <list type="bullet">
    ///   <item><c>stripped</c> — input with most CSI removed; cursor
    ///     positioning to column 1 (<c>ESC [ N ; 1 H</c> and friends)
    ///     is REPLACED by a literal <c>\n</c> so the stripped text
    ///     keeps its visible line structure. Without this, REPLs like
    ///     fsi that use cursor positioning instead of CR/LF would
    ///     collapse multi-line output into one long line and break
    ///     <c>^</c> anchoring in adapter prompt regexes.</item>
    ///   <item><c>map</c> — array of length <c>stripped.Length + 1</c>;
    ///     <c>map[i]</c> is the input-relative position of the i-th
    ///     stripped character, and <c>map[stripped.Length]</c> is the
    ///     input-relative position right after the last byte that
    ///     contributed to the stripped string (or to the trailing
    ///     CSI we consumed). This makes <c>map[matchEnd]</c> always
    ///     valid, including for matches that end at the very end of
    ///     stripped. For substituted <c>\n</c> entries, the map points
    ///     at the start of the original CSI sequence.</item>
    /// </list>
    /// CSI grammar: <c>ESC [ &lt;params&gt; &lt;final-byte&gt;</c> where
    /// params are zero or more characters in 0x30–0x3F (digits,
    /// semicolon, ?) optionally followed by intermediate bytes
    /// 0x20–0x2F, terminated by a final byte 0x40–0x7E. We accept the
    /// common subset used by Windows Terminal / conhost: digits,
    /// semicolons, question marks, then a letter terminator.
    /// Incomplete CSI at end of input is left as-is (mapped through to
    /// stripped) so the caller's buffer carry-over picks it up on the
    /// next chunk.
    /// </summary>
    private static (string stripped, int[] map) StripCsiWithMap(string input)
    {
        var sb = new StringBuilder(input.Length);
        // Pre-size for the common case where most of the input is plain.
        var map = new List<int>(input.Length + 1);

        int i = 0;
        while (i < input.Length)
        {
            var c = input[i];
            // OSC (Operating System Command): `ESC ]` ... `BEL` or `ESC \`.
            // Window-title setters (`ESC ] 0 ; <title> BEL`) are the common
            // case — emitted by ConPTY after launching a child process to
            // reflect the child's executable path in the terminal title
            // bar. These sequences sit in the PTY output stream right next
            // to a prompt and, because they're NOT `ESC [` (CSI), the CSI
            // loop below would leave them intact, pushing real content off
            // column 1 and breaking `^` anchoring for the adapter's prompt
            // regex. Strip them entirely — ripple's visible-console title
            // is managed separately via ConsoleWorker's _desiredTitle path,
            // so dropping OSC from the regex detector's view doesn't lose
            // any user-facing behaviour.
            if (c == '\x1b' && i + 1 < input.Length && input[i + 1] == ']')
            {
                int j = i + 2;
                while (j < input.Length)
                {
                    // BEL (0x07) terminator — the xterm-style form.
                    if (input[j] == '\x07') { j++; break; }
                    // ST (ESC \) terminator — the strict ECMA-48 form.
                    if (input[j] == '\x1b' && j + 1 < input.Length && input[j + 1] == '\\')
                    {
                        j += 2;
                        break;
                    }
                    j++;
                }
                // Completed OR unterminated — in either case we advance
                // past whatever we consumed. Unterminated OSC at end of
                // chunk is rare enough (titles are short and usually
                // flushed atomically) that we don't need the CSI path's
                // partial-carry-over logic; if it ever bites, the
                // subsequent chunk will bring the terminator and the
                // next scan will re-strip cleanly because the detector's
                // outer buffer retains unconsumed tail text.
                i = j;
                continue;
            }
            if (c == '\x1b' && i + 1 < input.Length && input[i + 1] == '[')
            {
                // Walk to the CSI terminator (any letter A-Z / a-z).
                int j = i + 2;
                while (j < input.Length && !IsCsiTerminator(input[j])) j++;
                if (j < input.Length)
                {
                    // Complete CSI. If it's a cursor positioning command
                    // landing at column 1 (`ESC [ H`, `ESC [ N H`,
                    // `ESC [ N ; 1 H`, or the `f` variant), substitute a
                    // newline in the stripped text so subsequent regex
                    // matching sees the new line. The map entry for the
                    // newline points at the START of the CSI in input,
                    // so a match that ends right after the newline
                    // translates to "right after the CSI in input"
                    // via the next map entry / sentinel.
                    var terminator = input[j];
                    if ((terminator == 'H' || terminator == 'f')
                        && IsCursorMoveToCol1(input, i + 2, j))
                    {
                        map.Add(i);
                        sb.Append('\n');
                    }
                    else if (terminator == 'C')
                    {
                        // Cursor Forward (CUF): ESC [ N C moves the
                        // cursor right by N columns. When drawing over
                        // blank space — which prompts typically are —
                        // this is visually equivalent to inserting N
                        // literal spaces. fsi uses `\x1b[1C` to pad its
                        // error-recovery prompt (`\r\n>\x1b[1C` instead
                        // of `\r\n> \x1b[?25h`), which would otherwise
                        // strip to `>` and miss the `^> $` regex.
                        // Substituting CUF with spaces keeps the
                        // stripped text visually faithful and the
                        // adapter-author regex happy. Default N = 1
                        // when the parameter is empty.
                        int n = ParseCsiParam(input, i + 2, j, defaultValue: 1);
                        for (int k = 0; k < n; k++)
                        {
                            map.Add(i);
                            sb.Append(' ');
                        }
                    }
                    i = j + 1;
                    continue;
                }
                // Incomplete CSI at end-of-input — emit as plain so the
                // caller's buffer keeps it for the next scan to retry
                // with the missing terminator in place.
                while (i < input.Length)
                {
                    map.Add(i);
                    sb.Append(input[i]);
                    i++;
                }
                break;
            }
            map.Add(i);
            sb.Append(c);
            i++;
        }
        // Sentinel: position just past the last consumed byte, so
        // map[stripped.Length] resolves cleanly for end-of-match
        // translations.
        map.Add(i);
        return (sb.ToString(), map.ToArray());
    }

    private static bool IsCsiTerminator(char c) =>
        (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');

    /// <summary>
    /// Inspect the parameter bytes of a CSI cursor-position command
    /// (the <c>H</c> or <c>f</c> family) and return true if the
    /// command moves the cursor to column 1. Forms recognised:
    /// <c>ESC [ H</c> (home → row 1 col 1), <c>ESC [ N H</c> (row N
    /// col 1 — column defaults to 1), and <c>ESC [ N ; 1 H</c>
    /// (row N col 1 explicitly).
    /// </summary>
    private static bool IsCursorMoveToCol1(string input, int paramStart, int paramEnd)
    {
        if (paramEnd <= paramStart) return true; // ESC [ H — both default to 1
        var semicolonIdx = input.IndexOf(';', paramStart, paramEnd - paramStart);
        if (semicolonIdx < 0) return true;       // ESC [ N H — col defaults to 1
        // ESC [ N ; M H — second param is the column.
        var colStart = semicolonIdx + 1;
        if (colStart >= paramEnd) return true;   // ESC [ N ; H — empty col, defaults to 1
        var colSpan = input.AsSpan(colStart, paramEnd - colStart);
        return colSpan.SequenceEqual("1".AsSpan());
    }

    /// <summary>
    /// Parse a numeric parameter from a CSI sequence between
    /// <paramref name="paramStart"/> (inclusive) and
    /// <paramref name="paramEnd"/> (exclusive). Used for single-param
    /// CSI commands like <c>ESC [ N C</c> (cursor forward). Returns
    /// <paramref name="defaultValue"/> when the param is empty or
    /// cannot be parsed. Clamped to a sane range so a malicious
    /// terminal sending `ESC[999999999C` can't make the detector
    /// allocate gigabytes of spaces.
    /// </summary>
    private static int ParseCsiParam(string input, int paramStart, int paramEnd, int defaultValue)
    {
        if (paramEnd <= paramStart) return defaultValue;
        var span = input.AsSpan(paramStart, paramEnd - paramStart);
        if (!int.TryParse(span, out var n) || n <= 0) return defaultValue;
        // Prompt-relevant cursor moves are typically 1–10 columns. 256
        // is a generous cap that still bounds memory for pathological
        // inputs.
        return Math.Min(n, 256);
    }
}
