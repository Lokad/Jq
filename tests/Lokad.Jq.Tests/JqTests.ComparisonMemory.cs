using Lokad.Jq.Benchmarking;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Fact]
    public async Task Jq_LongObjectComparisonStreamReusesBoundedAllowance()
    {
        var obj = WideObject(128);
        string record = new JqObject(NativeFixtures.Budget()) { ["left"] = obj, ["right"] = obj.DeepClone() }.ToJsonString() + "\n";
        const int count = 300;
        var root = new BenchmarkMemoryBudget(128 * 1024);
        var host = new MockFileSystem();
        host.SetStandardInput(string.Concat(Enumerable.Repeat(record, count)));
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", ".left <= .right")));
        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, root, CancellationToken.None));
        Assert.Equal(string.Concat(Enumerable.Repeat("true\n", count)), host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, root.ReservedBytes);
        Assert.InRange(root.PeakBytes, 1, 128 * 1024);
    }

    [Theory]
    [InlineData("try (.left < .right) catch \"caught\"")]
    [InlineData("(.left < .right)?")]
    [InlineData("try (.left | sort) catch \"caught\"")]
    public async Task Jq_ComparisonScratchRefusalPreservesPrefixAndCleanup(string operation)
    {
        const long limit = 8 * 1024 * 1024;
        var root = new BenchmarkMemoryBudget(limit);
        var obj = WideObject(1000);
        var input = new JqObject(NativeFixtures.Budget()) { ["left"] = new JqArray(obj, obj.DeepClone()), ["right"] = new JqArray(obj.DeepClone(), obj.DeepClone()) };
        var host = new MockFileSystem();
        host.AddFile("/input", input.ToJsonString());
        host.AddFile("/later", "0");
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", "\"ready\", (" + operation + ")", "/input", "/later")));
        IMemoryReservation? pressure = null;
        host.BeforeByteAppend = () =>
        {
            if (pressure is null) Assert.True(root.TryReserve(limit - root.ReservedBytes, out pressure));
        };
        Assert.Equal(5, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, root, CancellationToken.None));
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
        Assert.Equal(0, await laterCommand.ExecuteAsync(later, Utf8RegexAdapter.Instance, root, CancellationToken.None));
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
        using (var budget = new JqBudget(Utf8RegexAdapter.Instance, counted, CancellationToken.None))
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
        var memory = new AdmissionCallbackBudget(root, () =>
        {
            if (failure == "cancellation") cancellation.Cancel();
            else if (failure == "host") throw new ArgumentException("host contract sentinel");
            else if (failure == "allocation") throw new OutOfMemoryException("allocation sentinel");
        });
        using (var budget = new JqBudget(Utf8RegexAdapter.Instance, memory, cancellation.Token))
        using (var comparer = new JqComparer(budget))
        {
            JqValue?[] source = [WideObject(16), WideObject(16)];
            var exception = Record.Exception(() => JqComparer.OrderBy(source,
                static node => node, (IComparer<JqValue?>)comparer).ToArray());
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
    public void Jq_ComparisonScratchPreflightsOverlappingReplacementBuffers()
    {
        var root = new BenchmarkMemoryBudget(4 * 64 * IntPtr.Size);
        var counted = new CountingMemoryBudget(root);
        using (var budget = new JqBudget(Utf8RegexAdapter.Instance, counted, CancellationToken.None))
        using (var comparer = new JqComparer(budget))
        {
            Assert.Equal(0, comparer.Compare(WideObject(64), WideObject(64)));
            Assert.Equal(2 * 64 * IntPtr.Size, root.ReservedBytes);
            var failure = Assert.Throws<JqQuotaException>(() => comparer.Compare(WideObject(128), WideObject(128)));
            Assert.True(failure.IsMemoryAdmission);
            Assert.Equal(4 * 64 * IntPtr.Size, root.PeakBytes);
            Assert.Equal((128 + 64) * IntPtr.Size, root.ReservedBytes);
            Assert.Equal(1, counted.Counts.Refusals);
        }
        Assert.Equal(0, root.ReservedBytes);
    }

    [Fact]
    public void Jq_ComparisonChecksCancellationForBorrowedArraysStringsAndMinima()
    {
        using var cancellation = new CancellationTokenSource();
        using var budget = new JqBudget(Utf8RegexAdapter.Instance, cancellation.Token);
        var runtime = new JqRuntime(budget);
        var array = new JqArray(1, 2, 3);
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => runtime.Compare(array, array));
        Assert.Throws<OperationCanceledException>(() => runtime.Compare(NativeFixtures.Create("abc"), NativeFixtures.Create("abc")));
        Assert.Throws<OperationCanceledException>(() => runtime.MinMax(array, false));
        var text = NativeFixtures.Create("abc");
        Assert.Throws<OperationCanceledException>(() => text.Compare(text, cancellation.Token));
    }

    private static JqObject WideObject(int count)
    {
        var obj = new JqObject(NativeFixtures.Budget());
        for (int index = 0; index < count; index++) obj["k" + index.ToString("D5", CultureInfo.InvariantCulture)] = index;
        return obj;
    }

    private sealed class AdmissionCallbackBudget(IMemoryBudget inner, Action onAdmission) : IMemoryBudget
    {
        public IMemoryScope CreateScope() => new Scope(inner.CreateScope(), onAdmission);

        public bool TryReserve(long bytes, [NotNullWhen(true)] out IMemoryReservation? reservation) =>
            Reserve(inner, onAdmission, bytes, out reservation);

        private static bool Reserve(IMemoryBudget budget, Action callback, long bytes,
            [NotNullWhen(true)] out IMemoryReservation? reservation)
        {
            if (!budget.TryReserve(bytes, out var owned))
            {
                reservation = null;
                callback();
                return false;
            }
            reservation = new Reservation(owned, callback);
            callback();
            return true;
        }

        private sealed class Scope(IMemoryScope inner, Action callback) : IMemoryScope
        {
            public IMemoryScope CreateScope() => new Scope(inner.CreateScope(), callback);
            public bool TryReserve(long bytes, [NotNullWhen(true)] out IMemoryReservation? reservation) =>
                Reserve(inner, callback, bytes, out reservation);
            public void Dispose() => inner.Dispose();
        }

        private sealed class Reservation(IMemoryReservation inner, Action callback) : IMemoryReservation
        {
            public long Bytes => inner.Bytes;
            public bool TryResize(long bytes)
            {
                bool admitted = inner.TryResize(bytes);
                callback();
                return admitted;
            }
            public void Dispose() => inner.Dispose();
        }
    }
}
