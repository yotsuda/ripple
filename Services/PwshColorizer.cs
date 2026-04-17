using System.Text;

namespace Ripple.Services;

/// <summary>
/// Lightweight PowerShell syntax colorizer used to paint AI-entered commands
/// in the visible worker console.
///
/// ripple writes the AI's command directly to the worker's stdout so the
/// human user can see what the AI is doing, but it does this with
/// PSReadLine's live mirror suppressed (otherwise the user sees the
/// character-by-character typing animation and prediction ghost text). The
/// trade-off used to be that the echoed command was plain text with no
/// colors. This colorizer restores PSReadLine-like highlighting by walking
/// the command with a hand-written state machine and wrapping each token in
/// a matching ANSI SGR sequence.
///
/// Not a full PowerShell parser — it covers the common cases of:
///   - cmdlet / function names in command position (yellow)
///   - parameters like -Foo (dark gray)
///   - single-quoted and double-quoted strings (dark cyan)
///   - variables including scoped forms and ${curly} (green)
///   - numbers (white)
///   - line comments (dark green)
///   - operators ; | &amp;&amp; || that reset back to command position (dark gray)
///
/// Everything else (paths, bareword arguments, unknown syntax) is emitted
/// without color so the original text is preserved verbatim.
/// </summary>
internal static class PwshColorizer
{
    // PSReadLine's default token colors. Match the ones PowerShell.MCP's
    // Write-ColoredCommand falls back to when PSReadLineOption properties
    // are unavailable, so ripple's echo looks consistent with PSReadLine's
    // interactive rendering.
    private const string Reset     = "\x1b[0m";
    private const string Command   = "\x1b[93m";  // Yellow
    private const string Parameter = "\x1b[90m";  // DarkGray
    private const string String_   = "\x1b[36m";  // DarkCyan
    private const string Variable  = "\x1b[92m";  // Green
    private const string Keyword   = "\x1b[92m";  // Green (PSReadLine default)
    private const string Number    = "\x1b[97m";  // White
    private const string Operator  = "\x1b[90m";  // DarkGray
    private const string Comment   = "\x1b[32m";  // DarkGreen

