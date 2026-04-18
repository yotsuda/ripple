using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Ripple.Services;

namespace Ripple.Tests;

/// <summary>
/// E2E test: launch ConsoleWorker in --console mode, send commands via Named Pipe.
/// Tests ConPTY + shell integration + OSC parsing + command tracking.
/// </summary>
public class ConsoleWorkerTests
{
    /// <summary>
    /// Quick unit tests for UnescapeInput — runs without PTY/pipe setup.
    /// </summary>
    public static void RunUnitTests()
    {
        var pass = 0;
        var fail = 0;
        void Assert(bool condition, string name)
        {
            if (condition) { pass++; Console.WriteLine($"  PASS: {name}"); }
            else { fail++; Console.Error.WriteLine($"  FAIL: {name}"); }
        }

        Console.WriteLine("=== ConsoleWorker Unit Tests ===");

        // UnescapeInput
        Assert(ConsoleWorker.UnescapeInput("hello") == "hello", "unescape: plain text unchanged");
        Assert(ConsoleWorker.UnescapeInput("abc\\r") == "abc\r", "unescape: \\r → CR");
        Assert(ConsoleWorker.UnescapeInput("abc\\n") == "abc\n", "unescape: \\n → LF");
        Assert(ConsoleWorker.UnescapeInput("abc\\t") == "abc\t", "unescape: \\t → TAB");
        Assert(ConsoleWorker.UnescapeInput("\\x03") == "\x03", "unescape: \\x03 → ETX");
        Assert(ConsoleWorker.UnescapeInput("\\x1b[A") == "\x1b[A", "unescape: \\x1b[A → ESC[A");
        Assert(ConsoleWorker.UnescapeInput("a\\\\b") == "a\\b", "unescape: \\\\\\\\ → literal backslash");
        Assert(ConsoleWorker.UnescapeInput("\\r").Length == 1, "unescape: \\r length is 1");
        Assert(ConsoleWorker.UnescapeInput("\\x03").Length == 1, "unescape: \\x03 length is 1");

        // StripCmdInputEcho — strips ConPTY's input-echo prefix from cmd output.
        Assert(ConsoleWorker.StripCmdInputEcho("echo hello\r\nhello\r\n", "echo hello") == "hello\r\n",
            "strip cmd echo: simple single-line");
        Assert(ConsoleWorker.StripCmdInputEcho("echo hello\nhello\n", "echo hello") == "hello\n",
            "strip cmd echo: LF-only newlines");
        Assert(ConsoleWorker.StripCmdInputEcho("set\nVAR1=a\nVAR2=b\n", "set") == "VAR1=a\nVAR2=b\n",
            "strip cmd echo: command with empty args");
        Assert(ConsoleWorker.StripCmdInputEcho("echo hello world\n hello world\n", "echo hello world") == " hello world\n",
            "strip cmd echo: only first matching prefix is consumed");
        // Line wrap: ConPTY inserts \n mid-echo when the typed command exceeds terminal width.
        Assert(ConsoleWorker.StripCmdInputEcho("dir /b *.cs\n*.csproj\nProgram.cs\n", "dir /b *.cs") == "*.csproj\nProgram.cs\n",
            "strip cmd echo: trailing newline after echo is dropped");
        Assert(ConsoleWorker.StripCmdInputEcho("echo abc def\nghi\n", "echo abc defghi") == "",
            "strip cmd echo: wrap-fold absorbs entire output");
        Assert(ConsoleWorker.StripCmdInputEcho("real output", "echo no match") == "real output",
            "strip cmd echo: mismatch returns original output");
        Assert(ConsoleWorker.StripCmdInputEcho("", "echo something") == "",
            "strip cmd echo: empty output returns empty");
        Assert(ConsoleWorker.StripCmdInputEcho("hello", "") == "hello",
            "strip cmd echo: empty sent-input returns original");

        // ReplaceOscTitle — rewrites shell-emitted OSC 0/1/2 title
        // sequences with the proxy-supplied desired title so shells
        // like bash that encode `user@host: cwd` in their PROMPT_COMMAND
        // can't clobber the owned-console window title ripple just set.
        // The function runs on every chunk the read loop produces, so
        // split-chunk inputs are a realistic failure mode — covered
        // below. Each test declares its own `tail` so the scenarios
        // stay isolated; real callers (MirrorToVisible) thread a
        // single _oscTitlePending field across every chunk.
        const string desired = "#12345 Dolphin";

        // Fully-terminated OSC 0 + BEL: rewritten, tail stays empty.
        {
            var tail = "";
            var input = "before\x1b]0;shell-title\aafter";
            var expected = "before\x1b]0;" + desired + "\aafter";
            Assert(ConsoleWorker.ReplaceOscTitle(input, desired, ref tail) == expected,
                "title: OSC 0 BEL fully rewritten");
            Assert(tail == "", "title: OSC 0 BEL leaves tail empty");
        }

        // Fully-terminated OSC 2 + BEL: rewritten (OSC 2 = set icon+title on xterm).
        {
            var tail = "";
            var input = "x\x1b]2;shell-title\ay";
            var expected = "x\x1b]2;" + desired + "\ay";
            Assert(ConsoleWorker.ReplaceOscTitle(input, desired, ref tail) == expected,
                "title: OSC 2 BEL fully rewritten");
        }

        // Fully-terminated OSC 1 + BEL: rewritten (OSC 1 = set icon-name on xterm).
        {
            var tail = "";
            var input = "x\x1b]1;shell-title\ay";
            var expected = "x\x1b]1;" + desired + "\ay";
            Assert(ConsoleWorker.ReplaceOscTitle(input, desired, ref tail) == expected,
                "title: OSC 1 BEL fully rewritten");
        }

        // OSC 0 with ST (String Terminator) instead of BEL: the ESC \
        // terminator style is also legal per the VT spec; some modern
        // shells use it. Must be recognized and preserved.
        {
            var tail = "";
            var input = "a\x1b]0;shell-title\x1b\\b";
            var expected = "a\x1b]0;" + desired + "\x1b\\b";
            Assert(ConsoleWorker.ReplaceOscTitle(input, desired, ref tail) == expected,
                "title: OSC 0 ST (ESC\\) fully rewritten");
        }

        // OSC 633 (shell integration) must NOT be rewritten — only
        // 0/1/2 are title sequences. Other OSC types flow through
        // untouched.
        {
            var tail = "";
            var input = "\x1b]633;A\acommand output\x1b]633;D;0\a";
            Assert(ConsoleWorker.ReplaceOscTitle(input, desired, ref tail) == input,
                "title: OSC 633 passes through untouched");
        }

        // OSC 7 (pwd update on some terminals) must NOT be rewritten.
        {
            var tail = "";
            var input = "\x1b]7;file:///tmp\a";
            Assert(ConsoleWorker.ReplaceOscTitle(input, desired, ref tail) == input,
                "title: OSC 7 passes through untouched");
        }

        // desiredTitle null = passthrough (used before proxy claim).
        {
            var tail = "";
            var input = "\x1b]0;shell-title\arest";
            Assert(ConsoleWorker.ReplaceOscTitle(input, null, ref tail) == input,
                "title: null desiredTitle is pure passthrough");
        }

        // Two OSC 0 sequences in one chunk are both rewritten.
        {
            var tail = "";
            var input = "\x1b]0;first\amiddle\x1b]0;second\atail";
            var expected = "\x1b]0;" + desired + "\amiddle\x1b]0;" + desired + "\atail";
            Assert(ConsoleWorker.ReplaceOscTitle(input, desired, ref tail) == expected,
                "title: two OSC 0 in one chunk both rewritten");
        }

        // Empty title string inside OSC 0: valid per VT spec, must still rewrite.
        {
            var tail = "";
            var input = "\x1b]0;\arest";
            var expected = "\x1b]0;" + desired + "\arest";
            Assert(ConsoleWorker.ReplaceOscTitle(input, desired, ref tail) == expected,
                "title: empty OSC 0 body still rewritten");
        }

        // OSC 0 at the very start of the chunk.
        {
            var tail = "";
            var input = "\x1b]0;x\aafter";
            var expected = "\x1b]0;" + desired + "\aafter";
            Assert(ConsoleWorker.ReplaceOscTitle(input, desired, ref tail) == expected,
                "title: OSC 0 at chunk start");
        }

        // OSC 0 at the very end of the chunk (BEL is the last byte).
        {
            var tail = "";
            var input = "before\x1b]0;x\a";
            var expected = "before\x1b]0;" + desired + "\a";
            Assert(ConsoleWorker.ReplaceOscTitle(input, desired, ref tail) == expected,
                "title: OSC 0 at chunk end");
        }

        // 3-digit OSC like \x1b]112;... (set cursor color) must NOT
        // match because the byte after `1` is `1`, not `;`. Regression
        // guard — the length check in the matcher has to be strict.
        {
            var tail = "";
            var input = "\x1b]112;rgb:ff/00/00\a";
            Assert(ConsoleWorker.ReplaceOscTitle(input, desired, ref tail) == input,
                "title: OSC 112 (3-digit, not a title) passes through");
        }

        // Plain text without any OSC is unchanged.
        {
            var tail = "";
            var input = "hello world\nno escapes here";
            Assert(ConsoleWorker.ReplaceOscTitle(input, desired, ref tail) == input,
                "title: plain text passes through");
        }

        // Empty input.
        {
            var tail = "";
            Assert(ConsoleWorker.ReplaceOscTitle("", desired, ref tail) == "",
                "title: empty input passes through");
        }

        // === Split-chunk recovery ===
        // These scenarios exercise the cross-chunk buffering path.
        // The PTY read loop can cut an OSC title sequence anywhere:
        // between ESC and ], between ] and the type byte, between the
        // type byte and the semicolon, mid-body, or right at the
        // terminator. Each split must produce the SAME final visible
        // stream as if the sequence had arrived whole — the desired
        // title rewritten in place, the shell's title completely
        // hidden.

        // Opener fully in chunk 1, terminator in chunk 2.
        {
            var tail = "";
            var out1 = ConsoleWorker.ReplaceOscTitle("prefix \x1b]0;shell-", desired, ref tail);
            Assert(!out1.Contains("shell-"),
                "title-split: opener+body buffered, chunk1 emits nothing from the partial");
            Assert(out1 == "prefix ",
                $"title-split: chunk1 emits only the prefix before the OSC (got: {out1})");
            Assert(tail.StartsWith("\x1b]0;"),
                "title-split: tail holds the opener");

            var out2 = ConsoleWorker.ReplaceOscTitle("title\asuffix", desired, ref tail);
            Assert(out2 == "\x1b]0;" + desired + "\asuffix",
                $"title-split: chunk2 emits rewritten title + suffix (got: {out2})");
            Assert(tail == "",
                "title-split: tail cleared once terminator seen");
        }

        // Split between \x1b and ] — the raw ESC at end of chunk 1
        // must also be buffered, not emitted as a bare ESC (which
        // would leave the terminal in "waiting for sequence" state).
        {
            var tail = "";
            var out1 = ConsoleWorker.ReplaceOscTitle("before\x1b", desired, ref tail);
            Assert(out1 == "before",
                "title-split: lone trailing ESC buffered, not emitted");
            Assert(tail == "\x1b",
                "title-split: tail holds the lone ESC");

            var out2 = ConsoleWorker.ReplaceOscTitle("]0;x\arest", desired, ref tail);
            Assert(out2 == "\x1b]0;" + desired + "\arest",
                $"title-split: chunk2 reassembles ESC + ]0;x + BEL (got: {out2})");
            Assert(tail == "",
                "title-split: tail cleared after reassembly");
        }

        // Split between ] and the type byte — ambiguous until the
        // type arrives, so buffer.
        {
            var tail = "";
            var out1 = ConsoleWorker.ReplaceOscTitle("\x1b]", desired, ref tail);
            Assert(out1 == "",
                "title-split: \\x1b] alone buffered (can't classify type yet)");
            Assert(tail == "\x1b]",
                "title-split: tail holds \\x1b]");

            var out2 = ConsoleWorker.ReplaceOscTitle("2;x\arest", desired, ref tail);
            Assert(out2 == "\x1b]2;" + desired + "\arest",
                $"title-split: chunk2 reassembles OSC 2 (got: {out2})");
        }

        // Split right BEFORE the terminator (body incomplete).
        {
            var tail = "";
            var out1 = ConsoleWorker.ReplaceOscTitle("output \x1b]0;a-very-long-shell-title", desired, ref tail);
            Assert(out1 == "output ",
                $"title-split: body-only chunk emits only the prefix (got: {out1})");
            Assert(tail == "\x1b]0;a-very-long-shell-title",
                "title-split: tail holds opener + partial body");

            var out2 = ConsoleWorker.ReplaceOscTitle(" continued\atail", desired, ref tail);
            Assert(out2 == "\x1b]0;" + desired + "\atail",
                $"title-split: chunk2 produces the rewritten title + post-terminator tail (got: {out2})");
        }

        // Split right AFTER the BEL (nothing to carry over).
        {
            var tail = "";
            var out1 = ConsoleWorker.ReplaceOscTitle("\x1b]0;shell\a", desired, ref tail);
            Assert(out1 == "\x1b]0;" + desired + "\a",
                "title-split: chunk ending exactly at BEL is fully rewritten in-place");
            Assert(tail == "",
                "title-split: no tail carried over when chunk ends at terminator");
        }

        // Non-title OSC must NOT be buffered across chunks — it's
        // passed through immediately. Regression guard against the
        // buffer logic over-reaching to OSC 633/7/112.
        {
            var tail = "";
            var out1 = ConsoleWorker.ReplaceOscTitle("\x1b]633;A\a", desired, ref tail);
            Assert(out1 == "\x1b]633;A\a",
                "title-split: OSC 633 passes through in one chunk without buffering");
            Assert(tail == "",
                "title-split: OSC 633 does not leave residual tail");
        }

        // Split ST (ESC \) terminator: \x1b in chunk 1, \\ in chunk 2.
        // The classic VT string terminator is two bytes, so the split
        // point can be between them.
        {
            var tail = "";
            var out1 = ConsoleWorker.ReplaceOscTitle("\x1b]0;shell\x1b", desired, ref tail);
            Assert(out1 == "",
                $"title-split: ESC+body+ESC with ST pending is fully buffered (got: {out1})");
            Assert(tail == "\x1b]0;shell\x1b",
                "title-split: tail holds opener + body + leading ESC of ST");

            var out2 = ConsoleWorker.ReplaceOscTitle("\\rest", desired, ref tail);
            Assert(out2 == "\x1b]0;" + desired + "\x1b\\rest",
                $"title-split: ST completes in chunk2 (got: {out2})");
        }

        // AnswerAndStripDsr — DSR (\x1b[6n) scan + strip with a 3-char
        // partial-prefix buffer for chunks where the 4-byte query
        // straddles two PTY reads. Each test drives the pure static
        // helper with a counter action so the "how many replies fired"
        // check is independent of the PTY plumbing.

        // Fast path: no ESC → no allocation, forward untouched.
        {
            var pending = "";
            int replies = 0;
            var s = ConsoleWorker.AnswerAndStripDsr("plain text", ref pending, () => replies++);
            Assert(s == "plain text", "dsr: no-ESC chunk returned untouched");
            Assert(pending == "" && replies == 0, "dsr: no-ESC chunk leaves no state");
        }

        // Single complete DSR in one chunk: reply fires, query stripped.
        {
            var pending = "";
            int replies = 0;
            var s = ConsoleWorker.AnswerAndStripDsr("a\x1b[6nb", ref pending, () => replies++);
            Assert(s == "ab", $"dsr: single complete DSR stripped (got {s})");
            Assert(replies == 1, "dsr: single DSR fires one reply");
            Assert(pending == "", "dsr: single DSR leaves no pending");
        }

        // Two DSRs in one chunk: two replies, both stripped.
        {
            var pending = "";
            int replies = 0;
            var s = ConsoleWorker.AnswerAndStripDsr("x\x1b[6ny\x1b[6nz", ref pending, () => replies++);
            Assert(s == "xyz", $"dsr: two DSRs both stripped (got {s})");
            Assert(replies == 2, "dsr: two DSRs fire two replies");
        }

        // Split at boundary 1: "\x1b" | "[6n".
        {
            var pending = "";
            int replies = 0;
            var s1 = ConsoleWorker.AnswerAndStripDsr("abc\x1b", ref pending, () => replies++);
            Assert(s1 == "abc", $"dsr-split@1: first chunk stripped to 'abc' (got {s1})");
            Assert(pending == "\x1b", "dsr-split@1: pending holds ESC");
            Assert(replies == 0, "dsr-split@1: no reply yet");

            var s2 = ConsoleWorker.AnswerAndStripDsr("[6nxyz", ref pending, () => replies++);
            Assert(s2 == "xyz", $"dsr-split@1: second chunk completes + strips (got {s2})");
            Assert(pending == "", "dsr-split@1: pending cleared after completion");
            Assert(replies == 1, "dsr-split@1: exactly one reply across the boundary");
        }

        // Split at boundary 2: "\x1b[" | "6n".
        {
            var pending = "";
            int replies = 0;
            var s1 = ConsoleWorker.AnswerAndStripDsr("pre\x1b[", ref pending, () => replies++);
            Assert(s1 == "pre", $"dsr-split@2: prefix-stripped (got {s1})");
            Assert(pending == "\x1b[", "dsr-split@2: pending holds ESC[");

            var s2 = ConsoleWorker.AnswerAndStripDsr("6nrest", ref pending, () => replies++);
            Assert(s2 == "rest" && pending == "" && replies == 1,
                $"dsr-split@2: completes + strips (got {s2}, pending={pending}, replies={replies})");
        }

        // Split at boundary 3: "\x1b[6" | "n".
        {
            var pending = "";
            int replies = 0;
            var s1 = ConsoleWorker.AnswerAndStripDsr("hello\x1b[6", ref pending, () => replies++);
            Assert(s1 == "hello", $"dsr-split@3: prefix-stripped (got {s1})");
            Assert(pending == "\x1b[6", "dsr-split@3: pending holds ESC[6");

            var s2 = ConsoleWorker.AnswerAndStripDsr("nworld", ref pending, () => replies++);
            Assert(s2 == "world" && pending == "" && replies == 1,
                $"dsr-split@3: completes + strips (got {s2}, pending={pending}, replies={replies})");
        }

        // Partial-looking tail that doesn't complete to DSR on the next
        // chunk: buffered bytes must be flushed back to the output, NOT
        // silently dropped.
        {
            var pending = "";
            int replies = 0;
            var s1 = ConsoleWorker.AnswerAndStripDsr("abc\x1b[6", ref pending, () => replies++);
            Assert(s1 == "abc" && pending == "\x1b[6", "dsr-false-partial: first chunk holds prefix");

            var s2 = ConsoleWorker.AnswerAndStripDsr("XYZ", ref pending, () => replies++);
            Assert(s2 == "\x1b[6XYZ",
                $"dsr-false-partial: ESC[6 flushed when X doesn't match n (got {s2})");
            Assert(pending == "" && replies == 0,
                "dsr-false-partial: no pending, no reply fired");
        }

        // ESC mid-chunk that is NOT a DSR prefix — should not be held.
        // "\x1b[K" (EL) is a legitimate CSI that must flow through.
        {
            var pending = "";
            int replies = 0;
            var s = ConsoleWorker.AnswerAndStripDsr("a\x1b[Kb", ref pending, () => replies++);
            Assert(s == "a\x1b[Kb", $"dsr: non-DSR CSI flows through (got {s})");
            Assert(pending == "" && replies == 0, "dsr: non-DSR CSI leaves no state");
        }

        // DSR immediately preceded by an unrelated CSI — both must be
        // distinguished. The scan should find the DSR, fire once, and
        // leave the other CSI untouched.
        {
            var pending = "";
            int replies = 0;
            var s = ConsoleWorker.AnswerAndStripDsr("\x1b[Ka\x1b[6nb", ref pending, () => replies++);
            Assert(s == "\x1b[Kab", $"dsr: CSI + DSR — only DSR stripped (got {s})");
            Assert(replies == 1, "dsr: exactly one reply for the one DSR");
        }

        // Three-way split across three chunks: "\x1b" | "[" | "6n".
        // Realistic pathological case for extremely small PTY reads.
        {
            var pending = "";
            int replies = 0;
            var s1 = ConsoleWorker.AnswerAndStripDsr("a\x1b", ref pending, () => replies++);
            Assert(s1 == "a" && pending == "\x1b", "dsr-3way: chunk1");

            var s2 = ConsoleWorker.AnswerAndStripDsr("[", ref pending, () => replies++);
            Assert(s2 == "" && pending == "\x1b[", "dsr-3way: chunk2 advances prefix");

            var s3 = ConsoleWorker.AnswerAndStripDsr("6nb", ref pending, () => replies++);
            Assert(s3 == "b" && pending == "" && replies == 1,
                $"dsr-3way: chunk3 completes (got {s3}, replies={replies})");
        }

        Console.WriteLine($"\n{pass} passed, {fail} failed");
        if (fail > 0) Environment.Exit(1);
    }

