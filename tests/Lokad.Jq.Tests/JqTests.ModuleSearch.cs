using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    // Explicit metadata replaces defaults in jq 1.8.2; invalid entries are skipped.
    [Theory]
    [InlineData("[]", false)]
    [InlineData("[\"/missing\"]", false)]
    [InlineData("\"/missing\"", false)]
    [InlineData("null", false)]
    [InlineData("false", false)]
    [InlineData("[null,\"/explicit\"]", true)]
    [InlineData("[false,\"/explicit\"]", true)]
    [InlineData("[{},0,[],\"/explicit\"]", true)]
    [InlineData("\"/explicit\"", true)]
    [InlineData("\"..\"", false)]
    [InlineData("[\"../..\",\"/explicit\"]", true)]
    [InlineData("[\"/explicit\",\"../..\"]", true)]
    public async Task Jq_ExplicitModuleSearchReplacesDefaultsAndSkipsInvalidEntries(string search, bool found)
    {
        foreach (bool data in new[] { false, true })
        {
            var host = new MockFileSystem();
            string suffix = data ? ".json" : ".jq";
            host.AddFile("/m" + suffix, data ? "1" : "def f:1;");
            host.AddFile("/lib/m" + suffix, data ? "2" : "def f:2;");
            host.AddFile("/explicit/m" + suffix, data ? "3" : "def f:3;");
            string program = "import \"m\" as " + (data ? "$m" : "m") + " {search:" + search + "}; "
                + (data ? "$m" : "m::f");
            var memory = new BenchmarkMemoryBudget(1024 * 1024);
            var command = Assert.IsType<Jq>(Jq.TryParse(BuildModuleInvocation("-nc", "-L", "/lib", program)));
            Assert.Equal(found ? 0 : 3, await command.ExecuteAsync(host, memory, CancellationToken.None));
            Assert.Equal(found ? (data ? "[3]\n" : "3\n") : "", host.GetOutput(JqFileDescriptor.StdOut));
            Assert.Equal(found ? "" : "jq: module not found: m\n", host.GetOutput(JqFileDescriptor.StdErr));
            Assert.Equal(found ? 1 : 0, host.ClosedDescriptors.Count);
            Assert.Equal(0, host.OpenFileCount);
            Assert.Equal(0, memory.ReservedBytes);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Jq_InvalidModuleSearchKeepsOptionalAndRequiredOutcomes(bool data, bool optional)
    {
        var host = new MockFileSystem();
        var memory = new BenchmarkMemoryBudget(128 * 1024);
        string filter = "import \"m\" as " + (data ? "$m" : "m")
            + " {search:\"..\",optional:" + (optional ? "true" : "false") + "};42";
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildModuleInvocation("-nc", filter)));
        Assert.Equal(optional ? 0 : 3, await command.ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Equal(optional ? "42\n" : "", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal(optional ? "" : "jq: module not found: m\n", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.ClosedDescriptors);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Jq_DefaultModuleSearchUsesImporterThenLibraries(bool local)
    {
        var host = new MockFileSystem();
        if (local) host.AddFile("/m.jq", "def f:1;");
        host.AddFile("/lib/m.jq", "def f:2;");
        var result = await RunModulesAsync(host, "-nc", "-L", "/lib", "import \"m\" as m; m::f");
        Assert.Equal((0, local ? "1\n" : "2\n", ""), result);
        Assert.Single(host.ClosedDescriptors);
    }
}
