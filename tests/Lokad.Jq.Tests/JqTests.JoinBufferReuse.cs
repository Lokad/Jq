using System.Globalization;
using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Jq_RepeatedJoinsReuseBoundedBuilderAdmissionAndKeepIndependentResults(bool retained)
    {
        const int count = 1000;
        var root = new BenchmarkMemoryBudget(4 * 1024 * 1024);
        var memory = new CountingMemoryBudget(root);
        var host = new MockFileSystem();
        string rows = "range(1000) | [tostring,\"é😀\"] | join(\"|\")";
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nr", retained ? "[" + rows + "][]" : rows)));
        Assert.Equal(0, await command.ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Equal(string.Concat(Enumerable.Range(0, count).Select(index =>
            index.ToString(CultureInfo.InvariantCulture) + "|é😀\n")), host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        // Include cold scalar rendering and setup, without a reservation per row.
        Assert.InRange(memory.Counts.Reservations, 1, 64);
        Assert.Equal(0, memory.Counts.SameSizeResizes);
        Assert.Equal(0, root.ReservedBytes);
    }
}
