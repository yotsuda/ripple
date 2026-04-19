using System.Text;

namespace Ripple.Services;

/// <summary>
/// Light VT-100 / ECMA-48 interpreter — a fixed-viewport terminal emulator
/// strong enough to track cursor + screen state across PTY output.
///
/// Two consumption modes:
///   • <b>One-shot</b> — <see cref="VtLite"/> feeds a complete string and
///     returns a final-state snapshot. Used by <c>peek_console</c> /
///     <c>GetRecentOutputSnapshot</c>.
///   • <b>Streaming</b> — call <see cref="Feed"/> repeatedly with PTY
///     chunks. A pending-escape buffer stitches CSI / OSC sequences
///     that straddle chunk boundaries so cursor state stays correct
///     when an escape is split across two PTY reads. Used by the Unix
///     read loop to maintain an authoritative cursor that the DSR
///     (\x1b[6n) reply path can read from.
///
/// Implemented VT-medium subset:
///   - Soft line wrap at the right margin
///   - LF scrolling within the scroll region (DECSTBM, \e[top;bottom r)
///   - Primary + alternate screen buffers (\e[?1049h/l, ?1047, ?47)
///   - Save / restore cursor (\e7 / \e8, \e[s / \e[u)
///   - CUP / HVP / CUU / CUD / CUF / CUB / CHA / VPA
///   - EL / ED (erase in line / display)
///   - SGR / DSR / DA finals are no-ops for cursor math (correct: SGR
///     never advances the cursor; DSR / DA are queries, not state).
/// </summary>
public sealed class VtLiteState
{
    public readonly int ViewRows;
    public readonly int ViewCols;

    // Primary and alternate screen buffers.
    private readonly char[][] _primary;
    private readonly char[][] _alternate;
    private bool _useAlternate;

    // Per-row "this row was started by auto-wrap from the previous row"
    // flag, parallel to the grid arrays. CommandOutputRenderer uses this
    // at extraction time to re-join soft-wrapped logical lines so an AI
    // consumer never sees a single `git log --oneline` entry split across
    // multiple short rows just because the PTY was narrow. Set by
    // WriteChar's auto-wrap path; cleared when an explicit cursor move
    // (LineFeed, CR is irrelevant since CR doesn't change row,
    // ReverseIndex, CUP, EL/ED) overwrites the row content. Scrolling
    // shifts flags in lockstep with grid rows.
    private readonly bool[] _primarySoftWrap;
    private readonly bool[] _alternateSoftWrap;

    // Cursor position per buffer.
    private int _pRow, _pCol;
    private int _aRow, _aCol;

    // Saved cursor (DEC save/restore).
    private int _savedRow, _savedCol;

    // Scroll region (0-indexed, inclusive on both ends).
    private int _scrollTop;
    private int _scrollBottom;

    // Active SGR (color/attribute) state, accumulated as a string of the
    // ESC[<params>m sequences seen since the most recent full reset
    // (\e[0m, \e[m, or \e[m with no params). Snapshot()'d into the
    // baseline so a CommandOutputRenderer initialised from a mid-session
    // snapshot starts with the right SGR carry-over (`echo $RED hello`
    // patterns where `$RED` was set by an earlier command, not the
    // current one). No attribute parsing — for our purposes raw bytes
    // are the right unit since the renderer just emits them back.
    private readonly StringBuilder _activeSgr = new();

    // Streaming chunk-boundary buffer for incomplete escape sequences.
    // Bounded so a malformed unterminated OSC payload cannot grow without
    // limit; on overflow the tail is dropped and the cursor stays sane.
    private const int PendingMaxChars = 16 * 1024;
    private readonly StringBuilder _pending = new();

    private char[][] Grid => _useAlternate ? _alternate : _primary;
    private bool[] SoftWrap => _useAlternate ? _alternateSoftWrap : _primarySoftWrap;
    public int Row
    {
        get => _useAlternate ? _aRow : _pRow;
        set { if (_useAlternate) _aRow = value; else _pRow = value; }
    }
    public int Col
    {
        get => _useAlternate ? _aCol : _pCol;
        set { if (_useAlternate) _aCol = value; else _pCol = value; }
    }

