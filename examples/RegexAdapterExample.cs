using System;
using System.Threading;
using System.Threading.Tasks;
using Lokad.Jq;

namespace Lokad.Jq.Examples;

// Application source glue, compiled by the embedding application.
internal static class RegexAdapterExample
{
    internal static async Task<int> RunAsync(IJqHost host, IMemoryBudget memoryBudget,
        IJqRegexEngine regexEngine, CancellationToken cancellationToken)
    {
        const string filter = "[test(\"item-[0-9]+\"), "
            + "(match(\"item-(?<id>[0-9]+)\") | {offset, length, id: .captures[0].string}), "
            + "capture(\"item-(?<id>[0-9]+)\")]";
        if (!JqCommandInvocation.TryCreateWithStandardDescriptors(
            "jq", ["-c", filter], [], memoryBudget, out var invocation))
            return 5;
        using (invocation)
        {
            var command = Jq.TryParse(invocation)
                ?? throw new InvalidOperationException("Expected a jq command.");
            return await command.ExecuteAsync(host, regexEngine, cancellationToken).ConfigureAwait(false);
        }
    }
}
