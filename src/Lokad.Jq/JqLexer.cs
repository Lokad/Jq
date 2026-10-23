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
    internal static string DecodeLiterals(string raw, JqBudget budget)
    {
        budget.CheckCancellation();
        if (!raw.Contains("\\\\", StringComparison.Ordinal)) return raw;
        budget.ReserveString(raw.Length);
        string result = raw.Replace("\\\\", "\\", StringComparison.Ordinal);
        budget.CheckCancellation();
        return result;
    }

    // Comments share one continuation rule in ordinary code and interpolation.
    internal static int SkipComment(string source, int index, JqBudget budget)
    {
        while (index < source.Length)
        {
            budget.CheckCancellation();
            if (source[index] == '\\' && index + 1 < source.Length
                && (source[index + 1] == '\\' || source[index + 1] == '\n'
                    || (source[index + 1] == '\r' && index + 2 < source.Length && source[index + 2] == '\n')))
            {
                index += source[index + 1] == '\r' ? 3 : 2;
                continue;
            }
            if (source[index] == '\n') break;
            index++;
        }
        return index;
    }

    // Locate a fragment boundary without interpreting parentheses in comments or
    // strings. Nested string interpolations use the same code scanner.
    internal static int FindInterpolationEnd(string source, int start, JqSourceSpan span,
        JqProgramSource program, JqBudget budget)
    {
        return Scan(start, 0);

        int Scan(int index, int nesting)
        {
            if (nesting > JqRecursion.MaximumDepth)
                throw new JqCompileException("filter nesting limit exceeded", span, program);
            int parentheses = 1;
            while (index < source.Length)
            {
                budget.CheckCancellation();
                char ch = source[index];
                if (ch == '#') { index = SkipComment(source, index, budget); continue; }
                if (ch == '"') { index = SkipString(index + 1, nesting); continue; }
                if (ch == '(') parentheses++;
                else if (ch == ')' && --parentheses == 0) return index;
                index++;
            }
            return index;
        }

        int SkipString(int index, int nesting)
        {
            while (index < source.Length)
            {
                budget.CheckCancellation();
                char ch = source[index++];
                if (ch == '"') return index;
                if (ch != '\\' || index == source.Length) continue;
                if (source[index] == '(')
                {
                    index = Scan(index + 1, nesting + 1);
                    if (index == source.Length) return index;
                }
                index++;
            }
            return index;
        }

    }

    public static List<Token> Tokenize(string source, JqProgramSource programSource, JqBudget budget)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(programSource);
        ArgumentNullException.ThrowIfNull(budget);
        budget.CheckCancellation();
        budget.ReserveBytes(128);
        JqScopeStorage? literalStorage = null;
        try
        {
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
                if (c == '#')
                {
                    i = SkipComment(source, i, budget);
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
                    budget.ReserveString(i - start);
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
                    budget.ReserveString(i - start);
                    AddToken(new Token(TokenKind.Number, source[start..i], SpanAt(start)));
                    continue;
                }
                if (c == '"')
                {
                    var start = i;
                    JqScopeStorage workspace = literalStorage ??= new JqScopeStorage(budget);
                    long workspaceBytes = 64 + 2 * 16;
                    workspace.Reserve(workspaceBytes, 1);
                    budget.CheckCancellation();
                    var sb = new StringBuilder(16);
                    void Ensure(int count)
                    {
                        long required = (long)sb.Length + count;
                        if (required > int.MaxValue) throw new JqQuotaException("string result cannot be represented");
                        if (required <= sb.Capacity) return;
                        int capacity = (int)Math.Min(int.MaxValue, Math.Max(required, 2L * sb.Capacity));
                        long nextBytes = 64 + 2L * capacity;
                        workspace.Reserve(nextBytes, 1);
                        budget.CheckCancellation();
                        sb.EnsureCapacity(capacity);
                        workspace.Release(workspaceBytes);
                        workspaceBytes = nextBytes;
                    }
                    i++;
                    var closed = false;
                    while (i < source.Length)
                    {
                        budget.CheckCancellation();
                        c = source[i++];
                        if (c == '"') { closed = true; break; }
                        if (c == '\\' && i < source.Length)
                        {
                            if (source[i] == '(')
                            {
                                int end = FindInterpolationEnd(source, i + 1, SpanAt(start), programSource, budget);
                                if (end == source.Length) break;
                                Ensure(end - i + 2);
                                sb.Append(source, i - 1, end - i + 2);
                                i = end + 1;
                                continue;
                            }
                            var e = source[i++];
                            // Keep escaped backslash pairs raw so the interpolation
                            // splitter can tell literal backslash-paren from a marker.
                            if (e == (char)92)
                            {
                                Ensure(2);
                                sb.Append((char)92);
                                sb.Append(e);
                            }
                            else
                            {
                                Ensure(1);
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
                        }
                        else
                        {
                            Ensure(1);
                            sb.Append(c);
                        }
                    }
                    if (!closed)
                        throw new JqCompileException("unterminated string", SpanAt(start), programSource);
                    string text = budget.Finish(sb);
                    workspace.Release(workspaceBytes);
                    AddToken(new Token(TokenKind.String, text, SpanAt(start)));
                    continue;
                }

                ReadOnlySpan<char> remaining = source.AsSpan(i);
                if (remaining.StartsWith("?//", StringComparison.Ordinal))
                {
                    AddToken(new Token(TokenKind.Symbol, "?//", SpanAt(i)));
                    i += 3;
                    continue;
                }
                if (remaining.StartsWith("//=", StringComparison.Ordinal))
                {
                    AddToken(new Token(TokenKind.Symbol, "//=", SpanAt(i)));
                    i += 3;
                    continue;
                }
                string? pair = i + 1 == source.Length ? null : (c, source[i + 1]) switch
                {
                    ('=', '=') => "==", ('!', '=') => "!=", ('<', '=') => "<=", ('>', '=') => ">=",
                    ('/', '/') => "//", ('|', '=') => "|=", ('+', '=') => "+=", ('-', '=') => "-=",
                    ('*', '=') => "*=", ('/', '=') => "/=", ('%', '=') => "%=", _ => null,
                };
                if (pair is not null)
                {
                    AddToken(new Token(TokenKind.Symbol, pair, SpanAt(i)));
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
        finally { literalStorage?.Dispose(); }
    }
}
