using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Nodes;

namespace Lokad.Jq;

internal sealed class JqParser(
    string source,
    JqProgramSource programSource,
    IReadOnlyDictionary<string, JsonNode?> variables,
    JqBudget budget)
{
    private readonly JqProgramSource _programSource = programSource ?? throw new ArgumentNullException(nameof(programSource));
    private readonly IReadOnlyDictionary<string, JsonNode?> _variables = variables ?? throw new ArgumentNullException(nameof(variables));
    private readonly List<Token> _tokens = Lexer.Tokenize(source, programSource, budget);
    private int _index;
    private int _depth;

    public JqFilter Parse()
    {
        var filter = ParseComma();
        Expect(TokenKind.End);
        return filter;
    }

    private JqCompileException Error(string message, JqSourceSpan span) =>
        new(message, span, _programSource);

    private JqFilter ParseComma()
    {
        var left = ParsePipe();
        while (Match(","))
            left = new CommaFilter(left, ParsePipe());
        return left;
    }

    private JqFilter ParsePipe()
    {
        var left = ParseAlternative();
        while (Match("|"))
            left = new PipeFilter(left, ParseAlternative());
        return left;
    }

    private JqFilter ParseAlternative()
    {
        var left = ParseOr();
        // Right-associative alternatives: `a // b // c` groups as `a // (b // c)`.
        if (Match("//"))
            return new AlternativeFilter(left, ParseAlternative());
        return left;
    }

    private JqFilter ParseOr()
    {
        var left = ParseAnd();
        while (MatchIdentifier("or"))
            left = new BinaryFilter(left, "or", ParseAnd());
        return left;
    }

    private JqFilter ParseAnd()
    {
        var left = ParseComparison();
        while (MatchIdentifier("and"))
            left = new BinaryFilter(left, "and", ParseComparison());
        return left;
    }

    private JqFilter ParseComparison()
    {
        var left = ParseAdditive();
        // Comparisons do not chain: a second comparison operator is an error.
        if (Peek().Text is "==" or "!=" or "<" or "<=" or ">" or ">=")
        {
            var op = Next().Text;
            left = new BinaryFilter(left, op, ParseAdditive());
            if (Peek().Text is "==" or "!=" or "<" or "<=" or ">" or ">=")
                throw Error($"unexpected token {Peek().Text}", Peek().Span);
        }
        return left;
    }

    private JqFilter ParseAdditive()
    {
        var left = ParseMultiplicative();
        while (Peek().Text is "+" or "-")
        {
            var op = Next().Text;
            left = new BinaryFilter(left, op, ParseMultiplicative());
        }
        return left;
    }

    private JqFilter ParseMultiplicative()
    {
        var left = ParseUnary();
        while (Peek().Text is "*" or "/" or "%")
        {
            var op = Next().Text;
            left = new BinaryFilter(left, op, ParseUnary());
        }
        return left;
    }

    private JqFilter ParseUnary()
    {
        if (++_depth > JqBudget.MaximumDepth)
            throw Error("filter nesting limit exceeded", Peek().Span);
        try
        {
            if (Match("-"))
                return new UnaryFilter("-", ParseUnary());
            return ParsePostfix();
        }
        finally
        {
            _depth--;
        }
    }

    private JqFilter ParsePostfix()
    {
        var filter = ParsePrimary();
        while (true)
        {
            if (Match("."))
            {
                if (Peek().Kind == TokenKind.FieldName)
                {
                    var name = Next().Text;
                    var optional = Match("?");
                    filter = new FieldFilter(filter, name, optional);
                }
                else if (Peek().Kind == TokenKind.String)
                {
                    var name = Next().Text;
                    var optional = Match("?");
                    filter = new FieldFilter(filter, name, optional);
                }
                else if (Match("["))
                {
                    filter = ParseBracket(filter);
                }
                else
                {
                    _index--;
                    break;
                }
            }
            else if (Match("["))
            {
                filter = ParseBracket(filter);
            }
            else if (Match("?"))
            {
                filter = new OptionalFilter(filter);
            }
            else
                break;
        }
        return filter;
    }

    private JqFilter ParseBracket(JqFilter source)
    {
        if (Match("]"))
        {
            var optional = Match("?");
            return new IteratorFilter(source, optional);
        }
        JqFilter? index = null;
        if (!Match(":"))
        {
            index = ParseComma();
            if (!Match(":"))
            {
                Expect("]");
                var opt = Match("?");
                return new IndexFilter(source, index, opt);
            }
        }

        JqFilter? end = null;
        if (!Match("]"))
        {
            end = ParseComma();
            Expect("]");
        }
        var sliceOptional = Match("?");
        return new SliceFilter(source, index, end, sliceOptional);
    }

    private JqFilter ParsePrimary()
    {
        if (Match("@"))
        {
            var format = Expect(TokenKind.Identifier).Text;
            return Peek().Kind == TokenKind.String
                ? new InterpolatedStringFilter(Next().Text, format)
                : new FormatFilter(format);
        }
        if (Match("."))
        {
            if (Match("."))
                return new RecursiveDescentFilter();
            var identity = new IdentityFilter();
            if (Peek().Kind == TokenKind.FieldName)
                return new FieldFilter(identity, Next().Text, Match("?"));
            if (Peek().Kind == TokenKind.String)
                return new FieldFilter(identity, Next().Text, Match("?"));
            if (Match("["))
                return ParseBracket(identity);
            return identity;
        }
        if (Peek() is { Kind: TokenKind.Symbol, Text: "$" })
        {
            var dollar = Next();
            var name = Expect(TokenKind.Identifier);
            if (!_variables.ContainsKey(name.Text))
                throw Error($"undefined variable ${name.Text}", dollar.Span);
            return new VariableFilter(name.Text);
        }
        if (Match("("))
        {
            var inner = ParseComma();
            Expect(")");
            return inner;
        }
        if (Match("["))
        {
            if (Match("]"))
                return new LiteralFilter(new JsonArray());
            var item = ParseComma();
            Expect("]");
            return new ArrayFilter(item);
        }
        if (Match("{"))
            return ParseObject();
        if (MatchIdentifier("if"))
            return ParseIf();

        var token = Next();
        if (token.Kind == TokenKind.Number)
        {
            // Integer literals keep integral storage (exact through identity
            // and conversion); decimals and exponents use doubles. A signed
            // zero stays a double so it renders with its sign.
            if (long.TryParse(token.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long whole)
                && (whole != 0 || !token.Text.StartsWith("-", StringComparison.Ordinal)))
                return new LiteralFilter(JsonValue.Create(whole));
            if (!double.TryParse(token.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                throw Error($"invalid number {token.Text}", token.Span);
            return new LiteralFilter(JsonValue.Create(value));
        }
        if (token.Kind == TokenKind.String)
            return token.Text.Contains("\\(", StringComparison.Ordinal)
                ? new InterpolatedStringFilter(token.Text, null)
                : new LiteralFilter(JsonValue.Create(token.Text));
        if (token.Kind == TokenKind.Identifier)
        {
            if (token.Text == "null") return new LiteralFilter(null);
            if (token.Text == "true") return new LiteralFilter(JsonValue.Create(true));
            if (token.Text == "false") return new LiteralFilter(JsonValue.Create(false));
            if (Match("("))
                return ParseFunction(token.Text, token.Span);
            return CreateFunction(token.Text, [], token.Span);
        }
        throw Error($"unexpected token {token.Text}", token.Span);
    }

    private JqFilter ParseObject()
    {
        var properties = new List<ObjectProperty>();
        if (Match("}"))
            return new LiteralFilter(new JsonObject());
        do
        {
            if (Match("("))
            {
                var key = ParseComma();
                Expect(")");
                Expect(":");
                properties.Add(new ObjectProperty(null, key, ParsePipe()));
            }
            else
            {
                var keyToken = Next();
                if (keyToken.Kind != TokenKind.String && keyToken.Kind != TokenKind.Identifier)
                    throw Error("expected object key", keyToken.Span);
                if (Match(":"))
                {
                    // Interpolated string keys evaluate per combination; plain
                    // keys stay static.
                    bool isDynamic = KeyFilterForIsDynamic(keyToken);
                    properties.Add(isDynamic
                        ? new ObjectProperty(null, KeyFilterFor(keyToken), ParsePipe())
                        : new ObjectProperty(keyToken.Text, null, ParsePipe()));
                }
                else if (keyToken.Kind == TokenKind.String)
                {
                    // Bare plain strings keep field access; interpolated keys
                    // read through the evaluated key instead.
                    bool interpolated = KeyFilterForIsDynamic(keyToken);
                    properties.Add(interpolated
                        ? new ObjectProperty(null, KeyFilterFor(keyToken), new IndexFilter(new IdentityFilter(), KeyFilterFor(keyToken), false))
                        : new ObjectProperty(keyToken.Text, null, new FieldFilter(new IdentityFilter(), keyToken.Text, true)));
                }
                else
                {
                    properties.Add(new ObjectProperty(keyToken.Text, null, new FieldFilter(new IdentityFilter(), keyToken.Text, true)));
                }
            }
        } while (Match(","));
        Expect("}");
        return new ObjectFilter(properties);
    }

    private static JqFilter KeyFilterFor(Token token) => token.Text.Contains("\\(", StringComparison.Ordinal)
        ? new InterpolatedStringFilter(token.Text, null)
        : new LiteralFilter(JsonValue.Create(token.Text));

    private static bool KeyFilterForIsDynamic(Token token) => token.Kind == TokenKind.String && token.Text.Contains("\\(", StringComparison.Ordinal);

    private JqFilter ParseIf()
    {
        var branches = new List<(JqFilter, JqFilter)>();
        var condition = ParseComma();
        ExpectIdentifier("then");
        branches.Add((condition, ParseComma()));
        while (MatchIdentifier("elif"))
        {
            var elif = ParseComma();
            ExpectIdentifier("then");
            branches.Add((elif, ParseComma()));
        }
        ExpectIdentifier("else");
        var otherwise = ParseComma();
        ExpectIdentifier("end");
        return new IfFilter(branches, otherwise);
    }

    private JqFilter ParseFunction(string name, JqSourceSpan span)
    {
        var args = new List<JqFilter>();
        if (!Match(")"))
        {
            do
            {
                args.Add(ParseComma());
            } while (Match(";") || Match(","));
            Expect(")");
        }
        return CreateFunction(name, args, span);
    }

    private JqFilter CreateFunction(string name, IReadOnlyList<JqFilter> args, JqSourceSpan span)
    {
        if (!JqBuiltinRegistry.TryValidate(name, args.Count, out string error))
        {
            throw Error(error, span);
        }

        if (name == "gsub")
        {
            return new GsubFilter(args);
        }

        if (name == "test")
        {
            return new TestFilter(args);
        }

        return new FunctionFilter(name, args);
    }

    private bool Match(string text)
    {
        if (Peek().Kind != TokenKind.Symbol || Peek().Text != text)
            return false;
        _index++;
        return true;
    }

    private bool MatchIdentifier(string text)
    {
        if (Peek().Kind != TokenKind.Identifier || Peek().Text != text)
            return false;
        _index++;
        return true;
    }

    private Token Expect(TokenKind kind)
    {
        var token = Next();
        if (token.Kind != kind)
            throw Error($"expected {kind}, got {token.Text}", token.Span);
        return token;
    }

    private void Expect(string text)
    {
        if (!Match(text))
            throw Error($"expected {text}, got {Peek().Text}", Peek().Span);
    }

    private void ExpectIdentifier(string text)
    {
        if (!MatchIdentifier(text))
            throw Error($"expected {text}, got {Peek().Text}", Peek().Span);
    }

    private Token Peek() => _tokens[_index];

    private Token Next() => _tokens[_index++];
}
