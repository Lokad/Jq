using System;
using System.Threading;
using System.Threading.Tasks;

namespace Lokad.Jq;

/// <summary>An embeddable jq command whose IO is supplied by the caller.</summary>
public sealed class Jq
{
    private readonly JqInvocation _invocation;

    private Jq(JqInvocation invocation)
    {
        _invocation = invocation;
    }

    /// <summary>Returns null for another command name; jq argument errors are reported on execution.</summary>
    public static Jq? TryParse(JqCommandInvocation invocation)
    {
        if (!IsJqName(invocation.CommandName))
            return null;

        return new Jq(JqCommandLineParser.Parse(invocation.Arguments, invocation.CurrentDirectory, invocation.StdIn, invocation.StdOut, invocation.StdErr, invocation.Environment, invocation.Clock));
    }

    /// <summary>Executes with host-mediated IO; cancellation propagates to the caller.</summary>
    public Task<int> ExecuteAsync(IJqHost host, CancellationToken cancellationToken) =>
        ExecuteAsync(host, JqExecutionPolicy.Default, cancellationToken);

    /// <summary>Executes with an immutable resource policy and fresh counters; exhaustion reports status 5.</summary>
    public Task<int> ExecuteAsync(IJqHost host, JqExecutionPolicy policy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(policy);
        return JqExecutor.ExecuteAsync(host, _invocation, policy, null, cancellationToken);
    }

    /// <summary>Executes using a borrowed host memory allowance and the default work policy.</summary>
    public Task<int> ExecuteAsync(IJqHost host, IMemoryBudget memoryBudget, CancellationToken cancellationToken) =>
        ExecuteAsync(host, JqExecutionPolicy.Default, memoryBudget, cancellationToken);

    /// <summary>Executes using a borrowed shared memory allowance and independent work limits.</summary>
    /// <remarks>The execution owns a child scope until all evaluation, writes and descriptor cleanup finish.</remarks>
    public Task<int> ExecuteAsync(IJqHost host, JqExecutionPolicy policy,
        IMemoryBudget memoryBudget, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(memoryBudget);
        return JqExecutor.ExecuteAsync(host, _invocation, policy, memoryBudget, cancellationToken);
    }

    private static bool IsJqName(string name) => name is "jq" || name.EndsWith("/jq");
}
