using Ripple.Services;
using Ripple.Services.Adapters;

namespace Ripple.Tests;

public static class BalancedParensCounterTests
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

        Console.WriteLine("=== BalancedParensCounter Tests ===");

        // Lisp-family spec mirroring what racket.yaml declares. Every test
        // runs against this spec unless it explicitly builds its own.
        var lispSpec = new BalancedParensSpec
        {
            Open = ["(", "[", "{"],
            Close = [")", "]", "}"],
            StringDelims = ["\""],
            Escape = "\\",
            LineComment = ";",
            BlockComment = ["#|", "|#"],
            CharLiteralPrefix = "#\\",
            DatumCommentPrefix = "#;",
        };

        // --- baseline: balanced expressions are complete ---

        Assert(BalancedParensCounter.Evaluate(lispSpec, "(+ 1 2)").IsComplete,
            "simple balanced expression is complete");

        Assert(BalancedParensCounter.Evaluate(lispSpec, "").IsComplete,
            "empty input is trivially complete");

        Assert(BalancedParensCounter.Evaluate(lispSpec, "(define (f x) (+ x 1))").IsComplete,
            "nested balanced expression is complete");

        Assert(BalancedParensCounter.Evaluate(lispSpec, "[1 2 3]").IsComplete,
            "square brackets balanced");

        Assert(BalancedParensCounter.Evaluate(lispSpec, "(let ([x 1] [y 2]) (+ x y))").IsComplete,
            "mixed paren + square brackets balanced");

        // --- baseline: unbalanced expressions are incomplete ---

        {
            var r = BalancedParensCounter.Evaluate(lispSpec, "(+ 1");
            Assert(!r.IsComplete && r.Depth == 1,
                $"open paren without close: incomplete, depth=1 (got IsComplete={r.IsComplete}, depth={r.Depth})");
        }

        {
            var r = BalancedParensCounter.Evaluate(lispSpec, "(define (f x");
            Assert(!r.IsComplete && r.Depth == 2,
                $"two open parens without close: incomplete, depth=2 (got depth={r.Depth})");
        }

        {
            var r = BalancedParensCounter.Evaluate(lispSpec, "(+ 1 2))");
            Assert(!r.IsComplete && r.Diagnostic != null && r.Diagnostic.Contains("unexpected"),
                $"extra closing paren is incomplete with diagnostic (got {r.Diagnostic})");
        }

        // --- strings with embedded brackets don't count toward depth ---

        Assert(BalancedParensCounter.Evaluate(lispSpec, "\"(((\"").IsComplete,
            "string literal with embedded opens doesn't break counting");

        Assert(BalancedParensCounter.Evaluate(lispSpec, "(display \"(\")").IsComplete,
            "paren inside string inside call balanced");

        Assert(!BalancedParensCounter.Evaluate(lispSpec, "\"unterminated").IsComplete,
            "unterminated string literal is incomplete");

        Assert(BalancedParensCounter.Evaluate(lispSpec, "(display \"say \\\"hi\\\"\")").IsComplete,
            "escaped quote inside string balanced");

        // --- comments are ignored ---

        Assert(BalancedParensCounter.Evaluate(lispSpec, "; (((\n(+ 1 2)").IsComplete,
            "line comment with fake opens doesn't affect depth");

        Assert(BalancedParensCounter.Evaluate(lispSpec, "#| nested |# (+ 1 2)").IsComplete,
            "block comment with fake opens doesn't affect depth");

        Assert(!BalancedParensCounter.Evaluate(lispSpec, "#| open without close").IsComplete,
            "unterminated block comment is incomplete");

        // --- reader macros: char literals of parens (§18 Q1 extension) ---

        Assert(BalancedParensCounter.Evaluate(lispSpec, "(char->integer #\\()").IsComplete,
            "char literal #\\( does NOT count as open paren");

        Assert(BalancedParensCounter.Evaluate(lispSpec, "(list #\\( #\\) #\\[)").IsComplete,
            "multiple char literals of bracket chars don't affect depth");

        Assert(BalancedParensCounter.Evaluate(lispSpec, "(char->integer #\\space)").IsComplete,
            "named char literal #\\space consumed as atom run");

        {
            // Without the char-literal extension, this would be detected as
            // "3 open parens + 0 close = depth 3". The fix must ensure the
            // second and third parens inside char literals are skipped.
            var r = BalancedParensCounter.Evaluate(lispSpec, "(list #\\( #\\( #\\()");
            Assert(r.IsComplete,
                $"three char-literal opens in a balanced list are complete (got depth={r.Depth})");
        }

        // --- reader macros: datum comments (§18 Q1 extension) ---

        Assert(BalancedParensCounter.Evaluate(lispSpec, "(+ 1 #;999 2)").IsComplete,
            "datum comment on atom is complete");

        Assert(BalancedParensCounter.Evaluate(lispSpec, "(+ 1 #;(nested list here) 2)").IsComplete,
            "datum comment on list is complete");

        Assert(BalancedParensCounter.Evaluate(lispSpec, "(+ 1 #;\"skipped string\" 2)").IsComplete,
            "datum comment on string is complete");

        {
            // Nested datum comments: #;#;(a)(b) skips two following datums.
            var r = BalancedParensCounter.Evaluate(lispSpec, "(list 1 #;#;(a) (b) 2)");
            Assert(r.IsComplete,
                $"nested datum comments skip two datums (got IsComplete={r.IsComplete}, depth={r.Depth})");
        }

        // --- mixed: strings with char-literal-looking content ---

        Assert(BalancedParensCounter.Evaluate(lispSpec, "\"#\\(\"").IsComplete,
            "#\\( inside a string stays part of the string, not a char literal");

        // --- defaults: spec with no fields uses sensible defaults ---

        var defaultSpec = new BalancedParensSpec();
        Assert(BalancedParensCounter.Evaluate(defaultSpec, "(+ 1 2)").IsComplete,
            "defaults: ()/[]/{} balanced");
        Assert(!BalancedParensCounter.Evaluate(defaultSpec, "(+ 1").IsComplete,
            "defaults: unbalanced detected");

        // ================================================================
        // §18 Q1 stress: pathological reader-macro / comment / string
        // interleaving that a Lisp-family counter has to handle before
        // the schema can be stamped "closed". Each test frames an AI
        // failure mode — a half-finished expression that would deadlock
        // the REPL if submitted, or a fully-formed expression that uses
        // a reader construct the counter must understand.
        // ================================================================

        // --- dangling datum-comment prefixes ---
        // `#;` requires a following datum. `#; ` with nothing else is an
        // AI typo that the counter must reject.
        Assert(!BalancedParensCounter.Evaluate(lispSpec, "#;").IsComplete,
            "stress: datum comment prefix with no datum is incomplete");
        Assert(!BalancedParensCounter.Evaluate(lispSpec, "#; \n\t").IsComplete,
            "stress: datum comment prefix followed by only whitespace is incomplete");

        // Two `#;` prefixes stacked require TWO datums to follow. When
        // only one is given, the counter must report incomplete. Guard
        // against an earlier bug where an atom inside the first
        // datum-commented list decremented pendingDatumComments
        // prematurely, making the counter wrongly report complete.
        {
            var r = BalancedParensCounter.Evaluate(lispSpec, "#;#;(a)");
            Assert(!r.IsComplete,
                $"stress: `#;#;(a)` needs 2 datums, got 1 — incomplete (got IsComplete={r.IsComplete}, depth={r.Depth})");
        }

        // Same shape with a longer inner list — the inner atoms must
        // NOT resolve the outer pending counter either.
        {
            var r = BalancedParensCounter.Evaluate(lispSpec, "#;#;(a b c d)");
            Assert(!r.IsComplete,
                $"stress: `#;#;(a b c d)` still needs a second datum (got IsComplete={r.IsComplete})");
        }

        // Three stacked `#;` with two datums still needs one more.
        {
            var r = BalancedParensCounter.Evaluate(lispSpec, "#;#;#;(a) (b)");
            Assert(!r.IsComplete,
                $"stress: `#;#;#;(a) (b)` needs 3 datums, got 2 (got IsComplete={r.IsComplete})");
        }

        // Two `#;` with two following datums (one list, one atom) resolves cleanly.
        Assert(BalancedParensCounter.Evaluate(lispSpec, "#;#;(a) atom").IsComplete,
            "stress: `#;#;(a) atom` — two following datums, complete");

        // Two `#;` with three following datums leaves the third as
        // top-level result. Still complete (one leftover datum is a
        // valid top-level form).
        Assert(BalancedParensCounter.Evaluate(lispSpec, "#;#;(a) (b) (c)").IsComplete,
            "stress: `#;#;(a) (b) (c)` — third datum is the top-level result");

        // --- char literal of special chars: must not trigger the
        // respective reader state they normally open ---

        // `#\"` is a char literal of double-quote. Must NOT enter
        // string-literal mode. Without the char-literal extension, the
        // bare `"` would open a string that runs to EOF.
        Assert(BalancedParensCounter.Evaluate(lispSpec, "(char->integer #\\\")").IsComplete,
            "stress: `#\\\"` is a char literal, not a string opener");

        // `#\;` is a char literal of semicolon. Must NOT enter line-comment mode.
        // Without the extension, everything from `;` onwards would be a comment
        // and the trailing `)` would be missed.
        Assert(BalancedParensCounter.Evaluate(lispSpec, "(char->integer #\\;)").IsComplete,
            "stress: `#\\;` is a char literal, not a line-comment opener");

        // `#\|` is a char literal of pipe. The subsequent `)` must close
        // the outer list. Without the extension, `#\|` followed by `...)`
        // could be misread as entering a block comment (`|` alone isn't
        // `#|`, but it's worth pinning).
        Assert(BalancedParensCounter.Evaluate(lispSpec, "(char->integer #\\|)").IsComplete,
            "stress: `#\\|` is a char literal, not a block-comment opener");

        // `#\\` is a char literal of backslash. The char-literal prefix
        // `#\` consumes 2 bytes; then the following `\` is consumed as
        // the char payload. Then the close paren lands at i+3.
        Assert(BalancedParensCounter.Evaluate(lispSpec, "(char->integer #\\\\)").IsComplete,
            "stress: `#\\\\` is a char literal of backslash");

        // --- bracket type mismatches ---
        // `(1 2]` uses a paren as open and a square as close. Current
        // counter tracks only depth, not bracket type, so it reports
        // "complete". This is a KNOWN gap — the REPL would error out
        // on submit. Pinned here so any future fix that adds type
        // tracking intentionally flips this test.
        {
            var r = BalancedParensCounter.Evaluate(lispSpec, "(1 2]");
            Assert(r.IsComplete,
                "stress: `(1 2]` — bracket type mismatch currently NOT detected (depth-only counter)");
        }

        // --- char literal edge positions ---
        // Bare `#\(` as the entire expression — just a char literal, no
        // list. Must be complete (no pending brackets).
        Assert(BalancedParensCounter.Evaluate(lispSpec, "#\\(").IsComplete,
            "stress: bare `#\\(` is a complete top-level char literal");

        // `#\(` followed by content — the char literal consumes `(` plus
        // the atom run of `hello`, then EOF. Complete.
        Assert(BalancedParensCounter.Evaluate(lispSpec, "#\\( hello").IsComplete,
            "stress: `#\\( hello` is complete (char literal + atom)");

        // Char literal prefix at the very end of input with no char to
        // consume. Racket would flag a read error, but the counter's
        // job is just to say "do we have a complete expression?" —
        // with the prefix alone, the answer is ambiguous. Current impl
        // advances past the prefix without requiring a character,
        // treating it as complete. Pin the behaviour.
        Assert(BalancedParensCounter.Evaluate(lispSpec, "#\\").IsComplete,
            "stress: bare `#\\` prefix with no char currently reported complete");

        // --- strings that contain every special reader character ---
        // Counter must not false-trigger char-literal, datum-comment,
        // line-comment, block-comment, or bracket accounting while
        // inside a string.
        Assert(BalancedParensCounter.Evaluate(lispSpec, "\"#\\\" #; ;; #| |# ( [ {\"").IsComplete,
            "stress: string body with every special reader token is inert");

        // String opened on one line, closed on another (multi-line
        // string literal). Racket allows this; the counter's string
        // scanner should cross newlines happily.
        Assert(BalancedParensCounter.Evaluate(lispSpec, "\"line1\nline2\nline3\"").IsComplete,
            "stress: multi-line string literal closed on final line");

        // Unterminated multi-line string is incomplete.
        Assert(!BalancedParensCounter.Evaluate(lispSpec, "\"line1\nline2\nno close").IsComplete,
            "stress: unterminated multi-line string is incomplete");

        // Empty string literal.
        Assert(BalancedParensCounter.Evaluate(lispSpec, "\"\"").IsComplete,
            "stress: empty string literal is complete");

        // String containing just an escape + closing delim.
        // Body: `\"` → escape + next char (`"`). Then string needs
        // another `"` to close. Input `"\\""` = open, escape-`"`,
        // close. Complete.
        Assert(BalancedParensCounter.Evaluate(lispSpec, "\"\\\"\"").IsComplete,
            "stress: string containing only escaped quote closes correctly");

        // String with trailing escape at EOF — the escape consumes the
        // closing `"` and the string is left unterminated.
        Assert(!BalancedParensCounter.Evaluate(lispSpec, "\"\\").IsComplete,
            "stress: string with trailing escape-at-EOF is incomplete");

        // --- block comments containing every reader special ---
        Assert(BalancedParensCounter.Evaluate(lispSpec, "#| \"unclosed #; ;; #\\( ( [ { |# (+ 1 2)").IsComplete,
            "stress: block comment body is fully inert to reader specials");

        // Empty block comment `#||#` — open + close, no body.
        Assert(BalancedParensCounter.Evaluate(lispSpec, "#||#").IsComplete,
            "stress: empty block comment `#||#` is complete");

        // --- line comment: content is ignored, including other comment openers ---
        Assert(BalancedParensCounter.Evaluate(lispSpec, "; #| not a block comment\n(+ 1 2)").IsComplete,
            "stress: line comment containing `#|` does not open a block comment");

        // Line comment terminated by CRLF (Windows line endings).
        Assert(BalancedParensCounter.Evaluate(lispSpec, ";; comment\r\n(+ 1 2)").IsComplete,
            "stress: line comment terminates on CRLF");

        // Line comment at EOF without a trailing newline — the comment
        // body runs to end-of-input.
        Assert(BalancedParensCounter.Evaluate(lispSpec, "(+ 1 2) ; trailing comment").IsComplete,
            "stress: line comment at EOF without newline is complete");

        // --- datum comment resolving over a complex target ---

        // Datum comment on a nested list with its own inner reader
        // specials (char literals, strings) — the whole subtree is
        // skipped cleanly.
        Assert(BalancedParensCounter.Evaluate(lispSpec, "(+ 1 #;(let ([c #\\(] [s \"(\"]) c) 2)").IsComplete,
            "stress: datum comment on list containing char literal and string");

        // Pre-existing test expanded: `#;` on a block comment. The
        // datum comment must consume the comment-stripped content.
        // Racket semantics: block comments are whitespace, so `#;` on
        // a block comment is ambiguous. Current impl treats the block
        // comment body as invisible; pending remains until the next
        // real datum. Input: `#; #| cmt |# (foo)` — the `(foo)` is
        // the datum consumed by `#;`.
        Assert(BalancedParensCounter.Evaluate(lispSpec, "#; #| cmt |# (foo)").IsComplete,
            "stress: datum comment with intervening block comment");

        // --- really long input with many nesting levels ---
        {
            // 200 nested opens + 200 nested closes — stress depth tracking.
            var deepOpen = new string('(', 200);
            var deepClose = new string(')', 200);
            Assert(BalancedParensCounter.Evaluate(lispSpec, deepOpen + deepClose).IsComplete,
                "stress: 200-deep nesting balanced");
            Assert(!BalancedParensCounter.Evaluate(lispSpec, deepOpen + deepClose[..199]).IsComplete,
                "stress: 200-deep nesting missing one close is incomplete");
        }

        // --- quasi-quote and unquote prefixes: not special to the
        // counter, pass through as atoms ---
        Assert(BalancedParensCounter.Evaluate(lispSpec, "`(a ,b ,@c)").IsComplete,
            "stress: quasi-quote with unquote-splice is balanced");

        // --- CL reader conditionals `#+` and `#-` are NOT special to
        // this counter (CL would read them but the counter ignores).
        // The following form IS complete because `(feature)` and
        // `(then)` are two top-level lists.
        Assert(BalancedParensCounter.Evaluate(lispSpec, "#+nil (skipped) (included)").IsComplete,
            "stress: CL reader conditional #+nil flows through as an atom");

        Console.WriteLine($"\n{pass} passed, {fail} failed");
    }
}
