using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace Lokad.Jq;

// Immutable lexical environments: invocation variables form the root and
// each binding extends a new scope. Sibling generator branches hold their
// own environments, so captures can never leak across branches.
//
// Bindings have separate namespaces. Variables (`$name`) hold JSON values,
// filters (bare `name`) hold unevaluated argument closures, functions
// (`name/arity`) hold user definitions, and module aliases (`alias`)
// hold imported module scopes for qualified `alias::name` lookup. Labels retain
// lexical identity and an activation node. A function node carries its own
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
    private readonly JqUserClosure? _closure;
    private readonly JqEnvironment? _moduleScope;
    private readonly object? _label;
    private readonly bool _isFunction;
    private readonly bool _isFilter;
    private readonly bool _isModule;

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

    private JqEnvironment(JqEnvironment parent, string name, JqEnvironment moduleScope)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(moduleScope);
        _root = parent._root;
        _parent = parent;
        _name = name;
        _moduleScope = moduleScope;
        _isModule = true;
    }

    internal static JqEnvironment CreateRoot(IReadOnlyDictionary<string, JsonNode?> variables) =>
        new(variables);

    private JqEnvironment(JqEnvironment parent, object label)
    {
        _root = parent._root;
        _parent = parent;
        _label = label;
    }

    // The environment node identifies one activation of a lexical label.
    internal JqEnvironment ExtendLabel(object label) => new(this, label);

    internal bool TryGetLabel(object label, out JqEnvironment? activation)
    {
        for (JqEnvironment? scope = this; scope is not null; scope = scope._parent)
            if (ReferenceEquals(scope._label, label))
            {
                activation = scope;
                return true;
            }
        activation = null;
        return false;
    }

    internal JqEnvironment Extend(string name, JsonNode? value) =>
        new(this, name, value);

    internal JqEnvironment ExtendFilter(string name, JqFilterClosure filter) =>
        new(this, name, filter);

    internal JqEnvironment ExtendFunction(string name, int arity, JqFunctionDefinition definition) =>
        new(this, name, arity, definition);

    internal JqEnvironment ExtendModule(string alias, JqEnvironment moduleScope) =>
        new(this, alias, moduleScope);

    private JqEnvironment(JqEnvironment parent, string name, int arity, JqUserClosure closure)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(closure);
        _root = parent._root;
        _parent = parent;
        _name = name;
        _arity = arity;
        _closure = closure;
        _isFunction = true;
    }

    internal JqEnvironment ExtendClosure(string name, int arity, JqUserClosure closure) =>
        new(this, name, arity, closure);

    internal bool TryGetValue(string name, out JsonNode? value)
    {
        JqEnvironment? scope = this;
        while (scope is not null)
        {
            if (!scope._isFunction && !scope._isFilter && !scope._isModule && scope._name is not null && string.Equals(scope._name, name, StringComparison.Ordinal))
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
                && string.Equals(scope._name, name, StringComparison.Ordinal))
            {
                if (scope._closure is not null)
                {
                    closure = scope._closure;
                    return true;
                }
                if (scope._definition is not null)
                {
                    closure = new JqUserClosure(scope._definition, scope);
                    return true;
                }
            }
            scope = scope._parent;
        }

        closure = null;
        return false;
    }

    internal bool TryGetModule(string alias, out JqEnvironment? moduleScope)
    {
        JqEnvironment? scope = this;
        while (scope is not null)
        {
            if (scope._isModule && string.Equals(scope._name, alias, StringComparison.Ordinal))
            {
                moduleScope = scope._moduleScope;
                return true;
            }
            scope = scope._parent;
        }

        moduleScope = null;
        return false;
    }

    internal bool TryGetFunctionQualified(string qualifiedName, int arity, out JqUserClosure? closure)
    {
        ArgumentNullException.ThrowIfNull(qualifiedName);
        var separator = qualifiedName.IndexOf("::", System.StringComparison.Ordinal);
        if (separator < 0)
            return TryGetFunction(qualifiedName, arity, out closure);

        var alias = qualifiedName[..separator];
        var remainder = qualifiedName[(separator + 2)..];
        if (string.IsNullOrEmpty(alias) || string.IsNullOrEmpty(remainder))
        {
            closure = null;
            return false;
        }

        if (TryGetModule(alias, out JqEnvironment? moduleScope) && moduleScope is not null)
            return moduleScope.TryGetFunctionQualified(remainder, arity, out closure);

        closure = null;
        return false;
    }

    // Seeds compilation of interpolation fragments with visible module
    // definitions. Nearest bindings win; fragments compile once at definition.
    internal void CollectFunctions(IDictionary<(string Name, int Arity), JqFunctionDefinition> definitions, JqBudget budget)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        JqEnvironment? scope = this;
        while (scope is not null)
        {
            budget.CheckCancellation();
            if (scope._isFunction && scope._name is not null)
            {
                JqFunctionDefinition? definition = scope._closure?.Definition ?? scope._definition;
                if (definition is not null)
                {
                    var key = (scope._name, scope._arity);
                    if (!definitions.ContainsKey(key))
                    {
                        budget.ReserveMetadata(128, 1);
                        definitions[key] = definition;
                    }
                }
            }
            scope = scope._parent;
        }
    }

    internal void CollectFunctionClosures(IDictionary<(string Name, int Arity), JqUserClosure> closures, JqBudget budget)
    {
        ArgumentNullException.ThrowIfNull(closures);
        JqEnvironment? scope = this;
        while (scope is not null)
        {
            budget.CheckCancellation();
            if (scope._isFunction && scope._name is not null)
            {
                var key = (scope._name, scope._arity);
                if (!closures.ContainsKey(key))
                {
                    budget.ReserveMetadata(256, 1);
                    if (scope._closure is not null)
                        closures[key] = scope._closure;
                    else if (scope._definition is not null)
                        closures[key] = new JqUserClosure(scope._definition, scope);
                }
            }
            scope = scope._parent;
        }
    }
}
