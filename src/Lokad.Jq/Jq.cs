using System;
using System.Threading;
using System.Threading.Tasks;

namespace Lokad.Jq;

/// <summary>An embeddable jq command whose IO is supplied by the caller.</summary>
public sealed class Jq
{
    private readonly JqCommandInvocation _invocation;

    private Jq(JqCommandInvocation invocation)
    {
        _invocation = invocation;
    }

    /// <summary>Returns null for another command name; jq argument errors are reported on execution.</summary>
    public static Jq? TryParse(JqCommandInvocation invocation)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        invocation.CheckLive();
        if (!IsJqName(invocation.CommandName))
            return null;

        return new Jq(invocation);
    }

    /// <summary>Executes with host-mediated IO; cancellation propagates to the caller.</summary>
    public Task<int> ExecuteAsync(IJqHost host, CancellationToken cancellationToken) =>
        ExecuteAsync(host, JqUtf8RegexEngine.Instance, cancellationToken);

    /// <summary>Executes using a borrowed regex engine.</summary>
    public Task<int> ExecuteAsync(IJqHost host, IJqRegexEngine regexEngine, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(regexEngine);
        _invocation.CheckLive();
        return JqExecutor.ExecuteAsync(host, _invocation, regexEngine, _invocation.MemoryBudget, cancellationToken);
    }

    /// <summary>Executes using a borrowed host memory allowance and the default regex engine.</summary>
    public Task<int> ExecuteAsync(IJqHost host, IMemoryBudget memoryBudget, CancellationToken cancellationToken) =>
        ExecuteAsync(host, JqUtf8RegexEngine.Instance, memoryBudget, cancellationToken);

    /// <summary>Executes using a borrowed shared memory allowance and a borrowed regex engine.</summary>
    /// <remarks>The execution owns a child scope until all evaluation, writes and descriptor cleanup finish.</remarks>
    public Task<int> ExecuteAsync(IJqHost host, IJqRegexEngine regexEngine,
        IMemoryBudget memoryBudget, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(regexEngine);
        ArgumentNullException.ThrowIfNull(memoryBudget);
        _invocation.CheckLive();
        return JqExecutor.ExecuteAsync(host, _invocation, regexEngine, memoryBudget, cancellationToken);
    }

    private static bool IsJqName(string name) => name is "jq" || name.EndsWith("/jq");
}
