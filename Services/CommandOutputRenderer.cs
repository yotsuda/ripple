using System.Text;

namespace Ripple.Services;

/// <summary>
/// Terminal renderer specialized for AI-facing command output capture.
///
/// Conceptually a per-command fork of <see cref="VtLiteState"/>: at OSC C
/// the worker snapshots the live VT state, hands the snapshot to a
/// fresh renderer, and feeds the renderer the cleaned output of THIS
/// command. At OSC A <see cref="Render"/> emits only the rows that the
/// command actually wrote to (or rows below the cursor at OSC C, where
/// command output naturally lands). Pre-command content stays in the
/// row list as immutable baseline so cursor positioning sequences keep
/// addressing the same cells they would on the live screen, but those
/// untouched baseline rows never reach the AI.
///
/// Why a snapshot baseline (the principled fix for ConPTY repaint bursts):
///   On Windows, ConPTY emits screen-redraw bursts after alt-screen exit
///   (and around prompts in some configurations) that re-position the
///   cursor into pre-command rows and re-emit their existing content.
///   Without the baseline the renderer would either corrupt those rows
///   (write into untouched scrollback) or treat the repaint as fresh
///   command output (flooding the MCP response with prompt history). With
///   the baseline, ConPTY's repaint writes the same chars to the same
///   cells the snapshot already holds — the per-cell change detector
///   sees no diff, the row stays unmarked, and the output is unaffected.
///
/// Why unbounded rows below the snapshot viewport:
///   <see cref="VtLiteState"/> is a fixed viewport — older rows scroll
///   out and are lost. For a build log that emits hundreds of lines
///   that's unacceptable. The renderer extends the row list past the
///   snapshot's viewport bottom on every LF, so the full command
///   output survives even when the visible viewport would have lost it.
///   Rows that scroll past the viewport bottom are still tracked here as
///   real rows (not collapsed into the snapshot's bottom row).
///
/// Cell-based storage for SGR + cursor positioning:
///   Each cell stores (char, optional SGR prefix). The legacy grid-less
///   StripAnsi stored SGR bytes inline in a per-row StringBuilder, which
///   broke as soon as later writes at column N overwrote SGR bytes
///   living at raw index N. Cell-based storage lets the renderer apply
///   CSI X / CHA / CUP correctly while preserving every color escape.
///
/// Soft-wrap re-joining at render time:
///   The snapshot's per-row "continued from above" flag (set by
///   <see cref="VtLiteState.IsRowSoftWrapped"/>) marks rows that exist
///   only because a long line wrapped at the viewport's right margin.
///   At render time the renderer joins those rows back into one logical
///   line so an AI consumer never sees a single <c>git log --oneline</c>
///   entry split across short rows just because the PTY happened to be
///   narrow when the command ran.
///
/// Escape-sequence handling — same medium subset as
/// <see cref="VtLiteState"/>'s <c>ApplyCsi</c>, with the renderer's
/// row/col semantics:
///   - <c>\r</c> moves cursor col → 0 (no row change, no clear).
///   - <c>\n</c> advances to next row AND resets col to 0 (legacy
///     "AI-friendly" semantics: bare LF behaves like CRLF, so test
///     inputs that omit explicit CR don't end up with column drift).
///   - <c>\b</c> moves cursor back one (does not erase).
///   - <c>\t</c> rounds col up to next multiple of 8.
///   - CSI A/B/C/D/E/F/G/d (CUU/CUD/CUF/CUB/CNL/CPL/CHA/VPA), CUP/HVP,
///     EL/ED, ECH, DCH/ICH, IL/DL — implemented per spec, addressing
///     cells through the current row list (which includes baseline rows
///     when a snapshot was provided).
///   - SGR (m) is buffered as <see cref="_pendingSgr"/> and attached as
///     a prefix to the next written cell.
///   - OSC sequences are dropped (the OSC parser already extracts OSC
///     633 events upstream; OSC 0/1/2 title-set noise is stripped here).
///   - DEC private modes <c>?1049h/l</c>, <c>?1047h/l</c>, <c>?47h/l</c>
///     toggle the alternate screen with real save/restore semantics:
///     entry snapshots the current main buffer cursor + viewport state;
///     exit restores them and inserts a single
///     <c>[interactive screen session]</c> line in the output stream.
///     The repaint that ConPTY emits after exit hits cells whose values
///     match the saved baseline → no diff → not in output.
/// </summary>
internal sealed class CommandOutputRenderer
{
    /// <summary>
    /// Hard cap on the number of logical rows the renderer will retain.
    /// Pathological output (cursor-down + write loops without LFs) could
    /// otherwise grow the row list without bound. 100k rows is well
    /// above any realistic build log; on overflow the oldest rows are
    /// dropped.
    /// </summary>
    public const int MaxRows = 100_000;