    /// <summary>True while the alternate screen buffer is active (\e[?1049h).</summary>
    public bool IsAlternateBuffer => _useAlternate;

    /// <summary>
    /// True if the row at <paramref name="row"/> in the active buffer was
    /// started by auto-wrap from the previous row (i.e. it's logically a
    /// continuation, not an independent line).
    /// </summary>
    public bool IsRowSoftWrapped(int row)
    {
        if (row < 0 || row >= ViewRows) return false;
        return SoftWrap[row];
    }

    /// <summary>
    /// Active SGR sequence — a concatenation of the ESC[<paramref name="..."/>m
    /// bytes seen since the most recent reset. Empty when defaults are
    /// in effect.
    /// </summary>
    public string ActiveSgr => _activeSgr.ToString();

    public VtLiteState(int rows, int cols)
    {
        ViewRows = Math.Max(1, rows);
        ViewCols = Math.Max(1, cols);
        _primary = CreateGrid(ViewRows, ViewCols);
        _alternate = CreateGrid(ViewRows, ViewCols);
        _primarySoftWrap = new bool[ViewRows];
        _alternateSoftWrap = new bool[ViewRows];
        _scrollTop = 0;
        _scrollBottom = ViewRows - 1;
    }

    private static char[][] CreateGrid(int rows, int cols)
    {
        var grid = new char[rows][];
        for (int i = 0; i < rows; i++)
        {
            grid[i] = new char[cols];
            Array.Fill(grid[i], ' ');
        }
        return grid;
    }

    public void WriteChar(char c)
    {
        // Auto-wrap at right margin.
        if (Col >= ViewCols)
        {
            Col = 0;
            if (Row == _scrollBottom)
                ScrollUp(1);
            else if (Row < ViewRows - 1)
                Row++;
            // Mark target row as soft-wrap continuation. Done after the
            // row advance so we mark the row we're about to write into,
            // not the one we left.
            SoftWrap[Row] = true;
        }
        Grid[Row][Col] = c;
        Col++;
    }

    public void LineFeed()
    {
        if (Row == _scrollBottom)
            ScrollUp(1);
        else if (Row < ViewRows - 1)
            Row++;
        // Hard newline — destination row is NOT a wrap continuation.
        SoftWrap[Row] = false;
    }

    public void ReverseIndex()
    {
        if (Row == _scrollTop)
            ScrollDown(1);
        else if (Row > 0)
            Row--;
    }

    private void ScrollUp(int n)
    {
        var sw = SoftWrap;
        for (int i = 0; i < n; i++)
        {
            var top = Grid[_scrollTop];
            bool topSw = sw[_scrollTop];
            for (int r = _scrollTop; r < _scrollBottom; r++)
            {
                Grid[r] = Grid[r + 1];
                sw[r] = sw[r + 1];
            }
            Array.Fill(top, ' ');
            Grid[_scrollBottom] = top;
            sw[_scrollBottom] = false;
            _ = topSw; // discarded — the scrolled-out row's flag dies with it
        }
    }

    private void ScrollDown(int n)
    {
        var sw = SoftWrap;
        for (int i = 0; i < n; i++)
        {
            var bot = Grid[_scrollBottom];
            for (int r = _scrollBottom; r > _scrollTop; r--)
            {
                Grid[r] = Grid[r - 1];
                sw[r] = sw[r - 1];
            }
            Array.Fill(bot, ' ');
            Grid[_scrollTop] = bot;
            sw[_scrollTop] = false;
        }
    }

    public void CarriageReturn() { Col = 0; }
    public void Backspace() { if (Col > 0) Col--; }
    public void Tab() { Col = Math.Min(((Col / 8) + 1) * 8, ViewCols - 1); }

