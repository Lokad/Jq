using System.Globalization;
using System.Text;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("json")]
    [InlineData("raw")]
    [InlineData("seq")]
    [InlineData("stream")]
    public async Task Jq_LongStreamsKeepBoundedMemory(string mode)
    {
        const int records = 300_000;
        var memory = new TestMemoryBudget(128 * 1024);
        var host = new RepeatingRecordHost(mode, records);
        var command = LongStreamCommand(mode, false);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        Assert.Equal(0, await command.ExecuteAsync(host, memory, cancellation.Token));
        Assert.Equal(records, host.OutputRecords);
        Assert.Equal((long)records * host.InputRecordLength, host.ReadBytes);
        Assert.Equal((long)records * host.OutputRecordLength, host.WrittenBytes);
        Assert.True(host.ReadBytes > 16 * 1024 * 1024);
        Assert.True(host.WrittenBytes > 32 * 1024 * 1024);
        Assert.InRange(host.MaximumReadRequest, 1, 8192);
        Assert.Equal(1, host.Opens);
        Assert.Equal(1, host.Closes);
        Assert.InRange(memory.PeakBytes, 1, 128 * 1024);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData("json", false)]
    [InlineData("raw", false)]
    [InlineData("seq", false)]
    [InlineData("stream", false)]
    [InlineData("json", true)]
    [InlineData("raw", true)]
    [InlineData("seq", true)]
    [InlineData("stream", true)]
    public async Task Jq_LongStreamStopsBeforeAnotherReadOrFile(string mode, bool cancel)
    {
        var memory = new TestMemoryBudget(128 * 1024);
        var host = new RepeatingRecordHost(mode, 300_000);
        var command = LongStreamCommand(mode, true);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        host.AfterRecord = () =>
        {
            if (host.OutputRecords != 8) return true;
            if (cancel) cancellation.Cancel();
            return cancel;
        };

        if (cancel)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => command.ExecuteAsync(host, memory, cancellation.Token));
        else
            Assert.Equal(0, await command.ExecuteAsync(host, memory, cancellation.Token));
        Assert.Equal(8, host.OutputRecords);
        Assert.Equal(1, host.ReadCalls);
        Assert.Equal(1, host.Opens);
        Assert.Equal(1, host.Closes);
        Assert.Equal(0, memory.ReservedBytes);
        var reuse = new MockFileSystem();
        var next = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "42")));
        Assert.Equal(0, await next.ExecuteAsync(reuse, memory, CancellationToken.None));
        Assert.Equal("42\n", reuse.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData(".")]
    [InlineData(". as $v | $v")]
    [InlineData("def read($v): $v; read(.)")]
    [InlineData("select(true)")]
    [InlineData(".[0]")]
    [InlineData(".[]")]
    public async Task Jq_ReadsPreserveDeepValuesForIterativeConsumers(string read)
    {
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n",
            "null | setpath([range(1000)|0];0) | (" + read + ") | walk(true)")));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Assert.Equal(0, await command.ExecuteAsync(host, cancellation.Token));
        Assert.Equal("true\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("[.]", 64, 0)]
    [InlineData("[.]", 65, 5)]
    [InlineData("{a:.}", 64, 0)]
    [InlineData("{a:.}", 65, 5)]
    public async Task Jq_RecursiveConstructionChecksDepthBeforeScalarConsumer(string wrap, int depth, int status)
    {
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n",
            "reduce range(" + depth.ToString(CultureInfo.InvariantCulture) + ") as $_ (null; " + wrap + ") | length")));
        Assert.Equal(status, await command.ExecuteAsync(host, CancellationToken.None));
        if (status == 0)
        {
            Assert.Equal("1\n", host.GetOutput(JqFileDescriptor.StdOut));
            Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        }
        else
        {
            Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
            Assert.Equal("jq: value nesting limit exceeded\n", host.GetOutput(JqFileDescriptor.StdErr));
        }
    }

    [Theory]
    [InlineData("5 | while((true,false); .)", "5\n5\n5\n5\n5\n5\n5\n5\n")]
    [InlineData("0 | while(. < 2; 1,2)", "0\n1\n1\n1\n1\n1\n1\n1\n")]
    public async Task Jq_EmittingWhileCancelsWithExactPrefix(string filter, string expected)
    {
        var memory = new TestMemoryBudget(64 * 1024);
        var host = new MockFileSystem();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        host.BeforeByteAppend = () => { if (host.AppendCallCount == 8) cancellation.Cancel(); };
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => command.ExecuteAsync(host, memory, cancellation.Token));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public async Task Jq_FiniteSparseUpdateSurvivesWithoutExecutionFuel()
    {
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "-c",
            "reduce range(65540;65536;-1) as $i ([]; .[$i]=$i) | .[65536:]")));
        Assert.Equal(0, await command.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("[null,65537,65538,65539,65540]\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_FiniteCalendarLoopSurvivesWithoutExecutionFuel()
    {
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "-c",
            "last(range(365*67)|(\"1970-03-01T01:02:03Z\"|strptime(\"%Y-%m-%dT%H:%M:%SZ\")|mktime) + (86400 * .)|strftime(\"%Y-%m-%dT%H:%M:%SZ\")|strptime(\"%Y-%m-%dT%H:%M:%SZ\"))")));
        var final = new DateTimeOffset(1970, 3, 1, 1, 2, 3, TimeSpan.Zero).AddDays(365 * 67 - 1);
        string expected = "[" + string.Join(',', new[] { final.Year, final.Month - 1, final.Day, 1, 2, 3,
            (int)final.DayOfWeek, final.DayOfYear - 1 }.Select(value => value.ToString(CultureInfo.InvariantCulture))) + "]\n";
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Assert.Equal(0, await command.ExecuteAsync(host, cancellation.Token));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    private static Jq LongStreamCommand(string mode, bool laterFile)
    {
        string[] arguments = mode switch
        {
            "raw" => ["-Rr", ".", "/input"],
            "seq" => ["--seq", "-c", ".", "/input"],
            "stream" => ["--stream", "-c", ".[1]", "/input"],
            _ => ["-c", ".", "/input"]
        };
        if (laterFile) arguments = [.. arguments, "/later"];
        return Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", arguments)));
    }

    // Generates and checks records in place. Neither side retains the stream.
    private sealed class RepeatingRecordHost : IJqHost
    {
        private readonly byte[] _inputRecord;
        private readonly byte[] _outputRecord;
        private readonly long _length;
        private bool _closedOutput;
        private readonly JqFileDescriptor _input = new(3);

        internal RepeatingRecordHost(string mode, int records)
        {
            string payload = new('x', 128);
            string record = mode == "raw" ? payload + "\n" : "\"" + payload + "\"\n";
            _inputRecord = Encoding.UTF8.GetBytes(mode == "seq" ? "\u001e" + record : record);
            _outputRecord = _inputRecord;
            _length = (long)records * _inputRecord.Length;
        }

        internal int InputRecordLength => _inputRecord.Length;
        internal int OutputRecordLength => _outputRecord.Length;
        internal long ReadBytes { get; private set; }
        internal long WrittenBytes { get; private set; }
        internal int OutputRecords { get; private set; }
        internal int ReadCalls { get; private set; }
        internal int MaximumReadRequest { get; private set; }
        internal int Opens { get; private set; }
        internal int Closes { get; private set; }
        internal Func<bool>? AfterRecord { get; set; }

        public ValueTask<JqByteReadResult> ReadBytesAsync(JqFileDescriptor descriptor, Memory<byte> buffer, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(_input, descriptor);
            ReadCalls++;
            MaximumReadRequest = Math.Max(MaximumReadRequest, buffer.Length);
            int count = (int)Math.Min(buffer.Length, _length - ReadBytes);
            int written = 0;
            while (written < count)
            {
                int offset = (int)((ReadBytes + written) % _inputRecord.Length);
                int copy = Math.Min(count - written, _inputRecord.Length - offset);
                _inputRecord.AsSpan(offset, copy).CopyTo(buffer.Span[written..]);
                written += copy;
            }
            ReadBytes += count;
            return ValueTask.FromResult(count == 0 ? JqByteReadResult.EndOfFile
                : JqByteReadResult.Success(count, ReadBytes == _length));
        }

        public Task<JqAppendResult> AppendWhileOpenAsync(JqFileDescriptor descriptor, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(JqFileDescriptor.StdOut, descriptor);
            Assert.False(_closedOutput);
            Assert.True(content.Span.SequenceEqual(_outputRecord));
            OutputRecords++;
            WrittenBytes += content.Length;
            _closedOutput = AfterRecord is { } after && !after();
            return Task.FromResult(_closedOutput ? JqAppendResult.Closed : JqAppendResult.Open);
        }

        public Task<JqOpenedFile> OpenReadAsync(JqPath path, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal("/input", path.Path);
            Opens++;
            return Task.FromResult(JqOpenedFile.Success(_input));
        }

        public Task<int> CloseDescriptorAsync(JqFileDescriptor descriptor, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(_input, descriptor);
            Closes++;
            return Task.FromResult(0);
        }
    }
}
