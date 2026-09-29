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

    // Explicit host clock for time builtins; null means the capability is absent.
    public JqClock? Clock { get; init; }

    // Diagnostic byte queue for debug and stderr filters. Filters enqueue
    // synchronously while the executor drains to the host between outputs,
    // so retained bytes stay bounded by one output plus its diagnostics.
    private readonly List<ReadOnlyMemory<byte>> _stderr = new();

    internal bool HasPendingStderr => _stderr.Count > 0;

    internal void EmitStderr(byte[] chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        Budget.ChargeBytes(chunk.Length);
        _stderr.Add(chunk);
    }

    internal List<ReadOnlyMemory<byte>> TakePendingStderr()
    {
        var taken = new List<ReadOnlyMemory<byte>>(_stderr);
        _stderr.Clear();
        return taken;
    }

    public void Dispose() => Regexes.Dispose();
}
