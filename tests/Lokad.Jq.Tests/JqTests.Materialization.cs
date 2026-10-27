using Lokad.Jq.Benchmarking;
using System.Globalization;
using System.Text;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Fact]
    public async Task Jq_PaddingAndExistingUpdatesUseSharedAdmission()
    {
        var memory = new BenchmarkMemoryBudget(96 * 1024 * 1024);
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc",
            "[] | .[1000001] = 1 | .[1000001] = 2 | [length, .[1000001]]")));
        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        Assert.Equal("[1000002,2]\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData("try ([] | .[1000001] = 1) catch \"caught\"")]
    [InlineData("(null | setpath([1000001]; 1))?")]
    public async Task Jq_UnadmittedPaddingIsTerminal(string expression)
    {
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nr", "\"prefix\", (" + expression + "), \"later\"")));
        Assert.Equal(5, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        Assert.Equal("prefix\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("jq: memory budget exceeded\n", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public async Task Jq_HostedMaterializationUsesSharedAdmission()
    {
        const int length = 64 * 1024 * 1024 + 1;
        var memory = new BenchmarkMemoryBudget(256 * 1024 * 1024);
        using var budget = new JqBudget(Utf8RegexAdapter.Instance, memory, CancellationToken.None);
        var host = new GeneratedLargeValueHost(length, 0);
        using (var content = Assert.IsType<JqReadResult.Complete>(
            await host.TryReadAllBytesAsync(JqFileDescriptor.StdIn, budget, CancellationToken.None)))
        {
            Assert.Equal(length, content.Content.Length);
            Assert.Equal(-1, content.Content.Span.IndexOfAnyExcept((byte)'x'));
            Assert.True(memory.ReservedBytes >= length);
        }
        Assert.Equal(8192, host.LargestRead);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public async Task Jq_LargeRawValuesUseSharedAdmission()
    {
        const int length = 64 * 1024 * 1024 + 1;
        var memory = new BenchmarkMemoryBudget(512 * 1024 * 1024);
        var host = new GeneratedLargeValueHost(length, 0);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-R", "length")));
        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        Assert.Equal(1, host.Writes);
        Assert.Equal(8192, host.LargestRead);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public async Task Jq_LargeJsonValuesUseSharedAdmission()
    {
        const int nulls = 11184812;
        var memory = new BenchmarkMemoryBudget(512 * 1024 * 1024);
        var host = new GeneratedLargeValueHost(0, nulls);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "\"\\u0000\" * 11184812")));
        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        Assert.Equal(1, host.Writes);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public void Jq_BufferRepresentabilityRefusesBeforeAllocation()
    {
        using var budget = new JqBudget(Utf8RegexAdapter.Instance, CancellationToken.None);
        using var buffer = new JqJsonBuffer(budget);
        Assert.Equal("byte buffer cannot be represented",
            Assert.Throws<JqQuotaException>(() => buffer.GetMemory(int.MaxValue)).Message);
    }

    private sealed class GeneratedLargeValueHost(int inputLength, int escapedNulls) : IJqHost
    {
        private int _remaining = inputLength;
        private int InputLength { get; } = inputLength;
        internal int LargestRead { get; private set; }
        internal int Writes { get; private set; }

        public ValueTask<JqByteReadResult> ReadBytesAsync(JqFileDescriptor descriptor, Memory<byte> buffer, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(JqFileDescriptor.StdIn, descriptor);
            LargestRead = Math.Max(LargestRead, buffer.Length);
            Assert.InRange(buffer.Length, 1, 8192);
            int count = Math.Min(buffer.Length, _remaining);
            buffer.Span[..count].Fill((byte)'x');
            _remaining -= count;
            return ValueTask.FromResult(JqByteReadResult.Success(count, _remaining == 0));
        }

        public Task<JqAppendResult> AppendWhileOpenAsync(JqFileDescriptor descriptor, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(JqFileDescriptor.StdOut, descriptor);
            Writes++;
            if (escapedNulls == 0)
                Assert.True(content.Span.SequenceEqual(Encoding.UTF8.GetBytes(InputLength.ToString(CultureInfo.InvariantCulture) + "\n")));
            else
            {
                Assert.Equal(6 * escapedNulls + 3, content.Length);
                Assert.Equal((byte)'"', content.Span[0]);
                Assert.True(content.Span[^2..].SequenceEqual("\"\n"u8));
                ReadOnlySpan<byte> body = content.Span[1..^2];
                for (int index = 0; index < body.Length; index += 6)
                    if (!body.Slice(index, 6).SequenceEqual("\\u0000"u8)) Assert.Fail("Unexpected JSON escape bytes.");
            }
            return Task.FromResult(JqAppendResult.Open);
        }

        public Task<JqOpenedFile> OpenReadAsync(JqPath path, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Unexpected hosted open.");
        public Task<JqCloseResult> CloseDescriptorAsync(JqFileDescriptor descriptor, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Borrowed descriptor was closed.");
    }
}