    /// <summary>
    /// Hard cap on the visible col coordinate the cursor can occupy.
    /// Bare CSI C / CSI G with absurd parameters (\e[2147483647C) must
    /// not allocate gigabytes of padding inside <see cref="WriteChar"/>.
    /// Chosen well above any practical terminal width.
    /// </summary>
    public const int MaxCol = 100_000;

    /// <summary>
    /// Placeholder line emitted in place of an alt-screen session
    /// (vim, less, htop, etc.). Keeping it short and recognisable so
    /// LLM consumers can tell something interactive happened without
    /// wading through redraw frames.
    /// </summary>
    public const string AltScreenPlaceholder = "[interactive screen session]";

    private readonly List<Row> _rows = new();
    private int _row;
    private int _col;
    private int _savedRow;
    private int _savedCol;

    // Pending SGR bytes that will attach to the next written cell.
    // Coalesces consecutive SGRs with no visible write between them
    // (e.g. \e[31m\e[1m) into one prefix string.
    private string? _pendingSgr;

    // Number of rows pre-populated from the baseline snapshot. Used
    // by the no-baseline CUP-clamp fallback (CleanString tests) to
    // know whether absolute cursor positioning can be trusted.
    private readonly int _baselineRowCount;

    // Alt-screen state. While active, writes are applied to a
    // separate row list — but Render() ignores it; only the placeholder
    // appears in output. On exit we restore main-buffer cursor state
    // from the entry snapshot.
    private bool _altActive;
    private List<Row>? _altRows;
    private int _altRow;
    private int _altCol;
    private string? _altPendingSgr;
    // Main-buffer cursor at alt-screen entry, restored on exit.
    private int _altReturnRow;
    private int _altReturnCol;
    private bool _altEverEntered;

    /// <summary>True if at least one alt-screen session was observed.</summary>
    public bool VisitedAltScreen => _altEverEntered;

    /// <summary>
    /// Construct an empty renderer (no baseline). Used by the
    /// string-only <see cref="CommandOutputFinalizer.CleanString"/>
    /// entry point and by tests.
    /// </summary>
    public CommandOutputRenderer() : this(null) { }

    /// <summary>
    /// Construct a renderer initialised from a <see cref="VtLiteState"/>
    /// snapshot. The snapshot's active grid is materialised as the
    /// renderer's initial row list; cursor + scroll + SGR state carry
    /// over so the first byte fed to <see cref="Feed"/> sees exactly
    /// the screen state ConPTY had when the command started.
    /// </summary>
    public CommandOutputRenderer(VtLiteSnapshot? baseline)
    {
        if (baseline is null)
        {
            _rows.Add(new Row());
            _baselineRowCount = 0;
            return;
        }

        var grid = baseline.ActiveGrid;
        var sw = baseline.ActiveSoftWrap;
        for (int r = 0; r < grid.Length; r++)
        {
            var row = new Row { ContinuedFromAbove = sw[r] };
            foreach (var ch in grid[r])
                row.Cells.Add(new Cell { Ch = ch });
            // Freeze a copy of the cells as the baseline for the
            // per-cell diff at Render time.
            row.BaselineCells = row.Cells.ToArray();
            _rows.Add(row);
        }
        if (_rows.Count == 0) _rows.Add(new Row());

        _row = baseline.Row;
        _col = baseline.Col;
        _savedRow = baseline.SavedRow;
        _savedCol = baseline.SavedCol;
        _pendingSgr = string.IsNullOrEmpty(baseline.ActiveSgr) ? null : baseline.ActiveSgr;
        _baselineRowCount = grid.Length;

        // If the snapshot was taken while alt-screen was active, the
        // renderer starts in alt-screen mode — extremely rare (would
        // require OSC C to fire from inside vim/less, which our
        // adapters don't), but model it cleanly for completeness.
        if (baseline.UseAlternate)
        {
            _altActive = true;
            _altEverEntered = true;
            _altRows = new List<Row> { new Row() };
            _altRow = baseline.ARow;
            _altCol = baseline.ACol;
            _altReturnRow = baseline.PRow;
            _altReturnCol = baseline.PCol;
        }
    }