    public static async Task Run()
    {
        var pass = 0;
        var fail = 0;

        void Assert(bool condition, string name)
        {
            if (condition) { pass++; Console.WriteLine($"  PASS: {name}"); }
            else { fail++; Console.Error.WriteLine($"  FAIL: {name}"); }
        }

        Console.WriteLine("=== ConsoleWorker E2E Tests ===");

        // Find ripple executable
        var exePath = Process.GetCurrentProcess().MainModule?.FileName;
        if (exePath == null)
        {
            Console.Error.WriteLine("  SKIP: Cannot determine exe path");
            return;
        }

        var proxyPid = Environment.ProcessId;
        var agentId = "test";
        var shell = OperatingSystem.IsWindows() ? "pwsh.exe" : "bash";
        var cwd = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // Launch worker via ProcessLauncher (uses CREATE_NEW_CONSOLE on Windows,
        // required so the worker process has a console for ConPTY to attach to)
        var launcher = new Services.ProcessLauncher();
        int workerPid = launcher.LaunchConsoleWorker(proxyPid, agentId, shell, cwd);

        var pipeName = $"RP.{proxyPid}.{agentId}.{workerPid}";
        Console.WriteLine($"  Pipe: {pipeName}, Worker PID: {workerPid}");

        // Wait for pipe to be ready (up to 30s)
        Console.WriteLine("  Waiting for pipe...");
        var ready = await WaitForPipeAsync(pipeName, TimeSpan.FromSeconds(30));
        Assert(ready, "Worker pipe became ready");

        if (!ready)
        {
            try { Process.GetProcessById(workerPid).Kill(); } catch { }
            Console.WriteLine($"\n{pass} passed, {fail} failed");
            if (fail > 0) Environment.Exit(1);
            return;
        }

        // Test 1: ping
        {
            var resp = await SendRequest(pipeName, w => w.WriteString("type", "ping"));
            var status = resp.TryGetProperty("status", out var s) ? s.GetString() : null;
            Assert(status == "ok", "Ping returns ok");
        }

        // Test 2: get_status (should be standby)
        {
            var resp = await SendRequest(pipeName, w => w.WriteString("type", "get_status"));
            var status = resp.TryGetProperty("status", out var s) ? s.GetString() : null;
            Assert(status == "standby", $"Status is standby (got: {status})");
        }

        // Test 3: execute simple command
        {
            var command = OperatingSystem.IsWindows() ? "Write-Output 'hello ripple'" : "echo 'hello ripple'";
            Console.WriteLine($"  Executing: {command}");
            var resp = await SendRequest(pipeName, w => { w.WriteString("type", "execute"); w.WriteString("command", command); w.WriteNumber("timeout", 10000); }, TimeSpan.FromSeconds(15));

            var output = resp.TryGetProperty("output", out var o) ? o.GetString() ?? "" : "";
            var timedOut = resp.TryGetProperty("timedOut", out var t) && t.GetBoolean();
            var exitCode = resp.TryGetProperty("exitCode", out var e) ? e.GetInt32() : -1;
            var cwdResult = resp.TryGetProperty("cwd", out var c) ? c.GetString() : null;

            Console.WriteLine($"  Output: '{output.Replace("\n", "\\n")}'");
            Console.WriteLine($"  ExitCode: {exitCode}, TimedOut: {timedOut}, Cwd: {cwdResult}");

            Assert(!timedOut, "Command did not time out");
            Assert(output.Contains("hello ripple"), "Output contains expected text");
            Assert(exitCode == 0, "Exit code is 0");
            Assert(cwdResult != null, "Cwd is reported");
        }

        // Test 4: execute command with non-zero exit (native command)
        {
            var command = OperatingSystem.IsWindows()
                ? "cmd /c exit 42"
                : "bash -c 'exit 42'";
            Console.WriteLine($"  Executing: {command}");
            var resp = await SendRequest(pipeName, w => { w.WriteString("type", "execute"); w.WriteString("command", command); w.WriteNumber("timeout", 10000); }, TimeSpan.FromSeconds(15));

            var exitCode = resp.TryGetProperty("exitCode", out var e) ? e.GetInt32() : -1;
            var timedOut = resp.TryGetProperty("timedOut", out var t) && t.GetBoolean();
            Console.WriteLine($"  ExitCode: {exitCode}, TimedOut: {timedOut}");

            Assert(!timedOut, "Non-zero exit: did not time out");
            Assert(exitCode == 42, $"Non-zero exit: code is 42 (got: {exitCode})");
        }

        // Test 5: get_status after commands (should be standby again)
        {
            var resp = await SendRequest(pipeName, w => w.WriteString("type", "get_status"));
            var status = resp.TryGetProperty("status", out var s) ? s.GetString() : null;
            Assert(status == "standby", $"Status back to standby (got: {status})");
        }

        // Test 6: session persistence — a variable set in one execute is readable in the next.
        // This guards the core value proposition of ripple: persistent shell state across
        // AI tool calls. If the worker ever loses state (e.g. spawns a subshell per execute),
        // this test catches it immediately.
        {
            var setCmd = OperatingSystem.IsWindows()
                ? "$script:RIPPLE_SESSION_TEST = 'persistent-42'"
                : "export RIPPLE_SESSION_TEST='persistent-42'";
            var getCmd = OperatingSystem.IsWindows()
                ? "Write-Output $script:RIPPLE_SESSION_TEST"
                : "echo \"$RIPPLE_SESSION_TEST\"";

            await SendRequest(pipeName, w => { w.WriteString("type", "execute"); w.WriteString("command", setCmd); w.WriteNumber("timeout", 10000); }, TimeSpan.FromSeconds(15));
            var resp = await SendRequest(pipeName, w => { w.WriteString("type", "execute"); w.WriteString("command", getCmd); w.WriteNumber("timeout", 10000); }, TimeSpan.FromSeconds(15));
            var output = resp.TryGetProperty("output", out var o) ? o.GetString() ?? "" : "";
            Assert(output.Contains("persistent-42"), $"Session variable persists across execute calls (got: {output.Replace("\n", "\\n")})");
        }

        // Test 7: multi-line command (foreach loop).
        // Multi-line input flows through tempfile dot-sourcing on pwsh and
        // heredoc-style delivery on bash; either path must preserve output ordering.
        // Note: we don't assert exitCode here because pwsh's $LASTEXITCODE only updates
        // on native-command invocations; a prior `cmd /c exit 42` leaks into this test.
        // That is a pwsh semantic, not a ripple bug.
        {
            var multilineCmd = OperatingSystem.IsWindows()
                ? "foreach ($i in 1..3) {\n    Write-Output \"line $i\"\n}"
                : "for i in 1 2 3; do\n    echo \"line $i\"\ndone";
            var resp = await SendRequest(pipeName, w => { w.WriteString("type", "execute"); w.WriteString("command", multilineCmd); w.WriteNumber("timeout", 10000); }, TimeSpan.FromSeconds(15));
            var output = resp.TryGetProperty("output", out var o) ? o.GetString() ?? "" : "";
            Assert(output.Contains("line 1") && output.Contains("line 2") && output.Contains("line 3"),
                $"Multi-line: all three lines emitted (got: {output.Replace("\n", "\\n")})");
        }

        // Test 8: timeout → get_cached_output retrieval.
        // Execute a command with a very short timeout so it times out, then verify the
        // worker reports busy, and after the command completes, get_cached_output returns
        // the result. This exercises the busy-tracking + cache path that AI clients rely on
        // when commands exceed their wall-clock budget.
        {
            var slowCmd = OperatingSystem.IsWindows()
                ? "Start-Sleep -Milliseconds 1500; Write-Output 'slow done'"
                : "sleep 1.5; echo 'slow done'";

            // Short timeout forces busy return.
            var resp = await SendRequest(pipeName, w => { w.WriteString("type", "execute"); w.WriteString("command", slowCmd); w.WriteNumber("timeout", 300); }, TimeSpan.FromSeconds(5));
            var timedOut = resp.TryGetProperty("timedOut", out var t) && t.GetBoolean();
            Assert(timedOut, "Slow command: timedOut flag set");

            // Worker should be busy.
            var statusResp = await SendRequest(pipeName, w => w.WriteString("type", "get_status"));
            var status = statusResp.TryGetProperty("status", out var s) ? s.GetString() : null;
            Assert(status == "busy", $"Status is busy while command runs (got: {status})");

            // Poll for cached output until it's ready (max ~3s).
            // get_cached_output returns a `results` array — one entry per
            // cached CommandResult on the console. A single flipped
            // command shows up as results[0].
            string? cachedOutput = null;
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < deadline)
            {
                var cacheResp = await SendRequest(pipeName, w => w.WriteString("type", "get_cached_output"));
                var cacheStatus = cacheResp.TryGetProperty("status", out var cs) ? cs.GetString() : null;
                if (cacheStatus == "ok"
                    && cacheResp.TryGetProperty("results", out var resultsProp)
                    && resultsProp.ValueKind == System.Text.Json.JsonValueKind.Array
                    && resultsProp.GetArrayLength() > 0)
                {
                    var first = resultsProp[0];
                    cachedOutput = first.TryGetProperty("output", out var co) ? co.GetString() : null;
                    break;
                }
                await Task.Delay(200);
            }
            Assert(cachedOutput != null && cachedOutput.Contains("slow done"),
                $"Cached output contains expected result (got: {(cachedOutput ?? "<null>").Replace("\n", "\\n")})");
        }

