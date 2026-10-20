using Lokad.Jq.Benchmarking;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json.Nodes;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("try (.left < .right) catch \"caught\"")]
    [InlineData("(.left < .right)?")]
    [InlineData("try (.left | sort) catch \"caught\"")]
    public async Task Jq_ComparisonScratchRefusalPreservesPrefixAndCleanup(string operation)
    {
        const long limit = 8 * 1024 * 1024;
        var root = new BenchmarkMemoryBudget(limit);
        var obj = WideObject(1000);
        var input = new JsonObject { ["left"] = new JsonArray(obj, obj.DeepClone()), ["right"] = new JsonArray(obj.DeepClone(), obj.DeepClone()) };
        var host = new MockFileSystem();
        host.AddFile("/input", input.ToJsonString());
        host.AddFile("/later", "0");
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", "\"ready\", (" + operation + ")", "/input", "/later")));
        IMemoryReservation? pressure = null;
        host.BeforeByteAppend = () =>
        {
            if (pressure is null) Assert.True(root.TryReserve(limit - root.ReservedBytes, out pressure));
        };
        Assert.Equal(5, await command.ExecuteAsync(host, root, CancellationToken.None));
        Assert.Equal("\"ready\"\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("jq: memory budget exceeded\n", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Single(host.ClosedDescriptors);
        Assert.Equal(0, host.OpenFileCount);
        Assert.NotNull(pressure);
        Assert.Equal(pressure.Bytes, root.ReservedBytes);
        pressure.Dispose();
        Assert.Equal(0, root.ReservedBytes);

        var later = new MockFileSystem();
        var laterCommand = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc", "1")));
        Assert.Equal(0, await laterCommand.ExecuteAsync(later, root, CancellationToken.None));
        Assert.Equal("1\n", later.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(later.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, root.ReservedBytes);
    }

    [Fact]
    public void Jq_ComparisonScratchReusesCapacityAndKeepsRecursiveBuffersCharged()
    {
        var left = WideObject(128);
        left["nested"] = WideObject(16);
        var right = left.DeepClone();
        var root = new BenchmarkMemoryBudget(32 * 1024);
        var counted = new CountingMemoryBudget(root);
        using (var budget = new JqBudget(JqUtf8RegexEngine.Instance, counted, CancellationToken.None))
        using (var comparer = new JqComparer(budget))
        {
            Assert.Equal(0, comparer.Compare(left, right));
            Assert.Equal(2 * (129 + 16) * IntPtr.Size, root.ReservedBytes);
            MemoryCallCounts first = counted.Counts;
            for (int index = 0; index < 100; index++) Assert.Equal(0, comparer.Compare(left, right));
            Assert.Equal(first, counted.Counts);
            Assert.InRange(first.Reservations + first.Resizes, 1, 4);
            Assert.Equal(0, first.BytesReads);
            Assert.Equal(0, first.SameSizeResizes);
        }
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData("refusal")]
    [InlineData("cancellation")]
    [InlineData("host")]
    [InlineData("allocation")]
    public void Jq_OrderingPreservesTerminalComparerFailures(string failure)
    {
        using var cancellation = new CancellationTokenSource();
        var root = new BenchmarkMemoryBudget(failure == "refusal" ? 0 : 32 * 1024);
        var memory = new ComparisonAdmissionBudget(root, () =>
        {
            if (failure == "cancellation") cancellation.Cancel();
            else if (failure == "host") throw new ArgumentException("host contract sentinel");
            else if (failure == "allocation") throw new OutOfMemoryException("allocation sentinel");
        });
        using (var budget = new JqBudget(JqUtf8RegexEngine.Instance, memory, cancellation.Token))
        using (var comparer = new JqComparer(budget))
        {
            JsonNode?[] source = [WideObject(16), WideObject(16)];
            var exception = Record.Exception(() => JqComparer.OrderBy(source,
                static node => node, (IComparer<JsonNode?>)comparer).ToArray());
            switch (failure)
            {
                case "refusal": Assert.True(Assert.IsType<JqQuotaException>(exception).IsMemoryAdmission); break;
                case "cancellation": Assert.Equal(cancellation.Token, Assert.IsType<OperationCanceledException>(exception).CancellationToken); break;
                case "host": Assert.IsType<ArgumentException>(Assert.IsType<JqHostFailureException>(exception).InnerException); break;
                case "allocation": Assert.IsType<OutOfMemoryException>(exception); break;
            }
        }
        Assert.Equal(0, root.ReservedBytes);
    }

    [Fact]
    public void Jq_ComparisonChecksCancellationForBorrowedArraysStringsAndMinima()
    {
        using var cancellation = new CancellationTokenSource();
        using var budget = new JqBudget(cancellation.Token);
        var runtime = new JqRuntime(budget);
        var array = new JsonArray(1, 2, 3);
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => runtime.Compare(array, array));
        Assert.Throws<OperationCanceledException>(() => runtime.Compare(JsonValue.Create("abc"), JsonValue.Create("abc")));
        Assert.Throws<OperationCanceledException>(() => runtime.MinMax(array, false));
        Assert.Throws<OperationCanceledException>(() => JqComparer.CompareStrings("abc", "abc", cancellation.Token));
    }

    private static JsonObject WideObject(int count)
    {
        var obj = new JsonObject();
        for (int index = 0; index < count; index++) obj["k" + index.ToString("D5", CultureInfo.InvariantCulture)] = index;
        return obj;
    }

    private sealed class ComparisonAdmissionBudget(IMemoryBudget inner, Action onAdmission) : IMemoryBudget
    {
        public IMemoryScope CreateScope() => inner.CreateScope();
        public bool TryReserve(long bytes, [NotNullWhen(true)] out IMemoryReservation? reservation)
        {
            onAdmission();
            return inner.TryReserve(bytes, out reservation);
        }
    }
}
