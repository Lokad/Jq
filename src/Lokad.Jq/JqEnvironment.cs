using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace Lokad.Jq;

// Immutable lexical environments: invocation variables form the root and
// each binding extends a new scope. Sibling generator branches hold their
// own environments, so captures can never leak across branches.
//
// Bindings come in three namespaces. Variables (`$name`) hold JSON values,
// filters (bare `name`) hold unevaluated argument closures, and functions
// (`name/arity`) hold user definitions. A function node carries its own
// definition environment by construction: the node itself extends the
// captured parent, so lookups forge a correctly scoped closure without
// mutable cells or statics, and recursion resolves through the same path.
internal sealed class JqEnvironment
{
    private readonly IReadOnlyDictionary<string, JsonNode?> _root;
    private readonly JqEnvironment? _parent;
    private readonly string? _name;
    private readonly JsonNode? _value;
    private readonly int _arity;
    private readonly JqFunctionDefinition? _definition;
    private readonly JqFilterClosure? _filter;
    private readonly bool _isFunction;
    private readonly bool _isFilter;

    private JqEnvironment(IReadOnlyDictionary<string, JsonNode?> root)
    {
        _root = root ?? throw new ArgumentNullException(nameof(root));
    }

    private JqEnvironment(JqEnvironment parent, string name, JsonNode? value)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(name);
        _root = parent._root;
        _parent = parent;
        _name = name;
        _value = value;
    }

    private JqEnvironment(JqEnvironment parent, string name, JqFilterClosure filter)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(filter);
        _root = parent._root;
        _parent = parent;
        _name = name;
        _filter = filter;
        _isFilter = true;
    }

    private JqEnvironment(JqEnvironment parent, string name, int arity, JqFunctionDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(definition);
        _root = parent._root;
        _parent = parent;
        _name = name;
        _arity = arity;
        _definition = definition;
        _isFunction = true;
    }

    internal static JqEnvironment CreateRoot(IReadOnlyDictionary<string, JsonNode?> variables) =>
        new(variables);

    internal JqEnvironment Extend(string name, JsonNode? value) =>
        new(this, name, value);

    internal JqEnvironment ExtendFilter(string name, JqFilterClosure filter) =>
        new(this, name, filter);

    internal JqEnvironment ExtendFunction(string name, int arity, JqFunctionDefinition definition) =>
        new(this, name, arity, definition);

    internal bool TryGetValue(string name, out JsonNode? value)
    {
        JqEnvironment? scope = this;
        while (scope is not null)
        {
            if (!scope._isFunction && !scope._isFilter && scope._name is not null && string.Equals(scope._name, name, StringComparison.Ordinal))
            {
                value = scope._value;
                return true;
            }
            scope = scope._parent;
        }

        return _root.TryGetValue(name, out value);
    }

    internal bool TryGetFilter(string name, out JqFilterClosure? filter)
    {
        JqEnvironment? scope = this;
        while (scope is not null)
        {
            if (scope._isFilter && string.Equals(scope._name, name, StringComparison.Ordinal))
            {
                filter = scope._filter;
                return true;
            }
            scope = scope._parent;
        }

        filter = null;
        return false;
    }

    internal bool TryGetFunction(string name, int arity, out JqUserClosure? closure)
    {
        JqEnvironment? scope = this;
        while (scope is not null)
        {
            if (scope._isFunction
                && scope._arity == arity
                && scope._definition is not null
                && string.Equals(scope._name, name, StringComparison.Ordinal))
            {
                closure = new JqUserClosure(scope._definition, scope);
                return true;
            }
            scope = scope._parent;
        }

        closure = null;
        return false;
    }

    // Collects visible user definitions for reparsed fragments (string
    // interpolation re-enters the parser with the evaluation environment).
    // Nearest bindings win so shadowing survives the reparse.
    internal void CollectFunctions(IDictionary<(string Name, int Arity), JqFunctionDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        JqEnvironment? scope = this;
        while (scope is not null)
        {
            if (scope._isFunction && scope._definition is not null && scope._name is not null)
            {
                var key = (scope._name, scope._arity);
                if (!definitions.ContainsKey(key))
                    definitions[key] = scope._definition;
            }
            scope = scope._parent;
        }
    }
}