    public void CursorUp(int n) { Row = Math.Max(0, Row - Math.Max(1, n)); }
    public void CursorDown(int n) { Row = Math.Min(ViewRows - 1, Row + Math.Max(1, n)); }
    public void CursorForward(int n) { Col = Math.Min(ViewCols - 1, Col + Math.Max(1, n)); }
    public void CursorBack(int n) { Col = Math.Max(0, Col - Math.Max(1, n)); }
    public void CursorCol(int c1) { Col = Math.Clamp(c1 - 1, 0, ViewCols - 1); }

    public void CursorPos(int r1, int c1)
    {
        Row = Math.Clamp(r1 - 1, 0, ViewRows - 1);
        Col = Math.Clamp(c1 - 1, 0, ViewCols - 1);
    }

    public void SaveCursor() { _savedRow = Row; _savedCol = Col; }
    public void RestoreCursor() { Row = _savedRow; Col = _savedCol; }

    public void SetScrollRegion(int top1, int bottom1)
    {
        _scrollTop = Math.Clamp(top1 - 1, 0, ViewRows - 1);
        _scrollBottom = Math.Clamp(bottom1 - 1, 0, ViewRows - 1);
        if (_scrollTop > _scrollBottom)
        {
            _scrollTop = 0;
            _scrollBottom = ViewRows - 1;
        }
        // DECSTBM resets cursor to home.
        Row = 0;
        Col = 0;
    }

    public void SwitchToAlternate()
    {
        if (_useAlternate) return;
        SaveCursor();
        _useAlternate = true;
        ClearGrid(_alternate);
        Array.Clear(_alternateSoftWrap);
        _aRow = 0;
        _aCol = 0;
        _scrollTop = 0;
        _scrollBottom = ViewRows - 1;
    }

    public void SwitchToPrimary()
    {
        if (!_useAlternate) return;
        _useAlternate = false;
        _scrollTop = 0;
        _scrollBottom = ViewRows - 1;
        RestoreCursor();
    }

    private static void ClearGrid(char[][] grid)
    {
        for (int r = 0; r < grid.Length; r++) Array.Fill(grid[r], ' ');
    }

    public void EraseLine(int mode)
    {
        var row = Grid[Row];
        var sw = SoftWrap;
        if (mode == 0) // cursor to end
        {
            for (int c = Col; c < ViewCols; c++) row[c] = ' ';
        }
        else if (mode == 1) // start to cursor
        {
            for (int c = 0; c <= Math.Min(Col, ViewCols - 1); c++) row[c] = ' ';
        }
        else if (mode == 2) // whole line
        {
            Array.Fill(row, ' ');
            sw[Row] = false; // wholly cleared row is no longer a wrap continuation
        }
    }

    public void EraseDisplay(int mode)
    {
        var sw = SoftWrap;
        if (mode == 0)
        {
            EraseLine(0);
            for (int r = Row + 1; r < ViewRows; r++)
            {
                Array.Fill(Grid[r], ' ');
                sw[r] = false;
            }
        }
        else if (mode == 1)
        {
            for (int r = 0; r < Row; r++)
            {
                Array.Fill(Grid[r], ' ');
                sw[r] = false;
            }
            EraseLine(1);
        }
        else if (mode == 2)
        {
            ClearGrid(Grid);
            Array.Clear(sw);
            Row = 0;
            Col = 0;
        }
    }

    /// <summary>
    /// ECH — erase N characters at cursor without moving cursor. Heavily
    /// used by readline / PSReadLine to clear part of the input line
    /// during in-line editing without a full \r + redraw cycle.
    /// </summary>
    public void EraseChars(int n)
    {
        var row = Grid[Row];
        int end = Math.Min(Col + Math.Max(1, n), ViewCols);
        for (int c = Col; c < end; c++) row[c] = ' ';
    }

