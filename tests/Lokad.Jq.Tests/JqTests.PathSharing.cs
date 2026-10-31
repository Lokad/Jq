using System.Text;
using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Fact]
    public void Jq_PathUpdatesCopyOnlyModifiedContainersAndPreserveAliases()
    {
        var memory = new BenchmarkMemoryBudget(128 * 1024);
        using (var context = new JqContext(new Dictionary<string, JqValue?>(), new JqBudget(null, memory, CancellationToken.None)))
        {
            var original = Assert.IsType<JqObject>(context.Runtime.ReadJsonValue(
                "{\"left\":{\"changed\":[1,2],\"keep\":[3]},\"right\":[4,5],\"alias\":null}"u8, out _));
            original["alias"] = original["left"];
            JqValueSegment[] path = [new KeySegment(JqString.FromUtf8("left"u8, context.Budget)),
                new KeySegment(JqString.FromUtf8("changed"u8, context.Budget)), new IndexSegment(0, false)];
            var updated = Assert.IsType<JqObject>(JqPathUpdates.SetPath(original, path, JqScalar.Create(7), context));
            Assert.NotSame(original, updated);
            Assert.NotSame(original["left"], updated["left"]);
            Assert.NotSame(original["left"]?["changed"], updated["left"]?["changed"]);
            Assert.Same(original["right"], updated["right"]);
            Assert.Same(original["alias"], updated["alias"]);
            Assert.Same(original["left"]?["keep"], updated["left"]?["keep"]);
            var independent = Assert.IsType<JqObject>(JqPathUpdates.SetPath(updated, path, JqScalar.Create(9), context));
            Assert.Equal(1L, original["left"]?["changed"]?[0]?.GetValue<long>());
            Assert.Equal(1L, independent["alias"]?["changed"]?[0]?.GetValue<long>());
            Assert.Equal(7, updated["left"]?["changed"]?[0]?.GetValue<int>());
            Assert.Equal(9, independent["left"]?["changed"]?[0]?.GetValue<int>());
        }
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public void Jq_SparseUpdateBorrowsTheAlreadyFundedLargeSibling()
    {
        const long limit = 4 * 1024 * 1024;
        var memory = new BenchmarkMemoryBudget(limit);
        using (var context = new JqContext(new Dictionary<string, JqValue?>(), new JqBudget(null, memory, CancellationToken.None)))
        {
            byte[] json = Encoding.UTF8.GetBytes("{\"small\":[1],\"large\":[" + string.Join(',', Enumerable.Range(0, 10000)) + "]}");
            var original = Assert.IsType<JqObject>(context.Runtime.ReadJsonValue(json, out _));
            Assert.True(memory.TryReserve(limit - memory.ReservedBytes - 4096, out var pressure));
            using (pressure)
            {
                long before = memory.ReservedBytes;
                JqValueSegment[] path = [new KeySegment(JqString.FromUtf8("small"u8, context.Budget)), new IndexSegment(0, false)];
                var updated = Assert.IsType<JqObject>(JqPathUpdates.SetPath(original, path, JqScalar.Create(2), context));
                Assert.Same(original["large"], updated["large"]);
                Assert.Equal(1L, original["small"]?[0]?.GetValue<long>());
                Assert.Equal(2, updated["small"]?[0]?.GetValue<int>());
                Assert.InRange(memory.ReservedBytes - before, 0, 4096);
            }
        }
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Jq_SpineCopyFailureLeavesBorrowedInputUnchanged(bool cancel)
    {
        const long limit = 4 * 1024 * 1024;
        var memory = new BenchmarkMemoryBudget(limit);
        using var cancellation = new CancellationTokenSource();
        bool armed = false;
        var hooked = new AdmissionCallbackBudget(memory, () => { if (armed && cancel) cancellation.Cancel(); });
        using (var context = new JqContext(new Dictionary<string, JqValue?>(), new JqBudget(null, hooked, cancellation.Token)))
        {
            byte[] json = Encoding.UTF8.GetBytes("[" + string.Join(',', Enumerable.Range(0, 10000)) + "]");
            var original = Assert.IsType<JqArray>(context.Runtime.ReadJsonValue(json, out _));
            Assert.True(memory.TryReserve(limit - memory.ReservedBytes - (cancel ? 512 * 1024 : 4096), out var pressure));
            using (pressure)
            {
                armed = true;
                if (cancel)
                    Assert.Equal(cancellation.Token, Assert.Throws<OperationCanceledException>(() =>
                        JqPathUpdates.SetPath(original, [new IndexSegment(0, false)], JqScalar.Create(7), context)).CancellationToken);
                else
                    Assert.True(Assert.Throws<JqQuotaException>(() =>
                        JqPathUpdates.SetPath(original, [new IndexSegment(0, false)], JqScalar.Create(7), context)).IsMemoryAdmission);
                Assert.Equal(10000, original.Count);
                Assert.Equal(0L, original[0]?.GetValue<long>());
                Assert.Equal(9999L, original[9999]?.GetValue<long>());
            }
        }
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public void Jq_SharedSpinesPreserveIterativeDeepPathsAndRecursiveCopyProtection()
    {
        using var context = new JqContext(new Dictionary<string, JqValue?>(), new JqBudget(CancellationToken.None));
        var path = Enumerable.Repeat<JqValueSegment>(new IndexSegment(0, false), 1000).ToArray();
        var result = JqPathUpdates.SetPath(null, path, JqScalar.Create(1), context);
        Assert.IsType<JqArray>(result);
        var updated = JqPathUpdates.SetPath(result, path, JqScalar.Create(2), context);
        Assert.Equal(1, JqPathReads.GetPath(result, path, context)?.GetValue<int>());
        Assert.Equal(2, JqPathReads.GetPath(updated, path, context)?.GetValue<int>());
        Assert.Throws<JqQuotaException>(() => JqPathUpdates.SetPath(null, [], result, context));
    }

    [Theory]
    // Independently checked against jq 1.8.2: unchanged branches keep identity,
    // while changing a selected branch invalidates its previous path value.
    [InlineData("{a:[1],b:[2]}|.b as $b|.a[0]=3|.b|path($b)", "[]\n")]
    [InlineData("{a:[1],b:[2]}|.a as $a|.a[0]=3|.a|try path($a) catch .", "\"Invalid path expression with result [1]\"\n")]
    [InlineData("{a:[1],b:[2]} as $original|$original|setpath([\"a\",0];3) as $one|setpath([\"b\",0];4) as $two|[$original,$one,$two]", "[{\"a\":[1],\"b\":[2]},{\"a\":[3],\"b\":[2]},{\"a\":[1],\"b\":[4]}]\n")]
    public async Task Jq_PathSharingPreservesUpstreamIdentityAndIndependentOutputs(string filter, string expected)
    {
        var host = new MockFileSystem();
        using var invocation = JqCommandInvocation.CreateWithStandardDescriptors("jq", ["-nc", filter], []);
        Assert.Equal(0, await Assert.IsType<Jq>(Jq.TryParse(invocation)).ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }
}
