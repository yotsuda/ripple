namespace Ripple.Services;

/// <summary>
/// OSC 633 parser — extracts shell integration sequences from PTY output stream.
/// Runs in the console worker process.
///
/// Sequences: \x1b]633;{code}[;{data}]\x07  (or \x1b\\ as alternate terminator)
///   A = PromptStart
///   B = CommandInputStart
///   C = CommandExecuted
///   D;{exitCode} = CommandFinished
///   E;{errorCount} = ErrorsThisPipeline (PowerShell $Error.Count delta;
///                    surfaced as "Errors: N" in the proxy status line
///                    when N &gt; 0; ripple-specific extension).
///   L;{lastExitCode} = LastExitCode — raw $LASTEXITCODE at command end.
///                    Emitted ONLY when a native exe returned non-zero
///                    inside this pipeline AND the pipeline overall
///                    succeeded (so D is already 0). Surfaced as
///                    "LastExit: N" on the proxy status line;
///                    PowerShell-only, ripple-specific extension.
///   P;Cwd={path} = Property (cwd)
/// </summary>
public class OscParser
{
    private const string OscStart = "\x1b]633;";
    private const char OscEndBel = '\x07';
    private const string OscEndSt = "\x1b\\";

    private string _buffer = "";

    /// <summary>
    /// A parsed OSC event. <see cref="TextOffset"/> is the index into
    /// <see cref="ParseResult.Cleaned"/> where this event fired — i.e. the
    /// number of cleaned bytes that came before it in the byte stream. It
    /// lets consumers interleave FeedOutput and HandleEvent calls in the
    /// exact order the shell wrote them, instead of feeding everything then
    /// flushing events (which loses positional information).
    /// </summary>
    public record OscEvent(OscEventType Type, int ExitCode = 0, string? Cwd = null, int TextOffset = 0, int ErrorCount = 0, int LastExitCode = 0);

    public enum OscEventType
    {
        PromptStart,
        CommandInputStart,
        CommandExecuted,
        CommandFinished,
        Cwd,
        // Ripple-specific extension: PowerShell's prompt fn emits this with
        // the $Error.Count delta over the just-finished pipeline. Worker
        // captures the latest value into the snapshot so the proxy can
        // render `Errors: N` in the status line.
        ErrorCount,
        // Ripple-specific extension: raw $LASTEXITCODE at command end,
        // emitted ONLY when a native exe returned non-zero mid-pipeline
        // AND the overall pipeline succeeded (D == 0). Worker captures
        // the value into the snapshot so the proxy can render
        // `LastExit: N` in the status line alongside the ✓ / ⚠ badge.
        LastExitCode,
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
                // No more OSC sequences — check for incomplete OSC start at end.
                // Only buffer if the trailing bytes could plausibly be the start of
                // an OSC sequence (`\x1b` alone, or `\x1b]`). CSI sequences like
                // `\x1b[K` must be passed through unbuffered, otherwise they get
                // chopped across chunks and the visible console misrenders.
                var escIdx = input.IndexOf('\x1b', i);
                if (escIdx != -1 && escIdx >= input.Length - OscStart.Length)
                {
                    var remaining = input.Length - escIdx;
                    bool couldBeOscStart = remaining == 1 || input[escIdx + 1] == ']';
                    if (couldBeOscStart)
                    {
                        cleaned.Append(input, i, escIdx - i);
                        _buffer = input[escIdx..];
                        return new ParseResult(cleaned.ToString(), events);
                    }
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
                events.Add(evt with { TextOffset = cleaned.Length });

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
            'E' => new OscEvent(OscEventType.ErrorCount, ErrorCount: data != null && int.TryParse(data, out var n) ? n : 0),
            'L' => new OscEvent(OscEventType.LastExitCode, LastExitCode: data != null && int.TryParse(data, out var lec) ? lec : 0),
            'P' when data != null && data.StartsWith("Cwd=") => new OscEvent(OscEventType.Cwd, Cwd: data[4..]),
            _ => null,
        };
    }
}