    /// <summary>
    /// DCH — delete N characters at cursor; trailing chars on the line
    /// shift left. Cursor unchanged. Right-margin slots fill with blanks.
    /// Emitted by readline when the user backspaces / deletes mid-line.
    /// </summary>
    public void DeleteChars(int n)
    {
        var row = Grid[Row];
        n = Math.Max(1, Math.Min(n, ViewCols - Col));
        for (int c = Col; c < ViewCols - n; c++) row[c] = row[c + n];
        for (int c = ViewCols - n; c < ViewCols; c++) row[c] = ' ';
    }

    /// <summary>
    /// ICH — insert N blank characters at cursor; existing chars shift
    /// right and any beyond the right margin are lost. Cursor unchanged.
    /// Emitted by readline when the user types in the middle of a line.
    /// </summary>
    public void InsertChars(int n)
    {
        var row = Grid[Row];
        n = Math.Max(1, Math.Min(n, ViewCols - Col));
        for (int c = ViewCols - 1; c >= Col + n; c--) row[c] = row[c - n];
        for (int c = Col; c < Col + n; c++) row[c] = ' ';
    }

    /// <summary>
    /// IL — insert N blank lines at and below the cursor row, within the
    /// scroll region. Lines below shift down; lines pushed past
    /// scrollBottom are lost. Cursor unchanged. Emitted by full-screen
    /// editors and some readline variants on multi-line input.
    /// </summary>
    public void InsertLines(int n)
    {
        if (Row < _scrollTop || Row > _scrollBottom) return;
        n = Math.Max(1, Math.Min(n, _scrollBottom - Row + 1));
        for (int i = 0; i < n; i++)
        {
            var bot = Grid[_scrollBottom];
            for (int r = _scrollBottom; r > Row; r--) Grid[r] = Grid[r - 1];
            Array.Fill(bot, ' ');
            Grid[Row] = bot;
        }
    }

    /// <summary>
    /// DL — delete N lines at and below the cursor row, within the
    /// scroll region. Lines below shift up; new blank lines fill from
    /// the bottom. Cursor unchanged.
    /// </summary>
    public void DeleteLines(int n)
    {
        if (Row < _scrollTop || Row > _scrollBottom) return;
        n = Math.Max(1, Math.Min(n, _scrollBottom - Row + 1));
        for (int i = 0; i < n; i++)
        {
            var top = Grid[Row];
            for (int r = Row; r < _scrollBottom; r++) Grid[r] = Grid[r + 1];
            Array.Fill(top, ' ');
            Grid[_scrollBottom] = top;
        }
    }

    /// <summary>
    /// Streaming feed entry — call repeatedly with successive PTY chunks.
    /// Pending incomplete escape sequences are buffered internally and
    /// stitched to the next call so a CSI / OSC split across two PTY
    /// reads still produces correct cursor state. Safe to call with an
    /// empty span as a no-op.
    ///
    /// Hot path (no pending escape held) processes the span directly —
    /// no per-chunk string allocation. Cold path (pending held) merges
    /// pending + input into a rented char[] from ArrayPool or a
    /// stack-allocated buffer for short sequences, to avoid GC pressure
    /// on the read loop.
    /// </summary>
    public void Feed(ReadOnlySpan<char> input)
    {
        if (_pending.Length > 0)
        {
            int combinedLen = _pending.Length + input.Length;
            // Stackalloc / rent branches are kept separate so the stack-
            // allocated Span stays in its declaration scope — C#'s ref-
            // safety rules reject hoisting it out past the branch.
            if (combinedLen <= 512)
            {
                Span<char> stackBuf = stackalloc char[512];
                Span<char> combined = stackBuf.Slice(0, combinedLen);
                _pending.CopyTo(0, combined, _pending.Length);
                input.CopyTo(combined.Slice(_pending.Length));
                _pending.Clear();
                FeedSpan(combined);
            }
            else
            {
                var rented = System.Buffers.ArrayPool<char>.Shared.Rent(combinedLen);
                try
                {
                    Span<char> combined = rented.AsSpan(0, combinedLen);
                    _pending.CopyTo(0, combined, _pending.Length);
                    input.CopyTo(combined.Slice(_pending.Length));
                    _pending.Clear();
                    FeedSpan(combined);
                }
                finally
                {
                    System.Buffers.ArrayPool<char>.Shared.Return(rented);
                }
            }
        }
        else if (input.Length == 0)
        {
            return;
        }
        else
        {
            FeedSpan(input);
        }
    }

