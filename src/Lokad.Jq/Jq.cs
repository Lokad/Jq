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
        JqExecutor.ExecuteAsync(host, _invocation, cancellationToken);

    private static bool IsJqName(string name) => name is "jq" || name.EndsWith("/jq");
}
