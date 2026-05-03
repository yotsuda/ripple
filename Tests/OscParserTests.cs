using Ripple.Services;

namespace Ripple.Tests;

public class OscParserTests
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

        Console.WriteLine("=== OscParser Tests ===");

        // Compact one-liner for the OSC 633 extension payloads
        // (E / L / R / T). Each of those events is parsed identically
        // at the top level (the `\x1b]633;` prefix + BEL terminator)
        // and differs only in payload letter + per-event field. Before
        // this helper each test was 6 lines of boilerplate; now it's
        // one call with a validator lambda.
        //
        // Uses BEL-terminated sequences to exercise the complete OSC
        // path (parse → payload dispatch → event construction).
        void AssertOscExt(string payload, OscParser.OscEventType expectedType,
                          Func<OscParser.OscEvent, bool> validator, string label)
        {
            var p = new OscParser();
            var r = p.Parse("\x1b]633;" + payload + "\x07");
            Assert(r.Events.Count == 1, $"{label}: event count");
            if (r.Events.Count != 1) return;  // avoid IndexOutOfRangeException cascade
            Assert(r.Events[0].Type == expectedType, $"{label}: type");
            Assert(validator(r.Events[0]), $"{label}: value");
        }

        // Test 1: Basic PromptStart
        {
            var parser = new OscParser();
            var result = parser.Parse("\x1b]633;A\u0007");
            Assert(result.Events.Count == 1, "PromptStart event count");
            Assert(result.Events[0].Type == OscParser.OscEventType.PromptStart, "PromptStart type");
            Assert(result.Cleaned == "", "PromptStart cleaned empty");
        }

        // Test 2: CommandFinished with exit code
        {
            var parser = new OscParser();
            var result = parser.Parse("\x1b]633;D;42\u0007");
            Assert(result.Events.Count == 1, "CommandFinished event count");
            Assert(result.Events[0].Type == OscParser.OscEventType.CommandFinished, "CommandFinished type");
            Assert(result.Events[0].ExitCode == 42, "CommandFinished exit code");
        }

        // Test 3: Cwd property
        {
            var parser = new OscParser();
            var result = parser.Parse("\x1b]633;P;Cwd=/home/user\u0007");
            Assert(result.Events.Count == 1, "Cwd event count");
            Assert(result.Events[0].Type == OscParser.OscEventType.Cwd, "Cwd type");
            Assert(result.Events[0].Cwd == "/home/user", "Cwd value");
        }

        // Tests 3b-3j: OSC 633 extension payloads (E / L / R / T) —
        // ripple's PowerShell-only extensions. The parser handles each
        // identically at the framing level; the per-event field is
        // populated from the payload. Driven through AssertOscExt so
        // the shape (event-count + type + value assertion) is declared
        // once and each case fits on a single line. "boom" base64-
        // encoded is "Ym9vbQ==" (used for the R positive case).
        AssertOscExt("E;3",               OscParser.OscEventType.ErrorCount,          e => e.ErrorCount == 3,          "ErrorCount");
        AssertOscExt("E;0",               OscParser.OscEventType.ErrorCount,          e => e.ErrorCount == 0,          "ErrorCount-zero");
        AssertOscExt("E;not-a-number",    OscParser.OscEventType.ErrorCount,          e => e.ErrorCount == 0,          "ErrorCount-bad");
        AssertOscExt("L;7",               OscParser.OscEventType.LastExitCode,        e => e.LastExitCode == 7,        "LastExitCode");
        AssertOscExt("L;not-a-number",    OscParser.OscEventType.LastExitCode,        e => e.LastExitCode == 0,        "LastExitCode-bad");
        AssertOscExt("R;Ym9vbQ==",        OscParser.OscEventType.ErrorMessage,        e => e.ErrorMessage == "boom",   "ErrorMessage");
        AssertOscExt("R;!!!invalid!!!",   OscParser.OscEventType.ErrorMessage,        e => e.ErrorMessage == null,     "ErrorMessage-bad");
        AssertOscExt("T;5",               OscParser.OscEventType.TruncatedErrorCount, e => e.TruncatedErrorCount == 5, "TruncatedErrorCount");
        AssertOscExt("T;not-a-number",    OscParser.OscEventType.TruncatedErrorCount, e => e.TruncatedErrorCount == 0, "TruncatedErrorCount-bad");

        // Test 4: Mixed output + OSC
        {
            var parser = new OscParser();
            var result = parser.Parse("hello world\x1b]633;A\u0007more text");
            Assert(result.Events.Count == 1, "Mixed: event count");
            Assert(result.Cleaned == "hello worldmore text", "Mixed: cleaned output");
        }

        // Test 5: Multiple events in one chunk
        {
            var parser = new OscParser();
            var result = parser.Parse("\x1b]633;D;0\u0007\x1b]633;P;Cwd=C:\\Users\u0007\x1b]633;A\u0007");
            Assert(result.Events.Count == 3, "Multiple: event count");
            Assert(result.Events[0].Type == OscParser.OscEventType.CommandFinished, "Multiple: first is CommandFinished");
            Assert(result.Events[1].Type == OscParser.OscEventType.Cwd, "Multiple: second is Cwd");
            Assert(result.Events[1].Cwd == "C:\\Users", "Multiple: Cwd value with backslash");
            Assert(result.Events[2].Type == OscParser.OscEventType.PromptStart, "Multiple: third is PromptStart");
        }

        // Test 6: Incomplete sequence (buffered across chunks)
        {
            var parser = new OscParser();
            var r1 = parser.Parse("output\x1b]633;");
            Assert(r1.Events.Count == 0, "Incomplete: no events yet");
            Assert(r1.Cleaned == "output", "Incomplete: cleaned before OSC");

            var r2 = parser.Parse("D;1\u0007after");
            Assert(r2.Events.Count == 1, "Incomplete: event after reassembly");
            Assert(r2.Events[0].ExitCode == 1, "Incomplete: exit code correct");
            Assert(r2.Cleaned == "after", "Incomplete: cleaned after OSC");
        }

        // Test 7: ST terminator (\x1b\\)
        {
            var parser = new OscParser();
            var result = parser.Parse("\x1b]633;C\x1b\\");
            Assert(result.Events.Count == 1, "ST terminator: event count");
            Assert(result.Events[0].Type == OscParser.OscEventType.CommandExecuted, "ST terminator: type");
        }

        // Test 8: No OSC at all
        {
            var parser = new OscParser();
            var result = parser.Parse("plain text output\nwith newlines");
            Assert(result.Events.Count == 0, "No OSC: no events");
            Assert(result.Cleaned == "plain text output\nwith newlines", "No OSC: pass-through");
        }

        // Test 9: CommandInputStart (B)
        {
            var parser = new OscParser();
            var result = parser.Parse("\x1b]633;B\u0007");
            Assert(result.Events.Count == 1, "CommandInputStart: event count");
            Assert(result.Events[0].Type == OscParser.OscEventType.CommandInputStart, "CommandInputStart: type");
        }

        // Test 10: Unknown code ignored
        {
            var parser = new OscParser();
            var result = parser.Parse("\x1b]633;Z;unknown\u0007");
            Assert(result.Events.Count == 0, "Unknown code: no events");
            Assert(result.Cleaned == "", "Unknown code: cleaned empty");
        }

        // Test 11: Large but under-cap OSC parses normally (the cap must
        // not nuke legitimate big payloads — cwd values on Windows long-
        // path systems can run to multiple KB).
        {
            var parser = new OscParser();
            var bigCwd = new string('a', 16 * 1024); // 16 KB cwd, well under the 256 KB cap
            var result = parser.Parse("\x1b]633;P;Cwd=" + bigCwd + "");
            Assert(result.Events.Count == 1, "Under-cap OSC: parsed");
            Assert(result.Events[0].Type == OscParser.OscEventType.Cwd, "Under-cap OSC: type");
            Assert(result.Events[0].Cwd == bigCwd, "Under-cap OSC: payload intact");
        }

        // Test 12: Torn frame (OSC start with no terminator ever arriving)
        // doesn't grow the inter-chunk carry buffer unboundedly. The cap
        // bails by flushing the buffered bytes as cleaned text — the
        // visible console may show some `\x1b]633;` smear, but no events
        // are synthesized and the parser recovers on the next valid OSC.
        {
            var parser = new OscParser();
            // 300 KB of garbage after an OSC start — exceeds the 256 KB cap.
            var huge = new string('x', 300 * 1024);
            var r1 = parser.Parse("before\x1b]633;" + huge);
            Assert(r1.Events.Count == 0, "Torn-frame cap: no events from incomplete OSC");
            Assert(r1.Cleaned.StartsWith("before"), "Torn-frame cap: pre-OSC text preserved");
            Assert(r1.Cleaned.Contains("\x1b]633;"), "Torn-frame cap: bailed bytes flushed (not silently dropped)");

            // Buffer reset → next valid OSC parses cleanly.
            var r2 = parser.Parse("\x1b]633;Aafter");
            Assert(r2.Events.Count == 1, "Torn-frame cap: parser recovers on next valid OSC");
            Assert(r2.Events[0].Type == OscParser.OscEventType.PromptStart, "Torn-frame cap: recovered event type");
            Assert(r2.Cleaned == "after", "Torn-frame cap: recovered cleaned text");
        }

        Console.WriteLine($"\n{pass} passed, {fail} failed");
        if (fail > 0) Environment.Exit(1);
    }

    private static string Escape(string s)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var c in s)
        {
            if (c < 0x20 || c == 0x7f) sb.Append($"\\x{(int)c:x2}");
            else sb.Append(c);
        }
        return sb.ToString();
    }
}