        // Test 8b: multi-entry cache drain. Two commands on the same
        // console, both hit short-timeout preemptive flips before any
        // drain runs, their results stack in the tracker's
        // _cachedResults list. A single get_cached_output call must
        // return BOTH entries in the `results` array, in registration
        // order, each with its own baked statusLine. Regression guard
        // for the "single CommandResult field silently overwrites the
        // previous" bug shape the list-based cache fixes.
        {
            var slow1 = OperatingSystem.IsWindows()
                ? "Start-Sleep -Milliseconds 1200; Write-Output 'first-cached'"
                : "sleep 1.2; echo 'first-cached'";
            var slow2 = OperatingSystem.IsWindows()
                ? "Start-Sleep -Milliseconds 1200; Write-Output 'second-cached'"
                : "sleep 1.2; echo 'second-cached'";

            // First command — flips at 300ms, response returns timedOut=true.
            var r1 = await SendRequest(pipeName, w => { w.WriteString("type", "execute"); w.WriteString("command", slow1); w.WriteNumber("timeout", 300); }, TimeSpan.FromSeconds(5));
            Assert(r1.TryGetProperty("timedOut", out var t1) && t1.GetBoolean(),
                "multi-cache: first execute timedOut=true");

            // Wait for the first command's shell-side Resolve to land in
            // the cache list. Without this, the second RegisterCommand
            // sees the tracker still Busy and rejects with "busy".
            var settle1Deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < settle1Deadline)
            {
                var st = await SendRequest(pipeName, w => w.WriteString("type", "get_status"));
                var stStr = st.TryGetProperty("status", out var s) ? s.GetString() : null;
                if (stStr != "busy") break;
                await Task.Delay(100);
            }

