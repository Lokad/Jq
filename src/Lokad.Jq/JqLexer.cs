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
        if (source.Length > 1024 * 1024)
            throw new JqCompileException("filter exceeds the 1 Mi-character limit", JqSourceSpan.FromOffset(source, 0), programSource);
        budget.ChargeBytes(2L * source.Length);
        var tokens = new List<Token>();
        for (var i = 0; i < source.Length;)
        {
            budget.CheckCancellation();
            if (tokens.Count >= 4096)
                throw new JqCompileException("filter exceeds the 4096-token limit", JqSourceSpan.FromOffset(source, i), programSource);
            var c = source[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            // Comments run to the end of the line (or input), matching the
            // reference lexer; the newline itself stays whitespace.
            if (c == '#') { while (i < source.Length && source[i] != '\n') i++; continue; }
            if (char.IsLetter(c) || c == '_')
            {
                var start = i++;
                while (i < source.Length && (char.IsLetterOrDigit(source[i]) || source[i] == '_'))
                    i++;
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
                            i++;
                    }
                }
                var kind = isField ? TokenKind.FieldName : TokenKind.Identifier;
                tokens.Add(new Token(kind, source[start..i], JqSourceSpan.FromOffset(source, start)));
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
                    i++;
                if (i < source.Length && source[i] == '.')
                {
                    i++;
                    while (i < source.Length && char.IsDigit(source[i]))
                        i++;
                }
                if (i < source.Length && source[i] is 'e' or 'E')
                {
                    i++;
                    if (i < source.Length && source[i] is '+' or '-')
                        i++;
                    while (i < source.Length && char.IsDigit(source[i]))
                        i++;
                }
                tokens.Add(new Token(TokenKind.Number, source[start..i], JqSourceSpan.FromOffset(source, start)));
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
                    c = source[i++];
                    if (c == '"' && depth == 0) { closed = true; break; }
                    if (c == '"')
                    {
                        sb.Append(c);
                        while (i < source.Length)
                        {
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
                        while (i < source.Length && source[i] != '\n') { sb.Append(source[i++]); }
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
                            'u' => throw new JqCompileException("invalid unicode escape", JqSourceSpan.FromOffset(source, i - 2), programSource),
                            _ => throw new JqCompileException($"invalid escape '\\{e}'", JqSourceSpan.FromOffset(source, i - 2), programSource),
                        });
                    }
                    else
                        sb.Append(c);
                }
                if (!closed)
                    throw new JqCompileException("unterminated string", JqSourceSpan.FromOffset(source, start), programSource);
                tokens.Add(new Token(TokenKind.String, sb.ToString(), JqSourceSpan.FromOffset(source, start)));
                continue;
            }

            var two = i + 1 < source.Length ? source.Substring(i, 2) : string.Empty;
            // The destructuring alternative is one token only when contiguous;
            // spaced `?` and `//` stay a postfix suppression plus alternative.
            if (i + 3 <= source.Length && source.Substring(i, 3) == "?//")
            {
                tokens.Add(new Token(TokenKind.Symbol, "?//", JqSourceSpan.FromOffset(source, i)));
                i += 3;
                continue;
            }
            if (i + 3 <= source.Length && source.Substring(i, 3) == "//=")
            {
                tokens.Add(new Token(TokenKind.Symbol, "//=", JqSourceSpan.FromOffset(source, i)));
                i += 3;
                continue;
            }
            if (two is "==" or "!=" or "<=" or ">=" or "//" or "|=" or "+=" or "-=" or "*=" or "/=" or "%=")
            {
                tokens.Add(new Token(TokenKind.Symbol, two, JqSourceSpan.FromOffset(source, i)));
                i += 2;
                continue;
            }
            if (".,|+-*/%()[]{}:;$?<>@=".Contains(c, StringComparison.Ordinal))
            {
                tokens.Add(new Token(TokenKind.Symbol, c.ToString(), JqSourceSpan.FromOffset(source, i)));
                i++;
                continue;
            }
            throw new JqCompileException($"invalid character {c}", JqSourceSpan.FromOffset(source, i), programSource);
        }
        tokens.Add(new Token(TokenKind.End, "<end>", JqSourceSpan.FromOffset(source, source.Length)));
        return tokens;
    }
}