    /// <summary>
    /// Internal driver — processes a complete ReadOnlySpan directly
    /// (no pending merge, no allocation). On an incomplete escape, the
    /// tail is appended to <see cref="_pending"/> for the next <see
    /// cref="Feed"/> call.
    /// </summary>
    private void FeedSpan(ReadOnlySpan<char> text)
    {
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '\n') { LineFeed(); i++; }
            else if (c == '\r') { CarriageReturn(); i++; }
            else if (c == '\b') { Backspace(); i++; }
            else if (c == '\t') { Tab(); i++; }
            else if (c == '\x1b')
            {
                int next = ParseEscape(text, i, this);
                if (next < 0)
                {
                    int tailLen = text.Length - i;
                    if (tailLen <= PendingMaxChars)
                        _pending.Append(text.Slice(i, tailLen));
                    // else: runaway sequence, drop and keep cursor sane.
                    return;
                }
                i = next;
            }
            else if (c >= ' ') { WriteChar(c); i++; }
            else { i++; /* drop other C0 */ }
        }
    }

    /// <summary>
    /// Render the current grid as a final-state snapshot — trailing blank
    /// rows and trailing spaces per row are trimmed. Used by peek_console.
    /// Cursor state is unchanged.
    /// </summary>
    public string Render()
    {
        // Find last non-blank row.
        int lastNonBlank = -1;
        for (int r = ViewRows - 1; r >= 0; r--)
        {
            for (int c = 0; c < ViewCols; c++)
            {
                if (Grid[r][c] != ' ') { lastNonBlank = r; goto done; }
            }
        }
        done:
        if (lastNonBlank < 0) return "";

        var sb = new StringBuilder();
        for (int r = 0; r <= lastNonBlank; r++)
        {
            if (r > 0) sb.Append('\n');
            int end = ViewCols - 1;
            while (end >= 0 && Grid[r][end] == ' ') end--;
            if (end >= 0) sb.Append(new string(Grid[r], 0, end + 1));
        }
        return sb.ToString();
    }

    /// <summary>
    /// One-shot helper — feed a complete string and return the final
    /// snapshot. Equivalent to:
    ///   <c>var s = new VtLiteState(rows, cols); s.Feed(input); return s.Render();</c>
    /// Any unterminated trailing escape in <paramref name="input"/> is
    /// dropped (the streaming pending-buffer never gets flushed in a
    /// one-shot call), preserving the prior <c>VtLite()</c> behavior.
    /// </summary>
    public static string VtLite(string input, int viewRows = 30, int viewCols = 120)
    {
        var state = new VtLiteState(viewRows, viewCols);
        state.Feed(input.AsSpan());
        return state.Render();
    }

    /// <summary>
    /// Parse an ESC sequence starting at <paramref name="start"/>
    /// (where input[start] == 0x1b), mutate <paramref name="state"/>,
    /// and return the index of the byte immediately after the sequence.
    /// Returns -1 if the sequence ran past end-of-input mid-parse: the
    /// streaming caller buffers the tail for the next feed; the one-shot
    /// caller treats -1 as "drop the rest".
    /// </summary>
    private static int ParseEscape(ReadOnlySpan<char> input, int start, VtLiteState state)
    {
        int i = start + 1;
        if (i >= input.Length) return -1; // bare ESC at boundary

        char next = input[i];
        if (next == '[')
        {
            // CSI — \e[<param bytes><intermediate><final>
            int paramStart = i + 1;
            int j = paramStart;
            while (j < input.Length && input[j] >= 0x30 && input[j] <= 0x3f) j++;
            var paramsSpan = j > paramStart
                ? input.Slice(paramStart, j - paramStart)
                : ReadOnlySpan<char>.Empty;
            while (j < input.Length && input[j] >= 0x20 && input[j] <= 0x2f) j++;
            if (j >= input.Length) return -1; // CSI without final byte
            char final = input[j];
            ApplyCsi(paramsSpan, final, state);
            return j + 1;
        }
        if (next == ']')
        {
            // OSC — \e]...(BEL or ST). Drop entire sequence.
            int j = i + 1;
            bool terminated = false;
            while (j < input.Length)
            {
                if (input[j] == '\x07') { j++; terminated = true; break; }
                if (input[j] == '\x1b')
                {
                    if (j + 1 >= input.Length) return -1; // ESC at boundary
                    if (input[j + 1] == '\\') { j += 2; terminated = true; break; }
                    j += 2; // ESC followed by something else mid-OSC — skip both
                    continue;
                }
                j++;
            }
            if (!terminated) return -1; // ran out without BEL / ST
            return j;
        }
        if (next == '(' || next == ')')
        {
            // Character set selection — \e(<char>
            if (i + 1 >= input.Length) return -1;
            return i + 2;
        }
        if (next == '7') { state.SaveCursor(); return i + 1; }
        if (next == '8') { state.RestoreCursor(); return i + 1; }
        if (next == 'M') { state.ReverseIndex(); return i + 1; }
        // Other single-char ESC — skip the follower.
        return i + 1;
    }

    /// <summary>
    /// Extract the N-th semicolon-separated numeric parameter from a CSI
    /// params span, returning <paramref name="def"/> for missing / empty
    /// slots or parse failures. Avoids the string[] allocation of
    /// paramsStr.Split(';') on the hot path.
    /// </summary>
    private static int GetParam(ReadOnlySpan<char> paramsSpan, int idx, int def)
    {
        int count = 0;
        int start = 0;
        for (int k = 0; k <= paramsSpan.Length; k++)
        {
            if (k == paramsSpan.Length || paramsSpan[k] == ';')
            {
                if (count == idx)
                {
                    int len = k - start;
                    if (len == 0) return def;
                    return int.TryParse(paramsSpan.Slice(start, len), out var n) ? n : def;
                }
                count++;
                start = k + 1;
            }
        }
        return def;
    }

    private static void ApplyCsi(ReadOnlySpan<char> paramsSpan, char final, VtLiteState state)
    {
        // Private-mode sequences (DEC — \e[?...h / \e[?...l).
        if (paramsSpan.Length > 0 && paramsSpan[0] == '?')
        {
            // Only handle alternate screen buffer toggle.
            var modeSpan = paramsSpan.Slice(1);
            if (modeSpan.SequenceEqual("1049".AsSpan())
                || modeSpan.SequenceEqual("1047".AsSpan())
                || modeSpan.SequenceEqual("47".AsSpan()))
            {
                if (final == 'h') state.SwitchToAlternate();
                else if (final == 'l') state.SwitchToPrimary();
            }
            return;
        }

        switch (final)
        {
            case 'A': state.CursorUp(GetParam(paramsSpan, 0, 1)); break;         // CUU
            case 'B': state.CursorDown(GetParam(paramsSpan, 0, 1)); break;       // CUD
            case 'C': state.CursorForward(GetParam(paramsSpan, 0, 1)); break;    // CUF
            case 'D': state.CursorBack(GetParam(paramsSpan, 0, 1)); break;       // CUB
            case 'E': state.CursorDown(GetParam(paramsSpan, 0, 1)); state.Col = 0; break; // CNL
            case 'F': state.CursorUp(GetParam(paramsSpan, 0, 1)); state.Col = 0; break;   // CPL
            case 'G': state.CursorCol(GetParam(paramsSpan, 0, 1)); break;        // CHA
            case 'H':                                                             // CUP
            case 'f':                                                             // HVP
                state.CursorPos(GetParam(paramsSpan, 0, 1), GetParam(paramsSpan, 1, 1));
                break;
            case 'K': state.EraseLine(GetParam(paramsSpan, 0, 0)); break;        // EL
            case 'J': state.EraseDisplay(GetParam(paramsSpan, 0, 0)); break;     // ED
            case 'X': state.EraseChars(GetParam(paramsSpan, 0, 1)); break;       // ECH
            case 'P': state.DeleteChars(GetParam(paramsSpan, 0, 1)); break;      // DCH
            case '@': state.InsertChars(GetParam(paramsSpan, 0, 1)); break;      // ICH
            case 'L': state.InsertLines(GetParam(paramsSpan, 0, 1)); break;      // IL
            case 'M': state.DeleteLines(GetParam(paramsSpan, 0, 1)); break;      // DL
            case 'd': state.Row = Math.Clamp(GetParam(paramsSpan, 0, 1) - 1, 0, state.ViewRows - 1); break; // VPA
            case 't':
                // DEC / xterm window manipulation — e.g. \e[8;<h>;<w>t to
                // resize the text area. ConPTY emits this as the prelude
                // to a full-screen refresh: after the `t` sequence it
                // repaints the entire viewport starting from \e[H. Treat
                // it as a full clear so our grid stays in sync with
                // ConPTY's viewport and doesn't carry stale content
                // (PSReadLine prediction artifacts, prior command
                // fragments) forward from before the refresh.
                state.EraseDisplay(2);
                break;
            case 's': state.SaveCursor(); break;
            case 'u': state.RestoreCursor(); break;
            case 'r': // DECSTBM — scroll region
                state.SetScrollRegion(GetParam(paramsSpan, 0, 1), GetParam(paramsSpan, 1, state.ViewRows));
                break;
            case 'm': // SGR — colors/attrs; tracked for snapshot baseline
                state.RecordSgr(paramsSpan);
                break;
            case 'n': // device status report
            case 'c': // device attributes
                break;
            default:
                // Unknown final byte — drop silently.
                break;
        }
    }

    /// <summary>
    /// Update the active SGR carry-over. <paramref name="paramsSpan"/> is
    /// the SGR sequence's parameter list (the bytes between <c>\e[</c>
    /// and the trailing <c>m</c>).
    ///
    /// Reset detection: an empty parameter list (<c>\e[m</c>), a single
    /// "0" (<c>\e[0m</c>), or a leading "0;" within a compound sequence
    /// (<c>\e[0;1;31m</c> = reset then bold-red) clears the carry-over.
    /// Non-reset sequences are appended verbatim. The accumulated string
    /// is what <see cref="Snapshot"/> ships to the renderer so a command
    /// running with pre-existing color state (typed inside a
    /// <c>$RED ... $RESET</c> region) starts rendering with the right
    /// SGR prefix on its first cell.
    /// </summary>
    public void RecordSgr(ReadOnlySpan<char> paramsSpan)
    {
        // \e[m  → reset.
        if (paramsSpan.IsEmpty)
        {
            _activeSgr.Clear();
            return;
        }

        // Leading "0" with no other token, or "0" followed by ";", is a
        // reset that may be followed by additional non-reset attrs to
        // re-establish.
        bool startsWithReset = paramsSpan[0] == '0'
            && (paramsSpan.Length == 1 || paramsSpan[1] == ';');
        if (startsWithReset)
        {
            _activeSgr.Clear();
            if (paramsSpan.Length == 1) return;
            // Skip the leading "0;" — the remainder is the post-reset state.
            var rest = paramsSpan.Slice(2);
            if (rest.IsEmpty) return;
            _activeSgr.Append("\x1b[").Append(rest).Append('m');
            return;
        }

        _activeSgr.Append("\x1b[").Append(paramsSpan).Append('m');
    }

    /// <summary>
    /// Capture a deep-copy snapshot of the current screen state. The
    /// snapshot is independent of further mutations on this
    /// <see cref="VtLiteState"/> — feed-side bytes and snapshot-side
    /// reads can interleave safely.
    ///
    /// Used by the worker's command-finalize path: at OSC C the worker
    /// snapshots the live VtLiteState so <see cref="CommandOutputRenderer"/>
    /// can be initialised with the screen state that was visible when
    /// the command started running. This makes ConPTY's post-alt-screen
    /// repaint bursts (which target viewport cells with their pre-existing
    /// values) idempotent overwrites instead of fresh content the
    /// renderer has to invent context for.
    /// </summary>
    public VtLiteSnapshot Snapshot()
    {
        return new VtLiteSnapshot(
            ViewRows,
            ViewCols,
            DeepCopyGrid(_primary),
            (bool[])_primarySoftWrap.Clone(),
            DeepCopyGrid(_alternate),
            (bool[])_alternateSoftWrap.Clone(),
            _useAlternate,
            _pRow, _pCol,
            _aRow, _aCol,
            _savedRow, _savedCol,
            _scrollTop, _scrollBottom,
            _activeSgr.ToString());
    }

    private static char[][] DeepCopyGrid(char[][] src)
    {
        var dst = new char[src.Length][];
        for (int r = 0; r < src.Length; r++) dst[r] = (char[])src[r].Clone();
        return dst;
    }
}

