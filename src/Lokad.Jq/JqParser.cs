using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Nodes;

namespace Lokad.Jq;

internal sealed class JqParser(
    string source,
    JqProgramSource programSource,
    JqEnvironment environment,
    JqBudget budget)
{
    private readonly JqProgramSource _programSource = programSource ?? throw new ArgumentNullException(nameof(programSource));
    private readonly JqEnvironment _environment = environment ?? throw new ArgumentNullException(nameof(environment));
    private readonly Stack<ParserScope> _scopes = new();
    private readonly JqBudget _budget = budget ?? throw new ArgumentNullException(nameof(budget));
    private readonly List<Token> _tokens = Lexer.Tokenize(source, programSource, budget);
    private int _index;
    private int _depth;

    public JqFilter Parse()
    {
        SeedFunctionsFromEnvironment();
        var filter = ParseQuery();
        Expect(TokenKind.End);
        return filter;
    }

    private JqCompileException Error(string message, JqSourceSpan span) =>
        new(message, span, _programSource);

    private sealed class ParserScope
    {
        internal readonly HashSet<string> Variables = new(StringComparer.Ordinal);
        internal readonly HashSet<string> FilterParams = new(StringComparer.Ordinal);
        internal readonly HashSet<string> Labels = new(StringComparer.Ordinal);
        internal readonly Dictionary<(string Name, int Arity), JqFunctionDefinition> Functions = new();
    }

    private bool IsBound(string name)
    {
        foreach (var scope in _scopes)
            if (scope.Variables.Contains(name))
                return true;
        return _environment.TryGetValue(name, out _);
    }

    private void EnterScope() => _scopes.Push(new ParserScope());

    private void Declare(string name)
    {
        if (_scopes.Count == 0)
            throw new InvalidOperationException("No open binding scope.");
        _scopes.Peek().Variables.Add(name);
    }

    private void DeclareFilterParam(string name)
    {
        if (_scopes.Count == 0)
            throw new InvalidOperationException("No open binding scope.");
        _scopes.Peek().FilterParams.Add(name);
    }

    private void DeclareFunction(JqFunctionDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (_scopes.Count == 0)
            _scopes.Push(new ParserScope());
        _scopes.Peek().Functions[(definition.Name, definition.Arity)] = definition;
    }

    // Resolves a bare `name` use: a zero-argument user function wins over a
    // filter parameter in the same scope, matching definition-site lookup.
    private bool TryLookupZeroArg(string name, out bool isFilterParam)
    {
        foreach (var scope in _scopes)
        {
            if (scope.Functions.ContainsKey((name, 0)))
            {
                isFilterParam = false;
                return true;
            }
            if (scope.FilterParams.Contains(name))
            {
                isFilterParam = true;
                return true;
            }
        }
        isFilterParam = false;
        return false;
    }

    private bool TryLookupFunction(string name, int arity)
    {
        foreach (var scope in _scopes)
            if (scope.Functions.ContainsKey((name, arity)))
                return true;
        return false;
    }

    // Reparsed fragments (string interpolation re-enters the parser with the
    // evaluation environment) reseed visible user definitions so `"\(f)"`
    // keeps working inside function bodies. Nearest bindings win.
    private void SeedFunctionsFromEnvironment()
    {
        var collected = new Dictionary<(string Name, int Arity), JqFunctionDefinition>();
        _environment.CollectFunctions(collected);
        if (collected.Count == 0)
            return;
        var scope = new ParserScope();
        foreach (var entry in collected)
            scope.Functions[entry.Key] = entry.Value;
        _scopes.Push(scope);
    }

    private void ExitScope()
    {
        if (_scopes.Count == 0)
            throw new InvalidOperationException("No open binding scope.");
        _scopes.Pop();
    }

    // Definitions bind loosest: `def f: ...; rest` scopes the definition over
    // the whole following query, mirroring gojq `query: funcdef query`. This
    // layer fronts every full-expression position (top level, parentheses,
    // brackets, pipe right-hand sides starting with `def`, binding bodies,
    // conditions, branches, and call arguments).
    private JqFilter ParseQuery()
    {
        if (!PeekIsDef())
            return ParseComma();
        JqFunctionDefinition definition = ParseFuncDefHead();
        DeclareFunction(definition);
        EnterScope();
        JqFilter body;
        try
        {
            foreach (JqFunctionParameter parameter in definition.Parameters)
            {
                if (parameter.IsValue)
                    Declare(parameter.Name);
                else
                    DeclareFilterParam(parameter.Name);
            }
            body = ParseQuery();
        }
        finally
        {
            ExitScope();
        }
        Expect(";");
        var complete = AttachBody(definition, RewriteTailCalls(body, definition));
        DeclareFunction(complete);
        return new DefFilter(complete, ParseQuery());
    }

    private static JqFunctionDefinition AttachBody(JqFunctionDefinition definition, JqFilter body)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(body);
        return new JqFunctionDefinition(definition.Name, definition.Parameters, body);
    }

    private JqFunctionDefinition ParseFuncDefHead()
    {
        if (++_depth > JqBudget.MaximumDepth)
            throw Error("filter nesting limit exceeded", Peek().Span);
        try
        {
            ExpectIdentifier("def");
            Token name = Expect(TokenKind.Identifier);
            var parameters = new List<JqFunctionParameter>();
            if (Match("("))
            {
                if (!Match(")"))
                {
                    do
                    {
                        if (Match("$"))
                        {
                            Token variable = Expect(TokenKind.Identifier);
                            parameters.Add(new JqFunctionParameter(variable.Text, true));
                        }
                        else
                        {
                            Token filter = Expect(TokenKind.Identifier);
                            parameters.Add(new JqFunctionParameter(filter.Text, false));
                        }
                    } while (Match(";"));
                    Expect(")");
                }
            }
            Expect(":");
            return new JqFunctionDefinition(name.Text, parameters, new IdentityFilter());
        }
        finally
        {
            _depth--;
        }
    }

    private bool PeekIsDef() => Peek() is { Kind: TokenKind.Identifier, Text: "def" };

    // Marks direct self-calls in tail positions so long recursive runs loop
    // on the heap instead of nesting evaluator frames. Sound positions hold
    // no pending output work when the call fires: the body root, `if` branch
    // bodies (conditions yield no outputs), comma right-hand sides (the left
    // side is fully drained), `//` right-hand sides (same), postfix `?`, and
    // nested definition continuations. Pipe right-hand sides become dedicated
    // per-value loops so sources are never abandoned. Sources, arguments,
    // collected positions, and binding bodies stay regular depth-bounded
    // calls; a nested rebinding stops the descent.
    private static JqFilter RewriteTailCalls(JqFilter node, JqFunctionDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(definition);
        if (node is UserCallFilter call
            && call.FunctionName == definition.Name
            && call.FunctionArity == definition.Arity)
            return new TailSelfCallFilter(definition, call.CallArgs);
        if (node is PipeFilter pipe)
        {
            JqFilter right = RewriteTailCalls(pipe.Right, definition);
            if (right is TailSelfCallFilter tail)
                return new PipeTailLoop(pipe.Left, tail.TailDefinition, tail.TailArgs);
            return pipe.WithRight(right);
        }
        if (node is IfFilter iff)
            return iff.WithTails(branch => RewriteTailCalls(branch, definition));
        if (node is TryFilter attempted)
            return attempted.WithOperands(operand => RewriteTailCalls(operand, definition));
        if (node is LabelFilter labeled)
            return labeled.WithBody(RewriteTailCalls(labeled.LabelBody, definition));
        if (node is CommaFilter comma)
            return comma.WithRight(RewriteTailCalls(comma.Right, definition));
        if (node is AlternativeFilter alternative)
            return alternative.WithRight(RewriteTailCalls(alternative.Right, definition));
        if (node is OptionalFilter optional)
            return optional.WithInner(RewriteTailCalls(optional.Inner, definition));
        if (node is DefFilter nested)
        {
            if (nested.FunctionDefinition.Name == definition.Name
                && nested.FunctionDefinition.Arity == definition.Arity)
                return nested;
            return nested.WithContinuation(RewriteTailCalls(nested.ContinuationBody, definition));
        }
        return node;
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
        var left = ParseAs();
        while (Match("|"))
        {
            // A right-hand side starting with `def` takes the whole rest of
            // the query as its body, matching `query: funcdef query`.
            JqFilter right = PeekIsDef() ? ParseQuery() : ParseAs();
            left = new PipeFilter(left, right);
        }
        return left;
    }

    // Bindings sit between pipes and alternatives: `a | b as $x | c` groups
    // as `a | (b as $x | c)`, while the bound source stays pipe-free.
    private JqFilter ParseAs()
    {
        var left = ParseAlternative();
        if (!MatchIdentifier("as"))
            return left;
        var alternatives = ParsePatterns();
        Expect("|");
        EnterScope();
        try
        {
            DeclarePatterns(alternatives);
            return new AsFilter(left, alternatives, ParseQuery());
        }
        finally
        {
            ExitScope();
        }
    }

    private List<BindingPattern> ParsePatterns()
    {
        var alternatives = new List<BindingPattern> { ParsePattern() };
        while (Match("?//"))
            alternatives.Add(ParsePattern());
        return alternatives;
    }

    private BindingPattern ParsePattern()
    {
        if (Match("$"))
        {
            var name = Expect(TokenKind.Identifier);
            return new VariablePattern(name.Text);
        }
        if (Match("["))
        {
            if (++_depth > JqBudget.MaximumDepth)
                throw Error("filter nesting limit exceeded", Peek().Span);
            try
            {
                var items = new List<BindingPattern>();
                if (!Match("]"))
                {
                    do
                    {
                        items.Add(ParsePattern());
                    } while (Match(","));
                    Expect("]");
                }
                else
                {
                    throw Error("expected pattern, got ]", Peek().Span);
                }
                return new ArrayPattern(items);
            }
            finally
            {
                _depth--;
            }
        }
        if (Match("{"))
        {
            if (++_depth > JqBudget.MaximumDepth)
                throw Error("filter nesting limit exceeded", Peek().Span);
            try
            {
                var properties = new List<ObjectPatternProperty>();
                if (!Match("}"))
                {
                    do
                    {
                        properties.Add(ParseObjectPatternProperty());
                    } while (Match(","));
                    Expect("}");
                }
                else
                {
                    throw Error("expected pattern, got }", Peek().Span);
                }
                return new ObjectPattern(properties);
            }
            finally
            {
                _depth--;
            }
        }
        throw Error($"expected pattern, got {Peek().Text}", Peek().Span);
    }

    private ObjectPatternProperty ParseObjectPatternProperty()
    {
        if (Match("$"))
        {
            var name = Expect(TokenKind.Identifier);
            if (Match(":"))
                return new ObjectPatternProperty(new LiteralFilter(JsonValue.Create(name.Text)), new AliasPattern(name.Text, ParsePattern()));
            return new ObjectPatternProperty(new LiteralFilter(JsonValue.Create(name.Text)), new VariablePattern(name.Text));
        }
        JqFilter key;
        if (Peek() is { Kind: TokenKind.Symbol, Text: "(" })
        {
            Token open = Next();
            key = ParseComma();
            Expect(")");
        }
        else
        {
            var keyToken = Next();
            if (keyToken.Kind != TokenKind.String && keyToken.Kind != TokenKind.Identifier)
                throw Error("expected object key", keyToken.Span);
            key = KeyFilterFor(keyToken);
        }
        Expect(":");
        return new ObjectPatternProperty(key, ParsePattern());
    }

    private JqFilter ParseDictValue()
    {
        // Object values mirror DictExpr: pipes of update-level expressions
        // without top-level commas or bindings (those need parentheses).
        var left = ParseUpdate();
        while (Match("|"))
            left = new PipeFilter(left, ParseUpdate());
        return left;
    }

    private void CheckConstantKey(JqFilter key, JqSourceSpan span)
    {
        // Constant non-string keys fail at compile time; computed keys
        // report the same shape at runtime instead.
        if (key is not LiteralFilter literal)
            return;
        if (literal.Value is JsonValue scalar && scalar.TryGetValue<string>(out _))
            return;
        string text = new JqRuntime(_budget).ToJqString(literal.Value);
        throw Error($"Cannot use {JqRuntime.TypeName(literal.Value)} ({text}) as object key", span);
    }

    private JqFilter ParseAlternative()
    {
        var left = ParseUpdate();
        // Right-associative alternatives: `a // b // c` groups as `a // (b // c)`.
        if (Match("//"))
            return new AlternativeFilter(left, ParseAlternative());
        return left;
    }

    // Assignment binds tighter than `//` but looser than `or`: the left
    // and right sides are update-free, and chaining updates is an error.
    private JqFilter ParseUpdate()
    {
        var left = ParseOr();
        if (Peek().Text is "=" or "|=" or "+=" or "-=" or "*=" or "/=" or "%=" or "//=")
        {
            var op = Next().Text;
            var right = ParseOr();
            if (Peek().Text is "=" or "|=" or "+=" or "-=" or "*=" or "/=" or "%=" or "//=")
                throw Error($"unexpected token {Peek().Text}", Peek().Span);
            return new AssignFilter(op, left, right);
        }
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
            if (!IsBound(name.Text))
                throw Error($"undefined variable ${name.Text}", dollar.Span);
            return new VariableFilter(name.Text);
        }
        if (Match("("))
        {
            var inner = ParseQuery();
            Expect(")");
            return inner;
        }
        if (Match("["))
        {
            if (Match("]"))
                return new LiteralFilter(new JsonArray());
            var item = ParseQuery();
            Expect("]");
            return new ArrayFilter(item);
        }
        if (Match("{"))
            return ParseObject();
        if (MatchIdentifier("if"))
            return ParseIf();
        if (MatchIdentifier("label"))
        {
            Token dollar = Next();
            if (dollar.Kind != TokenKind.Symbol || dollar.Text != "$")
                throw Error($"expected $, got {dollar.Text}", dollar.Span);
            Token name = Expect(TokenKind.Identifier);
            Expect("|");
            EnterScope();
            try
            {
                _scopes.Peek().Labels.Add(name.Text);
                return new LabelFilter(name.Text, ParseQuery());
            }
            finally
            {
                ExitScope();
            }
        }
        if (MatchIdentifier("break"))
        {
            Token dollar = Next();
            if (dollar.Kind != TokenKind.Symbol || dollar.Text != "$")
                throw Error($"expected $, got {dollar.Text}", dollar.Span);
            Token name = Expect(TokenKind.Identifier);
            bool bound = false;
            foreach (var scope in _scopes)
                if (scope.Labels.Contains(name.Text))
                {
                    bound = true;
                    break;
                }
            if (!bound)
                throw Error($"undefined label ${name.Text}", dollar.Span);
            return new BreakFilter(name.Text);
        }
        if (MatchIdentifier("reduce"))
            return ParseReduce(isForeach: false);
        if (MatchIdentifier("foreach"))
            return ParseReduce(isForeach: true);
        if (MatchIdentifier("try"))
        {
            // `try` binds tightly: the operand is one update-level expression,
            // so pipes, commas, bindings, and definitions need parentheses.
            var body = ParseUpdate();
            JqFilter? handler = MatchIdentifier("catch") ? ParseUpdate() : null;
            return new TryFilter(body, handler);
        }

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
            if (Peek() is { Kind: TokenKind.Symbol, Text: "$" })
            {
                Token dollar = Next();
                Token name = Expect(TokenKind.Identifier);
                if (!IsBound(name.Text))
                    throw Error($"undefined variable ${name.Text}", dollar.Span);
                if (Match(":"))
                    properties.Add(new ObjectProperty(null, new VariableFilter(name.Text), ParseDictValue()));
                else
                    properties.Add(new ObjectProperty(name.Text, null, new VariableFilter(name.Text)));
            }
            else if (Peek() is { Kind: TokenKind.Symbol, Text: "(" })
            {
                Token open = Next();
                var key = ParseComma();
                Expect(")");
                Expect(":");
                CheckConstantKey(key, open.Span);
                properties.Add(new ObjectProperty(null, key, ParseDictValue()));
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
                        ? new ObjectProperty(null, KeyFilterFor(keyToken), ParseDictValue())
                        : new ObjectProperty(keyToken.Text, null, ParseDictValue()));
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

    // Reduction sources are update-level expressions with binding patterns;
    // initializers run outside the pattern scope while updates (and the
    // optional extraction) run inside it.
    private JqFilter ParseReduce(bool isForeach)
    {
        var source = ParseUpdate();
        ExpectIdentifier("as");
        List<BindingPattern> patterns = ParsePatterns();
        Expect("(");
        JqFilter init = ParseQuery();
        Expect(";");
        EnterScope();
        try
        {
            DeclarePatterns(patterns);
            JqFilter update = ParseQuery();
            JqFilter? extract = null;
            if (!isForeach)
            {
                Expect(")");
                return new ReduceFilter(source, patterns, init, update);
            }
            if (Match(";"))
                extract = ParseQuery();
            Expect(")");
            return new ForeachFilter(source, patterns, init, update, extract);
        }
        finally
        {
            ExitScope();
        }
    }

    private void DeclarePatterns(List<BindingPattern> patterns)
    {
        ArgumentNullException.ThrowIfNull(patterns);
        var declared = new HashSet<string>(StringComparer.Ordinal);
        foreach (BindingPattern alternative in patterns)
            alternative.CollectBoundNames(declared);
        foreach (string name in declared)
            Declare(name);
    }

    private JqFilter ParseIf()
    {
        var branches = new List<(JqFilter, JqFilter)>();
        var condition = ParseQuery();
        ExpectIdentifier("then");
        branches.Add((condition, ParseQuery()));
        while (MatchIdentifier("elif"))
        {
            var elif = ParseQuery();
            ExpectIdentifier("then");
            branches.Add((elif, ParseQuery()));
        }
        ExpectIdentifier("else");
        var otherwise = ParseQuery();
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
                args.Add(ParseQuery());
            } while (Match(";"));
            Expect(")");
        }
        return CreateFunction(name, args, span);
    }

    private JqFilter CreateFunction(string name, IReadOnlyList<JqFilter> args, JqSourceSpan span)
    {
        // User definitions shadow builtins; a bare name also matches a filter
        // parameter. Unknown names fail here at compile time, as before.
        if (args.Count == 0 && TryLookupZeroArg(name, out bool isFilterParam))
            return isFilterParam ? new FilterParamCallFilter(name) : new UserCallFilter(name, 0, args);
        if (args.Count > 0 && TryLookupFunction(name, args.Count))
            return new UserCallFilter(name, args.Count, args);
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

        if (name == "path")
        {
            return new PathBuiltinFilter(args[0]);
        }

        if (name == "del")
        {
            return new DelBuiltinFilter(args);
        }

        if (name == "getpath")
        {
            return new GetpathBuiltinFilter(args[0]);
        }

        if (name == "setpath")
        {
            return new SetpathBuiltinFilter(args[0], args[1]);
        }

        if (name == "delpaths")
        {
            return new DelpathsBuiltinFilter(args[0]);
        }

        if (name == "pick")
        {
            return new PickFilter(args);
        }

        if (name == "first" && args.Count == 0)
        {
            return new IndexFilter(new IdentityFilter(), new LiteralFilter(JsonValue.Create(0)), false);
        }

        if (name == "first")
        {
            return new FirstFilter(args[0]);
        }

        if (name == "last")
        {
            return new IndexFilter(new IdentityFilter(), new LiteralFilter(JsonValue.Create(-1)), false);
        }

        if (name == "nth" && args.Count == 1)
        {
            return new IndexFilter(new IdentityFilter(), args[0], false);
        }

        if (name == "nth")
        {
            return new NthFilter(args[0], args[1]);
        }

        if (name == "limit")
        {
            return new LimitFilter(args[0], args[1]);
        }

        if (name == "isempty")
        {
            return new IsemptyFilter(args[0]);
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