    // PowerShell reserved keywords. Matched case-insensitively so `FOREACH`
    // and `Foreach` are both colored. Mirrors the set flagged with
    // TokenFlags.Keyword by System.Management.Automation.Language.Parser —
    // ripple can't reference that assembly under NativeAOT, so we hard-code
    // the list instead.
    // Authoritative list copied from PowerShell's tokenizer.cs s_keywordText
    // (System.Management.Automation/engine/parser/tokenizer.cs:625-639). Every
    // entry here corresponds to a TokenKind whose traits include
    // TokenFlags.Keyword in token.cs:903-954, which is exactly what PSReadLine
    // checks to color tokens with _keywordColor.
    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "elseif", "if", "else", "switch",
        "foreach", "from", "in", "for",
        "while", "until", "do", "try",
        "catch", "finally", "trap", "data",
        "return", "continue", "break", "exit",
        "throw", "begin", "process", "end",
        "dynamicparam", "function", "filter", "param",
        "class", "define", "var", "using",
        "workflow", "parallel", "sequence", "inlinescript",
        "configuration", "public", "private", "static",
        "interface", "enum", "namespace", "module",
        "type", "assembly", "command", "hidden",
        "base", "default", "clean",
    };

    public static string Colorize(string command)
    {
        if (string.IsNullOrEmpty(command)) return command;

        var sb = new StringBuilder(command.Length + 64);
        int i = 0;
        int len = command.Length;
        bool atCommandPosition = true;

        while (i < len)
        {
            char c = command[i];

            // Whitespace passes through unchanged and doesn't move us out of
            // command position (leading whitespace before the cmdlet name).
            if (c == ' ' || c == '\t' || c == '\r' || c == '\n')
            {
                sb.Append(c);
                i++;
                continue;
            }

            // Line comment: `#` to end of line. The `#` must be at the start
            // of a token, not part of an identifier like `foo#bar`.
            if (c == '#' && (i == 0 || IsTokenBoundary(command[i - 1])))
            {
                int end = command.IndexOf('\n', i);
                if (end < 0) end = len;
                sb.Append(Comment).Append(command, i, end - i).Append(Reset);
                i = end;
                atCommandPosition = false;
                continue;
            }

            // Strings. Single-quoted strings are literal with no escapes.
            // Double-quoted strings are expandable — embedded $var, ${var},
            // and $(expr) interpolations are highlighted as Variable just
            // like PSReadLine's StringExpandableToken handling in
            // Render.cs:596-619.
            if (c == '\'')
            {
                int start = i;
                i++;
                while (i < len)
                {
                    if (command[i] == '\'') { i++; break; }
                    i++;
                }
                sb.Append(String_).Append(command, start, i - start).Append(Reset);
                atCommandPosition = false;
                continue;
            }
            if (c == '"')
            {
                sb.Append(String_).Append('"');
                i++;
                int segStart = i;
                bool closed = false;
                while (i < len)
                {
                    char q = command[i];
                    if (q == '`' && i + 1 < len)
                    {
                        i += 2;
                        continue;
                    }
                    if (q == '"')
                    {
                        if (i > segStart) sb.Append(command, segStart, i - segStart);
                        sb.Append('"').Append(Reset);
                        i++;
                        closed = true;
                        break;
                    }
                    if (q == '$' && i + 1 < len)
                    {
                        char next = command[i + 1];
                        bool isInterpolation = next == '(' || next == '{'
                            || char.IsLetter(next) || next == '_';
                        if (isInterpolation)
                        {
                            if (i > segStart) sb.Append(command, segStart, i - segStart);
                            sb.Append(Reset);
                            int varStart = i;
                            i++;
                            if (command[i] == '{')
                            {
                                int depth = 1;
                                i++;
                                while (i < len && depth > 0)
                                {
                                    if (command[i] == '{') depth++;
                                    else if (command[i] == '}') depth--;
                                    i++;
                                }
                            }
                            else if (command[i] == '(')
                            {
                                int depth = 1;
                                i++;
                                while (i < len && depth > 0)
                                {
                                    if (command[i] == '(') depth++;
                                    else if (command[i] == ')') depth--;
                                    i++;
                                }
                            }
                            else
                            {
                                while (i < len && (char.IsLetterOrDigit(command[i]) || command[i] == '_' || command[i] == ':'))
                                    i++;
                            }
                            sb.Append(Variable).Append(command, varStart, i - varStart).Append(Reset);
                            sb.Append(String_);
                            segStart = i;
                            continue;
                        }
                    }
                    i++;
                }
                if (!closed)
                {
                    if (i > segStart) sb.Append(command, segStart, i - segStart);
                    sb.Append(Reset);
                }
                atCommandPosition = false;
                continue;
            }

            // Variable: $name, $script:name, $env:PATH, $($expr), ${complex}.
            // For $(...) and ${...} we just grab the whole balanced section
            // as one variable-colored token — close enough for display.
            if (c == '$')
            {
                int start = i;
                i++;
                if (i < len && command[i] == '{')
                {
                    int depth = 1;
                    i++;
                    while (i < len && depth > 0)
                    {
                        if (command[i] == '{') depth++;
                        else if (command[i] == '}') depth--;
                        i++;
                    }
                }
                else if (i < len && command[i] == '(')
                {
                    int depth = 1;
                    i++;
                    while (i < len && depth > 0)
                    {
                        if (command[i] == '(') depth++;
                        else if (command[i] == ')') depth--;
                        i++;
                    }
                }
                else
                {
                    while (i < len && (char.IsLetterOrDigit(command[i]) || command[i] == '_' || command[i] == ':'))
                        i++;
                }
                sb.Append(Variable).Append(command, start, i - start).Append(Reset);
                atCommandPosition = false;
                continue;
            }

            // Parameter: `-Name` where Name starts with a letter. `-` alone
            // or followed by a digit is treated as an operator / negative
            // number and falls through below.
            if (c == '-' && i + 1 < len && (char.IsLetter(command[i + 1]) || command[i + 1] == '_'))
            {
                // If the previous character looks like we're still in a bareword
                // (e.g. cmdlet name "Get-Date"), don't treat this `-` as a
                // parameter; let the identifier reader continue instead.
                char prev = i > 0 ? command[i - 1] : ' ';
                if (!IsTokenBoundary(prev))
                {
                    // Fall through to identifier / bareword path below.
                }
                else
                {
                    int start = i;
                    i++;
                    while (i < len && (char.IsLetterOrDigit(command[i]) || command[i] == '_' || command[i] == '-'))
                        i++;
                    sb.Append(Parameter).Append(command, start, i - start).Append(Reset);
                    atCommandPosition = false;
                    continue;
                }
            }

            // Number literal (integer or decimal, optionally negative).
            if (char.IsDigit(c) || (c == '-' && i + 1 < len && char.IsDigit(command[i + 1]) && (i == 0 || IsTokenBoundary(command[i - 1]))))
            {
                int start = i;
                if (c == '-') i++;
                while (i < len && (char.IsDigit(command[i]) || command[i] == '.'))
                    i++;
                sb.Append(Number).Append(command, start, i - start).Append(Reset);
                atCommandPosition = false;
                continue;
            }

            // Identifier — could be a PowerShell keyword, a cmdlet/function
            // name in command position, or a bareword argument. We always
            // run the identifier scanner (even mid-statement) so that
            // keywords like `in` inside `foreach ($x in $xs)` or `else` in
            // `if { } else { }` get colored green regardless of whether
            // we'd otherwise consider the position a command position.
            if (char.IsLetter(c) || c == '_')
            {
                int start = i;
                while (i < len)
                {
                    char cc = command[i];
                    if (char.IsLetterOrDigit(cc) || cc == '_' || cc == '-' || cc == '.')
                        i++;
                    else break;
                }
                int length = i - start;
                if (Keywords.Contains(command.Substring(start, length)))
                {
                    sb.Append(Keyword).Append(command, start, length).Append(Reset);
                    atCommandPosition = false;
                }
                else if (atCommandPosition)
                {
                    sb.Append(Command).Append(command, start, length).Append(Reset);
                    atCommandPosition = false;
                }
                else
                {
                    sb.Append(command, start, length);
                }
                continue;
            }

            // Scriptblock braces. `{` opens a new statement context so the
            // next identifier inside (e.g. `Write-Host` inside
            // `foreach (...) { Write-Host ... }`) is colored as a command.
            // Only reset command position when `{` is preceded by obvious
            // statement boundaries — otherwise braces embedded in a path
            // like `C:\Users\{user}\` would wrongly re-enter command mode.
            if (c == '{' || c == '}')
            {
                sb.Append(c);
                if (c == '{')
                {
                    char prev = i > 0 ? command[i - 1] : ' ';
                    if (prev == ' ' || prev == '\t' || prev == '\n' || prev == '\r'
                        || prev == ')' || prev == '=' || prev == ';' || prev == '|')
                        atCommandPosition = true;
                }
                i++;
                continue;
            }

            // Operators that end the current statement and open a new one.
            // After `;`, `|`, `&&`, `||` the next identifier is again a
            // command in command position.
            if (c == ';' || c == '|' || c == '&')
            {
                int start = i;
                if ((c == '&' || c == '|') && i + 1 < len && command[i + 1] == c)
                    i += 2;
                else
                    i++;
                sb.Append(Operator).Append(command, start, i - start).Append(Reset);
                atCommandPosition = true;
                continue;
            }

            // Everything else (paths, bareword args, unrecognised syntax):
            // pass through until the next obvious token boundary so the
            // original text is preserved byte-for-byte.
            {
                int start = i;
                while (i < len)
                {
                    char cc = command[i];
                    if (cc == ' ' || cc == '\t' || cc == '\r' || cc == '\n' ||
                        cc == '\'' || cc == '"' || cc == '$' || cc == ';' ||
                        cc == '|' || cc == '&' || cc == '#' || cc == '{' || cc == '}')
                        break;
                    i++;
                }
                if (i > start) sb.Append(command, start, i - start);
                else { sb.Append(c); i++; }
                atCommandPosition = false;
            }
        }

        return sb.ToString();
    }

    private static bool IsTokenBoundary(char c)
        => c == ' ' || c == '\t' || c == '\r' || c == '\n'
        || c == ';' || c == '|' || c == '&' || c == '(' || c == '{';
}
