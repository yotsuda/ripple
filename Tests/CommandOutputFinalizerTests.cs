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

        Console.WriteLine($"\n{pass} passed, {fail} failed");
        if (fail > 0) Environment.Exit(1);
    }
}
