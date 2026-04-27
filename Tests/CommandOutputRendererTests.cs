using Ripple.Services;

namespace Ripple.Tests;

/// <summary>
/// Unit tests for <see cref="CommandOutputRenderer"/>, focused on
/// baseline-snapshot interactions.
///
/// These document the renderer's known sensitivity to baseline input:
/// if a snapshot's cursor lands on a grid row that holds non-blank
/// cells, a subsequent Feed that overwrites only the leading columns
/// leaves the trailing baseline cells in place, and the per-cell diff
/// emits the whole row — producing visible "bleed" of stale content.
///
/// The worker side prevents this from happening in practice by slicing
/// _vtState.Feed along OSC event offsets, so the baseline snapshot at
/// CommandExecuted reflects state as of the OSC C byte (a blank row
/// reached via synth-prompt \r\n), not end-of-chunk state that could
/// include downstream command-output bytes. The tests below intentionally
/// construct the "bad baseline" shape the worker will never produce, so
/// the renderer's response to that shape is pinned down and any future
/// attempt to remove the worker's slicing guard would surface here as a
/// change in rendered output.
/// </summary>
public static class CommandOutputRendererTests
{
    public static void Run()
    {
        var pass = 0;
        var fail = 0;

        void Assert(bool condition, string name)
        {
            if (condition) { pass++; Console.WriteLine($"  PASS: {name}"); }
            else { fail++; Console.Error.WriteLine($"  FAIL: {name}"); }
        }

        Console.WriteLine("=== CommandOutputRenderer Tests ===");

        // Documented limitation: snapshot with cursor on a row that
        // holds stale cells produces bleed. The worker's slice-by-offset
        // snapshot timing prevents this shape from arising in the real
        // pipeline — that guarantee is tested separately.
        {
            const int rows = 10;
            const int cols = 20;
            var grid = BuildBlankGrid(rows, cols);
            WriteLineAt(grid[5], "iter 3");
            var snapshot = BuildSnapshot(rows, cols, grid, pRow: 5, pCol: 0);

            var r = new CommandOutputRenderer(snapshot);
            r.Feed("a\r\nb\r\n");
            var output = r.Render();

            Assert(output == "ater 3\nb",
                $"renderer baseline sensitivity: short overwrite of stale row bleeds — expected \"ater 3\\nb\" (documented limitation), got {Quote(output)}");
        }

        // Cursor on a blank row → no stale cells to bleed → clean output.
        // This is the shape the worker's slice-by-offset snapshot
        // produces for multi-line pwsh command output (after CPL+ED and
        // the synth-prompt \r\n leaves cursor on a known-blank row).
        {
            const int rows = 10;
            const int cols = 20;
            var grid = BuildBlankGrid(rows, cols);
            WriteLineAt(grid[5], "iter 3");
            var snapshot = BuildSnapshot(rows, cols, grid, pRow: 7, pCol: 0);

            var r = new CommandOutputRenderer(snapshot);
            r.Feed("a\r\nb\r\n");
            var output = r.Render();

            Assert(output == "a\nb",
                $"cursor on blank row → clean output — expected \"a\\nb\", got {Quote(output)}");
        }

        // Full overwrite covers every stale cell on the cursor's row →
        // no bleed possible regardless of baseline shape.
        {
            const int rows = 10;
            const int cols = 20;
            var grid = BuildBlankGrid(rows, cols);
            WriteLineAt(grid[5], "iter 3");
            var snapshot = BuildSnapshot(rows, cols, grid, pRow: 5, pCol: 0);

            var r = new CommandOutputRenderer(snapshot);
            r.Feed("HELLO!\r\n");
            var output = r.Render();

            Assert(output == "HELLO!",
                $"full overwrite: expected \"HELLO!\", got {Quote(output)}");
        }

        // Slice-by-offset snapshot timing (the worker-side fix). Feed
        // VtLiteState a scripted chunk that mirrors the encoded_scriptblock
        // multi-line delivery path: scrollback containing a prior run's
        // "iter 3", then synth-prompt payload (\e[NF + \e[0J + prompt +
        // body + \r\n), then an OSC-C-like marker offset, then fresh
        // command output. Snapshot taken at the OSC-C offset reflects a
        // post-\r\n blank cursor row — so a subsequent Feed of the same
        // command output into CommandOutputRenderer lands "a" on a blank
        // row, no bleed. Snapshot taken at end-of-chunk (pre-fix shape)
        // would instead reflect the final cursor position after _vtState
        // had already consumed the command output, which, depending on
        // scrolling, can land on a row holding stale cells.
        {
            var vt = new VtLiteState(10, 20);
            // Seed the session with scrollback that includes the prior
            // run's "iter 3" on some row.
            vt.Feed("a\r\nb\r\niter 1\r\niter 2\r\niter 3\r\n".AsSpan());
            // Next-prompt row arrives (stand-in for pwsh PS1).
            vt.Feed("PS> ".AsSpan());
            // Echo the long scriptblock-invoker input (simulated by
            // space padding that fills columns and wraps the prompt
            // line).
            vt.Feed(new string('x', 80).AsSpan());
            // Enter: pwsh moves past the echo. This is the state at
            // which the scriptblock begins writing its payload.
            vt.Feed("\r\n".AsSpan());

            // Now build a chunk containing the scriptblock-emitted
            // payload and trailing command output. We deliberately feed
            // this chunk in two halves: first the payload through the
            // synth-prompt \r\n (what the worker-side fix feeds _vtState
            // before taking the snapshot), then the command output.
            var payload = "\x1b[2F" /* CPL 2 */ + "\x1b[0J" /* ED 0 */
                + "PS C:\\tmp> " /* synth prompt */
                + "\r\n" /* after-synth-prompt newline, cursor to blank row */;
            var commandOutput = "a\r\nb\r\niter 1\r\niter 2\r\niter 3\r\n";

            // Slice-by-offset path: feed payload, snapshot, feed output.
            vt.Feed(payload.AsSpan());
            var fixedBaseline = vt.Snapshot();
            // Continue feeding the output — mirrors the worker continuing
            // past the OSC C offset in the same chunk.
            vt.Feed(commandOutput.AsSpan());

            var r = new CommandOutputRenderer(fixedBaseline);
            r.Feed(commandOutput);
            var output = r.Render();

            Assert(output == "a\nb\niter 1\niter 2\niter 3",
                $"slice-by-offset snapshot → clean multi-line output — expected \"a\\nb\\niter 1\\niter 2\\niter 3\", got {Quote(output)}");
        }

        // Soft-wrap continuation detector: ConPTY wraps a long line at
        // the viewport's right margin by emitting "\r\n + CSI <viewportRow>;<Cols>H"
        // so the cursor visually lands on the next visible row at the
        // wrap point. The renderer must recognize this pair as a logical
        // wrap (not a real cursor move) and append the continuation to
        // the SAME logical row, otherwise the chars overwrite whatever
        // happens to live in the next row (a prompt echo, the next
        // warning line, blanks). See HANDOFF_GARBLING.md for the
        // dogfood report ("FontSitle.cs", "IDrawuches itxt.cs").
        {
            const int rows = 24;
            const int cols = 10;
            var grid = BuildBlankGrid(rows, cols);
            // Seed a stale row at row 22 so we can detect bleed if the
            // continuation lands there instead of getting joined.
            WriteLineAt(grid[22], "STALECON");
            var snapshot = BuildSnapshot(rows, cols, grid, pRow: 23, pCol: 0);

            var r = new CommandOutputRenderer(snapshot);
            // 10 chars fill the viewport row, then ConPTY's wrap pattern
            // (\r\n + CUP back to (24,10)), then the continuation, then
            // a real CRLF that ends the logical line. ConPTY DECAWM
            // re-emits the wrapping char (`9`) at the start of the
            // continuation; the renderer redirects to col N-1 so that
            // duplicate overwrites itself rather than appending.
            r.Feed("0123456789\r\n\x1b[24;10H9 next part\r\n");
            var output = r.Render();

            Assert(output == "0123456789 next part",
                $"single soft-wrap continuation joined onto logical row — expected \"0123456789 next part\", got {Quote(output)}");
        }

        // Multi-wrap: a line longer than 2× viewport width emits
        // \r\n + CUP TWICE, and both must redirect back to the same
        // logical row. Each ConPTY wrap continuation begins by re-emitting
        // the wrapping char (DECAWM auto-margin), so the second
        // continuation's first char is `h` (the last char of the first
        // continuation's `9 abcdefgh`).
        {
            const int rows = 24;
            const int cols = 10;
            var grid = BuildBlankGrid(rows, cols);
            var snapshot = BuildSnapshot(rows, cols, grid, pRow: 23, pCol: 0);

            var r = new CommandOutputRenderer(snapshot);
            r.Feed("0123456789\r\n\x1b[24;10H9 abcdefgh\r\n\x1b[24;10Hh last\r\n");
            var output = r.Render();

            Assert(output == "0123456789 abcdefgh last",
                $"multi-wrap soft-wrap chain joined — expected \"0123456789 abcdefghi last\", got {Quote(output)}");
        }

        // Negative: a CUP that arrives after a full-width line but
        // targets a column AWAY from the viewport's right margin is a
        // real cursor move (a TUI repositioning, not ConPTY's wrap),
        // and must NOT be redirected. Tests the heuristic gate that
        // prevents false positives.
        {
            const int rows = 24;
            const int cols = 10;
            var grid = BuildBlankGrid(rows, cols);
            var snapshot = BuildSnapshot(rows, cols, grid, pRow: 23, pCol: 0);

            var r = new CommandOutputRenderer(snapshot);
            // Fill row to width, LF, then CUP to (24,1) — col 1 is
            // nowhere near the wrap margin, so this is a real reposition.
            r.Feed("0123456789\r\n\x1b[24;1HX");
            var output = r.Render();

            // The X should land at row 24 col 0 (post-CUP target), not
            // get redirected back into row 23. Trailing rows are
            // trimmed so the rendered output ends with the X line.
            Assert(output == "0123456789\nX",
                $"non-margin CUP after LF must not trigger soft-wrap redirect — expected \"0123456789\\nX\", got {Quote(output)}");
        }

        // ConPTY DECAWM auto-margin re-emits the wrapping char on the
        // continuation line. When a long line fills col 0..N-1 (where
        // N=Cols), the prefix ends with whatever char landed at col N-1.
        // ConPTY's wrap continuation does not skip that char — it
        // re-emits the same char as the FIRST char after the CUP-back-to-
        // margin. So redirecting to col `_lastLfPreCol` (= N) leaves the
        // prefix's last char at col N-1 in place, and the continuation's
        // first char (a duplicate) lands at col N — visible as a doubled
        // character in the joined logical row. Real-world dogfood report:
        // a 105-col viewport showed `...CRLLF...` (LL doubled) for a git
        // CRLF warning whose prefix ended with `L` of `CRL` and whose
        // continuation began with `LF`. Fix: redirect to `lastLfPreCol-1`
        // so the continuation's first char OVERWRITES the prefix's last
        // (with the same char — DECAWM redraws the same byte), absorbing
        // the duplication.
        {
            const int rows = 24;
            const int cols = 105;
            var grid = BuildBlankGrid(rows, cols);
            var snapshot = BuildSnapshot(rows, cols, grid, pRow: 23, pCol: 0);

            var r = new CommandOutputRenderer(snapshot);
            // 105-char prefix ending with `L` (the L of CRL), wrap, CUP
            // back to margin (col 105 = 1-indexed), continuation begins
            // with `L` (DECAWM re-emit) followed by `F the next time...`
            r.Feed("warning: in the working copy of 'LilySharp.Core/Rendering/IDrawingContext.cs', LF will be replaced by CRL\r\n\x1b[24;105HLF the next time Git touches it\r\n");
            var output = r.Render();

            const string expected = "warning: in the working copy of 'LilySharp.Core/Rendering/IDrawingContext.cs', LF will be replaced by CRLF the next time Git touches it";
            Assert(output == expected,
                $"ConPTY DECAWM duplicate-char wrap: continuation's first char must overwrite prefix's last col — expected {Quote(expected)}, got {Quote(output)}");
        }

        // Negative: short line followed by CUP-to-margin must not be
        // treated as soft-wrap, because the LF that precedes it could
        // not have been emitted by ConPTY's wrap-at-margin path (the
        // row was nowhere near full).
        {
            const int rows = 24;
            const int cols = 10;
            var grid = BuildBlankGrid(rows, cols);
            var snapshot = BuildSnapshot(rows, cols, grid, pRow: 23, pCol: 0);

            var r = new CommandOutputRenderer(snapshot);
            // Short row "hi", LF, then CUP-to-margin. preLfCol=2 is
            // well below the viewport's right margin, so armSoftWrap
            // is false → CUP applies normally.
            r.Feed("hi\r\n\x1b[24;10HX");
            var output = r.Render();

            // X lands at (row 24, col 9) — leading 9 cols are spaces.
            Assert(output == "hi\n         X",
                $"short LF must not arm soft-wrap detector — expected \"hi\\n         X\", got {Quote(output)}");
        }

        Console.WriteLine($"CommandOutputRenderer: {pass} passed, {fail} failed");
        if (fail > 0) Environment.Exit(1);
    }

