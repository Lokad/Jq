using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace Lokad.Jq;

internal sealed class JqContext(
    IReadOnlyDictionary<string, JsonNode?> variables,
    JqProgramSource programSource,
    JqBudget budget) : IDisposable
{
    public IReadOnlyDictionary<string, JsonNode?> Variables { get; } = variables;
    public JqProgramSource ProgramSource { get; } = programSource ?? throw new ArgumentNullException(nameof(programSource));
    public JqEnvironment RootEnvironment { get; } = JqEnvironment.CreateRoot(variables);
    public JqBudget Budget { get; } = budget;
    public JqRuntime Runtime { get; } = new(budget);
    public JqRegexCache Regexes { get; } = new(budget, TimeProvider.System);

    public void Dispose() => Regexes.Dispose();
}