            // Second command — reuses the same console. Cache should now
            // carry first-cached, and a fresh RegisterCommand must NOT
            // clear it (see CommandTracker: RegisterCommand preserves
            // _cachedResults across command boundaries).
            var r2 = await SendRequest(pipeName, w => { w.WriteString("type", "execute"); w.WriteString("command", slow2); w.WriteNumber("timeout", 300); }, TimeSpan.FromSeconds(5));
            Assert(r2.TryGetProperty("timedOut", out var t2) && t2.GetBoolean(),
                "multi-cache: second execute timedOut=true");

            // Wait for the second command to also land in the cache.
            var settle2Deadline = DateTime.UtcNow.AddSeconds(5);
            int cacheCount = 0;
            List<string> cachedOutputs = new();
            List<string?> cachedStatusLines = new();
            while (DateTime.UtcNow < settle2Deadline)
            {
                var status = await SendRequest(pipeName, w => w.WriteString("type", "get_status"));
                var hasCached = status.TryGetProperty("hasCachedOutput", out var hc) && hc.GetBoolean();
                var busy = (status.TryGetProperty("status", out var sp) ? sp.GetString() : null) == "busy";
                if (!busy && hasCached)
                {
                    var drain = await SendRequest(pipeName, w => w.WriteString("type", "get_cached_output"));
                    var dStatus = drain.TryGetProperty("status", out var ds) ? ds.GetString() : null;
                    if (dStatus == "ok"
                        && drain.TryGetProperty("results", out var results)
                        && results.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        cacheCount = results.GetArrayLength();
                        foreach (var entry in results.EnumerateArray())
                        {
                            cachedOutputs.Add(entry.TryGetProperty("output", out var o) ? o.GetString() ?? "" : "");
                            cachedStatusLines.Add(entry.TryGetProperty("statusLine", out var sl) ? sl.GetString() : null);
                        }
                    }
                    break;
                }
                await Task.Delay(200);
            }

