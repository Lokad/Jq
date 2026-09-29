using System;
using System.Collections.Generic;

namespace Lokad.Jq;

// Binding patterns for `as`: plain variables, positional array patterns,
// keyed object patterns, and value aliases. Alternatives (`?//`) live in
// ordered lists on the binding filter, not in the nodes themselves.
internal abstract record BindingPattern
{
    internal abstract void CollectBoundNames(ICollection<string> names);
}

internal sealed record VariablePattern(string Name) : BindingPattern
{
    internal override void CollectBoundNames(ICollection<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        names.Add(Name);
    }
}

internal sealed record ArrayPattern(IReadOnlyList<BindingPattern> Items) : BindingPattern
{
    internal override void CollectBoundNames(ICollection<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        foreach (BindingPattern item in Items)
            item.CollectBoundNames(names);
    }
}

internal sealed record ObjectPatternProperty(JqFilter Key, BindingPattern Value);

internal sealed record ObjectPattern(IReadOnlyList<ObjectPatternProperty> Properties) : BindingPattern
{
    internal override void CollectBoundNames(ICollection<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        foreach (ObjectPatternProperty property in Properties)
            property.Value.CollectBoundNames(names);
    }
}

internal sealed record AliasPattern(string Name, BindingPattern Inner) : BindingPattern
{
    internal override void CollectBoundNames(ICollection<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        names.Add(Name);
        Inner.CollectBoundNames(names);
    }
}
