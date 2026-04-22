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
