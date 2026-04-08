namespace ShellPilot.Services;

/// <summary>
/// OSC 633 parser — extracts shell integration sequences from PTY output stream.
/// Runs in the console worker process.
///
/// Sequences: \x1b]633;{code}[;{data}]\x07  (or \x1b\\ as alternate terminator)
///   A = PromptStart
///   B = CommandInputStart
///   C = CommandExecuted
///   D;{exitCode} = CommandFinished
///   P;Cwd={path} = Property (cwd)
/// </summary>
public class OscParser
{
    private const string OscStart = "\x1b]633;";
    private const char OscEndBel = '\x07';
    private const string OscEndSt = "\x1b\\";

    private string _buffer = "";

    public record OscEvent(OscEventType Type, int ExitCode = 0, string? Cwd = null);

    public enum OscEventType
    {
        PromptStart,
        CommandInputStart,
        CommandExecuted,
        CommandFinished,
        Cwd,
    }

    public record ParseResult(string Cleaned, List<OscEvent> Events);

    /// <summary>
    /// Parse a chunk of PTY output.
    /// Returns cleaned output (OSC 633 stripped) and extracted events.
    /// Handles incomplete sequences at chunk boundaries by buffering.
    /// </summary>
    public ParseResult Parse(string chunk)
    {
        var events = new List<OscEvent>();
        var cleaned = new System.Text.StringBuilder();
        var input = _buffer + chunk;
        _buffer = "";

        int i = 0;
        while (i < input.Length)
        {
            var oscIdx = input.IndexOf(OscStart, i, StringComparison.Ordinal);

            if (oscIdx == -1)
            {
                // No more OSC sequences — check for incomplete escape at end
                var escIdx = input.IndexOf('\x1b', i);
                if (escIdx != -1 && escIdx >= input.Length - OscStart.Length)
                {
                    cleaned.Append(input, i, escIdx - i);
                    _buffer = input[escIdx..];
                    return new ParseResult(cleaned.ToString(), events);
                }
                cleaned.Append(input, i, input.Length - i);
                break;
            }

            // Text before the OSC sequence
            cleaned.Append(input, i, oscIdx - i);

            // Find end of OSC sequence
            int searchFrom = oscIdx + OscStart.Length;

            int belIdx = input.IndexOf(OscEndBel, searchFrom);
            int stIdx = input.IndexOf(OscEndSt, searchFrom, StringComparison.Ordinal);

            int endIdx;
            int endLen;

            if (belIdx != -1 && (stIdx == -1 || belIdx <= stIdx))
            {
                endIdx = belIdx;
                endLen = 1; // BEL is 1 char
            }
            else if (stIdx != -1)
            {
                endIdx = stIdx;
                endLen = OscEndSt.Length;
            }
            else
            {
                // Incomplete sequence — buffer from OSC start
                _buffer = input[oscIdx..];
                return new ParseResult(cleaned.ToString(), events);
            }

            // Extract payload
            var payload = input[searchFrom..endIdx];
            var evt = ParsePayload(payload);
            if (evt != null)
                events.Add(evt);

            i = endIdx + endLen;
        }

        return new ParseResult(cleaned.ToString(), events);
    }

    private static OscEvent? ParsePayload(string payload)
    {
        if (payload.Length == 0) return null;

        char code = payload[0];
        string? data = payload.Length > 2 && payload[1] == ';' ? payload[2..] : null;

        return code switch
        {
            'A' => new OscEvent(OscEventType.PromptStart),
            'B' => new OscEvent(OscEventType.CommandInputStart),
            'C' => new OscEvent(OscEventType.CommandExecuted),
            'D' => new OscEvent(OscEventType.CommandFinished, data != null && int.TryParse(data, out var ec) ? ec : 0),
            'P' when data != null && data.StartsWith("Cwd=") => new OscEvent(OscEventType.Cwd, Cwd: data[4..]),
            _ => null,
        };
    }
}