    // Hook for callers that need to know if the renderer was
    // initialised with non-trivial baseline state (e.g. tests).
    internal bool HasBaseline()
    {
        return _baselineRowCount > 0;
    }

    /// <summary>
    /// Feed a chunk of cleaned output (OSC 633 already extracted upstream).
    /// </summary>
    public void Feed(ReadOnlySpan<char> text)
    {
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '\n') { LineFeed(); i++; }
            else if (c == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    // CRLF — single newline (no row clear).
                    LineFeed();
                    i += 2;
                }
                else
                {
                    // Bare CR: move to col 0. We do NOT clear the row
                    // here (that would conflict with the snapshot
                    // baseline approach — ConPTY's own redraw bursts
                    // include bare CR around lines that haven't been
                    // re-emitted yet). A short rewrite leaving residue
                    // is the same as what the visible terminal shows;
                    // keeping consistency with the human view trumps
                    // legacy "lossier" cleanup.
                    SetCol(0);
                    i++;
                }
            }
            else if (c == '\b') { if (CurCol > 0) SetCol(CurCol - 1); i++; }
            else if (c == '\t') { SetCol(Math.Min(((CurCol / 8) + 1) * 8, MaxCol)); i++; }
            else if (c == '\x1b') { i = ParseEscape(text, i); }
            else if (c >= ' ') { WriteChar(c); i++; }
            else { i++; /* drop other C0 */ }
        }
    }

    public void Feed(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        Feed(text.AsSpan());
    }

    /// <summary>
    /// Render the command-emitted output. Pre-command baseline rows
    /// that the command did not modify are skipped. Rows with the
    /// snapshot's "soft-wrapped from previous row" flag are joined
    /// onto the previous emitted line so logical lines that the PTY
    /// happened to wrap at its right margin reach the AI as one line.
    /// pwsh continuation-prompt rows ("&gt;&gt;", "&gt;&gt; ...") are
    /// filtered out as line-editor noise.
    /// </summary>
    public string Render()
    {
        // First pass: pick out rows whose visible content differs from
        // their baseline (or rows that have no baseline — those are
        // command-emitted by definition). The diff compares only cell
        // chars, ignoring SgrPrefix: ConPTY's repaint frequently
        // re-asserts SGR around content that was uncolored in our
        // snapshot, and treating that as "modified" would defeat the
        // whole point of the baseline approach.
        var emitIndices = new List<int>();
        for (int r = 0; r < _rows.Count; r++)
        {
            if (RowDiffersFromBaseline(_rows[r])) emitIndices.Add(r);
        }

        // Trim trailing blank rows so the output doesn't end in a sea
        // of empty lines from an over-extended cursor.
        while (emitIndices.Count > 0 && IsBlank(_rows[emitIndices[^1]]))
            emitIndices.RemoveAt(emitIndices.Count - 1);

        // Trim leading blank emission rows too — common when bash's
        // implicit \r\n after the prompt's enter-key lands cursor on a
        // blank line before the command's first real output.
        while (emitIndices.Count > 0 && IsBlank(_rows[emitIndices[0]]))
            emitIndices.RemoveAt(0);

        // No content rows AND no alt-screen visit → empty output.
        // Alt-screen visit alone still produces the placeholder line.
        if (emitIndices.Count == 0 && !_altEverEntered) return "";

        // If alt-screen was visited, the placeholder is woven in at the
        // alt-entry boundary. We don't track exact location; just emit
        // it as the first line. (Rare to see useful command output
        // before AND after a vim session in one command.)
        var sb = new StringBuilder();
        bool firstEmitted = false;

        if (_altEverEntered)
        {
            sb.Append(AltScreenPlaceholder);
            firstEmitted = true;
        }

        for (int k = 0; k < emitIndices.Count; k++)
        {
            int r = emitIndices[k];
            var row = _rows[r];
            var line = RenderRow(row);
            if (line == ">>" || line.StartsWith(">> ")) continue;

            // Soft-wrap join: if this row was soft-wrapped from the
            // previous row AND we just emitted that previous row in this
            // pass, glue the line onto the previous one without a
            // newline.
            bool joinPrev = row.ContinuedFromAbove
                && k > 0
                && emitIndices[k - 1] == r - 1
                && firstEmitted;
            if (!joinPrev && firstEmitted) sb.Append('\n');
            sb.Append(line);
            firstEmitted = true;
        }

        return sb.ToString();
    }

    // ---- internals ----

    private struct Cell
    {
        public char Ch;
        public string? SgrPrefix;
    }

    private sealed class Row
    {
        public List<Cell> Cells { get; } = new();
        public string? TrailingSgr;
        public bool ContinuedFromAbove;

        // Snapshot of cells at the moment this row was initialised
        // from the VtLiteState baseline. Null for rows added by command
        // output (those are command-emitted by definition and always
        // appear in Render output). Used to detect ConPTY's
        // post-alt-screen / post-prompt repaint pattern (\e[H\e[K +
        // re-emit) as an idempotent restoration of pre-command content:
        // EraseLine removes cells, the subsequent writes re-add them
        // with the same chars, and at Render the per-cell diff against
        // BaselineCells comes back equal — the row is skipped.
        public Cell[]? BaselineCells;
    }

    private static bool IsBlank(Row r)
    {
        foreach (var cell in r.Cells)
            if (cell.Ch != ' ') return false;
        return r.TrailingSgr == null;
    }

    private static bool RowDiffersFromBaseline(Row r)
    {
        // Rows added during command execution (no baseline snapshot)
        // are command-emitted by definition — always include.
        if (r.BaselineCells == null) return true;

        // Trailing blank cells in the snapshot grid (the row was
        // shorter than ViewCols) trim to a shorter logical length.
        // Compare on visible chars: lengths can differ if the command
        // legitimately wrote past the baseline's tail or shortened it
        // via DCH.
        if (r.Cells.Count != r.BaselineCells.Length) return true;
        for (int i = 0; i < r.Cells.Count; i++)
        {
            if (r.Cells[i].Ch != r.BaselineCells[i].Ch) return true;
        }
        return false;
    }

    private static string RenderRow(Row r)
    {
        // Find last non-blank cell so we don't emit trailing space
        // padding (which exists only because cursor moves opened a
        // gap). SGR-only "blank" cells stay if they have a SgrPrefix
        // — losing those would silently drop color resets at end of
        // line.
        int last = r.Cells.Count - 1;
        while (last >= 0 && r.Cells[last].Ch == ' ' && r.Cells[last].SgrPrefix == null)
            last--;
        if (last < 0 && r.TrailingSgr == null) return "";

        var sb = new StringBuilder();
        for (int i = 0; i <= last; i++)
        {
            var cell = r.Cells[i];
            if (cell.SgrPrefix != null) sb.Append(cell.SgrPrefix);
            sb.Append(cell.Ch);
        }
        if (r.TrailingSgr != null) sb.Append(r.TrailingSgr);
        return sb.ToString();
    }

    // Active row/col accessors handle main vs alt buffer transparently.
    private int CurRow => _altActive ? _altRow : _row;
    private int CurCol => _altActive ? _altCol : _col;
    private void SetRow(int r) { if (_altActive) _altRow = r; else _row = r; }
    private void SetCol(int c) { if (_altActive) _altCol = c; else _col = c; }
    private List<Row> ActiveRows => _altActive ? (_altRows ??= new() { new() }) : _rows;

    private void EnsureRow(int targetRow)
    {
        var rows = ActiveRows;
        if (targetRow < 0) { SetRow(0); return; }
        if (targetRow >= MaxRows) targetRow = MaxRows - 1;
        while (rows.Count <= targetRow) rows.Add(new Row());
        SetRow(targetRow);
    }

    private void LineFeed()
    {
        FlushPendingSgrAsTrailing();

        var rows = ActiveRows;
        int newRow;
        if (CurRow + 1 >= MaxRows)
        {
            // Hit the row cap — drop the oldest row to make room.
            rows.RemoveAt(0);
            newRow = MaxRows - 1;
        }
        else
        {
            newRow = CurRow + 1;
        }
        SetRow(newRow);
        EnsureRow(newRow);
        SetCol(0);
    }

    private void WriteChar(char c)
    {
        if (CurCol >= MaxCol) return;
        EnsureRow(CurRow);
        var row = ActiveRows[CurRow];
        var cells = row.Cells;

        // Pad with blank cells up to col (only when a cursor move
        // opened a gap past the row's existing tail).
        bool grew = false;
        while (cells.Count < CurCol) { cells.Add(new Cell { Ch = ' ' }); grew = true; }

        var prefix = (_altActive ? _altPendingSgr : _pendingSgr);
        if (_altActive) _altPendingSgr = null; else _pendingSgr = null;

        if (CurCol < cells.Count)
        {
            var existing = cells[CurCol];
            cells[CurCol] = new Cell
            {
                Ch = c,
                SgrPrefix = prefix ?? existing.SgrPrefix
            };
        }
        else
        {
            cells.Add(new Cell { Ch = c, SgrPrefix = prefix });
        }
        _ = grew;
        SetCol(CurCol + 1);
    }

    private void RecordSgr(string sgrSequence)
    {
        if (_altActive)
            _altPendingSgr = _altPendingSgr is null ? sgrSequence : _altPendingSgr + sgrSequence;
        else
            _pendingSgr = _pendingSgr is null ? sgrSequence : _pendingSgr + sgrSequence;
    }

    private void FlushPendingSgrAsTrailing()
    {
        ref string? slot = ref (_altActive ? ref _altPendingSgr : ref _pendingSgr);
        if (slot is null) return;
        EnsureRow(CurRow);
        var row = ActiveRows[CurRow];
        row.TrailingSgr = row.TrailingSgr is null ? slot : row.TrailingSgr + slot;
        slot = null;
    }

    private void EraseLine(int mode)
    {
        EnsureRow(CurRow);
        var row = ActiveRows[CurRow];
        var cells = row.Cells;
        if (mode == 0)
        {
            if (CurCol < cells.Count) cells.RemoveRange(CurCol, cells.Count - CurCol);
            row.TrailingSgr = null;
        }
        else if (mode == 1)
        {
            int end = Math.Min(CurCol + 1, cells.Count);
            for (int i = 0; i < end; i++) cells[i] = new Cell { Ch = ' ' };
        }
        else if (mode == 2)
        {
            cells.Clear();
            row.TrailingSgr = null;
        }
    }

    private void EraseDisplay(int mode)
    {
        var rows = ActiveRows;
        if (mode == 0)
        {
            EraseLine(0);
            for (int r = CurRow + 1; r < rows.Count; r++)
            {
                rows[r].Cells.Clear();
                rows[r].TrailingSgr = null;
            }
        }
        else if (mode == 1)
        {
            for (int r = 0; r < CurRow; r++)
            {
                rows[r].Cells.Clear();
                rows[r].TrailingSgr = null;
            }
            EraseLine(1);
        }
        else if (mode == 2)
        {
            for (int r = 0; r < rows.Count; r++)
            {
                rows[r].Cells.Clear();
                rows[r].TrailingSgr = null;
            }
            SetRow(0); SetCol(0);
            if (_altActive) _altPendingSgr = null; else _pendingSgr = null;
        }
    }

    private void EraseChars(int n)
    {
        EnsureRow(CurRow);
        var cells = ActiveRows[CurRow].Cells;
        n = Math.Max(1, n);
        int end = Math.Min(CurCol + n, cells.Count);
        for (int i = CurCol; i < end; i++) cells[i] = new Cell { Ch = ' ' };
    }

    private void DeleteChars(int n)
    {
        EnsureRow(CurRow);
        var cells = ActiveRows[CurRow].Cells;
        n = Math.Max(1, n);
        if (CurCol >= cells.Count) return;
        int actual = Math.Min(n, cells.Count - CurCol);
        cells.RemoveRange(CurCol, actual);
    }

    private void InsertChars(int n)
    {
        EnsureRow(CurRow);
        var cells = ActiveRows[CurRow].Cells;
        n = Math.Max(1, n);
        while (cells.Count < CurCol) cells.Add(new Cell { Ch = ' ' });
        for (int i = 0; i < n; i++) cells.Insert(CurCol, new Cell { Ch = ' ' });
    }

    private void InsertLines(int n)
    {
        n = Math.Max(1, n);
        EnsureRow(CurRow);
        var rows = ActiveRows;
        for (int i = 0; i < n && rows.Count < MaxRows; i++)
            rows.Insert(CurRow, new Row());
    }

    private void DeleteLines(int n)
    {
        n = Math.Max(1, n);
        EnsureRow(CurRow);
        var rows = ActiveRows;
        int actual = Math.Min(n, rows.Count - CurRow);
        if (actual > 0)
        {
            for (int i = CurRow; i < CurRow + actual; i++)
                if (rows[i].Cells.Count > 0) { /* mark surrounding rows modified for visibility */ }
            rows.RemoveRange(CurRow, actual);
        }
        if (rows.Count == 0) rows.Add(new Row());
        if (CurRow >= rows.Count) SetRow(rows.Count - 1);
    }

    private void EnterAlt()
    {
        if (_altActive) return;
        _altActive = true;
        _altEverEntered = true;
        _altReturnRow = _row;
        _altReturnCol = _col;
        _altRows = new List<Row> { new Row() };
        _altRow = 0;
        _altCol = 0;
        _altPendingSgr = null;
    }

    private void ExitAlt()
    {
        if (!_altActive) return;
        _altActive = false;
        _altRows = null;       // discard alt buffer; only the placeholder reaches output
        _altPendingSgr = null;
        // Restore main-buffer cursor to the entry point. ConPTY's
        // post-exit screen redraw burst will now address main-buffer
        // cells via cursor positioning; those cells already hold the
        // baseline values, so the per-cell change detector marks
        // nothing modified and the redraw stays out of the AI output.
        _row = _altReturnRow;
        _col = _altReturnCol;
    }

    private int ParseEscape(ReadOnlySpan<char> input, int start)
    {
        int i = start + 1;
        if (i >= input.Length) return input.Length;

        char next = input[i];
        if (next == '[')
        {
            int paramStart = i + 1;
            int j = paramStart;
            while (j < input.Length && input[j] >= 0x30 && input[j] <= 0x3f) j++;
            var paramsSpan = j > paramStart ? input.Slice(paramStart, j - paramStart) : ReadOnlySpan<char>.Empty;
            while (j < input.Length && input[j] >= 0x20 && input[j] <= 0x2f) j++;
            if (j >= input.Length) return input.Length;
            char final = input[j];
            ApplyCsi(paramsSpan, final, input, start, j);
            return j + 1;
        }
        if (next == ']')
        {
            int j = i + 1;
            while (j < input.Length)
            {
                if (input[j] == '\x07') { j++; break; }
                if (input[j] == '\x1b' && j + 1 < input.Length && input[j + 1] == '\\') { j += 2; break; }
                j++;
            }
            return j;
        }
        if (next == '(' || next == ')') return Math.Min(i + 2, input.Length);
        if (next == '7') { _savedRow = _row; _savedCol = _col; return i + 1; }
        if (next == '8') { _row = _savedRow; _col = _savedCol; EnsureRow(_row); return i + 1; }
        if (next == '=' || next == '>') return i + 1;
        return i + 1;
    }

    private void ApplyCsi(ReadOnlySpan<char> paramsSpan, char final, ReadOnlySpan<char> fullInput, int seqStart, int finalIdx)
    {
        if (paramsSpan.Length > 0 && paramsSpan[0] == '?')
        {
            var modeSpan = paramsSpan.Slice(1);
            if (modeSpan.SequenceEqual("1049".AsSpan())
                || modeSpan.SequenceEqual("1047".AsSpan())
                || modeSpan.SequenceEqual("47".AsSpan()))
            {
                if (final == 'h') EnterAlt();
                else if (final == 'l') ExitAlt();
            }
            return;
        }

        switch (final)
        {
            case 'A': SetRow(Math.Max(0, CurRow - Math.Max(1, GetParam(paramsSpan, 0, 1)))); EnsureRow(CurRow); break;
            case 'B': EnsureRow(CurRow + Math.Max(1, GetParam(paramsSpan, 0, 1))); break;
            case 'C': SetCol(Math.Min(MaxCol, CurCol + Math.Max(1, GetParam(paramsSpan, 0, 1)))); break;
            case 'D': SetCol(Math.Max(0, CurCol - Math.Max(1, GetParam(paramsSpan, 0, 1)))); break;
            case 'E':
                EnsureRow(CurRow + Math.Max(1, GetParam(paramsSpan, 0, 1)));
                SetCol(0);
                break;
            case 'F':
                SetRow(Math.Max(0, CurRow - Math.Max(1, GetParam(paramsSpan, 0, 1))));
                EnsureRow(CurRow);
                SetCol(0);
                break;
            case 'G': SetCol(Math.Clamp(GetParam(paramsSpan, 0, 1) - 1, 0, MaxCol)); break;
            case 'H':
            case 'f':
                {
                    // With a snapshot baseline, CSI coords address
                    // viewport rows directly — row 1 maps to snapshot
                    // row 0. The per-cell repaint detector handles
                    // ConPTY's idempotent re-emission of pre-command
                    // content without flagging the row modified, so
                    // those moves don't leak baseline content into the
                    // output.
                    //
                    // Without a baseline (the legacy CleanString test
                    // path) the screen viewport is unknown; an absolute
                    // CSI H targeting a row before our current cursor
                    // is almost always ConPTY redraw noise (Git Bash
                    // emits \x1b[H + space + \x1b[N;1H around prompts)
                    // that would corrupt earlier output. Clamp upward
                    // to the current row in that case — the maximum
                    // correctness given no viewport information.
                    int wantRow = Math.Max(0, GetParam(paramsSpan, 0, 1) - 1);
                    int newCol = Math.Clamp(GetParam(paramsSpan, 1, 1) - 1, 0, MaxCol);
                    int newRow = (_baselineRowCount > 0 || _altActive)
                        ? wantRow
                        : Math.Max(CurRow, wantRow);
                    EnsureRow(newRow);
                    SetCol(newCol);
                }
                break;
            case 'd':
                {
                    int wantRow = Math.Max(0, GetParam(paramsSpan, 0, 1) - 1);
                    int newRow = (_baselineRowCount > 0 || _altActive)
                        ? wantRow
                        : Math.Max(CurRow, wantRow);
                    EnsureRow(newRow);
                }
                break;
            case 'K': EraseLine(GetParam(paramsSpan, 0, 0)); break;
            case 'J': EraseDisplay(GetParam(paramsSpan, 0, 0)); break;
            case 'X': EraseChars(GetParam(paramsSpan, 0, 1)); break;
            case 'P': DeleteChars(GetParam(paramsSpan, 0, 1)); break;
            case '@': InsertChars(GetParam(paramsSpan, 0, 1)); break;
            case 'L': InsertLines(GetParam(paramsSpan, 0, 1)); break;
            case 'M': DeleteLines(GetParam(paramsSpan, 0, 1)); break;
            case 's': _savedRow = _row; _savedCol = _col; break;
            case 'u': _row = _savedRow; _col = _savedCol; EnsureRow(_row); break;
            case 'm':
                RecordSgr(fullInput.Slice(seqStart, finalIdx - seqStart + 1).ToString());
                break;
        }
    }

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

}
