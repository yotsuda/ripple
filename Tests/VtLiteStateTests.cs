using System.Diagnostics;
using System.Text;
using Ripple.Services;

namespace Ripple.Tests;

/// <summary>
/// Unit tests for <see cref="VtLiteState"/>'s streaming Feed() entry,
/// chunk-boundary buffer, and parity with the static one-shot VtLite()
/// on which peek_console depends. Phase 0 of the Unix VT parity round —
/// these run on Windows because they're pure-state tests over synthetic
/// byte sequences.
/// </summary>
public class VtLiteStateTests
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

        Console.WriteLine("=== VtLiteState Tests ===");

        // ---- one-shot parity with the static VtLite() ----

        // Feed-then-Render must match the static one-shot for the same
        // input — otherwise peek_console behavior would diverge.
        {
            const string input = "hello\r\nworld";
            var s = new VtLiteState(30, 120);
            s.Feed(input.AsSpan());
            var streamed = s.Render();
            var oneShot = VtLiteState.VtLite(input);
            Assert(streamed == oneShot && streamed == "hello\nworld",
                $"Feed→Render matches one-shot VtLite — streamed={streamed}, oneShot={oneShot}");
        }

        // Splitting the input at every byte boundary must produce the
        // same final (Row, Col, Render) as feeding it whole. This is the
        // core chunk-boundary correctness property — if it holds for
        // every split point on a representative byte stream, the
        // pending-escape buffer is stitching everything correctly.
        {
            // Includes: text, CR/LF, CSI cursor moves, SGR (no-op for
            // cursor), alt-screen enter/exit, OSC (dropped), DSR (no-op).
            const string input =
                "PS C:\\dev> \x1b[?25l\x1b[36mecho\x1b[0m hi\r\n" +
                "row2\x1b[3;5Hat3,5\x1b[2A" +
                "\x1b]0;title\x07tail" +
                "\x1b[?1049hALT\x1b[?1049lback";

            var whole = new VtLiteState(10, 40);
            whole.Feed(input.AsSpan());
            var wholeRender = whole.Render();
            var wholeRow = whole.Row;
            var wholeCol = whole.Col;

            int matched = 0;
            int mismatched = 0;
            for (int split = 1; split < input.Length; split++)
            {
                var s = new VtLiteState(10, 40);
                s.Feed(input.AsSpan(0, split));
                s.Feed(input.AsSpan(split));
                if (s.Render() == wholeRender && s.Row == wholeRow && s.Col == wholeCol)
                    matched++;
                else
                    mismatched++;
            }
            Assert(mismatched == 0,
                $"every byte-boundary split agrees with whole-feed: {matched} match, {mismatched} mismatch (final={wholeRow},{wholeCol})");
        }

        // ---- chunk-boundary explicit cases ----

        // CSI split between the introducer and the final byte must be
        // stitched: \e[3;5 in chunk A, H in chunk B should land cursor
        // at row 3 col 5 (1-indexed) → (2,4) zero-indexed.
        {
            var s = new VtLiteState(10, 40);
            s.Feed("hello\x1b[3;5".AsSpan());
            // Pending should hold "\x1b[3;5" — cursor still at end of "hello".
            Assert(s.Row == 0 && s.Col == 5, "CSI split A: cursor unchanged before final byte arrives");
            s.Feed("HX".AsSpan());
            Assert(s.Row == 2 && s.Col == 5,
                $"CSI split B: cursor moved to (2,5) after 'X' written — got ({s.Row},{s.Col})");
        }

        // OSC split: \e]0;tit in chunk A, le<BEL>after in chunk B. The
        // OSC body must be dropped, "after" written from the post-OSC
        // position (cursor where OSC started). Note: the BEL is encoded
        // as \u0007, not \x07 — C#'s \x is variable-width and would
        // greedily consume \x07af as a single 4-hex-digit codepoint.
        {
            var s = new VtLiteState(10, 40);
            s.Feed("pre\x1b]0;tit".AsSpan());
            // Cursor parked at end of "pre" — OSC pending, no write yet.
            Assert(s.Row == 0 && s.Col == 3,
                $"OSC split A: cursor at end of 'pre' — got ({s.Row},{s.Col})");
            s.Feed("le\u0007after".AsSpan());
            Assert(s.Render() == "preafter",
                $"OSC split B: OSC body dropped, 'after' appended — got {s.Render()}");
        }

        // Bare ESC at end of chunk must be buffered, not consumed.
        {
            var s = new VtLiteState(10, 40);
            s.Feed("x\x1b".AsSpan());
            Assert(s.Col == 1, $"bare-ESC at boundary: cursor stays at 1 — got ({s.Row},{s.Col})");
            s.Feed("[2C".AsSpan()); // CUF 2 — moves Col forward by 2
            Assert(s.Col == 3, $"bare-ESC stitched + CUF: cursor advanced to col 3 — got ({s.Row},{s.Col})");
        }

        // OSC ESC \\ (ST) terminator split across chunks: ESC at the very
        // end of chunk A must be held, the \\ in chunk B completes the ST.
        {
            var s = new VtLiteState(10, 40);
            s.Feed("\x1b]0;long-title\x1b".AsSpan());
            Assert(s.Col == 0, "OSC ST split: cursor unchanged while ST awaits second byte");
            s.Feed("\\after".AsSpan());
            Assert(s.Render() == "after",
                $"OSC ST split: completed correctly, 'after' written from origin — got {s.Render()}");
        }

        // ---- cursor-math correctness ----

        // SGR runs do not advance the cursor — CRITICAL. The whole point
        // of routing through VtLiteState is that SGR escapes parse as
        // CSI 'm' finals which are no-ops for cursor position. The prior
        // estimator regressed precisely because it counted SGR bytes as
        // printable-character advances.
        {
            var plain = new VtLiteState(10, 40);
            plain.Feed("hello".AsSpan());

            var sgr = new VtLiteState(10, 40);
            sgr.Feed("\x1b[1;31mhello\x1b[0m".AsSpan());

            Assert(plain.Col == sgr.Col && plain.Row == sgr.Row,
                $"SGR no-op: ({plain.Row},{plain.Col}) vs ({sgr.Row},{sgr.Col})");
        }

        // Bracketed-paste markers (\e[200~ ... \e[201~) — '~' is the CSI
        // final byte, '200' / '201' are params. ApplyCsi has no '~' case
        // so it falls into the silent-drop default, which is the correct
        // behaviour: bracketed-paste markers are signaling, not output.
        {
            var s = new VtLiteState(10, 40);
            s.Feed("\x1b[200~pasted\x1b[201~".AsSpan());
            Assert(s.Render() == "pasted" && s.Row == 0 && s.Col == 6,
                $"bracketed-paste markers don't shift cursor — render={s.Render()}, ({s.Row},{s.Col})");
        }

        // CUP (CSI H) absolute positioning, 1-indexed → 0-indexed.
        {
            var s = new VtLiteState(10, 40);
            s.Feed("\x1b[5;7H".AsSpan());
            Assert(s.Row == 4 && s.Col == 6, $"CUP(5,7) → (4,6) — got ({s.Row},{s.Col})");
        }

        // CUU (CSI A) cursor up — clamped at row 0.
        {
            var s = new VtLiteState(10, 40);
            s.Feed("\x1b[5;1H\x1b[3A".AsSpan()); // start at row 4, up 3 → row 1
            Assert(s.Row == 1, $"CUU 3 from row 4 → row 1 — got row {s.Row}");

            s.Feed("\x1b[10A".AsSpan()); // up 10 from row 1, clamp to 0
            Assert(s.Row == 0, $"CUU 10 clamped at row 0 — got row {s.Row}");
        }

        // CUD (CSI B) clamped to ViewRows-1.
        {
            var s = new VtLiteState(10, 40);
            s.Feed("\x1b[1;1H\x1b[100B".AsSpan());
            Assert(s.Row == 9, $"CUD 100 clamped at last row (9) — got row {s.Row}");
        }

        // CHA (CSI G) — column-only absolute, 1-indexed.
        {
            var s = new VtLiteState(10, 40);
            s.Feed("hello\x1b[20G".AsSpan());
            Assert(s.Col == 19, $"CHA 20 → col 19 — got col {s.Col}");
        }

        // VPA (CSI d) — row-only absolute, 1-indexed.
        {
            var s = new VtLiteState(10, 40);
            s.Feed("\x1b[7d".AsSpan());
            Assert(s.Row == 6, $"VPA 7 → row 6 — got row {s.Row}");
        }

        // ---- wrapping + scrolling ----

        // Soft wrap at right margin: writing past ViewCols moves cursor
        // to col 0 of next row.
        {
            var s = new VtLiteState(5, 4);
            s.Feed("ABCDE".AsSpan()); // 4-col grid, 5 chars → wrap on E
            Assert(s.Row == 1 && s.Col == 1,
                $"soft-wrap to next row — got ({s.Row},{s.Col})");
            // Render should show ABCD on row 0, E on row 1.
            var rendered = s.Render();
            Assert(rendered == "ABCD\nE", $"soft-wrap render — got {rendered}");
        }

        // LF at bottom of scroll region scrolls up — top row drops off.
        {
            var s = new VtLiteState(3, 6);
            s.Feed("row1\r\nrow2\r\nrow3\r\nrow4".AsSpan());
            // After "row1", LF, "row2", LF, "row3" → fills 3 rows.
            // Then LF at bottom row scrolls; "row4" written on row 2
            // (the new bottom after scroll). Top "row1" dropped.
            Assert(s.Render() == "row2\nrow3\nrow4",
                $"LF-at-bottom scrolls — got {s.Render()}");
        }

        // ---- alt-screen save/restore ----

        // Alt-screen enter saves primary cursor; alt-screen exit
        // restores it. Content written in alt-screen does not bleed
        // into the primary buffer's render.
        {
            var s = new VtLiteState(10, 40);
            s.Feed("primary text\x1b[?1049hALT CONTENT\x1b[?1049l".AsSpan());
            // After exit, primary's saved cursor is restored. The
            // saved cursor was end-of-"primary text" (col 12 on row 0).
            Assert(s.Row == 0 && s.Col == 12,
                $"alt-screen exit restores primary cursor (0,12) — got ({s.Row},{s.Col})");
            // Primary buffer render must NOT contain alt content.
            Assert(s.Render() == "primary text" && !s.Render().Contains("ALT"),
                $"alt-screen exit returns to primary buffer untouched — got {s.Render()}");
        }

        // ---- in-line editing CSI sequences (Phase 3) ----

        // ECH (\e[<n>X) — erase N chars at cursor without moving it.
        // Readline / PSReadLine emit this to clear part of the input
        // line during in-line editing.
        {
            var s = new VtLiteState(5, 10);
            s.Feed("ABCDEFGH".AsSpan());
            // Move back to col 2 (between 'B' and 'C').
            s.Feed("\x1b[3G".AsSpan()); // CHA col 3 → col 2 (0-indexed)
            s.Feed("\x1b[3X".AsSpan()); // ECH 3 — erase 'CDE'
            Assert(s.Col == 2, $"ECH preserves cursor col — got col {s.Col}");
            Assert(s.Render() == "AB   FGH",
                $"ECH erased 3 chars at cursor — got {s.Render()}");
        }

        // DCH (\e[<n>P) — delete N chars at cursor; trailing chars
        // shift left. Used by readline when the user backspaces in the
        // middle of a line.
        {
            var s = new VtLiteState(5, 10);
            s.Feed("ABCDEFGH".AsSpan());
            s.Feed("\x1b[3G".AsSpan()); // back to col 2
            s.Feed("\x1b[2P".AsSpan()); // delete 2 chars ('CD')
            Assert(s.Col == 2, $"DCH preserves cursor col — got col {s.Col}");
            Assert(s.Render() == "ABEFGH",
                $"DCH deleted 2 chars, rest shifted — got {s.Render()}");
        }

        // ICH (\e[<n>@) — insert N blank chars at cursor; existing
        // chars shift right. Used by readline when the user types in
        // the middle of a line.
        {
            var s = new VtLiteState(5, 10);
            s.Feed("ABCDEFGH".AsSpan());
            s.Feed("\x1b[3G".AsSpan()); // back to col 2
            s.Feed("\x1b[2@".AsSpan()); // insert 2 blanks
            Assert(s.Col == 2, $"ICH preserves cursor col — got col {s.Col}");
            // Row had ABCDEFGH (8 chars in 10-col grid). Insert 2
            // blanks at col 2 → AB__CDEFGH but limited to 10 cols, so
            // 'GH' would be pushed past margin and lost. Result:
            // "AB  CDEFGH"[0..10] = "AB  CDEFGH"... wait — 10 chars
            // total. After insert: A,B, , ,C,D,E,F,G,H. Col 9 = 'H',
            // 'GH' from original at cols 6,7 now at cols 8,9. Trim
            // trailing spaces on render → all 10 cols are non-blank.
            Assert(s.Render() == "AB  CDEFGH",
                $"ICH inserted 2 blanks, rest shifted right — got {s.Render()}");
        }

        // IL (\e[<n>L) — insert blank lines at cursor row; rows below
        // shift down within the scroll region.
        {
            var s = new VtLiteState(4, 6);
            s.Feed("row1\r\nrow2\r\nrow3".AsSpan());
            // Cursor at end of row3 (row 2, col 4). Move to row 1 col 0.
            s.Feed("\x1b[2;1H".AsSpan());
            s.Feed("\x1b[1L".AsSpan()); // insert 1 blank line at row 1
            // row 0 stays, blank line inserted at row 1, row1's old
            // contents shift to row 2, row3's old contents shift to row 3.
            // (Actually, row 1 had "row2" — that shifts to row 2; row 2
            // had "row3" — that shifts to row 3.)
            Assert(s.Render() == "row1\n\nrow2\nrow3",
                $"IL inserted blank line — got {s.Render().Replace("\n", "\\n")}");
        }

        // DL (\e[<n>M) — delete lines at cursor row; rows below shift
        // up; blank lines fill from bottom of scroll region.
        {
            var s = new VtLiteState(4, 6);
            s.Feed("row1\r\nrow2\r\nrow3".AsSpan());
            s.Feed("\x1b[2;1H".AsSpan()); // cursor at row 1 col 0
            s.Feed("\x1b[1M".AsSpan()); // delete 1 line at row 1
            // row 0 stays "row1"; row 1 was "row2" — deleted; row 2
            // ("row3") shifts up to row 1; row 3 (blank) stays blank.
            Assert(s.Render() == "row1\nrow3",
                $"DL deleted 1 line — got {s.Render().Replace("\n", "\\n")}");
        }

        // ---- robustness ----

        // Feed empty span is a no-op.
        {
            var s = new VtLiteState(10, 40);
            s.Feed("hello".AsSpan());
            var before = (s.Row, s.Col, s.Render());
            s.Feed(ReadOnlySpan<char>.Empty);
            Assert(s.Row == before.Row && s.Col == before.Col && s.Render() == before.Item3,
                "Feed(empty span) is a no-op");
        }

        // Pending-escape overflow: feed a malformed OSC that never
        // terminates and is larger than the pending cap. Cursor must
        // stay sane; subsequent writes must succeed.
        {
            var s = new VtLiteState(10, 40);
            // 32 KB of OSC body with no terminator — exceeds 16 KB cap.
            var bigPayload = new string('x', 32 * 1024);
            s.Feed(("\x1b]0;" + bigPayload).AsSpan());
            // Overflow → pending was dropped; cursor still at origin.
            Assert(s.Row == 0 && s.Col == 0, "pending overflow leaves cursor sane");
            // A subsequent honest write must land.
            s.Feed("ok".AsSpan());
            Assert(s.Render() == "ok", $"post-overflow write succeeds — got {s.Render()}");
        }

        // ---- snapshot / soft-wrap / SGR tracking (renderer baseline) ----

        // Snapshot is independent: mutations after snapshot don't affect it.
        {
            var s = new VtLiteState(10, 20);
            s.Feed("hello\r\nworld".AsSpan());
            var snap = s.Snapshot();
            // Mutate state, snapshot should be unaffected.
            s.Feed("\x1b[2J\x1b[Hwiped".AsSpan());
            // snap.PrimaryGrid[0] should still spell "hello".
            var row0 = new string(snap.PrimaryGrid[0], 0, 5);
            Assert(row0 == "hello", $"snapshot is independent of post-snapshot mutation — got '{row0}'");
            Assert(snap.PRow == 1 && snap.PCol == 5, $"snapshot cursor captured — row={snap.PRow}, col={snap.PCol}");
        }

        // Soft-wrap flag: writes past the right margin set the next row's flag.
        {
            var s = new VtLiteState(10, 5);
            s.Feed("abcdefgh".AsSpan()); // 5 chars row 0, 3 chars row 1 via wrap
            Assert(!s.IsRowSoftWrapped(0), "row 0 not wrap-continued");
            Assert(s.IsRowSoftWrapped(1), "row 1 IS wrap-continued (auto-wrap from row 0)");
        }

        // Hard LF clears soft-wrap flag on destination row.
        {
            var s = new VtLiteState(10, 5);
            s.Feed("abcde\nf".AsSpan()); // 5 chars then explicit LF
            Assert(!s.IsRowSoftWrapped(1), "row 1 NOT wrap-continued after hard LF");
        }

        // Active SGR tracking: simple set + reset.
        {
            var s = new VtLiteState(10, 20);
            s.Feed("\x1b[31m".AsSpan());
            Assert(s.ActiveSgr == "\x1b[31m", $"single SGR captured — got '{s.ActiveSgr.Replace("\x1b", "ESC")}'");
            s.Feed("\x1b[1m".AsSpan());
            Assert(s.ActiveSgr == "\x1b[31m\x1b[1m", "stacked SGRs accumulate");
            s.Feed("\x1b[0m".AsSpan());
            Assert(s.ActiveSgr == "", "explicit \\e[0m clears active SGR");
            s.Feed("\x1b[m".AsSpan());
            Assert(s.ActiveSgr == "", "empty params \\e[m also clears (default reset)");
        }

        // SGR reset-then-set in one sequence: \e[0;1;31m → bold red.
        {
            var s = new VtLiteState(10, 20);
            s.Feed("\x1b[44m".AsSpan());
            Assert(s.ActiveSgr == "\x1b[44m", "background set");
            s.Feed("\x1b[0;1;31m".AsSpan());
            Assert(s.ActiveSgr == "\x1b[1;31m",
                $"compound reset+set keeps post-reset attrs — got '{s.ActiveSgr.Replace("\x1b", "ESC")}'");
        }

        Console.WriteLine($"\n{pass} passed, {fail} failed");
        if (fail > 0) Environment.Exit(1);

        // --- Perf bench (not a pass/fail test; prints throughput so the
        // memo's "Expected overhead: negligible (<5%) but measure on
        // large outputs (1 MB+) before merging" claim has a concrete
        // number attached). Runs after the assertion summary so a bad
        // number doesn't flip a green test run red, but still surfaces
        // in CI / HANDOFF.
        RunFeedBenchmark();
    }

    /// <summary>
    /// Feeds ~1 MB of synthetic PTY-like output (printable + CSI cursor
    /// moves + SGR runs) through Feed() in 4096-char chunks and reports
    /// elapsed time + throughput. Used to sanity-check the allocation-
    /// minimal refactor's overhead claim before shipping Phase 1 on
    /// hot-loop platforms.
    /// </summary>
    private static void RunFeedBenchmark()
    {
        const int TargetBytes = 1 * 1024 * 1024; // 1 MiB
        const int ChunkSize = 4096;

        // Build a representative input pattern repeating a 256-char
        // template that exercises the Feed hot path: SGR (no-op for
        // cursor), short run of printable, cursor-position CSI, more
        // printable, erase-line CSI. Dominated by printable chars the
        // way real shell output is.
        var template = new StringBuilder();
        for (int i = 0; i < 8; i++)
        {
            template.Append("\x1b[38;5;")
                    .Append(31 + i)
                    .Append('m')
                    .Append("abcdefghijklmnop")
                    .Append("\x1b[0m");
            template.Append("\x1b[2;")
                    .Append((i * 13) % 80 + 1)
                    .Append('H');
            template.Append("qrstuvwxyzQRSTUVWXYZ ");
            template.Append("\x1b[K");
        }
        var templateStr = template.ToString();
        var sb = new StringBuilder(TargetBytes + templateStr.Length);
        while (sb.Length < TargetBytes) sb.Append(templateStr);
        var payload = sb.ToString();

        // Fresh VtLiteState, 30×120 — the default production viewport.
        var state = new VtLiteState(30, 120);

        // Warm-up pass so JIT / AOT tiering settles before measurement.
        for (int off = 0; off + ChunkSize <= payload.Length; off += ChunkSize)
            state.Feed(payload.AsSpan(off, ChunkSize));

        state = new VtLiteState(30, 120);
        var sw = Stopwatch.StartNew();
        for (int off = 0; off + ChunkSize <= payload.Length; off += ChunkSize)
            state.Feed(payload.AsSpan(off, ChunkSize));
        sw.Stop();

        double mib = payload.Length / 1024.0 / 1024.0;
        double mibPerSec = mib / sw.Elapsed.TotalSeconds;
        Console.WriteLine(
            $"  BENCH VtLiteState.Feed: {mib:F2} MiB in {sw.Elapsed.TotalMilliseconds:F1} ms " +
            $"({mibPerSec:F0} MiB/s)");
    }
}
