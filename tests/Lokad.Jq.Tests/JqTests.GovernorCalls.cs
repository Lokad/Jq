using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData(false, 85, 100)]
    [InlineData(false, 85, 1000)]
    [InlineData(false, 128, 100)]
    [InlineData(false, 128, 1000)]
    [InlineData(true, 97, 100)]
    [InlineData(true, 97, 1000)]
    [InlineData(true, 128, 100)]
    [InlineData(true, 128, 1000)]
    public async Task Jq_GovernedHotCursorTextReusesBoundedCredit(bool stream, int width, int count)
    {
        var root = new TestMemoryBudget(64 * 1024);
        var memory = new CountingMemoryBudget(root);
        var host = new MockFileSystem();
        string quoted = "\"" + new string('a', width) + "\"";
        string expected = string.Concat(Enumerable.Repeat((stream ? "" : "\u001e") + quoted + "\n", count));
        host.SetStandardInput(stream ? "[" + string.Join(',', Enumerable.Repeat(quoted, count)) + "]" : expected);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", stream ? "--stream" : "--seq", "-c",
            stream ? ".[1] // empty" : ".")));
        Assert.Equal(0, await command.ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Equal(Encoding.UTF8.GetBytes(expected), host.GetOutputBytes(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutputBytes(JqFileDescriptor.StdErr));
        Assert.Equal(count, host.AppendCallCount);
        Assert.InRange(memory.Counts.Reservations + memory.Counts.Resizes, 1, 100);
        Assert.Equal(0, memory.Counts.BytesReads);
        Assert.Equal(0, memory.Counts.SameSizeResizes);
        Assert.Equal(0, root.ReservedBytes);
    }

    [Fact]
    public void Jq_GovernedCursorCreditWarmsWithoutPrepayingAndKeepsRetainedTreesCharged()
    {
        const int limit = 16 * 1024;
        var root = new TestMemoryBudget(limit);
        var memory = new CountingMemoryBudget(root);
        using (var storage = new JqStorage(memory))
        {
            storage.ReconcileCursor(0);
            Assert.Equal(0, memory.Counts.Reservations);
            storage.ReserveBytes(1024);
            storage.ReconcileCursor(0);
            Assert.Equal(256, root.ReservedBytes);
            for (int i = 0; i < 31; i++)
            {
                storage.ReserveBytes(64);
                storage.ReconcileCursor(0);
            }
            storage.ReserveBytes(512);
            storage.ReconcileCursor(0);
            Assert.Equal(256, root.ReservedBytes);
            storage.ReserveBytes(64);
            storage.ReconcileCursor(0);
            Assert.Equal(256, root.ReservedBytes); // Warming alone requests no additional credit.
            storage.ReserveBytes(1024);
            storage.ReconcileCursor(256);
            Assert.Equal(256, storage.UsedBytes);
            Assert.Equal(768, root.ReservedBytes);
            MemoryCallCounts admitted = memory.Counts;
            storage.ReserveBytes(512);
            storage.ReconcileCursor(256);
            Assert.Equal(admitted, memory.Counts);
            storage.ReconcileCursor(0);
            Assert.Equal(512, root.ReservedBytes);
            Assert.True(root.TryReserve(limit - 512, out var competing));
            using (competing)
            {
                Assert.False(root.TryReserve(1, out var refused));
                Assert.Null(refused);
                storage.Dispose();
                Assert.Equal(competing.Bytes, root.ReservedBytes);
            }
        }
        Assert.Equal(0, root.ReservedBytes);
    }

    [Fact]
    public void Jq_GovernedPressuredCursorKeepsSmallerCreditAfterPaddingRefusal()
    {
        var root = new TestMemoryBudget(7000);
        var memory = new CountingMemoryBudget(root);
        using (var storage = new JqStorage(memory))
        {
            for (int i = 0; i < 32; i++)
            {
                storage.ReserveBytes(64);
                storage.ReconcileCursor(0);
            }
            storage.ReserveBytes(4096);
            storage.ReserveBytes(1);
            Assert.Equal(1, memory.Counts.Refusals);
            storage.ReconcileCursor(1000);
            Assert.Equal(1000, storage.UsedBytes);
            Assert.Equal(1256, root.ReservedBytes);
        }
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData(false, 100)]
    [InlineData(false, 1000)]
    [InlineData(true, 100)]
    [InlineData(true, 1000)]
    public async Task Jq_GovernedCursorScalarEventsReuseBoundedCredit(bool stream, int count)
    {
        var root = new TestMemoryBudget(32 * 1024);
        var memory = new CountingMemoryBudget(root);
        var host = new MockFileSystem();
        string expected = stream
            ? string.Concat(Enumerable.Range(0, count).Select(value => value.ToString(CultureInfo.InvariantCulture) + "\n"))
            : string.Concat(Enumerable.Repeat("\u001e1\n", count));
        host.SetStandardInput(stream ? "[" + string.Join(',', Enumerable.Range(0, count)) + "]" : expected);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", stream ? "--stream" : "--seq", "-c",
            stream ? ".[1] // empty" : ".")));
        Assert.Equal(0, await command.ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutputBytes(JqFileDescriptor.StdErr));
        Assert.InRange(memory.Counts.Reservations + memory.Counts.Resizes, 1, 80);
        Assert.Equal(0, memory.Counts.BytesReads);
        Assert.Equal(0, memory.Counts.SameSizeResizes);
        Assert.Equal(0, root.ReservedBytes);
    }

    [Fact]
    public void Jq_GovernedCursorReconciliationKeepsOnlyBoundedExistingCredit()
    {
        var root = new TestMemoryBudget(16 * 1024);
        var memory = new CountingMemoryBudget(root);
        using (var storage = new JqStorage(memory))
        {
            storage.ReconcileCursor(0);
            Assert.Equal(0, memory.Counts.Reservations);
            storage.ReserveBytes(4096);
            storage.ReserveBytes(1);
            storage.ReconcileCursor(1000);
            Assert.Equal(1000, storage.UsedBytes);
            Assert.Equal(1256, root.ReservedBytes);
            MemoryCallCounts reconciled = memory.Counts;
            storage.ReserveBytes(200);
            storage.ReconcileCursor(1000);
            Assert.Equal(reconciled, memory.Counts);
            storage.ReconcileCursor(0);
            Assert.Equal(0, storage.UsedBytes);
            Assert.Equal(256, root.ReservedBytes);
            Assert.Throws<JqQuotaException>(() => storage.ReconcileCursor(20000));
            Assert.Equal(0, storage.UsedBytes);
            Assert.Equal(256, root.ReservedBytes);
        }
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(1000)]
    public async Task Jq_GovernedGrowingRegexCallsFollowSubjectCapacity(int count)
    {
        var root = new TestMemoryBudget(4 * 1024 * 1024);
        var memory = new CountingMemoryBudget(root);
        var host = new MockFileSystem();
        host.SetStandardInput("[" + string.Join(',', Enumerable.Range(1, count).Select(length =>
            "\"" + new string('a', length) + "\"")) + "]");
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", ".[] | test(\"a\")")));
        MemoryCallCounts? firstOutput = null;
        host.BeforeByteAppend = () => firstOutput ??= memory.Counts;

        Assert.Equal(0, await command.ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Equal(string.Concat(Enumerable.Repeat("true\n", count)), host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutputBytes(JqFileDescriptor.StdErr));
        Assert.NotNull(firstOutput);
        Assert.InRange(memory.Counts.Resizes - firstOutput.Value.Resizes, 1, 32);
        Assert.Equal(0, memory.Counts.BytesReads);
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(1000)]
    public async Task Jq_GovernedEmptyStderrKeepsHostEffectsWithoutAdmissionChurn(int count)
    {
        var root = new TestMemoryBudget(64 * 1024);
        var memory = new CountingMemoryBudget(root);
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nr",
            "range(" + count.ToString(CultureInfo.InvariantCulture) + ") | \"\" | stderr")));

        Assert.Equal(0, await command.ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Equal(Enumerable.Repeat((byte)'\n', count).ToArray(), host.GetOutputBytes(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutputBytes(JqFileDescriptor.StdErr));
        Assert.Equal(2 * count, host.AppendCallCount);
        Assert.InRange(memory.Counts.Reservations + memory.Counts.Resizes, 1, 32);
        Assert.Equal(0, memory.Counts.BytesReads);
        Assert.Equal(0, memory.Counts.SameSizeResizes);
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData(false, 100)]
    [InlineData(false, 1000)]
    [InlineData(true, 100)]
    [InlineData(true, 1000)]
    public async Task Jq_GovernedRegexCallsReuseBoundedOperationOwners(bool detailed, int count)
    {
        var root = new TestMemoryBudget(2 * 1024 * 1024);
        var memory = new CountingMemoryBudget(root);
        var host = new MockFileSystem();
        string filter = detailed ? "scan(\"a\")" : "range(" + count.ToString(CultureInfo.InvariantCulture) + ") | \"x\" | test(\"x\")";
        if (detailed) host.SetStandardInput("\"" + new string('a', count) + "\"");
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", detailed ? "-c" : "-nc", filter)));

        Assert.Equal(0, await command.ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Equal(string.Concat(Enumerable.Repeat(detailed ? "\"a\"\n" : "true\n", count)),
            host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.InRange(memory.Counts.Reservations, 1, 16);
        Assert.InRange(memory.Counts.Resizes, 0, 80);
        Assert.Equal(0, memory.Counts.BytesReads);
        Assert.Equal(0, root.ReservedBytes);
    }

    [Fact]
    public void Jq_GovernedSmallBurstsUseBoundedCreditWithoutPaddingColdOwners()
    {
        var root = new TestMemoryBudget(512);
        var memory = new CountingMemoryBudget(root);
        using (var storage = new JqStorage(memory))
        {
            for (int i = 0; i < 31; i++) storage.ReserveBytes(1);
            Assert.Equal(31, root.ReservedBytes);
            for (int i = 31; i < 300; i++) storage.ReserveBytes(1);
            Assert.Equal(300, storage.UsedBytes);
            Assert.InRange(root.ReservedBytes, 300, 555);
            Assert.InRange(memory.Counts.Reservations + memory.Counts.Resizes, 1, 40);
        }
        Assert.Equal(0, root.ReservedBytes);
    }

    [Fact]
    public void Jq_GovernedSmallBurstPaddingRefusalFallsBackOnce()
    {
        var root = new TestMemoryBudget(300);
        var memory = new CountingMemoryBudget(root);
        using (var storage = new JqStorage(memory))
        {
            for (int i = 0; i < 300; i++) storage.ReserveBytes(1);
            Assert.Equal(300, storage.UsedBytes);
            Assert.Equal(300, root.ReservedBytes);
            Assert.Equal(1, memory.Counts.Refusals);
            Assert.Throws<JqQuotaException>(() => storage.ReserveBytes(1));
            Assert.Equal(300, storage.UsedBytes);
            Assert.Equal(300, root.ReservedBytes);
        }
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(1000)]
    public async Task Jq_GovernedRawCapacityGrowsGeometricallyAndKeepsExactOutput(int count)
    {
        var root = new TestMemoryBudget(4 * 1024 * 1024);
        var memory = new CountingMemoryBudget(root);
        var host = new MockFileSystem();
        host.SetStandardInput("[" + string.Join(',', Enumerable.Range(1, count).Select(length =>
            "\"" + new string('x', length) + "\"")) + "]");
        // Bind input once; only the changing raw capacity grows during output.
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-r", ".[]")));
        MemoryCallCounts? firstOutput = null;
        host.BeforeByteAppend = () => firstOutput ??= memory.Counts;

        Assert.Equal(0, await command.ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Equal(string.Concat(Enumerable.Range(1, count).Select(length =>
            new string('x', length) + "\n")), host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.NotNull(firstOutput);
        Assert.InRange(memory.Counts.Resizes - firstOutput.Value.Resizes, 0, 32);
        Assert.Equal(0, memory.Counts.BytesReads);
        Assert.Equal(0, root.ReservedBytes);
    }

    [Fact]
    public async Task Jq_GovernedRawGrowthFallsBackOnceUnderSharedPressure()
    {
        const long limit = 64 * 1024;
        var root = new TestMemoryBudget(limit);
        var memory = new CountingMemoryBudget(root);
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "-r", "\"x\",\"xx\",\"xxx\"")));
        IMemoryReservation? pressure = null;
        int appended = 0;
        host.BeforeByteAppend = () =>
        {
            if (appended == 0)
                Assert.True(root.TryReserve(limit - root.ReservedBytes - 3, out pressure));
            else if (appended == 1)
            {
                Assert.NotNull(pressure);
                Assert.True(pressure.TryResize(pressure.Bytes - 2));
            }
            appended++;
        };

        Assert.Equal(0, await command.ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Equal("x\nxx\nxxx\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(1, memory.Counts.Refusals);
        Assert.NotNull(pressure);
        Assert.Equal(pressure.Bytes, root.ReservedBytes);
        pressure.Dispose();
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData(false, 100)]
    [InlineData(false, 1000)]
    [InlineData(true, 100)]
    [InlineData(true, 1000)]
    public async Task Jq_GovernedRecordReservationsReuseTheirAllowance(bool raw, int count)
    {
        var root = new TestMemoryBudget(64 * 1024);
        var memory = new CountingMemoryBudget(root);
        var host = new MockFileSystem();
        string line = raw ? "x\n" : "1\n";
        string content = string.Concat(Enumerable.Repeat(line, count));
        host.SetStandardInput(content);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", raw ? "-Rr" : "-c", ".")));

        Assert.Equal(0, await command.ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Equal(content, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.InRange(memory.Counts.Reservations + memory.Counts.Resizes, 1, 32);
        Assert.InRange(memory.Counts.ReservationDisposals, 1, 8);
        Assert.Equal(0, memory.Counts.BytesReads);
        Assert.Equal(0, root.ReservedBytes);
    }

    [Fact]
    public void Jq_GovernedRecordReuseKeepsOnlyBoundedAdmittedCredit()
    {
        var root = new TestMemoryBudget(32 * 1024);
        var memory = new CountingMemoryBudget(root);
        using (var storage = new JqStorage(memory))
        {
            storage.ReserveBytes(16000);
            storage.ReuseForNextRecord();
            Assert.Equal(0, storage.UsedBytes);
            Assert.Equal(4096, root.ReservedBytes);
            MemoryCallCounts reused = memory.Counts;
            storage.ReserveBytes(2000);
            storage.ReuseForNextRecord();
            storage.ReserveBytes(3000);
            Assert.Equal(reused, memory.Counts);
            Assert.Equal(3000, storage.UsedBytes);
            Assert.Equal(4096, root.ReservedBytes);
        }
        Assert.Equal(0, root.ReservedBytes);
    }

    [Fact]
    public void Jq_GovernedStorageCreditStaysChargedAndRefundsAtReconciliation()
    {
        var root = new TestMemoryBudget(16 * 1024);
        var memory = new CountingMemoryBudget(root);
        using (var storage = new JqStorage(memory))
        {
            storage.ReserveBytes(4096);
            storage.ReserveBytes(1);
            Assert.Equal(4097, storage.UsedBytes);
            Assert.Equal(8192, root.ReservedBytes);
            MemoryCallCounts admitted = memory.Counts;
            storage.ReserveBytes(2048);
            Assert.Equal(6145, storage.UsedBytes);
            Assert.Equal(admitted, memory.Counts);
            storage.ReconcileCursor(1000);
            Assert.Equal(1256, root.ReservedBytes);
            Assert.Equal(1, memory.Counts.ShrinkingResizes);
        }
        Assert.Equal(0, root.ReservedBytes);
    }

    [Fact]
    public void Jq_GovernedStorageFallsBackToExactOnceAndPreservesFailedDemand()
    {
        var root = new TestMemoryBudget(7000);
        var memory = new CountingMemoryBudget(root);
        using (var storage = new JqStorage(memory))
        {
            storage.ReserveBytes(4096);
            storage.ReserveBytes(1);
            Assert.Equal(4097, root.ReservedBytes);
            Assert.Equal(1, memory.Counts.Refusals);
            long previousCalls = memory.Counts.Resizes;
            storage.ReserveBytes(1);
            Assert.Equal(previousCalls + 1, memory.Counts.Resizes);
            Assert.Equal(1, memory.Counts.Refusals);
            Assert.Throws<JqQuotaException>(() => storage.ReserveBytes(3000));
            Assert.Equal(4098, storage.UsedBytes);
            Assert.Equal(4098, root.ReservedBytes);
            storage.ReserveBytes(1);
            Assert.Equal(4099, root.ReservedBytes);
        }
        Assert.Equal(0, root.ReservedBytes);
    }

    [Fact]
    public void Jq_GovernedKnownCapacityAdmissionsStayExactAndOverflowLeavesDemandUnchanged()
    {
        var root = new TestMemoryBudget(long.MaxValue);
        using (var storage = new JqStorage(root))
        {
            storage.ReserveBytes(4096);
            storage.ReserveBytes(1024);
            Assert.Equal(5120, root.ReservedBytes);
            storage.ReserveBytes(long.MaxValue - 5120);
            Assert.Throws<JqQuotaException>(() => storage.ReserveBytes(1));
            Assert.Equal(long.MaxValue, storage.UsedBytes);
            Assert.Equal(long.MaxValue, root.ReservedBytes);
        }
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(100000)]
    public async Task Jq_GovernedHtmlUsesWholeBuilderAdmissionForSmallAndLargeText(int count)
    {
        var root = new TestMemoryBudget(2 * 1024 * 1024);
        var memory = new CountingMemoryBudget(root);
        string value = new('a', count);
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "-r",
            "--arg", "value", value, "$value | @html")));

        Assert.Equal(0, await command.ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Equal(value + "\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.InRange(memory.Counts.Reservations + memory.Counts.Resizes, 1, 32);
        Assert.Equal(0, memory.Counts.BytesReads);
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(10000)]
    public async Task Jq_GovernedMapBatchesNodeAndSlotAdmission(int count)
    {
        var root = new TestMemoryBudget(4 * 1024 * 1024);
        var memory = new CountingMemoryBudget(root);
        var host = new MockFileSystem();
        host.SetStandardInput("[" + string.Join(',', Enumerable.Range(0, count)) + "]");
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", "map(. + 1)")));

        Assert.Equal(0, await command.ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Equal("[" + string.Join(',', Enumerable.Range(1, count)) + "]\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.InRange(memory.Counts.Reservations + memory.Counts.Resizes, 1, count / 8 + 200);
        Assert.Equal(0, memory.Counts.BytesReads);
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(1000)]
    public async Task Jq_GovernedJsonOutputCallsFollowCapacityInsteadOfRecordCount(int count)
    {
        var root = new TestMemoryBudget(64 * 1024);
        var memory = new CountingMemoryBudget(root);
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "-c",
            "range(" + count.ToString(CultureInfo.InvariantCulture) + ")")));

        Assert.Equal(0, await command.ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Equal(string.Concat(Enumerable.Range(0, count).Select(number =>
            number.ToString(CultureInfo.InvariantCulture) + "\n")), host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, root.ReservedBytes);
        MemoryCallCounts calls = memory.Counts;
        Assert.Equal(0, calls.BytesReads);
        Assert.Equal(0, calls.SameSizeResizes);
        Assert.InRange(calls.Reservations + calls.Resizes, 1, 32);
    }

    [Fact]
    public void Jq_GovernedBudgetUsesCachedAdmissionAndPreservesRefusalAndZeroCancellation()
    {
        var root = new TestMemoryBudget(6);
        var memory = new CountingMemoryBudget(root);
        using var cancellation = new CancellationTokenSource();
        using (var budget = new JqBudget(JqExecutionPolicy.Default, memory, cancellation.Token))
        {
            budget.ReserveBytes(3);
            MemoryCallCounts initial = memory.Counts;
            budget.ReserveBytes(0);
            budget.ReserveCloneStorage(JsonValue.Create(1));
            Assert.Equal(initial, memory.Counts);
            budget.ReserveBytes(2);
            Assert.Throws<JqQuotaException>(() => budget.ReserveBytes(2));
            Assert.Equal(5, root.ReservedBytes);
            budget.ReserveBytes(1);
            Assert.Equal(6, root.ReservedBytes);
            Assert.Equal(0, memory.Counts.BytesReads);
            Assert.Equal(1, memory.Counts.Refusals);
            cancellation.Cancel();
            Assert.Throws<OperationCanceledException>(() => budget.ReserveBytes(0));
        }
        Assert.Equal(0, root.ReservedBytes);
    }

    [Fact]
    public void Jq_GovernedJsonBufferKeepsRealReplacementPeaksWithoutNoopNotifications()
    {
        var root = new TestMemoryBudget(1024);
        var memory = new CountingMemoryBudget(root);
        using var budget = new JqBudget(JqExecutionPolicy.Default, memory, CancellationToken.None);
        using (var buffer = new JqJsonBuffer(budget))
        {
            buffer.Reset();
            Assert.Equal(0, memory.Counts.Reservations);
            buffer.GetSpan(3);
            buffer.ReleaseReplacedStorage();
            buffer.Reset();
            Assert.Equal(256, root.ReservedBytes);
            buffer.GetMemory(300);
            Assert.Equal(812, root.ReservedBytes);
            buffer.ReleaseReplacedStorage();
            buffer.ReleaseReplacedStorage();
            Assert.Equal(556, root.ReservedBytes);
            Assert.Equal(1, memory.Counts.ShrinkingResizes);
            Assert.Equal(0, memory.Counts.BytesReads);
            Assert.Equal(0, memory.Counts.SameSizeResizes);
        }
        Assert.Equal(812, root.PeakBytes);
        Assert.Equal(0, root.ReservedBytes);
    }
}
