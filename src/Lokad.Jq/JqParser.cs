using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Nodes;

namespace Lokad.Jq;

internal sealed class JqParser(string source, JqBudget budget)
{
    private readonly List<Token> _tokens = Lexer.Tokenize(source, budget);
    private int _index;
    private int _depth;

    public JqFilter Parse()
    {
        var filter = ParseComma();
        Expect(TokenKind.End);
        return filter;
    }

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
        while (Match("//"))
            left = new BinaryFilter(left, "//", ParseOr());
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
        while (Peek().Text is "==" or "!=" or "<" or "<=" or ">" or ">=")
        {
            var op = Next().Text;
            left = new BinaryFilter(left, op, ParseAdditive());
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
            throw new JqException("filter nesting limit exceeded");
        try
        {
            if (Match("-"))
                return new UnaryFilter("-", ParseUnary());
            if (MatchIdentifier("not"))
                return new UnaryFilter("not", ParseUnary());
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
            index = ParsePipe();
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
            end = ParsePipe();
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
            var identity = new IdentityFilter();
            if (Peek().Kind == TokenKind.FieldName)
                return new FieldFilter(identity, Next().Text, Match("?"));
            if (Match("["))
                return ParseBracket(identity);
            return identity;
        }
        if (Match("$"))
            return new VariableFilter(Expect(TokenKind.Identifier).Text);
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
            if (!double.TryParse(token.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                throw new JqException($"invalid number {token.Text}");
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
                return ParseFunction(token.Text);
            return CreateFunction(token.Text, []);
        }
        throw new JqException($"unexpected token {token.Text}");
    }

    private JqFilter ParseObject()
    {
        var properties = new List<(string, JqFilter)>();
        if (Match("}"))
            return new LiteralFilter(new JsonObject());
        do
        {
            var keyToken = Next();
            string key;
            JqFilter value;
            if (keyToken.Kind == TokenKind.String || keyToken.Kind == TokenKind.Identifier)
                key = keyToken.Text;
            else
                throw new JqException("expected object key");

            if (Match(":"))
                value = ParsePipe();
            else
                value = new FieldFilter(new IdentityFilter(), key, true);
            properties.Add((key, value));
        } while (Match(","));
        Expect("}");
        return new ObjectFilter(properties);
    }

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

    private JqFilter ParseFunction(string name)
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
        return CreateFunction(name, args);
    }

    private static JqFilter CreateFunction(string name, IReadOnlyList<JqFilter> args) => name switch
    {
        "gsub" => new GsubFilter(args),
        "test" => new TestFilter(args),
        _ => new FunctionFilter(name, args)
    };

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
            throw new JqException($"expected {kind}, got {token.Text}");
        return token;
    }

    private void Expect(string text)
    {
        if (!Match(text))
            throw new JqException($"expected {text}, got {Peek().Text}");
    }

    private void ExpectIdentifier(string text)
    {
        if (!MatchIdentifier(text))
            throw new JqException($"expected {text}, got {Peek().Text}");
    }

    private Token Peek() => _tokens[_index];

    private Token Next() => _tokens[_index++];
}
