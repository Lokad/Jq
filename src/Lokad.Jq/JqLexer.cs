using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Lokad.Jq;

internal enum TokenKind { Identifier, FieldName, Number, String, Symbol, End }

internal readonly record struct Token(TokenKind Kind, string Text);

internal static class Lexer
{
    public static List<Token> Tokenize(string source, JqBudget budget)
    {
        if (source.Length > 1024 * 1024)
            throw new JqException("filter exceeds the 1 Mi-character limit");
        budget.ChargeBytes(2L * source.Length);
        var tokens = new List<Token>();
        for (var i = 0; i < source.Length;)
        {
            budget.CheckCancellation();
            if (tokens.Count >= 4096)
                throw new JqException("filter exceeds the 4096-token limit");
            var c = source[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (char.IsLetter(c) || c == '_')
            {
                var start = i++;
                while (i < source.Length && (char.IsLetterOrDigit(source[i]) || source[i] == '_'))
                    i++;
                // An adjacent dot token distinguishes .else from . else without mistaking
                // a numeric decimal point (1.then) for field access.
                var kind = start > 0 && source[start - 1] == '.'
                    && tokens[^1] is { Kind: TokenKind.Symbol, Text: "." }
                    ? TokenKind.FieldName
                    : TokenKind.Identifier;
                tokens.Add(new Token(kind, source[start..i]));
                continue;
            }
            if (char.IsDigit(c) || c == '-' && i + 1 < source.Length && char.IsDigit(source[i + 1]))
            {
                var start = i++;
                while (i < source.Length && (char.IsDigit(source[i]) || source[i] is '.' or 'e' or 'E' or '+' or '-'))
                    i++;
                tokens.Add(new Token(TokenKind.Number, source[start..i]));
                continue;
            }
            if (c == '"')
            {
                var sb = new StringBuilder();
                i++;
                while (i < source.Length)
                {
                    c = source[i++];
                    if (c == '"') break;
                    if (c == '\\' && i < source.Length)
                    {
                        var e = source[i++];
                        sb.Append(e switch
                        {
                            '"' => '"',
                            '\\' => '\\',
                            '/' => '/',
                            'b' => '\b',
                            'f' => '\f',
                            'n' => '\n',
                            'r' => '\r',
                            't' => '\t',
                            '(' => "\\(",
                            'u' when i + 4 <= source.Length && int.TryParse(source[(i)..(i += 4)], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var codePoint) => (char)codePoint,
                            'u' => throw new JqException("invalid unicode escape"),
                            _ => e
                        });
                    }
                    else
                        sb.Append(c);
                }
                tokens.Add(new Token(TokenKind.String, sb.ToString()));
                continue;
            }

            var two = i + 1 < source.Length ? source.Substring(i, 2) : string.Empty;
            if (two is "==" or "!=" or "<=" or ">=" or "//")
            {
                tokens.Add(new Token(TokenKind.Symbol, two));
                i += 2;
                continue;
            }
            if (".,|+-*/%()[]{}:;$?<>@".Contains(c, StringComparison.Ordinal))
            {
                tokens.Add(new Token(TokenKind.Symbol, c.ToString()));
                i++;
                continue;
            }
            throw new JqException($"invalid character {c}");
        }
        tokens.Add(new Token(TokenKind.End, "<end>"));
        return tokens;
    }
}