    // ---- helpers ----

    private static char[][] BuildBlankGrid(int rows, int cols)
    {
        var g = new char[rows][];
        for (int r = 0; r < rows; r++)
        {
            g[r] = new char[cols];
            for (int c = 0; c < cols; c++) g[r][c] = ' ';
        }
        return g;
    }

    private static void WriteLineAt(char[] row, string text)
    {
        for (int i = 0; i < text.Length && i < row.Length; i++) row[i] = text[i];
    }

    private static VtLiteSnapshot BuildSnapshot(int rows, int cols, char[][] grid, int pRow, int pCol)
    {
        var softWrap = new bool[rows];
        var altGrid = BuildBlankGrid(rows, cols);
        var altSoftWrap = new bool[rows];
        return new VtLiteSnapshot(
            rows: rows,
            cols: cols,
            primaryGrid: grid,
            primarySoftWrap: softWrap,
            alternateGrid: altGrid,
            alternateSoftWrap: altSoftWrap,
            useAlternate: false,
            pRow: pRow, pCol: pCol,
            aRow: 0, aCol: 0,
            savedRow: 0, savedCol: 0,
            scrollTop: 0, scrollBottom: rows - 1,
            activeSgr: "");
    }

    private static string Quote(string s)
    {
        return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r") + "\"";
    }
}