/// <summary>
/// Immutable, deep-copy snapshot of a <see cref="VtLiteState"/> screen.
/// Owned by <see cref="CommandOutputRenderer"/> as its initial state
/// baseline so screen-redraw bursts emitted by ConPTY after alt-screen
/// exit (or as part of any other refresh sequence) can be absorbed
/// without corrupting the renderer's row list — every cell ConPTY
/// repaints already has its expected value here.
///
/// Field naming mirrors <see cref="VtLiteState"/>'s internal field names
/// so the renderer can map state 1:1 without translation.
/// </summary>
public sealed class VtLiteSnapshot
{
    public int Rows { get; }
    public int Cols { get; }
    public char[][] PrimaryGrid { get; }
    public bool[] PrimarySoftWrap { get; }
    public char[][] AlternateGrid { get; }
    public bool[] AlternateSoftWrap { get; }
    public bool UseAlternate { get; }
    public int PRow { get; }
    public int PCol { get; }
    public int ARow { get; }
    public int ACol { get; }
    public int SavedRow { get; }
    public int SavedCol { get; }
    public int ScrollTop { get; }
    public int ScrollBottom { get; }
    public string ActiveSgr { get; }

    public VtLiteSnapshot(
        int rows, int cols,
        char[][] primaryGrid, bool[] primarySoftWrap,
        char[][] alternateGrid, bool[] alternateSoftWrap,
        bool useAlternate,
        int pRow, int pCol, int aRow, int aCol,
        int savedRow, int savedCol,
        int scrollTop, int scrollBottom,
        string activeSgr)
    {
        Rows = rows;
        Cols = cols;
        PrimaryGrid = primaryGrid;
        PrimarySoftWrap = primarySoftWrap;
        AlternateGrid = alternateGrid;
        AlternateSoftWrap = alternateSoftWrap;
        UseAlternate = useAlternate;
        PRow = pRow; PCol = pCol;
        ARow = aRow; ACol = aCol;
        SavedRow = savedRow; SavedCol = savedCol;
        ScrollTop = scrollTop; ScrollBottom = scrollBottom;
        ActiveSgr = activeSgr ?? "";
    }

    /// <summary>Active grid at snapshot time (alt or primary).</summary>
    public char[][] ActiveGrid => UseAlternate ? AlternateGrid : PrimaryGrid;

    /// <summary>Active soft-wrap flags at snapshot time.</summary>
    public bool[] ActiveSoftWrap => UseAlternate ? AlternateSoftWrap : PrimarySoftWrap;

    /// <summary>Active cursor row at snapshot time.</summary>
    public int Row => UseAlternate ? ARow : PRow;

    /// <summary>Active cursor column at snapshot time.</summary>
    public int Col => UseAlternate ? ACol : PCol;
}
