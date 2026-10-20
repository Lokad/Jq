using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Lokad.Jq;

internal enum TokenKind { Identifier, FieldName, Number, String, Symbol, End }

internal readonly record struct Token(TokenKind Kind, string Text, JqSourceSpan Span);

internal static class Lexer
{
    // Template alphabet: literal segments keep escaped backslash pairs raw so an
    // interpolation marker is a backslash-paren that does not follow an escape.
    internal static bool ContainsInterpolation(string template)
    {
        for (int index = 0; index + 1 < template.Length; index++)
        {
            if (template[index] != (char)92)
                continue;
            if (template[index + 1] == (char)92)
            {
                index++;
                continue;
            }
            if (template[index + 1] == (char)40)
                return true;
            index++;
        }
        return false;
    }

    // Collapses the surviving escaped backslash pairs after splitting. Other
    // escapes were decoded while lexing, so no other backslashes remain.
    internal static string DecodeLiterals(string raw)
    {
        return raw.Replace("\\\\", "\\");
    }

    public static List<Token> Tokenize(string source, JqProgramSource programSource, JqBudget budget)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(programSource);
        ArgumentNullException.ThrowIfNull(budget);
        budget.CheckCancellation();
        budget.ReserveBytes(128 + 4L * source.Length);
        var tokens = new List<Token>();
        int position = 0;
        int line = 1;
        int lineStart = 0;
        JqSourceSpan SpanAt(int offset)
        {
            for (; position < offset; position++)
            {
                if ((position & 4095) == 0) budget.CheckCancellation();
                if (source[position] == '\n') { line++; lineStart = position + 1; }
            }
            return new JqSourceSpan(line, offset - lineStart + 1);
        }
        void AddToken(Token token)
        {
            if (tokens.Count == tokens.Capacity)
            {
                int capacity = (int)Math.Min(Array.MaxLength, Math.Max(4, 2L * tokens.Capacity));
                if (tokens.Count == Array.MaxLength)
                    throw new JqQuotaException("token backing cannot be represented");
                // Token backing capacity plus a coarse allowance for syntax and
                // literal metadata retained with this compiled program.
                budget.ReserveBytes(128L * capacity);
                tokens.EnsureCapacity(capacity);
            }
            tokens.Add(token);
        }
        for (var i = 0; i < source.Length;)
        {
            budget.CheckCancellation();
            var c = source[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            // Comments mirror the reference IN_COMMENT state: a backslash
            // escapes the next character (a line break included, across CRLF),
            // so only a bare line break ends the comment while odd trailing
            // backslash runs continue it; the newline itself stays whitespace.
            if (c == '#')
            {
                while (i < source.Length)
                {
                    budget.CheckCancellation();
                    if (source[i] == '\\' && i + 1 < source.Length
                        && (source[i + 1] == '\\' || source[i + 1] == '\n'
                            || (source[i + 1] == '\r' && i + 2 < source.Length && source[i + 2] == '\n')))
                    {
                        i += source[i + 1] == '\r' ? 3 : 2;
                        continue;
                    }
                    if (source[i] == '\n')
                        break;
                    i++;
                }
                continue;
            }
            if (char.IsLetter(c) || c == '_')
            {
                var start = i++;
                while (i < source.Length && (char.IsLetterOrDigit(source[i]) || source[i] == '_'))
                {
                    budget.CheckCancellation();
                    i++;
                }
                // An adjacent dot token distinguishes .else from . else without mistaking
                // a numeric decimal point (1.then) for field access.
                var isField = start > 0 && source[start - 1] == '.'
                    && tokens.Count > 0 && tokens[^1] is { Kind: TokenKind.Symbol, Text: "." };
                if (!isField)
                {
                    // Qualified names (foo::bar) are single tokens when contiguous,
                    // matching the reference IDENT rule. A trailing :: without a
                    // following name stays separate symbols.
                    while (i + 2 < source.Length && source[i] == ':' && source[i + 1] == ':'
                        && (char.IsLetter(source[i + 2]) || source[i + 2] == '_'))
                    {
                        i += 2;
                        while (i < source.Length && (char.IsLetterOrDigit(source[i]) || source[i] == '_'))
                        {
                            budget.CheckCancellation();
                            i++;
                        }
                    }
                }
                var kind = isField ? TokenKind.FieldName : TokenKind.Identifier;
                AddToken(new Token(kind, source[start..i], SpanAt(start)));
                continue;
            }
            // Numbers never take a sign; `-` stays an operator so `1+2` and
            // `1 - -2` parse. A trailing dot stays literal (even before
            // keywords, as in `1.then`), while `+`/`-` only continue exponents.
            // Leading-dot fractions (`.5`) match the reference literal syntax.
            if (char.IsDigit(c) || (c == '.' && i + 1 < source.Length && char.IsDigit(source[i + 1])))
            {
                var start = i;
                while (i < source.Length && char.IsDigit(source[i]))
                {
                    budget.CheckCancellation();
                    i++;
                }
                if (i < source.Length && source[i] == '.')
                {
                    i++;
                    while (i < source.Length && char.IsDigit(source[i]))
                    {
                        budget.CheckCancellation();
                        i++;
                    }
                }
                if (i < source.Length && source[i] is 'e' or 'E')
                {
                    i++;
                    if (i < source.Length && source[i] is '+' or '-')
                        i++;
                    while (i < source.Length && char.IsDigit(source[i]))
                    {
                        budget.CheckCancellation();
                        i++;
                    }
                }
                AddToken(new Token(TokenKind.Number, source[start..i], SpanAt(start)));
                continue;
            }
if (c == '"')
            {
                var start = i;
                var sb = new StringBuilder();
                i++;
                var closed = false;
                // Interpolation nesting: `\(` opens code mode where nested
                // strings, comments, and parentheses stay raw for the inner
                // parse; only the matching `)` returns to decoding.
                var depth = 0;
                while (i < source.Length)
                {
                    budget.CheckCancellation();
                    c = source[i++];
                    if (c == '"' && depth == 0) { closed = true; break; }
                    if (c == '"')
                    {
                        sb.Append(c);
                        while (i < source.Length)
                        {
                            budget.CheckCancellation();
                            char nested = source[i++];
                            sb.Append(nested);
                            if (nested == '\\' && i < source.Length) { sb.Append(source[i++]); continue; }
                            if (nested == '"') break;
                        }
                        continue;
                    }
                    if (c == '#' && depth > 0)
                    {
                        sb.Append(c);
                        while (i < source.Length && source[i] != '\n') { budget.CheckCancellation(); sb.Append(source[i++]); }
                        continue;
                    }
                    if (c == '(' && depth > 0) { depth++; sb.Append(c); continue; }
                    if (c == ')' && depth > 0) { depth--; sb.Append(c); continue; }
                    if (c == '\\' && i < source.Length)
                    {
                        if (depth > 0) { sb.Append(c); sb.Append(source[i++]); continue; }
                        if (source[i] == '(') { sb.Append("\\("); i++; depth++; continue; }
                        var e = source[i++];
                        // Keep escaped backslash pairs raw so the interpolation
                        // splitter can tell literal backslash-paren from a marker.
                        if (e == (char)92)
                        {
                            sb.Append((char)92);
                            sb.Append(e);
                        }
                        else
                        sb.Append(e switch
                        {
                            '"' => '"',
                            '/' => '/',
                            'b' => '\b',
                            'f' => '\f',
                            'n' => '\n',
                            'r' => '\r',
                            't' => '\t',
                            'u' when i + 4 <= source.Length && int.TryParse(source[(i)..(i += 4)], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var codePoint) => (char)codePoint,
                            'u' => throw new JqCompileException("invalid unicode escape", SpanAt(i - 2), programSource),
                            _ => throw new JqCompileException($"invalid escape '\\{e}'", SpanAt(i - 2), programSource),
                        });
                    }
                    else
                        sb.Append(c);
                }
                if (!closed)
                    throw new JqCompileException("unterminated string", SpanAt(start), programSource);
                AddToken(new Token(TokenKind.String, sb.ToString(), SpanAt(start)));
                continue;
            }

            var two = i + 1 < source.Length ? source.Substring(i, 2) : string.Empty;
            // The destructuring alternative is one token only when contiguous;
            // spaced `?` and `//` stay a postfix suppression plus alternative.
            if (i + 3 <= source.Length && source.Substring(i, 3) == "?//")
            {
                AddToken(new Token(TokenKind.Symbol, "?//", SpanAt(i)));
                i += 3;
                continue;
            }
            if (i + 3 <= source.Length && source.Substring(i, 3) == "//=")
            {
                AddToken(new Token(TokenKind.Symbol, "//=", SpanAt(i)));
                i += 3;
                continue;
            }
            if (two is "==" or "!=" or "<=" or ">=" or "//" or "|=" or "+=" or "-=" or "*=" or "/=" or "%=")
            {
                AddToken(new Token(TokenKind.Symbol, two, SpanAt(i)));
                i += 2;
                continue;
            }
            if (".,|+-*/%()[]{}:;$?<>@=".Contains(c, StringComparison.Ordinal))
            {
                AddToken(new Token(TokenKind.Symbol, c.ToString(), SpanAt(i)));
                i++;
                continue;
            }
            throw new JqCompileException($"invalid character {c}", SpanAt(i), programSource);
        }
        AddToken(new Token(TokenKind.End, "<end>", SpanAt(source.Length)));
        return tokens;
    }
}
