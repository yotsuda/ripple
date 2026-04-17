using Ripple.Services.Adapters;

namespace Ripple.Services;

/// <summary>
/// Scans a block of input text against an adapter's
/// <see cref="BalancedParensSpec"/> and reports whether the block is
/// syntactically balanced (ready to submit to a Lisp-family REPL) or
/// still open (the AI probably sent a half-finished expression and
/// the runtime should reject it rather than deadlocking the REPL).
///
/// The counter is a single forward pass that tracks bracket depth
/// plus four state machines: inside a string literal, inside a line
/// comment, inside a block comment, and "next datum is a comment".
/// The char-literal and datum-comment machines are the schema §18
/// Q1 extensions added alongside the Racket adapter — without them
/// the counter would mishandle `#\(` (treat as an unclosed paren)
/// and `#;(foo)` (count the skipped datum's brackets).
///
/// Returns a <see cref="BalancedParensResult"/> with the final depth
/// and, when unbalanced, a short diagnostic. The runtime uses
/// IsComplete to decide whether to submit the block or return an
/// error; the diagnostic is surfaced in the error payload so the
/// AI sees `expected 2 more ')' before submit` rather than a
/// generic "invalid input".
/// </summary>
public static class BalancedParensCounter
{
    public static BalancedParensResult Evaluate(BalancedParensSpec spec, string input)
    {
        var open = spec.Open ?? ["(", "[", "{"];
        var close = spec.Close ?? [")", "]", "}"];
        var stringDelims = spec.StringDelims ?? ["\""];
        var escape = spec.Escape ?? "\\";
        var lineComment = spec.LineComment;
        var blockCommentOpen = spec.BlockComment is { Count: >= 2 } bc ? bc[0] : null;
        var blockCommentClose = spec.BlockComment is { Count: >= 2 } bc2 ? bc2[1] : null;
        var charLiteralPrefix = spec.CharLiteralPrefix;
        var datumCommentPrefix = spec.DatumCommentPrefix;

        int depth = 0;
        // Datum-comment stack: when we encounter `#;`, push a sentinel
        // depth onto the stack; the next balanced expression "pops" it
        // by cancelling its contribution to the outer count. Multiple
        // `#;` may nest (#;#;(a)(b) skips two following datums), so
        // this is a counter rather than a bool.
        int pendingDatumComments = 0;
        // Tracks the outer depth at the moment a datum-comment became
        // active on an atom (not a list). For list datums, we rely on
        // bracket accounting instead.
        var datumCommentAnchorDepths = new Stack<int>();

        string? activeStringDelim = null;
        bool inLineComment = false;
        bool inBlockComment = false;
        int i = 0;

        while (i < input.Length)
        {
            // --- inside a line comment: consume until newline ---
            if (inLineComment)
            {
                if (input[i] == '\n' || input[i] == '\r')
                    inLineComment = false;
                i++;
                continue;
            }

            // --- inside a block comment: consume until block close ---
            if (inBlockComment && blockCommentClose != null)
            {
                if (MatchAt(input, i, blockCommentClose))
                {
                    inBlockComment = false;
                    i += blockCommentClose.Length;
                    continue;
                }
                i++;
                continue;
            }

            // --- inside a string literal: consume escapes and delim ---
            if (activeStringDelim != null)
            {
                if (!string.IsNullOrEmpty(escape) && MatchAt(input, i, escape))
                {
                    // Skip the escape byte(s) and whatever follows.
                    i += escape.Length;
                    if (i < input.Length) i++;
                    continue;
                }
                if (MatchAt(input, i, activeStringDelim))
                {
                    i += activeStringDelim.Length;
                    activeStringDelim = null;
                    continue;
                }
                i++;
                continue;
            }

            // --- char literal: `#\X` — consume prefix + one char, no bracket accounting ---
            if (!string.IsNullOrEmpty(charLiteralPrefix) && MatchAt(input, i, charLiteralPrefix))
            {
                i += charLiteralPrefix.Length;
                // Consume exactly one character as the literal. Racket
                // also allows named chars like `#\space` and `#\nul`,
                // which are an identifier run rather than a single
                // char — walk until whitespace or a close bracket
                // for the common case.
                if (i < input.Length)
                {
                    i++;
                    while (i < input.Length
                           && !char.IsWhiteSpace(input[i])
                           && !IsBracketChar(input[i], open, close))
                    {
                        i++;
                    }
                }
                continue;
            }

            // --- datum comment: `#;` — mark the next datum as skipped ---
            if (!string.IsNullOrEmpty(datumCommentPrefix) && MatchAt(input, i, datumCommentPrefix))
            {
                i += datumCommentPrefix.Length;
                pendingDatumComments++;
                continue;
            }

            // --- line / block comment openers ---
            if (!string.IsNullOrEmpty(lineComment) && MatchAt(input, i, lineComment))
            {
                inLineComment = true;
                i += lineComment.Length;
                continue;
            }
            if (!string.IsNullOrEmpty(blockCommentOpen) && MatchAt(input, i, blockCommentOpen))
            {
                inBlockComment = true;
                i += blockCommentOpen.Length;
                continue;
            }

            // --- string opener ---
            bool matchedString = false;
            foreach (var delim in stringDelims)
            {
                if (MatchAt(input, i, delim))
                {
                    // A pending datum comment on a string literal
                    // consumes the string and resolves the comment
                    // without any bracket accounting.
                    if (pendingDatumComments > 0)
                        pendingDatumComments--;
                    activeStringDelim = delim;
                    i += delim.Length;
                    matchedString = true;
                    break;
                }
            }
            if (matchedString) continue;

            // --- open bracket ---
            bool matchedOpen = false;
            foreach (var o in open)
            {
                if (MatchAt(input, i, o))
                {
                    if (pendingDatumComments > 0)
                    {
                        // Remember the outer depth at which this
                        // datum-commented list opens; we'll resolve
                        // the datum comment when the matching close
                        // returns to the same depth.
                        datumCommentAnchorDepths.Push(depth);
                        pendingDatumComments--;
                    }
                    depth++;
                    i += o.Length;
                    matchedOpen = true;
                    break;
                }
            }
            if (matchedOpen) continue;

            // --- close bracket ---
            bool matchedClose = false;
            foreach (var c in close)
            {
                if (MatchAt(input, i, c))
                {
                    depth--;
                    if (depth < 0)
                    {
                        return new BalancedParensResult(
                            IsComplete: false,
                            Depth: depth,
                            Diagnostic: $"unexpected '{c}' at byte {i} — more closing brackets than opening ones");
                    }
                    // If this close matches a datum-commented list's
                    // open (same depth), the comment is now resolved.
                    if (datumCommentAnchorDepths.Count > 0
                        && datumCommentAnchorDepths.Peek() == depth)
                    {
                        datumCommentAnchorDepths.Pop();
                    }
                    i += c.Length;
                    matchedClose = true;
                    break;
                }
            }
            if (matchedClose) continue;

            // --- ordinary atom character: resolves a pending datum comment ---
            // IMPORTANT: only fires when there is NO active datum-commented
            // list (anchor stack empty). Atoms inside a datum-commented
            // list are already being skipped by the list's bracket
            // accounting — decrementing pending for them would prematurely
            // resolve a still-pending outer `#;`. Example: `#;#;(a)` needs
            // two datums; without this guard the inner `a` would decrement
            // pending from 1 to 0 and the counter would wrongly report the
            // whole input as complete.
            if (pendingDatumComments > 0
                && datumCommentAnchorDepths.Count == 0
                && !char.IsWhiteSpace(input[i]))
            {
                // Consume the atom run (non-whitespace, non-bracket,
                // non-string-delim) and decrement the pending counter.
                while (i < input.Length
                       && !char.IsWhiteSpace(input[i])
                       && !IsBracketChar(input[i], open, close)
                       && !IsAnyPrefix(input, i, stringDelims))
                {
                    i++;
                }
                pendingDatumComments--;
                continue;
            }

            i++;
        }

        // At EOF: depth > 0 means open brackets remain; active string /
        // block comment / pending datum comment also count as incomplete.
        if (activeStringDelim != null)
            return new BalancedParensResult(false, depth,
                $"unterminated string literal (missing closing {activeStringDelim})");
        if (inBlockComment)
            return new BalancedParensResult(false, depth,
                $"unterminated block comment (missing closing {blockCommentClose})");
        if (pendingDatumComments > 0)
            return new BalancedParensResult(false, depth,
                $"{pendingDatumComments} pending datum comment(s) with no following datum");
        if (depth > 0)
            return new BalancedParensResult(false, depth,
                $"expected {depth} more closing bracket(s) before submit");

        return new BalancedParensResult(true, 0, null);
    }

    private static bool MatchAt(string input, int i, string pattern)
    {
        if (i + pattern.Length > input.Length) return false;
        for (int k = 0; k < pattern.Length; k++)
        {
            if (input[i + k] != pattern[k]) return false;
        }
        return true;
    }

    private static bool IsBracketChar(char ch, List<string> open, List<string> close)
    {
        var s = ch.ToString();
        foreach (var o in open) if (o == s) return true;
        foreach (var c in close) if (c == s) return true;
        return false;
    }

    private static bool IsAnyPrefix(string input, int i, List<string> prefixes)
    {
        foreach (var p in prefixes)
            if (MatchAt(input, i, p)) return true;
        return false;
    }
}

public record BalancedParensResult(bool IsComplete, int Depth, string? Diagnostic);
