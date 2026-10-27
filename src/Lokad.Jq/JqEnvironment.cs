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
// captured parent, so its closure captures that node once without
// mutable cells or statics, and recursion resolves through the same path.
internal sealed class JqEnvironment
{
    private readonly IReadOnlyDictionary<string, JsonNode?> _root;
    private readonly JqEnvironment? _parent;
    private readonly string? _name;
    private readonly JsonNode? _value;
    private readonly JqFilterClosure? _filter;
    private readonly JqUserClosure? _closure;
    private readonly JqEnvironment? _moduleScope;
    private readonly object? _label;

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
    }

    private JqEnvironment(JqEnvironment parent, JqFunctionDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(definition);
        _root = parent._root;
        _parent = parent;
        _name = definition.Name;
        _closure = new JqUserClosure(definition, this);
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
    }

    internal static JqEnvironment CreateRoot(IReadOnlyDictionary<string, JsonNode?> variables) =>
        new(variables);

    private JqEnvironment(JqEnvironment parent, object label, uint labelId)
    {
        _root = parent._root;
        _parent = parent;
        _label = label;
        LabelId = labelId;
    }

    // The environment node identifies one activation of a lexical label.
    internal uint LabelId { get; }
    internal JqEnvironment ExtendLabel(object label, uint labelId) => new(this, label, labelId);

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

    internal JqEnvironment ExtendFunction(JqFunctionDefinition definition) =>
        new(this, definition);

    internal JqEnvironment ExtendModule(string alias, JqEnvironment moduleScope) =>
        new(this, alias, moduleScope);

    private JqEnvironment(JqEnvironment parent, string name, JqUserClosure closure)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(closure);
        _root = parent._root;
        _parent = parent;
        _name = name;
        _closure = closure;
    }

    internal JqEnvironment ExtendClosure(string name, JqUserClosure closure) =>
        new(this, name, closure);

    internal bool TryGetValue(string name, out JsonNode? value)
    {
        JqEnvironment? scope = this;
        while (scope is not null)
        {
            if (scope._closure is null && scope._filter is null && scope._moduleScope is null
                && scope._name is not null && string.Equals(scope._name, name, StringComparison.Ordinal))
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
            if (scope._filter is { } found && string.Equals(scope._name, name, StringComparison.Ordinal))
            {
                filter = found;
                return true;
            }
            scope = scope._parent;
        }

        filter = null;
        return false;
    }

    private bool TryGetFunction(ReadOnlySpan<char> name, int arity, out JqUserClosure? closure)
    {
        JqEnvironment? scope = this;
        while (scope is not null)
        {
            if (scope._closure is { } found
                && found.Definition.Arity == arity
                && name.SequenceEqual(scope._name.AsSpan()))
            {
                closure = found;
                return true;
            }
            scope = scope._parent;
        }

        closure = null;
        return false;
    }

    internal bool TryGetModule(ReadOnlySpan<char> alias, out JqEnvironment? moduleScope)
    {
        JqEnvironment? scope = this;
        while (scope is not null)
        {
            if (scope._moduleScope is { } found && alias.SequenceEqual(scope._name.AsSpan()))
            {
                moduleScope = found;
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
        ReadOnlySpan<char> remaining = qualifiedName;
        JqEnvironment scope = this;
        while (true)
        {
            int separator = remaining.IndexOf("::");
            if (separator < 0)
                return scope.TryGetFunction(remaining, arity, out closure);
            if (separator == 0 || separator + 2 == remaining.Length
                || !scope.TryGetModule(remaining[..separator], out JqEnvironment? moduleScope)
                || moduleScope is null)
            {
                closure = null;
                return false;
            }
            scope = moduleScope;
            remaining = remaining[(separator + 2)..];
        }
    }

    // Collect visible definitions only when compilation needs them. Nearest
    // bindings win; an environment without functions needs no dictionary.
    internal Dictionary<(string Name, int Arity), JqFunctionDefinition>? CollectFunctions(JqBudget budget)
    {
        Dictionary<(string Name, int Arity), JqFunctionDefinition>? definitions = null;
        JqEnvironment? scope = this;
        while (scope is not null)
        {
            budget.CheckCancellation();
            if (scope._closure is { } closure && scope._name is not null)
            {
                var key = (scope._name, closure.Definition.Arity);
                if (definitions is null || !definitions.ContainsKey(key))
                {
                    budget.ReserveMetadata(128, 1);
                    (definitions ??= new())[key] = closure.Definition;
                }
            }
            scope = scope._parent;
        }
        return definitions;
    }

    internal void CollectFunctionClosures(IDictionary<(string Name, int Arity), JqUserClosure> closures, JqBudget budget)
    {
        ArgumentNullException.ThrowIfNull(closures);
        JqEnvironment? scope = this;
        while (scope is not null)
        {
            budget.CheckCancellation();
            if (scope._closure is { } closure && scope._name is not null)
            {
                var key = (scope._name, closure.Definition.Arity);
                if (!closures.ContainsKey(key))
                {
                    budget.ReserveMetadata(224, 1);
                    closures[key] = closure;
                }
            }
            scope = scope._parent;
        }
    }
}
