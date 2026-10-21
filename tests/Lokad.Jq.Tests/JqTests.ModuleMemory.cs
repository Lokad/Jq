using System.Text.Json.Nodes;
using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Fact]
    public async Task Jq_CachedIncludesAdmitRetainedBindingsAndRefundOnRefusal()
    {
        const long allowance = 32 * 1024 * 1024;
        var memory = new BenchmarkMemoryBudget(allowance);
        var host = new MockFileSystem();
        host.AddFile("/lib/m.jq", string.Concat(Enumerable.Range(0, 1000).Select(i => "def f" + i + ":" + i + ";")));
        using (var context = new JqContext(new Dictionary<string, JsonNode?>(),
            new JqBudget(JqUtf8RegexEngine.Instance, memory, CancellationToken.None)))
        {
            var loader = new JqModuleLoader(host, context.Budget, context.Runtime, context.RootEnvironment, ["/lib"], "/");
            var import = new JqModuleImport("m", null, false, null);
            _ = await loader.LoadMainImportsAsync([import], "/", CancellationToken.None);
            Assert.True(memory.TryReserve(allowance - memory.ReservedBytes - 65536, out var pressure));
            using (pressure)
            {
                var failure = await Assert.ThrowsAsync<JqQuotaException>(() => loader.LoadMainImportsAsync(
                    Enumerable.Repeat(import, 10).ToArray(), "/", CancellationToken.None));
                Assert.True(failure.IsMemoryAdmission);
                Assert.Equal(0, host.OpenFileCount);
            }
        }
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public async Task Jq_RepeatedIncludesBatchMetadataAdmissionAndKeepClosures()
    {
        var root = new BenchmarkMemoryBudget(32 * 1024 * 1024);
        var memory = new CountingMemoryBudget(root);
        var host = new MockFileSystem();
        host.AddFile("/lib/m.jq", string.Concat(Enumerable.Range(0, 1000).Select(i => "def f" + i + ":" + i + ";")));
        using (var context = new JqContext(new Dictionary<string, JsonNode?>(),
            new JqBudget(JqUtf8RegexEngine.Instance, memory, CancellationToken.None)))
        {
            var loader = new JqModuleLoader(host, context.Budget, context.Runtime, context.RootEnvironment, ["/lib"], "/");
            var env = await loader.LoadMainImportsAsync(Enumerable.Repeat(new JqModuleImport("m", null, false, null), 5).ToArray(),
                "/", CancellationToken.None);
            var result = new UserCallFilter("f999", 0, []).Evaluate(null, context, env).Single();
            Assert.Equal(999, JqRuntime.Number(result));
            Assert.InRange(memory.Counts.Reservations + memory.Counts.Resizes, 1, 1000);
        }
        Assert.Equal(0, root.ReservedBytes);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, memory.Counts.Refusals);
        Assert.Equal(0, memory.Counts.SameSizeResizes);
    }

    [Fact]
    public void Jq_ModuleExportCollectionObservesCancellation()
    {
        var env = JqEnvironment.CreateRoot(new Dictionary<string, JsonNode?>());
        var definition = new JqFunctionDefinition("f", [], new IdentityFilter());
        for (int i = 0; i < 10000; i++) env = env.ExtendFunction("f", 0, definition);
        using var cancellation = new CancellationTokenSource();
        using var budget = new JqBudget(cancellation.Token);
        var exports = new Dictionary<(string Name, int Arity), JqUserClosure>();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => env.CollectFunctionClosures(exports, budget));
        Assert.Empty(exports);
    }
    [Fact]
    public async Task Jq_ModuleSearchStopsBeforeUnneededEntries()
    {
        var memory = new BenchmarkMemoryBudget(128 * 1024);
        var host = new MockFileSystem();
        host.AddFile("/lib/m.jq", "def f: 42;");
        var metadata = new JsonObject { ["search"] = new JsonArray("/lib", new string('x', 1000000)) };
        using (var context = new JqContext(new Dictionary<string, JsonNode?>(),
            new JqBudget(JqUtf8RegexEngine.Instance, memory, CancellationToken.None)))
        {
            var loader = new JqModuleLoader(host, context.Budget, context.Runtime, context.RootEnvironment, [], "/");
            var env = await loader.LoadMainImportsAsync([new JqModuleImport("m", null, false, metadata)], "/", CancellationToken.None);
            Assert.Equal(42, JqRuntime.Number(new UserCallFilter("f", 0, []).Evaluate(null, context, env).Single()));
            Assert.Single(host.ClosedDescriptors);
        }
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public async Task Jq_NullModuleSearchDoesNotCopyItsUnusedTail()
    {
        var search = new JsonArray();
        for (int i = 0; i < 100000; i++) search.Add((JsonNode?)null);
        var metadata = new JsonObject { ["optional"] = true, ["search"] = search };
        var root = new BenchmarkMemoryBudget(0);
        var memory = new CountingMemoryBudget(root);
        var host = new MockFileSystem();
        using (var context = new JqContext(new Dictionary<string, JsonNode?>(),
            new JqBudget(JqUtf8RegexEngine.Instance, memory, CancellationToken.None)))
        {
            var loader = new JqModuleLoader(host, context.Budget, context.Runtime, context.RootEnvironment, ["/lib"], "/");
            var import = new JqModuleImport("missing", null, false, metadata);
            long before = GC.GetAllocatedBytesForCurrentThread();
            Assert.Same(context.RootEnvironment,
                await loader.LoadMainImportsAsync([import], "/", CancellationToken.None));
            Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 32768);
        }
        Assert.Equal(0, memory.Counts.Reservations);
        Assert.Equal(0, host.ReadBytesCallCount);
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Jq_ModuleResolutionObservesCancellationBeforeScanning(bool path)
    {
        var search = new JsonArray();
        for (int i = 0; i < 100000; i++) search.Add("subdir");
        var import = path
            ? new JqModuleImport(string.Join('/', Enumerable.Repeat("a/b", 100000)), null, false, null)
            : new JqModuleImport("missing", null, false, new JsonObject { ["search"] = search });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var root = new BenchmarkMemoryBudget(0);
        var memory = new CountingMemoryBudget(root);
        using (var context = new JqContext(new Dictionary<string, JsonNode?>(),
            new JqBudget(JqUtf8RegexEngine.Instance, memory, cancellation.Token)))
        {
            var host = new MockFileSystem();
            var loader = new JqModuleLoader(host, context.Budget, context.Runtime, context.RootEnvironment, [], "/");
            long before = GC.GetAllocatedBytesForCurrentThread();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                loader.LoadMainImportsAsync([import], "/", cancellation.Token));
            Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 32768);
            Assert.Equal(0, host.OpenFileCount);
        }
        Assert.Equal(0, memory.Counts.Refusals);
        Assert.Equal(0, root.ReservedBytes);
    }

    [Fact]
    public async Task Jq_ModuleSearchPreflightsBeforeResolvingDirectories()
    {
        var search = new JsonArray();
        for (int i = 0; i < 100000; i++) search.Add("subdir");
        var import = new JqModuleImport("missing", null, false, new JsonObject { ["search"] = search });
        var root = new BenchmarkMemoryBudget(0);
        var memory = new CountingMemoryBudget(root);
        var host = new MockFileSystem();
        using (var context = new JqContext(new Dictionary<string, JsonNode?>(),
            new JqBudget(JqUtf8RegexEngine.Instance, memory, CancellationToken.None)))
        {
            var loader = new JqModuleLoader(host, context.Budget, context.Runtime, context.RootEnvironment, [], "/");
            long before = GC.GetAllocatedBytesForCurrentThread();
            var error = await Assert.ThrowsAsync<JqQuotaException>(() =>
                loader.LoadMainImportsAsync([import], "/", CancellationToken.None));
            Assert.True(error.IsMemoryAdmission);
            Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 32768);
        }
        Assert.Equal(1, memory.Counts.Refusals);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, root.ReservedBytes);
    }

}
