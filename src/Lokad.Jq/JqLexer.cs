using System;
using System.Collections.Generic;
using System.Text;

namespace Lokad.Jq;

internal enum TokenKind { Identifier, FieldName, Number, String, Symbol, End }

// Text contains syntax identifiers/operators. Quoted data stays in native bytes;
// interpolation code borrows the original host-supplied program source.
internal readonly record struct Token
{
    private readonly object _content;
    internal TokenKind Kind { get; }
    internal JqSourceSpan Span { get; }
    internal string Text => _content as string ?? string.Empty;
    internal JqString? StringValue => _content switch
    {
        JqString literal => literal,
        Template template => template.Literal,
        _ => null
    };
    internal IReadOnlyList<JqStringInterpolation>? Interpolations => (_content as Template)?.Interpolations;

    internal Token(TokenKind kind, string text, JqSourceSpan span)
    {
        Kind = kind;
        Span = span;
        _content = text;
    }

    internal Token(JqString literal, IReadOnlyList<JqStringInterpolation>? interpolations, JqSourceSpan span)
    {
        Kind = TokenKind.String;
        Span = span;
        _content = interpolations is null ? literal : new Template(literal, interpolations);
    }

    private sealed record Template(JqString Literal, IReadOnlyList<JqStringInterpolation> Interpolations);
}

internal readonly record struct JqStringInterpolation(int ByteOffset, int EndOffset, ReadOnlyMemory<char> Source);

internal static class Lexer
{
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
            if (c is ' ' or '\t' or '\r' or '\n') { i++; continue; }
            if (c == '#')
            {
                i = SkipComment(source, i, budget);
                continue;
            }
            if (char.IsAsciiLetter(c) || c == '_')
            {
                var start = i++;
                while (i < source.Length && (char.IsAsciiLetterOrDigit(source[i]) || source[i] == '_'))
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
                        && (char.IsAsciiLetter(source[i + 2]) || source[i + 2] == '_'))
                    {
                        i += 2;
                        while (i < source.Length && (char.IsAsciiLetterOrDigit(source[i]) || source[i] == '_'))
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
            if (char.IsAsciiDigit(c) || (c == '.' && i + 1 < source.Length && char.IsAsciiDigit(source[i + 1])))
            {
                var start = i;
                while (i < source.Length && char.IsAsciiDigit(source[i]))
                {
                    budget.CheckCancellation();
                    i++;
                }
                if (i < source.Length && source[i] == '.')
                {
                    i++;
                    while (i < source.Length && char.IsAsciiDigit(source[i]))
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
                    while (i < source.Length && char.IsAsciiDigit(source[i]))
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
                int start = i++;
                int bodyStart = i;
                int byteLength = 0;
                List<JqStringInterpolation>? interpolations = null;
                while (i < source.Length && source[i] != '"')
                {
                    budget.CheckCancellation();
                    if (source[i] == '\\' && i + 1 < source.Length && source[i + 1] == '(')
                    {
                        int end = FindInterpolationEnd(source, i + 2, SpanAt(start), programSource, budget);
                        if (end == source.Length) break;
                        if (interpolations is null)
                        {
                            budget.ReserveMetadata(32, 1);
                            interpolations = [];
                        }
                        if (interpolations.Count == interpolations.Capacity)
                        {
                            int capacity = JqBudget.BufferCapacity((long)interpolations.Count + 1,
                                Math.Max(4, 2L * interpolations.Capacity));
                            budget.ReserveMetadata(32L * capacity, 1);
                            budget.CheckCancellation();
                            interpolations.EnsureCapacity(capacity);
                        }
                        interpolations.Add(new(byteLength, end, source.AsMemory(i + 2, end - i - 2)));
                        i = end + 1;
                        continue;
                    }
                    Rune rune = ReadScalar(ref i);
                    byteLength = JqBudget.BufferCapacity((long)byteLength + rune.Utf8SequenceLength, 0);
                }
                if (i == source.Length || source[i] != '"')
                    throw new JqCompileException("unterminated string", SpanAt(start), programSource);
                int bodyEnd = i++;
                JqString literal = JqString.Create(byteLength, bodyStart, (output, cursor) =>
                {
                    int written = 0, interpolation = 0;
                    while (cursor < bodyEnd)
                    {
                        budget.CheckCancellation();
                        if (source[cursor] == '\\' && cursor + 1 < bodyEnd && source[cursor + 1] == '(')
                        {
                            if (interpolations is null) throw new InvalidOperationException("Missing interpolation source.");
                            cursor = interpolations[interpolation++].EndOffset + 1;
                            continue;
                        }
                        written += ReadScalar(ref cursor).EncodeToUtf8(output[written..]);
                    }
                }, budget);
                AddToken(new Token(literal, interpolations, SpanAt(start)));
                continue;

                Rune ReadScalar(ref int cursor)
                {
                    char first = source[cursor++];
                    if (first != '\\')
                    {
                        Rune.DecodeFromUtf16(source.AsSpan(cursor - 1), out Rune rune, out int consumed);
                        cursor += consumed - 1;
                        return rune;
                    }
                    int escape = cursor - 1;
                    if (cursor == source.Length)
                        throw new JqCompileException("unterminated string", SpanAt(start), programSource);
                    char escaped = source[cursor++];
                    char decoded;
                    if (escaped == 'u')
                    {
                        decoded = ReadHex(ref cursor);
                        if (char.IsHighSurrogate(decoded))
                        {
                            if (cursor + 2 > source.Length || source[cursor] != '\\' || source[cursor + 1] != 'u')
                                throw new JqCompileException("invalid unicode surrogate pair", SpanAt(escape), programSource);
                            cursor += 2;
                            char low = ReadHex(ref cursor);
                            if (!char.IsLowSurrogate(low))
                                throw new JqCompileException("invalid unicode surrogate pair", SpanAt(escape), programSource);
                            return new Rune(decoded, low);
                        }
                        if (char.IsLowSurrogate(decoded)) return Rune.ReplacementChar;
                    }
                    else decoded = escaped switch
                    {
                        '"' => '"', '/' => '/', '\\' => '\\',
                        'b' => '\b', 'f' => '\f', 'n' => '\n', 'r' => '\r', 't' => '\t',
                        _ => throw new JqCompileException($"invalid escape '\\{escaped}'", SpanAt(escape), programSource),
                    };
                    return new Rune(decoded);

                    char ReadHex(ref int offset)
                    {
                        if (source.Length - offset < 4)
                            throw new JqCompileException("invalid unicode escape", SpanAt(escape), programSource);
                        int value = 0;
                        for (int digit = 0; digit < 4; digit++)
                        {
                            char hex = source[offset++];
                            int part = hex is >= '0' and <= '9' ? hex - '0'
                                : hex is >= 'A' and <= 'F' ? hex - 'A' + 10
                                : hex is >= 'a' and <= 'f' ? hex - 'a' + 10 : -1;
                            if (part < 0)
                                throw new JqCompileException("invalid unicode escape", SpanAt(escape), programSource);
                            value = (value << 4) | part;
                        }
                        return (char)value;
                    }
                }
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
}
