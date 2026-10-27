using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Nodes;

namespace Lokad.Jq;

internal sealed class JqParser(
    IReadOnlyList<Token> tokens,
    JqProgramSource programSource,
    JqEnvironment environment,
    JqBudget budget)
{
    private readonly JqProgramSource _programSource = programSource ?? throw new ArgumentNullException(nameof(programSource));
    private readonly JqEnvironment _environment = environment ?? throw new ArgumentNullException(nameof(environment));
    private readonly Stack<ParserScope> _scopes = new();
    private readonly JqBudget _budget = budget ?? throw new ArgumentNullException(nameof(budget));
    private readonly IReadOnlyList<Token> _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
    private int _index;
    private int _depth;
    private bool _inspectLibrary;
    private bool _functionsSeeded;

    public JqParser(string source, JqProgramSource programSource, JqEnvironment environment, JqBudget budget)
        : this(Lexer.Tokenize(source, programSource, budget), programSource, environment, budget) { }

    public JqFilter Parse()
    {
        ParsePrefix();
        return ParseBody();
    }

    internal JqFilter ParseBody(int bodyIndex)
    {
        _index = bodyIndex;
        return ParseBody();
    }

    private JqFilter ParseBody()
    {
        SeedFunctionsFromEnvironment();
        if (Peek().Kind == TokenKind.End)
            throw Error("Top-level program not given (try \".\")", Peek().Span);
        var filter = ParseQuery(topLevel: true);
        Expect(TokenKind.End);
        return filter;
    }

    // Validate metadata/imports before dependency loading; retain the position
    // where parsing resumes with the linked environment.
    internal (JsonObject? Metadata, IReadOnlyList<JqModuleImport> Imports, int BodyIndex) ParsePrefix()
    {
        _budget.CheckCancellation();
        if (Peek() is { Kind: TokenKind.Identifier, Text: "module" or "import" or "include" })
            SeedFunctionsFromEnvironment();
        var metadata = ParseModuleHeader();
        var imports = ParseImportList();
        return (metadata, imports, _index);
    }

    // Inspect library syntax without resolving dependencies or callable names.
    internal (JsonObject? Metadata, IReadOnlyList<JqModuleImport> Imports, IReadOnlyList<JqFunctionDefinition> Definitions) InspectLibrary()
    {
        _inspectLibrary = true;
        var (metadata, imports, bodyIndex) = ParsePrefix();
        return (metadata, imports, ParseDefinitions(bodyIndex));
    }

    // Library bodies contain definitions only, with dependencies in scope.
    internal IReadOnlyList<JqFunctionDefinition> ParseDefinitions(int bodyIndex)
    {
        _index = bodyIndex;
        SeedFunctionsFromEnvironment();
        var definitions = new List<JqFunctionDefinition>();
        while (PeekIsDef())
        {
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
            var complete = AttachBody(definition, _inspectLibrary ? body : RewriteTailCalls(body, definition));
            DeclareFunction(complete);
            definitions.Add(complete);
        }
        if (Peek().Kind != TokenKind.End)
            throw Error("library should only have function definitions, not a main expression", Peek().Span);
        return definitions;
    }

    private JsonObject? ParseModuleHeader()
    {
        if (!MatchIdentifier("module"))
            return null;
        var metaSpan = Peek().Span;
        JqFilter metadata = ParseQuery();
        Expect(";");
        if (!TryGetConstant(metadata, out JsonNode? value))
            throw Error("Module metadata must be constant", metaSpan);
        if (value is not JsonObject obj)
            throw Error("Module metadata must be an object", metaSpan);
        return obj;
    }

    private IReadOnlyList<JqModuleImport> ParseImportList()
    {
        List<JqModuleImport>? imports = null;
        while (Peek() is { Kind: TokenKind.Identifier, Text: "import" or "include" })
        {
            bool include = Peek().Text == "include";
            (imports ??= new()).Add(ParseImportDirective(include));
        }
        return imports is null ? Array.Empty<JqModuleImport>() : imports;
    }

    private JqModuleImport ParseImportDirective(bool isInclude)
    {
        Next();
        if (Peek().Kind != TokenKind.String)
            throw Error("Import path must be constant", Peek().Span);
        var pathToken = Next();
        if (Lexer.ContainsInterpolation(pathToken.Text))
            throw Error("Import path must be constant", pathToken.Span);
        string relPath = Lexer.DecodeLiterals(pathToken.Text, _budget);
        string? alias = null;
        bool isData = false;
        if (!isInclude)
        {
            ExpectIdentifier("as");
            if (Match("$"))
            {
                var varName = Expect(TokenKind.Identifier);
                alias = varName.Text;
                isData = true;
            }
            else
            {
                var aliasToken = Expect(TokenKind.Identifier);
                alias = aliasToken.Text;
            }
        }
        JsonObject? metadata = null;
        if (Match(";"))
            return new JqModuleImport(relPath, alias, isData, metadata);
        var metaSpan = Peek().Span;
        JqFilter metaFilter = ParseQuery();
        Expect(";");
        if (!TryGetConstant(metaFilter, out JsonNode? metaValue))
            throw Error("Module metadata must be constant", metaSpan);
        if (metaValue is not JsonObject metaObj)
            throw Error("Module metadata must be an object", metaSpan);
        metadata = metaObj;
        return new JqModuleImport(relPath, alias, isData, metadata);
    }

    private bool TryGetConstant(JqFilter filter, out JsonNode? value)
    {
        // Build each private constant once. A shared pending stack flattens
        // comma generators without sizing every nested array to the program.
        using var traversal = new JqScopeStorage(_budget);
        var pending = new Stack<JqFilter>();
        long stackBytes = 0;
        int stackCapacity = 0;
        return Build(filter, 0, out value);

        bool Build(JqFilter node, int depth, out JsonNode? built)
        {
            _budget.CheckCancellation();
            built = null;
            if (node is LiteralFilter literal)
            {
                built = _budget.CopyValue(literal.Value, depth);
                return true;
            }
            if (node is ArrayFilter array)
            {
                JqRecursion.CheckDepth(depth + 1, "value nesting limit exceeded");
                _budget.ReserveBytes(64);
                var result = new JsonArray();
                int boundary = pending.Count;
                Push(array.Item);
                while (pending.Count > boundary)
                {
                    _budget.CheckCancellation();
                    JqFilter current = pending.Pop();
                    if (current is CommaFilter comma)
                    {
                        Push(comma.Right);
                        Push(comma.Left);
                        continue;
                    }
                    if (!Build(current, depth + 1, out JsonNode? child)) return false;
                    _budget.ReserveArrayItem(child);
                    result.Add(child);
                }
                built = result;
                return true;
            }
            if (node is ObjectFilter obj)
            {
                JqRecursion.CheckDepth(depth + 1, "value nesting limit exceeded");
                _budget.ReserveBytes(64);
                var result = new JsonObject();
                foreach (ObjectProperty property in obj.Properties)
                {
                    _budget.CheckCancellation();
                    if (property.StaticKey is null || property.KeyFilter is not null
                        || !Build(property.Value, depth + 1, out JsonNode? child)) return false;
                    _budget.ReserveObjectItem(child);
                    result[property.StaticKey] = child;
                }
                built = result;
                return true;
            }
            return false;
        }

        void Push(JqFilter node)
        {
            if (pending.Count == stackCapacity)
            {
                int capacity = JqBudget.BufferCapacity((long)pending.Count + 1, Math.Max(4, 2L * pending.Count));
                long bytes = 64 + 8L * capacity;
                traversal.Reserve(bytes, 1);
                try { stackCapacity = pending.EnsureCapacity(capacity); }
                catch { traversal.Release(bytes); throw; }
                traversal.Release(stackBytes);
                stackBytes = bytes;
            }
            pending.Push(node);
        }
    }

    private JqCompileException Error(string message, JqSourceSpan span) =>
        new(message, span, _programSource);

    private sealed class ParserScope
    {
        internal HashSet<string>? Variables;
        internal HashSet<string>? FilterParams;
        internal Dictionary<string, object>? Labels;
        internal Dictionary<(string Name, int Arity), JqFunctionDefinition>? Functions;
    }

    private bool IsBound(string name)
    {
        foreach (var scope in _scopes)
            if (scope.Variables?.Contains(name) == true)
                return true;
        return _environment.TryGetValue(name, out _);
    }

    private void EnterScope() => _scopes.Push(new ParserScope());

    private void Declare(string name)
    {
        if (_scopes.Count == 0)
            throw new InvalidOperationException("No open binding scope.");
        (_scopes.Peek().Variables ??= new(StringComparer.Ordinal)).Add(name);
    }

    private void DeclareFilterParam(string name)
    {
        if (_scopes.Count == 0)
            throw new InvalidOperationException("No open binding scope.");
        (_scopes.Peek().FilterParams ??= new(StringComparer.Ordinal)).Add(name);
    }

    private void DeclareFunction(JqFunctionDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (_scopes.Count == 0)
            _scopes.Push(new ParserScope());
        (_scopes.Peek().Functions ??= new())[(definition.Name, definition.Arity)] = definition;
    }

    // Resolves a bare `name` use: a zero-argument user function wins over a
    // filter parameter in the same scope, matching definition-site lookup.
    private bool TryLookupZeroArg(string name, out bool isFilterParam)
    {
        foreach (var scope in _scopes)
        {
            if (scope.Functions?.ContainsKey((name, 0)) == true)
            {
                isFilterParam = false;
                return true;
            }
            if (scope.FilterParams?.Contains(name) == true)
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
            if (scope.Functions?.ContainsKey((name, arity)) == true)
                return true;
        return false;
    }

    // Seed visible user definitions from the environment so top-level filters
    // and definition-site interpolation pieces resolve module functions.
    // Nearest bindings win.
    private void SeedFunctionsFromEnvironment()
    {
        if (_functionsSeeded) return;
        _functionsSeeded = true;
        if (_environment.CollectFunctions(_budget) is { } functions)
            _scopes.Push(new ParserScope { Functions = functions });
    }

    private void ExitScope()
    {
        if (_scopes.Count == 0)
            throw new InvalidOperationException("No open binding scope.");
        _scopes.Pop();
    }

    // Parses string-interpolation pieces once at construction with the
    // definition-site scopes, so unknown names fail at compile time (stage 3)
    // instead of surfacing per evaluation as stage 5. The sub-parser shares
    // the program source, environment, and budget. Parent scopes are borrowed
    // only during synchronous parsing; declarations use a private top scope.
    private JqFilter ParseInterpolationPiece(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var piece = new JqParser(source, _programSource, _environment, _budget);
        piece._inspectLibrary = _inspectLibrary;
        piece._depth = _depth + 1;
        _budget.CheckCancellation();
        _budget.ReserveMetadata(384 + 32L * _scopes.Count, _scopes.Count + 1);
        var visible = _scopes.ToArray();
        for (int index = visible.Length - 1; index >= 0; index--)
        {
            _budget.CheckCancellation();
            piece._scopes.Push(visible[index]);
        }
        piece.EnterScope();
        return piece.ParseFragment();
    }

    // Fragment parsing for interpolation pieces: a single query without module
    // headers or imports. Leading defs stay scoped to the piece like the
    // reference, and the caller already seeded module functions.
    private JqFilter ParseFragment()
    {
        if (Peek().Kind == TokenKind.End)
            throw Error("Top-level program not given (try \".\")", Peek().Span);
        var filter = ParseQuery();
        Expect(TokenKind.End);
        return filter;
    }

    // Definitions bind loosest: `def f: ...; rest` scopes the definition over
    // the whole following query, mirroring gojq `query: funcdef query`. This
    // layer fronts every full-expression position (top level, parentheses,
    // brackets, pipe right-hand sides starting with `def`, binding bodies,
    // conditions, branches, and call arguments).
    private JqFilter ParseQuery()
    {
        return ParseQuery(topLevel: false);
    }

    // A trailing top-level `def` with no following query means the program
    // has definitions but no main expression, like an empty program;
    // nested positions keep their generic unexpected-token errors.
    private JqFilter ParseQuery(bool topLevel)
    {
        var definitions = new List<JqFunctionDefinition>();
        while (PeekIsDef())
        {
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
                if (++_depth > JqRecursion.MaximumDepth)
                    throw Error("filter nesting limit exceeded", Peek().Span);
                try { body = ParseQuery(topLevel: false); }
                finally { _depth--; }
            }
            finally
            {
                ExitScope();
            }
            Expect(";");
            var complete = AttachBody(definition, _inspectLibrary ? body : RewriteTailCalls(body, definition));
            DeclareFunction(complete);
            definitions.Add(complete);
        }
        if (topLevel && definitions.Count != 0 && Peek().Kind == TokenKind.End)
            throw Error("Top-level program not given (try \".\")", Peek().Span);
        JqFilter result = ParsePipe();
        for (int index = definitions.Count - 1; index >= 0; index--)
            result = new DefFilter(definitions[index], result);
        return result;
    }

    private static JqFunctionDefinition AttachBody(JqFunctionDefinition definition, JqFilter body)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(body);
        return new JqFunctionDefinition(definition.Name, definition.Parameters, body);
    }

    private JqFunctionDefinition ParseFuncDefHead()
    {
        if (++_depth > JqRecursion.MaximumDepth)
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
                        // The reference closure operand has 12 index bits. This is
                        // a function-arity rule, independent of source/token size.
                        if (parameters.Count > 4096)
                            throw Error("function has too many parameters (max 4096)", Peek().Span);
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

    // Direct self-calls may replace a frame only when no pending work is lost.
    // A bare pipe tail transfers its complete source cursor to the call driver.
    // Other pipe/if rewrites require proven single-valued sources/conditions.
    // Try, optional and label scopes stay active through ordinary guarded calls.
    private JqFilter RewriteTailCalls(JqFilter node, JqFunctionDefinition definition) =>
        RewriteTail(node, definition, 1);

    private JqFilter RewriteTail(JqFilter node, JqFunctionDefinition definition, int depth)
    {
        _budget.CheckCancellation();
        if (depth > JqRecursion.MaximumDepth)
            throw Error("filter nesting limit exceeded", Peek().Span);
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(definition);
        if (node is UserCallFilter call
            && call.FunctionName == definition.Name
            && call.FunctionArity == definition.Arity)
            return new TailSelfCallFilter(definition, call.CallArgs);
        if (node is PipeFilter pipe)
        {
            if (pipe.Right is UserCallFilter direct
                && direct.FunctionName == definition.Name && direct.FunctionArity == definition.Arity)
                return new PipeTailLoop(pipe.Left, definition, direct.CallArgs);
            if (!pipe.Left.IsSingleValued(_environment, 0)) return pipe;
            return pipe.WithRight(RewriteTail(pipe.Right, definition, depth + 1));
        }
        if (node is IfFilter iff)
            return iff.WithTails(_environment, branch => RewriteTail(branch, definition, depth + 1));
        if (node is CommaFilter comma)
            return comma.WithRight(RewriteTail(comma.Right, definition, depth + 1));
        if (node is AlternativeFilter alternative)
            return alternative.WithRight(RewriteTail(alternative.Right, definition, depth + 1));
        if (node is DefFilter nested)
        {
            if (nested.FunctionDefinition.Name == definition.Name
                && nested.FunctionDefinition.Arity == definition.Arity)
                return nested;
            return nested.WithContinuation(RewriteTail(nested.ContinuationBody, definition, depth + 1));
        }
        return node;
    }

    private JqFilter ParseComma()
    {
        var left = ParseAs();
        while (Match(","))
        {
            // Like pipe right-hand sides, a comma branch starting with `def`
            // takes the whole rest of the query as its body, matching the
            // reference `Query , Query` rule where the right query may open
            // with a definition.
            left = new CommaFilter(left, PeekIsDef() ? ParseQuery() : ParseAs());
        }
        return left;
    }

    private JqFilter ParsePipe()
    {
        var left = ParseComma();
        while (Match("|"))
        {
            // A right-hand side starting with `def` takes the whole rest of
            // the query as its body, matching `query: funcdef query`.
            JqFilter right = PeekIsDef() ? ParseQuery() : ParseComma();
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
            if (++_depth > JqRecursion.MaximumDepth)
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
            if (++_depth > JqRecursion.MaximumDepth)
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
            key = ParsePipe();
            Expect(")");
            // Like construction keys and the reference pattern check,
            // scalar non-string constants fail at compile time here too.
            CheckConstantKey(key, open.Span);
        }
        else
        {
            var keyToken = Next();
            if (keyToken.Kind != TokenKind.String && keyToken.Kind != TokenKind.Identifier)
                throw Error("expected object key", keyToken.Span);
            key = StringFilterFor(keyToken, null);
        }
        Expect(":");
        return new ObjectPatternProperty(key, ParsePattern());
    }

    private JqFilter ParseDictValue()
    {
        // Object values mirror DictExpr: pipes of alternative-level
        // expressions without top-level commas or bindings (those need
        // parentheses), so `//` works in values like the reference.
        var left = ParseAlternative();
        while (Match("|"))
            left = new PipeFilter(left, ParseAlternative());
        return left;
    }

    private void CheckConstantKey(JqFilter key, JqSourceSpan span)
    {
        // Constant non-string keys fail at compile time; computed keys
        // report the same shape at runtime instead. Like the reference
        // const-key check, only scalar constants qualify: container
        // literals evaluate like computed keys and fail staged instead.
        if (key is not LiteralFilter literal)
            return;
        if (literal.Value is JsonValue scalar && scalar.TryGetValue<string>(out _))
            return;
        if (literal.Value is JsonObject || literal.Value is JsonArray)
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
        if (++_depth > JqRecursion.MaximumDepth)
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
                    var name = Lexer.DecodeLiterals(Next().Text, _budget);
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
        bool sawColon = Match(":");
        if (!sawColon)
        {
            index = ParsePipe();
            if (!Match(":"))
            {
                Expect("]");
                var opt = Match("?");
                return new IndexFilter(source, index, opt);
            }
        }

        // A bare [:] has no upstream production (gen_slice_index always
        // carries at least one bound), so it fails like any unexpected token.
        if (index is null && Peek().Text == "]")
            throw Error($"unexpected token {Peek().Text}", Peek().Span);

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
            if (Peek().Kind != TokenKind.String)
                return new FormatFilter(format);
            return StringFilterFor(Next(), format);
        }
        if (Match("."))
        {
            if (Match("."))
                return new RecursiveDescentFilter();
            var identity = new IdentityFilter();
            if (Peek().Kind == TokenKind.FieldName)
            return new FieldFilter(identity, Lexer.DecodeLiterals(Next().Text, _budget), Match("?"));
            if (Peek().Kind == TokenKind.String)
                return new FieldFilter(identity, Lexer.DecodeLiterals(Next().Text, _budget), Match("?"));
            if (Match("["))
                return ParseBracket(identity);
            return identity;
        }
        if (Peek() is { Kind: TokenKind.Symbol, Text: "$" })
        {
            var dollar = Next();
            var name = Expect(TokenKind.Identifier);
            if (name.Text == "__loc__")
            {
                string file = _programSource.Path ?? "<top-level>";
                return new LocFilter(file, name.Span.Line);
            }
            // Qualified data references (alias::name) resolve at runtime via
            // module scopes; unqualified names still fail here when unbound.
            if (!_inspectLibrary && !name.Text.Contains("::", StringComparison.Ordinal) && !IsBound(name.Text))
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
                var label = new object();
                (_scopes.Peek().Labels ??= new(StringComparer.Ordinal)).Add(name.Text, label);
                return new LabelFilter(label, ParseQuery());
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
            foreach (var scope in _scopes)
                if (scope.Labels is { } labels && labels.TryGetValue(name.Text, out object? label))
                    return new BreakFilter(label);
            if (_inspectLibrary) return new BreakFilter(new object());
            throw Error($"undefined label ${name.Text}", dollar.Span);
        }
        if (MatchIdentifier("reduce"))
            return ParseReduce(isForeach: false);
        if (MatchIdentifier("foreach"))
            return ParseReduce(isForeach: true);
        if (MatchIdentifier("try"))
        {
            // Like the reference `"try" Expr` rules, the body takes
            // alternative-level combinations while the handler stops before
            // trailing binary operators, so pipes, commas, bindings, and
            // definitions still need parentheses. Without a handler the
            // body stops before `//` per the reference reduce, so a trailing
            // fallback still runs (upstream: try error(0) // 1 yields 1).
            var body = ParseAlternative();
            JqFilter? handler = MatchIdentifier("catch") ? ParseUnary() : null;
            if (handler is null && body is AlternativeFilter alternative)
                return new AlternativeFilter(new TryFilter(alternative.Left, null), alternative.Right);
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
            return StringFilterFor(token, null);
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
                JqFilter valueFilter;
                if (name.Text == "__loc__")
                {
                    string locFile = _programSource.Path ?? "<top-level>";
                    valueFilter = new LocFilter(locFile, name.Span.Line);
                }
                else
                {
                    if (!_inspectLibrary && !name.Text.Contains("::", StringComparison.Ordinal) && !IsBound(name.Text))
                        throw Error($"undefined variable ${name.Text}", dollar.Span);
                    valueFilter = new VariableFilter(name.Text);
                }
                if (Match(":"))
                    properties.Add(new ObjectProperty(null, valueFilter, ParseDictValue()));
                else
                    properties.Add(new ObjectProperty(name.Text == "__loc__" ? "__loc__" : name.Text, null, valueFilter));
            }
            else if (Peek() is { Kind: TokenKind.Symbol, Text: "(" })
            {
                Token open = Next();
                var key = ParsePipe();
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
                        ? new ObjectProperty(null, StringFilterFor(keyToken, null), ParseDictValue())
                        : new ObjectProperty(Lexer.DecodeLiterals(keyToken.Text, _budget), null, ParseDictValue()));
                }
                else if (keyToken.Kind == TokenKind.String)
                {
                    // Bare plain strings keep field access; interpolated keys
                    // read through the evaluated key instead.
                    bool interpolated = KeyFilterForIsDynamic(keyToken);
                    properties.Add(interpolated
                        ? new ObjectProperty(null, StringFilterFor(keyToken, null), new IndexFilter(new IdentityFilter(), StringFilterFor(keyToken, null), false))
                        : new ObjectProperty(Lexer.DecodeLiterals(keyToken.Text, _budget), null, new FieldFilter(new IdentityFilter(), Lexer.DecodeLiterals(keyToken.Text, _budget), true)));
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

    private JqFilter StringFilterFor(Token token, string? format) => Lexer.ContainsInterpolation(token.Text)
        ? new InterpolatedStringFilter(token.Text, format, token.Span, _programSource, ParseInterpolationPiece, _budget)
        : new LiteralFilter(JsonValue.Create(Lexer.DecodeLiterals(token.Text, _budget)));

    private static bool KeyFilterForIsDynamic(Token token) => token.Kind == TokenKind.String && Lexer.ContainsInterpolation(token.Text);

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
        // A missing `else` behaves like `else .`, matching the reference
        // ElseBody rule that accepts a bare `end`.
        JqFilter otherwise = MatchIdentifier("else") ? ParseQuery() : new IdentityFilter();
        ExpectIdentifier("end");
        return new IfFilter(branches, otherwise);
    }

    private JqFilter ParseFunction(string name, JqSourceSpan span)
    {
        var args = new List<JqFilter>();
        if (_inspectLibrary && Peek() is { Kind: TokenKind.Symbol, Text: ")" })
            throw Error("expected function argument", Peek().Span);
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
        if (_inspectLibrary)
        {
            // The lexer represents keywords as identifiers. Unresolved callable
            // names are valid library syntax; standalone grammar keywords are not.
            if (name is "as" or "import" or "include" or "module" or "def"
                or "if" or "then" or "else" or "elif" or "and" or "or" or "end"
                or "reduce" or "foreach" or "try" or "catch" or "label" or "break")
                throw Error("unexpected token " + name, span);
            return new FunctionFilter(name, args);
        }
        // Qualified references (alias::name) resolve at runtime via module
        // scopes; parse them blind so link-time binding can shadow correctly.
        // Later includes and same-alias imports win through environment order.
        if (name.Contains("::", StringComparison.Ordinal))
            return new UserCallFilter(name, args.Count, args);
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
            return new GsubFilter(args, false);
        }

        if (name == "test")
        {
            return new TestFilter(args);
        }

        if (name == "match")
        {
            return new MatchFilter(args);
        }

        if (name == "capture")
        {
            return new CaptureFilter(args);
        }

        if (name == "sub")
        {
            return new GsubFilter(args, true);
        }

        if (name == "scan")
        {
            return new ScanFilter(args);
        }

        if (name == "splits")
        {
            return new SplitsFilter(args);
        }

        if (name == "_match_impl")
        {
            return new MatchImplFilter(args);
        }

        if (name == "range")
        {
            return new RangeFilter(args);
        }

        if (name == "path")
        {
            return new PathBuiltinFilter(args[0]);
        }

        if (name == "debug")
        {
            return new DebugFilter(args);
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

        if (name == "last" && args.Count == 0)
        {
            return new IndexFilter(new IdentityFilter(), new LiteralFilter(JsonValue.Create(-1)), false);
        }

        if (name == "last")
        {
            return new LastFilter(args[0]);
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

        if (name == "skip")
        {
            return new SkipFilter(args[0], args[1]);
        }

        if (name == "isempty")
        {
            return new IsemptyFilter(args[0]);
        }

        if (name == "while")
        {
            return new WhileFilter(args[0], args[1]);
        }

        if (name == "until")
        {
            return new UntilFilter(args[0], args[1]);
        }

        if (name == "repeat")
        {
            return new RepeatFilter(args[0]);
        }

        if (name == "recurse" && args.Count == 0)
        {
            return new RecurseFilter(new IteratorFilter(new IdentityFilter(), true), null);
        }

        if (name == "recurse")
        {
            return args.Count == 1
                ? new RecurseFilter(args[0], null)
                : new RecurseFilter(args[0], args[1]);
        }

        if (name == "walk")
        {
            return new WalkFilter(args[0]);
        }

        if (name == "map")
        {
            return new MapFilter(args[0]);
        }

        if (name == "map_values")
        {
            return new MapValuesFilter(args[0]);
        }

        if (name == "with_entries")
        {
            return new WithEntriesFilter(args[0]);
        }

        if (name == "flatten" && args.Count == 0)
        {
            return new FlattenFilter(null);
        }

        if (name == "flatten")
        {
            return new FlattenFilter(args[0]);
        }

        if (name == "_flatten")
        {
            return new FlattenImplFilter(args[0]);
        }

        if (name == "add" && args.Count == 0)
        {
            return new AddValuesFilter(new IteratorFilter(new IdentityFilter(), false));
        }

        if (name == "add")
        {
            return new AddValuesFilter(args[0]);
        }

        if (name == "sort_by")
        {
            return new SortByFilter(args[0]);
        }

        if (name == "group_by")
        {
            return new GroupByFilter(args[0]);
        }

        if (name == "unique_by")
        {
            return new UniqueByFilter(args[0]);
        }

        if (name == "min_by")
        {
            return new MinMaxByFilter(args[0], false);
        }

        if (name == "max_by")
        {
            return new MinMaxByFilter(args[0], true);
        }
        if (name == "_sort_by_impl")
        {
            return new SortByImplFilter(args[0]);
        }

        if (name == "_group_by_impl")
        {
            return new GroupByImplFilter(args[0]);
        }

        if (name == "_unique_by_impl")
        {
            return new UniqueByImplFilter(args[0]);
        }

        if (name == "_min_by_impl")
        {
            return new MinMaxByImplFilter(args[0], false);
        }

        if (name == "_max_by_impl")
        {
            return new MinMaxByImplFilter(args[0], true);
        }


        if (name == "transpose")
        {
            return new TransposeFilter();
        }

        if (name == "tostream" && args.Count == 0)
        {
            return new TostreamFilter();
        }

        if (name == "fromstream" && args.Count == 1)
        {
            return new FromstreamFilter(args[0]);
        }

        if (name == "truncate_stream" && args.Count == 1)
        {
            return new TruncateStreamFilter(args[0]);
        }

        if (name == "bsearch")
        {
            return new BsearchFilter(args[0]);
        }

        if (name == "any" && args.Count == 0)
        {
            return new AnyFilter(new IteratorFilter(new IdentityFilter(), false), new IdentityFilter());
        }

        if (name == "any" && args.Count == 1)
        {
            return new AnyFilter(new IteratorFilter(new IdentityFilter(), false), args[0]);
        }

        if (name == "any")
        {
            return new AnyFilter(args[0], args[1]);
        }

        if (name == "all" && args.Count == 0)
        {
            return new AllFilter(new IteratorFilter(new IdentityFilter(), false), new IdentityFilter());
        }

        if (name == "all" && args.Count == 1)
        {
            return new AllFilter(new IteratorFilter(new IdentityFilter(), false), args[0]);
        }

        if (name == "all")
        {
            return new AllFilter(args[0], args[1]);
        }

        if (name == "IN" && args.Count == 1)
        {
            return new AnyFilter(new BinaryFilter(args[0], "==", new IdentityFilter()), new IdentityFilter());
        }

        if (name == "IN")
        {
            return new AnyFilter(new BinaryFilter(args[0], "==", args[1]), new IdentityFilter());
        }

        if (name == "INDEX" && args.Count == 1)
        {
            return new SqlIndexFilter(null, args[0]);
        }

        if (name == "INDEX")
        {
            return new SqlIndexFilter(args[0], args[1]);
        }

        if (name == "JOIN" && args.Count == 2)
        {
            return new SqlJoinFilter(args[0], null, args[1], null);
        }

        if (name == "JOIN" && args.Count == 3)
        {
            return new SqlJoinFilter(args[0], args[1], args[2], null);
        }

        if (name == "JOIN")
        {
            return new SqlJoinFilter(args[0], args[1], args[2], args[3]);
        }

        if (name == "combinations")
        {
            return args.Count == 0
                ? new CombinationsFilter(null)
                : new CombinationsFilter(args[0]);
        }

        // Compiler-generated operator implementations: direct calls share
        // the operator evaluation (second-operand-outer combinations) with
        // the input threaded into both arguments like the reference prelude.
        if (name == "_plus")
        {
            return new BinaryFilter(args[0], "+", args[1]);
        }

        if (name == "_minus")
        {
            return new BinaryFilter(args[0], "-", args[1]);
        }

        if (name == "_multiply")
        {
            return new BinaryFilter(args[0], "*", args[1]);
        }

        if (name == "_divide")
        {
            return new BinaryFilter(args[0], "/", args[1]);
        }

        if (name == "_mod")
        {
            return new BinaryFilter(args[0], "%", args[1]);
        }

        if (name == "_equal")
        {
            return new BinaryFilter(args[0], "==", args[1]);
        }

        if (name == "_notequal")
        {
            return new BinaryFilter(args[0], "!=", args[1]);
        }

        if (name == "_less")
        {
            return new BinaryFilter(args[0], "<", args[1]);
        }

        if (name == "_lesseq")
        {
            return new BinaryFilter(args[0], "<=", args[1]);
        }

        if (name == "_greater")
        {
            return new BinaryFilter(args[0], ">", args[1]);
        }

        if (name == "_greatereq")
        {
            return new BinaryFilter(args[0], ">=", args[1]);
        }

        // Parser-support assignment helpers: _assign folds setpath over the
        // enumerated paths per value like `=`, while _modify threads the
        // update through first-only state like `|=`.
        if (name == "_assign")
        {
            return new AssignFilter("=", args[0], args[1]);
        }

        if (name == "_modify")
        {
            return new ModifyBuiltinFilter(args[0], args[1]);
        }

        if (name == "in")
        {
            return new InFilter(args[0]);
        }

        if (name == "paths")
        {
            return args.Count == 0
                ? new PathsFilter(null)
                : new PathsFilter(args[0]);
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

    private Token Peek() { _budget.CheckCancellation(); return _tokens[_index]; }

    private Token Next() { _budget.CheckCancellation(); return _tokens[_index++]; }
}
