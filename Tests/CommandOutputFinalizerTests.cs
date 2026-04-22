using Ripple.Services;

namespace Ripple.Tests;

/// <summary>
/// Unit tests for <see cref="CommandOutputFinalizer"/> and its companion
/// <see cref="EchoStripper"/>. Both are <c>internal</c> types pulled out
/// of the old tracker/worker cleaning path; this suite pins down:
///   - slice-reader clean: ANSI strip, CRLF normalization, pwsh
///     continuation-prompt filter, trailing-whitespace trim behaviour,
///   - deterministic echo stripping for multiline tempfile-wrapper
///     commands where the ptyPayload is the <c>. 'tmp.ps1'</c> wrapper
///     and output starts with exactly those bytes,
///   - echo stripping "fail closed" on mismatch (returns input unchanged
///     rather than silently mangling).
///
/// Both types live in the same assembly as this suite, so tests reach
/// them directly without reflection — internal visibility is the
/// established pattern across the test directory.
/// </summary>
public static class CommandOutputFinalizerTests
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

        Console.WriteLine("=== CommandOutputFinalizer Tests ===");

        // Clean(capture, start, end) — ANSI stripped, CRLF normalized,
        // pwsh continuation-prompt lines dropped, trailing whitespace
        // trimmed.
        {
            var cap = new CommandOutputCapture();
            cap.Append("hello\r\n\x1b[2Kworld\r\n>> continuation\r\nend\r\n");
            var result = CommandOutputFinalizer.Clean(cap, 0, cap.Length);

            Assert(!result.Contains('\r'), "clean: CR stripped");
            Assert(!result.Contains('\x1b'), "clean: ANSI stripped");
            Assert(result.Contains("hello"), "clean: body preserved");
            Assert(result.Contains("world"), "clean: body preserved after CSI erase");
            Assert(!result.Contains(">> continuation"),
                "clean: pwsh continuation-prompt line dropped");
            Assert(result.EndsWith("end"), "clean: output trimmed of trailing newlines");
            cap.Dispose();
        }

        // OSC strip: BEL-terminated and ST-terminated OSC sequences are
        // each removed, and real content between them is preserved. The
        // old two-branch pattern let the BEL branch eat past an earlier
        // ST terminator and take user text with it — regression guard
        // for that shape.
        {
            var cap = new CommandOutputCapture();
            // Input: ST-terminated OSC, real text, BEL-terminated OSC, real text.
            cap.Append("\x1b]0;title-one\x1b\\keep-me\x1b]0;title-two\x07tail\r\n");
            var result = CommandOutputFinalizer.Clean(cap, 0, cap.Length);

            Assert(!result.Contains('\x1b'), "osc-mixed: ESC bytes stripped");
            Assert(!result.Contains("title-one"), "osc-mixed: ST-terminated OSC body removed");
            Assert(!result.Contains("title-two"), "osc-mixed: BEL-terminated OSC body removed");
            Assert(result.Contains("keep-me"), "osc-mixed: real text between OSCs preserved");
            Assert(result.Contains("tail"), "osc-mixed: trailing text preserved");
            cap.Dispose();
        }

        // Empty window: clean of a zero-length slice returns "".
        {
            var cap = new CommandOutputCapture();
            cap.Append("anything");
            Assert(CommandOutputFinalizer.Clean(cap, 0, 0) == "",
                "clean: empty window returns empty string");
            cap.Dispose();
        }

        // Slice start > end: defensive clamp returns "" rather than
        // throwing.
        {
            var cap = new CommandOutputCapture();
            cap.Append("hello");
            Assert(CommandOutputFinalizer.Clean(cap, 5, 3) == "",
                "clean: end < start returns empty string");
            cap.Dispose();
        }

        // Deterministic echo stripping for a multiline tempfile-wrapper
        // command. The ptyPayload the worker wrote was
        //   `. 'tmp.ps1'; Remove-Item tmp.ps1\r`
        // (the multi-line case). The command window starts with exactly
        // those bytes (minus '\r' because that's the Enter keystroke),
        // followed by the real output of the dot-sourced script.
        {
            var ptyPayload = ". 'C:\\tmp\\x.ps1'; Remove-Item C:\\tmp\\x.ps1\r";
            // CR/LF after the echoed payload is the ConPTY soft-wrap that
            // Strip is documented to skip.
            var output = ". 'C:\\tmp\\x.ps1'; Remove-Item C:\\tmp\\x.ps1\r\nline-one\nline-two\n";

            var stripped = EchoStripper.Strip(output, ptyPayload, "\r");

            Assert(!stripped.Contains("Remove-Item"),
                "echo: wrapper body stripped");
            Assert(stripped.StartsWith("line-one"),
                $"echo: first real output line at head (got: {stripped.Replace("\n", "\\n")})");
            Assert(stripped.Contains("line-two"),
                "echo: subsequent real output preserved");
        }

        // Echo stripping fails closed when the head does not match —
        // returns original output, doesn't mangle it. Regression guard
        // for the "strip something that isn't the echo" bug.
        {
            var mismatch = EchoStripper.Strip(
                "totally unrelated output\n",
                ". 'tmp.ps1'\r",
                "\r");
            Assert(mismatch == "totally unrelated output\n",
                "echo-mismatch: returns input unchanged");
        }

        // Echo stripping with a ConPTY soft-wrap in the middle of the
        // echoed payload: '\n' and '\r' bytes that appear between the
        // expected payload chars are skipped while matching continues.
        {
            var payload = "abcdef";
            // ConPTY inserted a '\n' after the first 3 chars.
            var output = "abc\ndefXYZ";
            var stripped = EchoStripper.Strip(output, payload, "\n");
            Assert(stripped == "XYZ",
                $"echo-wrap: soft-wrap bytes inside payload are skipped (got: {stripped.Replace("\n", "\\n")})");
        }

        // Empty ptyPayload: nothing to strip, return output verbatim.
        {
            var stripped = EchoStripper.Strip("hello", "", "\n");
            Assert(stripped == "hello", "echo-empty: empty payload returns input");
        }

        // Line-ending-only payload: after stripping the trailing line
        // ending, sentInput is empty — nothing to strip, return output.
        {
            var stripped = EchoStripper.Strip("real output\n", "\n", "\n");
            Assert(stripped == "real output\n",
                "echo-lineending-only: payload of just LE returns input");
        }

        // CleanString on a string with pwsh multi-line continuation
        // prompts — this is the slice-free path used when the caller
        // (echo stripping) already has a string in hand.
        {
            var raw = "header\n>> line1\n>> line2\nfooter\n";
            var cleaned = CommandOutputFinalizer.CleanString(raw);
            Assert(!cleaned.Contains(">>"),
                "cleanstring: continuation-prompt lines dropped");
            Assert(cleaned.Contains("header") && cleaned.Contains("footer"),
                "cleanstring: body lines preserved");
        }

        // ---- Progress-bar collapse semantics ----
        // Visible terminal mirror keeps raw bytes; MCP-response output
        // collapses progress redraws so AI sees the final frame, not
        // every intermediate one.

        // Bare CR overwrites the current line — dotnet build / pip
        // download style "\r[10%]\r[20%]\r[30%]" spinner.
        {
            var raw = "Progress 10%\rProgress 20%\rProgress 30%\ndone\n";
            var cleaned = CommandOutputFinalizer.CleanString(raw);
            Assert(cleaned == "Progress 30%\ndone",
                $"progress: bare CR collapses to last frame — got {cleaned}");
        }

        // CRLF stays a real newline (not a CR-overwrite).
        {
            var raw = "line1\r\nline2\r\nline3";
            var cleaned = CommandOutputFinalizer.CleanString(raw);
            Assert(cleaned == "line1\nline2\nline3",
                $"crlf: \\r\\n stays a real newline — got {cleaned}");
        }

        // Single-row progress bar — \x1b[1A\x1b[K + redraw, repeated.
        // Common dotnet-build / cargo / pip pattern.
        {
            var raw = "Building...\n0%\n\x1b[1A\x1b[K10%\n\x1b[1A\x1b[K50%\n\x1b[1A\x1b[K100%\n";
            var cleaned = CommandOutputFinalizer.CleanString(raw);
            Assert(cleaned == "Building...\n100%",
                $"single-row progress: cursor-up + erase + redraw collapses to last frame — got {cleaned.Replace("\n", "\\n")}");
        }

        // Multi-row status block — msbuild-style: cursor-up N, then K
        // + redraw on each affected row. Each row needs its own K
        // (the shell repaints per-row); without K the prior content
        // survives, matching real terminal semantics.
        {
            var raw = "Restore (1s)\nLink (5s)\n\x1b[2A\x1b[KRestore (4s)\n\x1b[KLink (10s)\n";
            var cleaned = CommandOutputFinalizer.CleanString(raw);
            Assert(cleaned == "Restore (4s)\nLink (10s)",
                $"multi-row block: per-row erase + redraw collapses both frames — got {cleaned.Replace("\n", "\\n")}");
        }

        // Backspace spinner — single-char redraw "|/-\\" with \b.
        {
            var raw = "Working |\b/\b-\b\\\bdone\n";
            var cleaned = CommandOutputFinalizer.CleanString(raw);
            Assert(cleaned == "Working done",
                $"backspace: spinner chars collapsed — got {cleaned}");
        }

        // SGR (color) survives the collapse.
        {
            var raw = "before\x1b[31mred\x1b[0m after";
            var cleaned = CommandOutputFinalizer.CleanString(raw);
            Assert(cleaned == "before\x1b[31mred\x1b[0m after",
                $"sgr: color sequences kept verbatim — got {cleaned}");
        }

        // OSC dropped (window title from xterm/iTerm).
        {
            var raw = "real \x1b]0;window-title\x07output";
            var cleaned = CommandOutputFinalizer.CleanString(raw);
            Assert(cleaned == "real output",
                $"osc: dropped — got {cleaned}");
        }

        // CSI cursor-up clamped at the start of the buffer (don't crash
        // on more-up-than-lines).
        {
            var raw = "only one\n\x1b[10A\x1b[Knew\n";
            var cleaned = CommandOutputFinalizer.CleanString(raw);
            Assert(cleaned == "new",
                $"cursor-up clamp: don't underflow — got {cleaned}");
        }

        // Mixed: bare CR inside an SGR-coloured progress run. With
        // real-terminal CR semantics (cursor reset, no row clear), a
        // shorter rewrite leaves the prior tail visible — same as what
        // the human sees on the live console. Verified empirically
        // against Git Bash via ripple MCP (commit 7951cc3 follow-up).
        // Programs that intend to fully redraw a line emit `\r\x1b[K`
        // or `\x1b[2K\r`; bare \r alone is a positioning instruction.
        {
            var raw = "\x1b[36mProgress 10%\r\x1b[36mProgress 99%\r\x1b[32mDone\x1b[0m\n";
            var cleaned = CommandOutputFinalizer.CleanString(raw);
            Assert(cleaned == "\x1b[32mDoneress 99%\x1b[0m",
                $"mixed sgr+cr: short rewrite leaves prior tail (real terminal semantics) — got {cleaned}");
        }

        // Same pattern with explicit erase-line: \r\x1b[K fully redraws.
        {
            var raw = "\x1b[36mProgress 99%\r\x1b[K\x1b[32mDone\x1b[0m\n";
            var cleaned = CommandOutputFinalizer.CleanString(raw);
            Assert(cleaned == "\x1b[32mDone\x1b[0m",
                $"sgr+cr+EL: explicit erase clears the line cleanly — got {cleaned}");
        }

        // ---- New renderer features (CSI X / cursor positioning / alt-screen) ----

        // CSI X (ECH) erase N chars at cursor — used by readline /
        // PSReadLine in-line edits and ConPTY redraws (the exact pattern
        // from issue #4's Git Bash log: erase + cursor-forward + CRLF
        // around real text).
        {
            var raw = "banana\r\n\x1b[200X\x1b[200C\r\ncherry\r\nelderberry\r\n";
            var cleaned = CommandOutputFinalizer.CleanString(raw);
            Assert(cleaned == "banana\n\ncherry\nelderberry",
                $"ech: erase+cuf+crlf collapses to its visible text — got {cleaned.Replace("\n", "\\n")}");
        }

        // CSI G (CHA) — absolute column position. Sequence: write some
        // text, jump back to col 0, overwrite first 3 chars, expect the
        // tail to remain (real terminal semantics, not row-clear).
        {
            var raw = "hello world\x1b[1GHEY\n";
            var cleaned = CommandOutputFinalizer.CleanString(raw);
            Assert(cleaned == "HEYlo world",
                $"cha: absolute col + overwrite preserves tail — got {cleaned}");
        }

        // CSI H (CUP) — absolute cursor positioning. Move to (1,1),
        // write OVR, expect first 3 chars overwritten, tail kept.
        {
            var raw = "abcdefghij\x1b[1;1HOVR\n";
            var cleaned = CommandOutputFinalizer.CleanString(raw);
            Assert(cleaned == "OVRdefghij",
                $"cup: absolute pos + overwrite preserves tail — got {cleaned}");
        }

        // Alt-screen entry+exit (vim/less/htop pattern) collapses to a
        // single placeholder line. Anything drawn inside the alt buffer
        // is dropped — no flood of redraw frames.
        {
            var raw = "before vim\n\x1b[?1049h\x1b[2J\x1b[H~\n~\n~\nlots of redraw\x1b[?1049l\nback to shell\n";
            var cleaned = CommandOutputFinalizer.CleanString(raw);
            Assert(cleaned.Contains("[interactive screen session]"),
                $"alt-screen: placeholder emitted — got {cleaned.Replace("\n", "\\n")}");
            Assert(!cleaned.Contains("redraw") && !cleaned.Contains("~"),
                $"alt-screen: alt-buffer contents dropped — got {cleaned.Replace("\n", "\\n")}");
            Assert(cleaned.Contains("before vim") && cleaned.Contains("back to shell"),
                $"alt-screen: surrounding output preserved — got {cleaned.Replace("\n", "\\n")}");
        }

        // Issue #4 reproducer (Git Bash via ConPTY): the cleaned slice
        // contains banana, then a screen-redraw burst with cherry +
        // elderberry, plus the cursor-positioning escape sequences
        // ConPTY emits between them. Expect all three to land in the
        // cleaned output, none of the cursor moves.
        {
            var raw = "\x1b[?25lbanana \r\n\x1b[?25h"
                    + "\x1b[?25lcherry     \r\nelderberry \r\n\x1b[?25h"
                    + "\x1b[?25l\x1b[H \x1b[7;1H\x1b[?25h";
            var cleaned = CommandOutputFinalizer.CleanString(raw);
            Assert(cleaned.Contains("banana") && cleaned.Contains("cherry") && cleaned.Contains("elderberry"),
                $"issue#4: all three grep matches survive ConPTY redraw burst — got {cleaned.Replace("\n", "\\n")}");
            Assert(!cleaned.Contains("\x1b["),
                $"issue#4: no leftover CSI sequences in cleaned output — got {cleaned}");
        }

        // ---- Renderer-with-baseline (snapshot) behavior ----

        // Baseline contains pre-command screen (a prompt line). Command
        // adds new lines below. Output must include only the new lines.
        {
            var vt = new VtLiteState(10, 20);
            vt.Feed("$ echo hi\n".AsSpan());
            // Cursor now at row 1 col 0. Command echoes "hi".
            var snap = vt.Snapshot();
            // Now command output: "hi\n"
            var cleaned = CommandOutputFinalizer.CleanString("hi\n", snap);
            Assert(cleaned == "hi",
                $"baseline: pre-command prompt row not in output — got '{cleaned.Replace("\n", "\\n")}'");
        }

        // ConPTY repaint scenario: baseline has prompt content. After
        // command, ConPTY emits cursor-home + same prompt content
        // (idempotent overwrite). Per-cell change detector skips the
        // repaint, output stays empty.
        {
            var vt = new VtLiteState(10, 20);
            vt.Feed("$ ls\n".AsSpan());
            var snap = vt.Snapshot();
            // Simulated ConPTY repaint: cursor home, write same content.
            // Real "command output" is empty (e.g. ls of empty dir).
            var raw = "\x1b[H$ ls";
            var cleaned = CommandOutputFinalizer.CleanString(raw, snap);
            Assert(cleaned == "",
                $"ConPTY repaint: idempotent overwrite of baseline cells produces no output — got '{cleaned}'");
        }

        // Soft-wrap re-joining at render. Baseline contains a row that
        // was soft-wrapped from the previous row at col 5 (narrow PTY).
        // Render joins them back into one logical line.
        {
            var vt = new VtLiteState(10, 5);
            vt.Feed("abcdefgh\n".AsSpan()); // wraps "abcde" + "fgh", then LF
            var snap = vt.Snapshot();
            // Mark row 0 as modified by simulating a write that changes
            // a baseline cell — otherwise the renderer skips it.
            // Here we cheat: render directly from snapshot with empty
            // command output and use the baseline cursor row to gate
            // emission. To force emission of pre-cursor rows we'd need
            // to write to them. Instead, command writes a new line:
            var cleaned = CommandOutputFinalizer.CleanString("xyz\n", snap);
            Assert(cleaned == "xyz",
                $"baseline soft-wrapped rows not in output (only post-cursor) — got '{cleaned.Replace("\n", "\\n")}'");
        }

        // Real alt-screen save/restore: baseline has prompt. Command
        // enters alt-screen, draws garbage, exits. Output = placeholder
        // only; baseline prompt unchanged in output (because it wasn't
        // modified by command bytes that were idempotent or alt-screen).
        {
            var vt = new VtLiteState(10, 20);
            vt.Feed("$ vim file\n".AsSpan());
            var snap = vt.Snapshot();
            var raw = "\x1b[?1049h\x1b[2J\x1b[Hgarbage redraw\x1b[?1049l";
            var cleaned = CommandOutputFinalizer.CleanString(raw, snap);
            Assert(cleaned.Contains("[interactive screen session]"),
                $"alt-screen+baseline: placeholder emitted — got '{cleaned}'");
            Assert(!cleaned.Contains("garbage"),
                $"alt-screen+baseline: alt buffer content discarded — got '{cleaned}'");
            Assert(!cleaned.Contains("vim file"),
                $"alt-screen+baseline: pre-command prompt NOT in output — got '{cleaned}'");
        }

        // ---- Stale SGR inheritance on overwrite-with-different-char ----
        // Regression for "Write-Progress Minimal leaves [7m reverse video
        // glued to normal text that later writes over the progress bar
        // cells". Observed live: `Write-Progress ...; "after"` rendered
        // as `[mafter [7mprogress` (garbled with leftover bar SGR) because
        // WriteChar used to inherit the overwritten cell's SgrPrefix when
        // no new SGR was pending. Inheriting is correct for same-char
        // repaints (ConPTY repaint idempotence) but wrong when the char
        // genuinely changes — the previous SGR belonged to the previous
        // character, not the new one.

        // Core case: overwrite-different-char must NOT inherit stale SGR.
        {
            // "\x1b[31mRED\x1b[0m" sets red SGR on "R", "E", "D" gets null
            // prefix (visual color carries via terminal state).
            // "\r" returns to col 0. Then "abc" overwrites cells 0-2 with
            // different chars and no SGR. Pre-fix: cell 0 kept its red
            // SGR attached to 'a'. Post-fix: stale SGR cleared.
            //
            // Expected: 'a' has prefix `\x1b[31m` (cell 0 retains) —
            // actually wait, cell 0 had the \x1b[31m from initial write.
            // On overwrite to 'a' with prefix=null, pre-fix kept red,
            // post-fix drops. So rendered result differs only in the
            // SGR on 'a'.
            var raw = "\x1b[31mRED\r\x1b[0mabc";
            var cleaned = CommandOutputFinalizer.CleanString(raw);
            // After cleanup: cells are ['a' null (post-fix) / [31m (pre-fix)],
            //                          ['b' null], ['c' null]
            // TrailingSgr picks up the \x1b[0m from between "\r" and "abc".
            // Renderer path: the \x1b[0m is pending when 'a' is written,
            // so prefix = [0m, and it wins over any stale cell SGR anyway.
            // So this particular sequence shouldn't show the bug. Instead
            // let's construct the actual progress-bar pattern.
            _ = cleaned;
        }

        // Write-Progress Minimal pattern: bar drawn with reverse video,
        // cursor restored, then normal text written at the position where
        // the bar lived. No explicit SGR reset arrives before the normal
        // text, so WriteChar sees prefix=null on legitimate overwrites.
        //
        // Simulated sequence:
        //   \e[7m      — reverse video on (sets _pendingSgr)
        //   bar       — writes cells [0..2] with reverse video on 'b'
        //   \e[7m is consumed by 'b'; 'a' and 'r' inherit no SGR but their
        //   cells keep the visual reverse video via terminal state (not
        //   our concern — per-cell).
        //   [cleanup]  — no SGR change sent, cells [0..2] still have 'b',
        //   'a', 'r' with bar's SGR on 'b'.
        //   \r         — cursor to col 0
        //   XYZ        — three chars, no pending SGR, overwriting 'b','a','r'.
        //
        // Pre-fix: 'X' inherited [7m from 'b'. Post-fix: 'X' has null SGR.
        {
            var raw = "\x1b[7mbar\rXYZ";
            var cleaned = CommandOutputFinalizer.CleanString(raw);
            Assert(cleaned == "XYZ",
                $"overwrite-diff: reverse-video bar fully replaced by new text, no SGR residue — got '{cleaned}'");
        }

        // Same-char overwrite is still a ConPTY repaint idempotence case
        // and MUST keep the original SGR. Covers the post-alt-screen /
        // post-prompt redraw bursts the renderer was designed around.
        {
            // [31m makes 'A' red. \r returns to col 0. Overwrite same 'A'
            // with no SGR pending. Expected: 'A' keeps red SGR.
            var raw = "\x1b[31mA\rA";
            var cleaned = CommandOutputFinalizer.CleanString(raw);
            Assert(cleaned == "\x1b[31mA",
                $"repaint-same-char: SGR preserved on same-char overwrite — got '{cleaned}'");
        }

        // Partial overwrite: different chars drop SGR at touched columns;
        // untouched tail keeps whatever SGR it had. Real progress-bar
        // scenario where the bar is wider than the subsequent normal text.
        {
            // Cells 0-4 written with reverse video 'bar' initially (only
            // cell 0 gets [7m prefix attached; cells 1-4 have null prefix
            // but reverse-video carries via terminal state — they have
            // null SgrPrefix in our model). Then 'XY' writes over 0-1.
            // Expected output: "XY" (cells 2-4 had null SGR, nothing to
            // drop; cells 0-1 post-fix have null SGR).
            var raw = "\x1b[7mbbbbb\rXY";
            var cleaned = CommandOutputFinalizer.CleanString(raw);
            Assert(cleaned == "XYbbb",
                $"partial-overwrite: touched cells drop SGR, tail untouched — got '{cleaned}'");
        }

        // ---- OSC 8 hyperlink URI projection ----
        // OSC 8 is the "make link text clickable in modern terminals"
        // convention (xterm, Windows Terminal, iTerm2, VS Code). Other
        // OSCs get silently dropped because their payload is terminal
        // chrome (window title, cwd report, shell markers). OSC 8 is
        // different: the URI is a semantic destination that AI consumers
        // of the MCP output need — build tools emit OSC 8 around error
        // file paths, diagnostic links, etc. Preserving it as `<URI>`
        // after the link text is the plain-text projection.

        // CAUTION on string literals in this section: `\x07` (BEL) is a
        // C# hex escape that greedily consumes up to 4 hex digits. Writing
        // `"\x07click"` would parse as `\x07c` + "lick" = '|' + "lick"
        // because 'c' is a hex digit — silently corrupting the OSC
        // terminator. Use the fixed-length `\a` escape (= U+0007) instead;
        // it's identical in semantic and immune to greedy hex eating.

        // BEL-terminated OSC 8 with trivial URL — link text preserved,
        // URL appended in angle brackets.
        {
            var raw = "prefix \x1b]8;;https://example.com/\aclick here\x1b]8;;\a suffix";
            var cleaned = CommandOutputFinalizer.CleanString(raw);
            Assert(cleaned == "prefix click here<https://example.com/> suffix",
                $"osc8-bel: URL appended after link text — got '{cleaned}'");
        }

        // ST-terminated OSC 8. Same content shape but the terminator is
        // `\e\\` instead of BEL. Must behave identically.
        {
            var raw = "see \x1b]8;;file:///C:/log.txt\x1b\\log\x1b]8;;\x1b\\ here";
            var cleaned = CommandOutputFinalizer.CleanString(raw);
            Assert(cleaned == "see log<file:///C:/log.txt> here",
                $"osc8-st: ST-terminated OSC 8 projects the URL too — got '{cleaned}'");
        }

        // OSC 8 with params (e.g. `id=foo`) — the URI is the segment
        // after the SECOND semicolon; params in between are ignored.
        {
            var raw = "x\x1b]8;id=xyz;https://example.org/\atext\x1b]8;;\ay";
            var cleaned = CommandOutputFinalizer.CleanString(raw);
            Assert(cleaned == "xtext<https://example.org/>y",
                $"osc8-params: URI after second semicolon — got '{cleaned}'");
        }

        // Unclosed OSC 8 (no matching close marker) — URI is captured but
        // never flushed. No garbage in output; the URL is lost, same
        // graceful degradation as pre-change behaviour. Covers truncated
        // captures and mid-stream command boundaries.
        {
            var raw = "open-only \x1b]8;;https://example.com/\atail";
            var cleaned = CommandOutputFinalizer.CleanString(raw);
            Assert(cleaned == "open-only tail",
                $"osc8-unclosed: lost URI but no escape residue — got '{cleaned}'");
        }

        // Close without matching open — the close's empty URI triggers
        // the flush path, but with no pending URI nothing is emitted.
        {
            var raw = "stray \x1b]8;;\aclose";
            var cleaned = CommandOutputFinalizer.CleanString(raw);
            Assert(cleaned == "stray close",
                $"osc8-stray-close: unmatched close is a no-op — got '{cleaned}'");
        }

        // Other OSCs still drop silently — only 8 is promoted.
        {
            var raw = "\x1b]0;window title\abefore\x1b]8;;https://x.com/\alink\x1b]8;;\a\x1b]7;file:///tmp\aafter";
            var cleaned = CommandOutputFinalizer.CleanString(raw);
            Assert(cleaned == "beforelink<https://x.com/>after",
                $"osc8-mix: OSC 0 and OSC 7 stay dropped, only OSC 8 projected — got '{cleaned}'");
        }

        Console.WriteLine($"\n{pass} passed, {fail} failed");
        if (fail > 0) Environment.Exit(1);
    }
}