            Assert(cacheCount == 2,
                $"multi-cache: two cached entries drained from one RPC (got {cacheCount})");
            if (cacheCount == 2)
            {
                Assert(cachedOutputs[0].Contains("first-cached"),
                    $"multi-cache: first entry preserves output (got: {cachedOutputs[0].Replace("\n", "\\n")})");
                Assert(cachedOutputs[1].Contains("second-cached"),
                    $"multi-cache: second entry preserves output (got: {cachedOutputs[1].Replace("\n", "\\n")})");
                Assert(!string.IsNullOrEmpty(cachedStatusLines[0]),
                    "multi-cache: first entry has baked statusLine");
                Assert(!string.IsNullOrEmpty(cachedStatusLines[1]),
                    "multi-cache: second entry has baked statusLine");

                // Second drain after the first must be empty — the whole
                // list is atomically consumed per call.
                var drain2 = await SendRequest(pipeName, w => w.WriteString("type", "get_cached_output"));
                var d2Status = drain2.TryGetProperty("status", out var d2s) ? d2s.GetString() : null;
                Assert(d2Status == "no_cache",
                    $"multi-cache: follow-up drain returns no_cache (got: {d2Status})");
            }
        }

        // Test 9: send_input rejected on idle console.
        // The worker must refuse send_input when there is no running command, so the AI
        // can't accidentally inject keystrokes into the next prompt.
        {
            var resp = await SendRequest(pipeName, w =>
            {
                w.WriteString("type", "send_input");
                w.WriteString("input", "garbage\\r");
            });
            var status = resp.TryGetProperty("status", out var s) ? s.GetString() : null;
            Assert(status == "rejected", $"send_input on idle console rejected (got: {status})");
        }

        // Test 10: send_input Ctrl+C interrupts a running command.
        // Start a 30 s sleep with a 10 s drain window. If Ctrl+C
        // actually interrupts, we see standby within ~3 s. If Ctrl+C
        // is silently dropped (or consumed elsewhere), the sleep runs
        // for its full 30 s and the drain window gives up at 10 s —
        // test fails. This makes "worked" and "didn't work" dynamically
        // distinguishable, unlike earlier drafts with 3 / 60 s sleeps
        // where natural completion and interrupt converged in the drain
        // window and hid the failure mode.
        {
            var sleepCmd = OperatingSystem.IsWindows()
                ? "Start-Sleep -Seconds 30"
                : "sleep 30";

            var execResp = await SendRequest(pipeName, w => { w.WriteString("type", "execute"); w.WriteString("command", sleepCmd); w.WriteNumber("timeout", 500); }, TimeSpan.FromSeconds(5));
            var execTimedOut = execResp.TryGetProperty("timedOut", out var t) && t.GetBoolean();
            Assert(execTimedOut, "Long sleep: timed out as expected");

            var inputResp = await SendRequest(pipeName, w =>
            {
                w.WriteString("type", "send_input");
                w.WriteString("input", "\\x03");
            });
            var inputStatus = inputResp.TryGetProperty("status", out var isp) ? isp.GetString() : null;
            Assert(inputStatus == "ok", $"send_input Ctrl+C accepted while busy (got: {inputStatus})");

            // Drain cached output so the tracker clears HasCachedOutput and the next
            // get_status can return "standby" instead of "completed". 10 s window
            // — comfortably longer than a real Ctrl+C interrupt (~2-3 s end-to-end)
            // but comfortably shorter than a 30 s Start-Sleep natural completion,
            // so a silent-drop failure mode is visibly distinguishable. If Ctrl+C
            // actually reaches pwsh, standby is reported within ~3 s. If the byte
            // is dropped or mis-routed (current flake behaviour on this box), the
            // sleep runs to completion at T+30 s and the drain gives up first.
            //
            // On failure, the recent-output ring is dumped so the next debug pass
            // can see what pwsh was actually doing — if the prompt is visible
            // in the ring, Ctrl+C worked and the tracker failed to notice; if
            // only `Start-Sleep -Seconds 30` is visible with no following
            // output, Ctrl+C was silently dropped and pwsh is still sleeping.
            var interrupted = false;
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                await SendRequest(pipeName, w => w.WriteString("type", "get_cached_output"));
                var statusResp = await SendRequest(pipeName, w => w.WriteString("type", "get_status"));
                var st = statusResp.TryGetProperty("status", out var s) ? s.GetString() : null;
                if (st == "standby") { interrupted = true; break; }
                await Task.Delay(200);
            }
            if (!interrupted)
            {
                // Dump the raw ring bytes so the next debug pass can see
                // whether pwsh's prompt actually came back (tracker bug) or
                // whether Start-Sleep is still running (Ctrl+C dropped).
                var peekResp = await SendRequest(pipeName, w => { w.WriteString("type", "peek"); w.WriteBoolean("raw", true); });
                var recent = peekResp.TryGetProperty("recentOutput", out var ro) ? ro.GetString() ?? "" : "";
                Console.Error.WriteLine($"    [Ctrl+C flake] recent ring tail = <{(recent.Length > 300 ? recent[^300..] : recent).Replace("\n", "\\n").Replace("\r", "\\r")}>");
            }
            Assert(interrupted, "Shell returned to standby after Ctrl+C interrupt");
        }

        // Test 11: version check (worker refuses claim from strictly newer proxy)
        // Send claim with a fake proxy_version that is strictly greater than any real
        // version. The worker's HandleClaim is version-aware: it marks itself obsolete
        // and returns status="obsolete". The shell (PTY) must remain alive afterwards
        // so the human user can keep working in the terminal.
        {
            var unownedPipe = $"RP.{workerPid}";
            var resp = await SendRequest(unownedPipe, w =>
            {
                w.WriteString("type", "claim");
                w.WriteNumber("proxy_pid", proxyPid);
                w.WriteString("proxy_version", "99.99.99");
                w.WriteString("agent_id", "v2test");
                w.WriteString("title", "#fake high version");
            });
            var status = resp.TryGetProperty("status", out var s) ? s.GetString() : null;
            Assert(status == "obsolete", $"Claim with higher proxy_version returns obsolete (got: {status})");

            var workerVersion = resp.TryGetProperty("worker_version", out var wv) ? wv.GetString() : null;
            Assert(!string.IsNullOrEmpty(workerVersion), $"Response includes worker_version (got: {workerVersion})");

            // PTY must still be alive so the user can continue working.
            var execResp = await SendRequest(pipeName,
                w => { w.WriteString("type", "execute"); w.WriteString("command", "Write-Output 'still-alive'"); w.WriteNumber("timeout", 10000); },
                TimeSpan.FromSeconds(15));
            var execOutput = execResp.TryGetProperty("output", out var eo) ? eo.GetString() ?? "" : "";
            Assert(execOutput.Contains("still-alive"), $"PTY still alive after obsolete state (output: {execOutput.Replace("\n", "\\n")})");
        }

        // Cleanup
        try
        {
            var proc = Process.GetProcessById(workerPid);
            proc.Kill();
            await proc.WaitForExitAsync();
        }
        catch { }

        Console.WriteLine($"\n{pass} passed, {fail} failed");
        if (fail > 0) Environment.Exit(1);
    }

    /// <summary>
    /// Cross-shell smoke test. The main <see cref="Run"/> suite only covers pwsh
    /// because that's ripple's primary target; this runs a smaller set of
    /// assertions against Windows PowerShell 5.1 (powershell.exe) and cmd.exe so
    /// both are exercised end-to-end from the Pipe protocol.
    ///
    /// Each shell profile declares how to echo a literal, set a session variable,
    /// and read it back. cmd.exe has a documented limitation: its PROMPT can't
    /// expand %ERRORLEVEL% at display time, so the exit code is always reported
    /// as 0 and the command echo from ConPTY appears in the output — the cmd
    /// assertions here are deliberately loose about both.
    /// </summary>
    public static async Task RunMultiShell()
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("=== ConsoleWorker Multi-Shell Tests === SKIP (Windows-only)");
            return;
        }

        var totalPass = 0;
        var totalFail = 0;

        Console.WriteLine("=== ConsoleWorker Multi-Shell Tests ===");

        var profiles = new[]
        {
            new ShellProfile(
                label: "powershell (5.1)",
                shellExe: "powershell.exe",
                simpleEcho: "Write-Output 'hello ps51'",
                simpleEchoExpect: "hello ps51",
                setVar: "$script:RIPPLE_MS_TEST = 'ps51-persist'",
                getVar: "$global:LASTEXITCODE = 0; Write-Output $script:RIPPLE_MS_TEST",
                getVarExpect: "ps51-persist",
                multiLine: "foreach ($i in 1..3) {\n    Write-Output \"ps51-line $i\"\n}",
                multiLineExpects: new[] { "ps51-line 1", "ps51-line 2", "ps51-line 3" },
                assertExitCode: true),
            new ShellProfile(
                label: "cmd",
                shellExe: "cmd.exe",
                simpleEcho: "echo hello cmd",
                simpleEchoExpect: "hello cmd",
                setVar: "set RIPPLE_MS_TEST=cmd-persist",
                getVar: "echo %RIPPLE_MS_TEST%",
                getVarExpect: "cmd-persist",
                // Multi-line cmd goes through a tempfile .cmd batch (see
                // HandleExecuteAsync). Verifies both that the tempfile path
                // works and that cmd batch block syntax (nested blocks with
                // line breaks) survives the round-trip. cwd-independent —
                // uses a variable set inside the block.
                multiLine: "set _RIPPLE_MSH=ok\nif \"%_RIPPLE_MSH%\"==\"ok\" (\n    echo cmd-line-a\n    echo cmd-line-b\n) else (\n    echo cmd-else\n)",
                multiLineExpects: new[] { "cmd-line-a", "cmd-line-b" },
                // cmd's PROMPT fires a fake D;0 after every command — exit code
                // assertions would always see 0, so don't bother.
                assertExitCode: false),
            new ShellProfile(
                label: "bash",
                shellExe: "bash.exe",
                simpleEcho: "echo hello bash",
                simpleEchoExpect: "hello bash",
                setVar: "export RIPPLE_MS_TEST=bash-persist",
                getVar: "echo \"$RIPPLE_MS_TEST\"",
                getVarExpect: "bash-persist",
                // Multi-line bash goes through a tempfile .sh dot-source
                // (see HandleExecuteAsync). Also exercises the bash
                // integration's "single OSC C per command line submit"
                // gate — without that gate, the for-loop's per-iteration
                // DEBUG trap firings would clobber _commandStart and the
                // tracker would only capture the last iteration.
                multiLine: "for i in 1 2 3; do\n    echo \"bash-iter $i\"\ndone",
                multiLineExpects: new[] { "bash-iter 1", "bash-iter 2", "bash-iter 3" },
                assertExitCode: true),
        };

        foreach (var profile in profiles)
        {
            Console.WriteLine($"\n--- {profile.label} ---");
            var (pass, fail) = await RunShellProfileAsync(profile);
            totalPass += pass;
            totalFail += fail;
            Console.WriteLine($"  {profile.label}: {pass} passed, {fail} failed");
        }

        Console.WriteLine($"\n{totalPass} passed, {totalFail} failed");
        if (totalFail > 0) Environment.Exit(1);
    }

    /// <summary>
    /// Verify integration.ps1 doesn't crash when PSReadLine isn't loaded.
    /// Spawns a fresh pwsh, removes PSReadLine, then dot-sources the
    /// embedded integration script. The script has best-effort guards
    /// around its PSReadLine cmdlets — if they regress, this test catches
    /// it before it becomes a worker-startup hang.
    /// </summary>
    public static async Task RunIntegrationScriptGuardTest()
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("=== ConsoleWorker PSReadLine Guard Test === SKIP (Windows-only)");
            return;
        }

        Console.WriteLine("=== ConsoleWorker PSReadLine Guard Test ===");
        var pass = 0;
        var fail = 0;
        void Assert(bool condition, string name)
        {
            if (condition) { pass++; Console.WriteLine($"  PASS: {name}"); }
            else { fail++; Console.Error.WriteLine($"  FAIL: {name}"); }
        }

        // Read the embedded integration script the same way ConsoleWorker does.
        string? scriptBody;
        using (var stream = typeof(ConsoleWorker).Assembly.GetManifestResourceStream("Ripple.ShellIntegration.integration.ps1"))
        {
            if (stream == null)
            {
                Assert(false, "integration.ps1 embedded resource located");
                Console.WriteLine($"\n{pass} passed, {fail} failed");
                if (fail > 0) Environment.Exit(1);
                return;
            }
            using var reader = new StreamReader(stream);
            scriptBody = reader.ReadToEnd();
        }
        Assert(true, "integration.ps1 embedded resource located");

        var tmp = Path.Combine(Path.GetTempPath(), $"ripple-psrl-guard-{Guid.NewGuid():N}.ps1");
        await File.WriteAllTextAsync(tmp, scriptBody);

        try
        {
            // Run pwsh with PSReadLine forcibly removed, then source the
            // integration. Exit 0 + empty stderr = clean load.
            var psi = new ProcessStartInfo
            {
                FileName = "pwsh.exe",
                Arguments = $"-NoProfile -NonInteractive -Command \"$ErrorActionPreference='Stop'; Remove-Module PSReadLine -ErrorAction Ignore; . '{tmp}'; exit 0\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc == null)
            {
                Assert(false, "pwsh.exe started");
                return;
            }
            var stderr = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();

            Assert(proc.ExitCode == 0, $"integration loads cleanly without PSReadLine (exit={proc.ExitCode}, stderr={stderr.Trim().Replace("\n", "\\n")})");
            Assert(string.IsNullOrWhiteSpace(stderr), $"no stderr noise from integration load (got: {stderr.Trim().Replace("\n", "\\n")})");
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }

        Console.WriteLine($"\n{pass} passed, {fail} failed");
        if (fail > 0) Environment.Exit(1);
    }

    private record ShellProfile(
        string label,
        string shellExe,
        string simpleEcho,
        string simpleEchoExpect,
        string setVar,
        string getVar,
        string getVarExpect,
        string multiLine,
        string[] multiLineExpects,
        bool assertExitCode);

    private static async Task<(int pass, int fail)> RunShellProfileAsync(ShellProfile profile)
    {
        var pass = 0;
        var fail = 0;
        void Assert(bool condition, string name)
        {
            if (condition) { pass++; Console.WriteLine($"  PASS: {name}"); }
            else { fail++; Console.Error.WriteLine($"  FAIL: {name}"); }
        }

        var proxyPid = Environment.ProcessId;
        var agentId = "multi";
        var cwd = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var launcher = new Services.ProcessLauncher();
        int workerPid;
        try
        {
            workerPid = launcher.LaunchConsoleWorker(proxyPid, agentId, profile.shellExe, cwd);
        }
        catch (Exception ex)
        {
            Assert(false, $"{profile.label}: launch worker ({ex.GetType().Name}: {ex.Message})");
            return (pass, fail);
        }

        var pipeName = $"RP.{proxyPid}.{agentId}.{workerPid}";

        try
        {
            var ready = await WaitForPipeAsync(pipeName, TimeSpan.FromSeconds(30));
            Assert(ready, $"{profile.label}: worker pipe became ready");
            if (!ready) return (pass, fail);

            // get_status should report standby once the shell is fully initialised.
            {
                var resp = await SendRequest(pipeName, w => w.WriteString("type", "get_status"));
                var status = resp.TryGetProperty("status", out var s) ? s.GetString() : null;
                Assert(status == "standby", $"{profile.label}: initial status is standby (got: {status})");

                var shellFamily = resp.TryGetProperty("shellFamily", out var sf) ? sf.GetString() : null;
                Assert(!string.IsNullOrEmpty(shellFamily), $"{profile.label}: shellFamily reported ({shellFamily})");
            }

            // Basic echo command returns expected text.
            {
                var resp = await SendRequest(pipeName,
                    w => { w.WriteString("type", "execute"); w.WriteString("command", profile.simpleEcho); w.WriteNumber("timeout", 15000); },
                    TimeSpan.FromSeconds(20));
                var output = resp.TryGetProperty("output", out var o) ? o.GetString() ?? "" : "";
                var timedOut = resp.TryGetProperty("timedOut", out var t) && t.GetBoolean();
                Assert(!timedOut, $"{profile.label}: simple echo did not time out");
                Assert(output.Contains(profile.simpleEchoExpect),
                    $"{profile.label}: simple echo output contains expected text (got: {output.Replace("\n", "\\n")})");
                // Strict echo check: the captured output must NOT contain the
                // typed command itself (e.g. "echo hello cmd"). pwsh and bash
                // strip the input echo via OSC 633 C; cmd's StripCmdInputEcho
                // does the same job for the cmd path. Regression guard for
                // the cmd cleanup we added.
                Assert(!output.Contains(profile.simpleEcho),
                    $"{profile.label}: input echo stripped from output (got: {output.Replace("\n", "\\n")})");
                if (profile.assertExitCode)
                {
                    var exitCode = resp.TryGetProperty("exitCode", out var e) ? e.GetInt32() : -1;
                    Assert(exitCode == 0, $"{profile.label}: simple echo exit code 0 (got: {exitCode})");
                }
            }

            // Session variable persists across separate execute calls.
            {
                var setResp = await SendRequest(pipeName,
                    w => { w.WriteString("type", "execute"); w.WriteString("command", profile.setVar); w.WriteNumber("timeout", 15000); },
                    TimeSpan.FromSeconds(20));
                var setTimedOut = setResp.TryGetProperty("timedOut", out var t) && t.GetBoolean();
                Assert(!setTimedOut, $"{profile.label}: set variable did not time out");

                var getResp = await SendRequest(pipeName,
                    w => { w.WriteString("type", "execute"); w.WriteString("command", profile.getVar); w.WriteNumber("timeout", 15000); },
                    TimeSpan.FromSeconds(20));
                var getOutput = getResp.TryGetProperty("output", out var o) ? o.GetString() ?? "" : "";
                Assert(getOutput.Contains(profile.getVarExpect),
                    $"{profile.label}: session variable persists (got: {getOutput.Replace("\n", "\\n")})");
            }

            // Multi-line commands: powershell uses tempfile dot-sourcing,
            // cmd uses tempfile `call`. Both must preserve newlines through
            // the PTY round-trip so block-level syntax (if/else, foreach)
            // still works. Each expected fragment must appear in the output
            // in order of declaration.
            {
                var resp = await SendRequest(pipeName,
                    w => { w.WriteString("type", "execute"); w.WriteString("command", profile.multiLine); w.WriteNumber("timeout", 15000); },
                    TimeSpan.FromSeconds(20));
                var output = resp.TryGetProperty("output", out var o) ? o.GetString() ?? "" : "";
                var timedOut = resp.TryGetProperty("timedOut", out var t) && t.GetBoolean();
                Assert(!timedOut, $"{profile.label}: multi-line did not time out");

                int cursor = 0;
                bool allInOrder = true;
                foreach (var frag in profile.multiLineExpects)
                {
                    var idx = output.IndexOf(frag, cursor, StringComparison.Ordinal);
                    if (idx < 0) { allInOrder = false; break; }
                    cursor = idx + frag.Length;
                }
                Assert(allInOrder,
                    $"{profile.label}: multi-line output contains all fragments in order (got: {output.Replace("\n", "\\n")})");
            }

            // bash subshell regression guard. The PS0-based OSC C emission
            // (replacing the old DEBUG-trap approach) must fire OSC C in the
            // PARENT shell before the subshell forks, so output captured
            // inside `(...)` lands in the AI command slice. Without this the
            // command either hangs forever waiting for a missing OSC D or
            // resolves with empty output.
            if (profile.label == "bash")
            {
                var resp = await SendRequest(pipeName,
                    w => { w.WriteString("type", "execute"); w.WriteString("command", "(echo bash-sub-foo; echo bash-sub-bar)"); w.WriteNumber("timeout", 10000); },
                    TimeSpan.FromSeconds(15));
                var output = resp.TryGetProperty("output", out var o) ? o.GetString() ?? "" : "";
                var timedOut = resp.TryGetProperty("timedOut", out var t) && t.GetBoolean();
                Assert(!timedOut, "bash: subshell did not time out");
                Assert(output.Contains("bash-sub-foo") && output.Contains("bash-sub-bar"),
                    $"bash: subshell output captured (got: {output.Replace("\n", "\\n")})");

                // Subshell with non-zero exit must propagate to the AI tracker.
                var exitResp = await SendRequest(pipeName,
                    w => { w.WriteString("type", "execute"); w.WriteString("command", "(exit 17)"); w.WriteNumber("timeout", 10000); },
                    TimeSpan.FromSeconds(15));
                var exitCode = exitResp.TryGetProperty("exitCode", out var e) ? e.GetInt32() : -1;
                Assert(exitCode == 17, $"bash: subshell exit code propagated (got: {exitCode})");
            }

            // After commands finish, the worker goes back to standby.
            {
                var resp = await SendRequest(pipeName, w => w.WriteString("type", "get_status"));
                var status = resp.TryGetProperty("status", out var s) ? s.GetString() : null;
                Assert(status == "standby", $"{profile.label}: status back to standby (got: {status})");
            }
        }
        finally
        {
            try
            {
                var proc = Process.GetProcessById(workerPid);
                proc.Kill();
                await proc.WaitForExitAsync();
            }
            catch { }
        }

        return (pass, fail);
    }

    internal static async Task<bool> WaitForPipeAsync(string pipeName, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var resp = await SendRequest(pipeName, w => w.WriteString("type", "ping"), TimeSpan.FromSeconds(2));
                return true;
            }
            catch
            {
                await Task.Delay(500);
            }
        }
        return false;
    }

    internal static async Task<JsonElement> SendRequest(string pipeName, Action<Utf8JsonWriter> writeBody, TimeSpan? timeout = null)
    {
        timeout ??= TimeSpan.FromSeconds(5);
        using var cts = new CancellationTokenSource(timeout.Value);
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

        await client.ConnectAsync(cts.Token);

        var msgBytes = PipeJson.BuildObjectBytes(writeBody);
        var lenBytes = BitConverter.GetBytes(msgBytes.Length);

        await client.WriteAsync(lenBytes, cts.Token);
        await client.WriteAsync(msgBytes, cts.Token);
        await client.FlushAsync(cts.Token);

        var recvLenBytes = new byte[4];
        await ReadExactAsync(client, recvLenBytes, cts.Token);
        var recvLen = BitConverter.ToInt32(recvLenBytes);

        var recvBytes = new byte[recvLen];
        await ReadExactAsync(client, recvBytes, cts.Token);

        return PipeJson.ParseElement(recvBytes);
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), ct);
            if (read == 0) throw new IOException("Pipe closed");
            offset += read;
        }
    }
}
