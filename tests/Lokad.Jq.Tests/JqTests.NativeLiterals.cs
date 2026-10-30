using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Jq_NativeLiteralLexingAllocatesItsExactUtf8Payload(bool escaped)
    {
        const int length = 1024 * 1024;
        string source = "\"" + (escaped ? string.Concat(Enumerable.Repeat("\\u0061", length)) : new string('a', length)) + "\"";
        var memory = new BenchmarkMemoryBudget(length + 4096);
        using (var budget = new JqBudget(null, memory, CancellationToken.None))
        {
            _ = Lexer.Tokenize("\"warm\"", JqProgramSource.Inline, budget);
            long before = GC.GetAllocatedBytesForCurrentThread();
            var tokens = Lexer.Tokenize(source, JqProgramSource.Inline, budget);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(2, tokens.Count);
            JqString value = Assert.IsType<JqString>(tokens[0].StringValue);
            Assert.Equal(length, value.ByteLength);
            Assert.Equal(length, value.ScalarLength);
            Assert.Null(tokens[0].Interpolations);
            Assert.True(value.Utf8.Span.IndexOfAnyExcept((byte)'a') < 0);
            Assert.InRange(allocated, length, length + 16 * 1024);
            JqEnvironment environment = JqEnvironment.CreateRoot(new Dictionary<string, JqValue?>());
            var filter = Assert.IsType<LiteralFilter>(new JqParser(tokens, JqProgramSource.Inline, environment, budget).Parse());
            Assert.Same(value, filter.Value);
            Assert.InRange(memory.PeakBytes, length, length + 4096);
        }
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public void Jq_NativeLiteralAdmissionChecksCancellationBeforePayloadAllocation()
    {
        const int length = 1024 * 1024;
        string source = "\"" + new string('a', length) + "\"";
        var memory = new BenchmarkMemoryBudget(2 * length);
        using var cancellation = new CancellationTokenSource();
        var hooked = new AdmissionCallbackBudget(memory, () =>
        {
            if (memory.ReservedBytes >= length) cancellation.Cancel();
        });
        using (var budget = new JqBudget(null, hooked, cancellation.Token))
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            var error = Assert.Throws<OperationCanceledException>(() => Lexer.Tokenize(source, JqProgramSource.Inline, budget));
            Assert.Equal(cancellation.Token, error.CancellationToken);
            Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 32 * 1024);
        }
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public void Jq_NativeLiteralRefusalPrecedesPayloadAllocation()
    {
        const int length = 1024 * 1024;
        string source = "\"" + new string('a', length) + "\"";
        var memory = new BenchmarkMemoryBudget(length);
        using (var budget = new JqBudget(null, memory, CancellationToken.None))
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            Assert.True(Assert.Throws<JqQuotaException>(() => Lexer.Tokenize(source, JqProgramSource.Inline, budget)).IsMemoryAdmission);
            Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 32 * 1024);
        }
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData("{a1:7}|.\"a\\(1)\"", "7\n")]
    [InlineData("{child:{a1:7},key:1}|.child.\"a\\(.key)\"", "7\n")]
    [InlineData("{a1:7,a2:8}|.\"a\\((1,2))\"", "7\n8\n")]
    [InlineData("{child:{a1:7},key:1}|path(.child.\"a\\(.key)\")", "[\"child\",\"a1\"]\n")]
    [InlineData("{\"\\\\(1)\":7}|.\"\\u005c(1)\"", "7\n")]
    [InlineData("try (1|.\"a\\(error(\"key\"))\"?) catch .", "\"key\"\n")]
    public async Task Jq_QuotedFieldsUseNativeLiteralAndInterpolationSemantics(string filter, string expected)
    {
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc", filter)));
        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public async Task Jq_NativeCompilerLiteralIsReusedAcrossALongResultStream()
    {
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        var host = new MockFileSystem();
        string source = "range(4096)|\"" + new string('a', 8192) + "\"|length";
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nr", source)));
        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        Assert.Equal(string.Concat(Enumerable.Repeat("8192\n", 4096)), host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.InRange(memory.PeakBytes, 8192, 32 * 1024);
        Assert.Equal(0, memory.ReservedBytes);
    }
}
