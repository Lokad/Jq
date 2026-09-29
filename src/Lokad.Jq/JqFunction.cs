using System;
using System.Collections.Generic;

namespace Lokad.Jq;

// A single `def` parameter: `$name` takes a JSON value stream evaluated once
// per call, while a bare `name` takes a filter closure re-evaluated per use.
internal sealed class JqFunctionParameter(string Name, bool IsValue)
{
    internal string Name { get; } = Name ?? throw new ArgumentNullException(nameof(Name));
    internal bool IsValue { get; } = IsValue;
}

// A parsed user-function definition. Instances are immutable and shared by
// every closure forged for the definition; recursion resolves through the
// definition environment carried by each closure, never through statics.
internal sealed class JqFunctionDefinition(
    string Name,
    IReadOnlyList<JqFunctionParameter> Parameters,
    JqFilter Body)
{
    internal string Name { get; } = Name ?? throw new ArgumentNullException(nameof(Name));
    internal IReadOnlyList<JqFunctionParameter> Parameters { get; } = Parameters ?? throw new ArgumentNullException(nameof(Parameters));
    internal JqFilter Body { get; } = Body ?? throw new ArgumentNullException(nameof(Body));

    internal int Arity => Parameters.Count;
}

// A filter argument paired with the caller environment active at the call
// site. Each use evaluates the filter afresh against the use-site input,
// so backtracking demand propagates exactly like upstream jq.
internal sealed class JqFilterClosure(JqFilter Filter, JqEnvironment Environment)
{
    internal JqFilter Filter { get; } = Filter ?? throw new ArgumentNullException(nameof(Filter));
    internal JqEnvironment Environment { get; } = Environment ?? throw new ArgumentNullException(nameof(Environment));
}

// A resolved user-function reference: the definition plus the immutable
// environment captured when its `def` executed. Nested closures observe the
// bindings visible at their own definition site; later redefinitions never
// leak into older closures.
internal sealed class JqUserClosure(JqFunctionDefinition Definition, JqEnvironment Environment)
{
    internal JqFunctionDefinition Definition { get; } = Definition ?? throw new ArgumentNullException(nameof(Definition));
    internal JqEnvironment Environment { get; } = Environment ?? throw new ArgumentNullException(nameof(Environment));
}
