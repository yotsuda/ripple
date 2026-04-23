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

        // Test 3b: ErrorCount (OSC 633;E;{N}) — ripple's PowerShell-only
        // extension that lets the proxy render `Errors: N` in the status
        // line. Parsed identically to D's exit code; the count flows
        // through OscEvent.ErrorCount.
        {
            var parser = new OscParser();
            var result = parser.Parse("\x1b]633;E;3");
            Assert(result.Events.Count == 1, "ErrorCount event count");
            Assert(result.Events[0].Type == OscParser.OscEventType.ErrorCount, "ErrorCount type");
            Assert(result.Events[0].ErrorCount == 3, "ErrorCount value");
        }

        // Test 3c: ErrorCount with zero — still emitted, downstream
        // formatter gates on > 0.
        {
            var parser = new OscParser();
            var result = parser.Parse("\x1b]633;E;0");
            Assert(result.Events.Count == 1, "ErrorCount-zero event count");
            Assert(result.Events[0].ErrorCount == 0, "ErrorCount-zero value");
        }

        // Test 3d: ErrorCount with malformed payload defaults to 0.
        {
            var parser = new OscParser();
            var result = parser.Parse("\x1b]633;E;not-a-number");
            Assert(result.Events.Count == 1, "ErrorCount-bad event count");
            Assert(result.Events[0].ErrorCount == 0, "ErrorCount-bad defaults to 0");
        }

        // Test 3e: LastExitCode (OSC 633;L;{N}) — ripple's PowerShell-only
        // extension. Integration script emits it only when a native exe
        // returned non-zero mid-pipeline AND the overall pipeline succeeded,
        // but the parser treats any well-formed payload as a valid event
        // (gating lives in integration.ps1).
        {
            var parser = new OscParser();
            var result = parser.Parse("\x1b]633;L;7");
            Assert(result.Events.Count == 1, "LastExitCode event count");
            Assert(result.Events[0].Type == OscParser.OscEventType.LastExitCode, "LastExitCode type");
            Assert(result.Events[0].LastExitCode == 7, "LastExitCode value");
        }

        // Test 3f: LastExitCode with malformed payload defaults to 0.
        {
            var parser = new OscParser();
            var result = parser.Parse("\x1b]633;L;not-a-number");
            Assert(result.Events.Count == 1, "LastExitCode-bad event count");
            Assert(result.Events[0].LastExitCode == 0, "LastExitCode-bad defaults to 0");
        }

        // Test 3g: ErrorMessage (OSC 633;R;{base64}) — ripple's
        // PowerShell-only extension. One event per new $Error entry;
        // payload is base64(UTF-8) of the error's ToString().
        {
            var parser = new OscParser();
            // "boom" base64-encoded is "Ym9vbQ=="
            var result = parser.Parse("]633;R;Ym9vbQ==");
            Assert(result.Events.Count == 1, "ErrorMessage event count");
            Assert(result.Events[0].Type == OscParser.OscEventType.ErrorMessage, "ErrorMessage type");
            Assert(result.Events[0].ErrorMessage == "boom", "ErrorMessage decoded value");
        }

        // Test 3h: ErrorMessage with malformed base64 keeps the event
        // but leaves ErrorMessage null — tracker filters nulls before
        // appending, so a corrupted payload is lost but the pipeline
        // continues.
        {
            var parser = new OscParser();
            var result = parser.Parse("]633;R;!!!not-valid-base64!!!");
            Assert(result.Events.Count == 1, "ErrorMessage-bad event count");
            Assert(result.Events[0].ErrorMessage == null, "ErrorMessage-bad value is null");
        }


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
