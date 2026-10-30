using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("stderr|empty", "", true)]
    [InlineData("_strindices(.)|length", "1\n", false)]
    [InlineData("path(ltrimstr(\"z\"))", "[]\n", false)]
    [InlineData("path(rtrimstr(\"z\"))", "[]\n", false)]
    [InlineData("path(trimstr(\"z\"))", "[]\n", false)]
    public async Task Jq_NativeReadBoundariesNeedNoWholePayloadTranscoding(string operation, string output, bool stderr)
    {
        const long limit = 2 * 1024 * 1024;
        string payload = new string('x', 1024 * 1024) + "é🚀";
        var memory = new BenchmarkMemoryBudget(limit);
        var host = new MockFileSystem();
        IMemoryReservation? pressure = null;
        host.BeforeByteAppend = () =>
        {
            if (pressure is null)
                Assert.True(memory.TryReserve(limit - memory.ReservedBytes - 4096 - 256, out pressure));
        };
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nrc", "--arg", "text", payload,
            "\"ready\", ($text|" + operation + ")")));
        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        Assert.Equal("ready\n" + output, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal(stderr ? payload : "", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.NotNull(pressure);
        Assert.Equal(pressure.Bytes, memory.ReservedBytes);
        pressure.Dispose();
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public void Jq_NativeNumberParsingAvoidsWholeValueUtf16()
    {
        byte[] bytes = new byte[1024 * 1024];
        bytes.AsSpan().Fill((byte)'0');
        using var budget = new JqBudget(CancellationToken.None);
        JqString input = JqString.FromUtf8(bytes, budget);
        var runtime = new JqRuntime(budget);
        _ = runtime.ToJsonNumber(NativeFixtures.Create("0"));
        long before = GC.GetAllocatedBytesForCurrentThread();
        JqValue result = runtime.ToJsonNumber(input);
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 8192);
        Assert.Equal(0, JqRuntime.Number(result));
    }

    [Fact]
    public async Task Jq_NativeInterpolationAllocatesOnlyItsUtf8Result()
    {
        byte[] bytes = new byte[1024 * 1024];
        bytes.AsSpan().Fill((byte)'x');
        using var context = new JqContext(new Dictionary<string, JqValue?>(), new JqBudget(CancellationToken.None));
        JqString input = JqString.FromUtf8(bytes, context.Budget);
        var filter = new JqParser("\"pre\\(.)post\"", JqProgramSource.Inline, context.RootEnvironment, context.Budget).Parse();
        _ = await filter.Evaluate(NativeFixtures.Create("warm"), context, context.RootEnvironment).SingleAsync();
        long before = GC.GetAllocatedBytesForCurrentThread();
        var output = Assert.IsType<JqString>(await filter.Evaluate(input, context, context.RootEnvironment).SingleAsync());
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, bytes.Length, bytes.Length + 32 * 1024);
        Assert.Equal(bytes.Length + 7, output.ByteLength);
        Assert.True(output.Utf8.Span.StartsWith("pre"u8));
        Assert.True(output.Utf8.Span.EndsWith("post"u8));
        Assert.True(output.Utf8.Span.Slice(3, bytes.Length).SequenceEqual(bytes));
    }

    [Fact]
    public async Task Jq_NativeRegexReplacementBorrowsAnUnchangedLargeSubject()
    {
        byte[] bytes = new byte[1024 * 1024];
        bytes.AsSpan().Fill((byte)'x');
        using var context = new JqContext(new Dictionary<string, JqValue?>(),
            new JqBudget(Utf8RegexAdapter.Instance, CancellationToken.None));
        JqString input = JqString.FromUtf8(bytes, context.Budget);
        var filter = new GsubFilter([new LiteralFilter(NativeFixtures.Create("z")), new LiteralFilter(NativeFixtures.Create("y"))], false);
        _ = await filter.Evaluate(NativeFixtures.Create("warm"), context, context.RootEnvironment).SingleAsync();
        long before = GC.GetAllocatedBytesForCurrentThread();
        var output = Assert.IsType<JqString>(await filter.Evaluate(input, context, context.RootEnvironment).SingleAsync());
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 32 * 1024);
        Assert.NotSame(input, output);
        Assert.Equal(input.Utf8, output.Utf8);
    }

    [Theory]
    [InlineData("base64")]
    [InlineData("uri")]
    [InlineData("html")]
    public void Jq_NativeFormatsAllocateTheirResultWithoutUtf16Input(string format)
    {
        byte[] bytes = new byte[128 * 1024];
        bytes.AsSpan().Fill((byte)'x');
        bytes[^1] = (byte)'&';
        using var budget = new JqBudget(CancellationToken.None);
        JqString input = JqString.FromUtf8(bytes, budget);
        var runtime = new JqRuntime(budget);
        _ = runtime.FormatValue(format, NativeFixtures.Create("x"));
        long before = GC.GetAllocatedBytesForCurrentThread();
        JqString output = runtime.FormatValue(format, input);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.InRange(allocated, output.ByteLength, output.ByteLength + 8192);
        Assert.Equal(bytes.Length, input.ByteLength);
        if (format == "uri") Assert.True(output.Utf8.Span.EndsWith("%26"u8));
        if (format == "html") Assert.True(output.Utf8.Span.EndsWith("&amp;"u8));
        if (format == "base64") Assert.Equal(Convert.ToBase64String(bytes), output.GetValue<string>());
    }

    [Fact]
    public void Jq_NativeRegexTestBorrowsItsLargeSubject()
    {
        byte[] bytes = new byte[1024 * 1024];
        bytes.AsSpan().Fill((byte)'x');
        using var budget = new JqBudget(Utf8RegexAdapter.Instance, CancellationToken.None);
        using var context = new JqContext(new Dictionary<string, JqValue?>(), budget);
        JqString subject = JqString.FromUtf8(bytes, budget);
        JqString pattern = JqString.FromUtf8("x"u8, budget);
        Assert.True(JqMatch.Test(context, subject, pattern, null));
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.True(JqMatch.Test(context, subject, pattern, null));
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 32 * 1024);
    }

    [Theory]
    [InlineData("/w==")]
    [InlineData("wIA=")]
    [InlineData("4oI=")]
    [InlineData("8J+A")]
    public void Jq_NativeBase64ReplacementMatchesTheDeclaredUtf8Policy(string encoded)
    {
        byte[] bytes = Convert.FromBase64String(encoded);
        using var budget = new JqBudget(CancellationToken.None);
        var result = new JqRuntime(budget).FormatValue("base64d", NativeFixtures.Create(encoded));
        Assert.Equal(Encoding.UTF8.GetString(bytes), result.GetValue<string>());
    }

    [Fact]
    public void Jq_NativeJsonFieldAndJoinedOutputAvoidWholeValueUtf16()
    {
        const int length = 1024 * 1024;
        byte[] prefix = "{\"\\u00e9\":\""u8.ToArray();
        byte[] suffix = "é🚀é\"}"u8.ToArray();
        byte[] input = new byte[prefix.Length + length + suffix.Length];
        prefix.CopyTo(input, 0);
        input.AsSpan(prefix.Length, length).Fill((byte)'a');
        suffix.CopyTo(input, prefix.Length + length);
        using var budget = new JqBudget(CancellationToken.None);
        using var context = new JqContext(new Dictionary<string, JqValue?>(), budget);
        var reader = new JqRuntime.InputReader(context.Runtime, budget);
        var filter = Assert.IsAssignableFrom<SingleValueFilter>(FieldFilter.Create(new IdentityFilter(), NativeFixtures.Create("é"), false));

        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.True(reader.TryRead(input, true, out JqValue? root, out int consumed));
        JqValue? selected = filter.ReadValue(root, context, context.RootEnvironment);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(input.Length, consumed);
        var text = Assert.IsType<JqString>(selected);
        Assert.Equal(length + 4, text.ScalarLength);
        Assert.InRange(allocated, length, length + 32 * 1024);
        input.AsSpan().Clear();
        Assert.Equal((byte)'a', text.Utf8.Span[0]);
        Assert.True(text.Utf8.Span.EndsWith("é🚀é"u8));

        using var output = new JqRuntime.OutputRenderer(context.Runtime, budget,
            new JqInvocation { RawOutput = true, JoinOutput = true });
        before = GC.GetAllocatedBytesForCurrentThread();
        ReadOnlyMemory<byte> rendered = output.Render(text);
        Assert.Equal(text.Utf8, rendered);
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 4096);
    }

    [Theory]
    [InlineData("\"ascii\"", "ascii", 5)]
    [InlineData("\"aé🚀é\"", "aé🚀é", 5)]
    [InlineData("\"a\\u00e9\\ud83d\\ude80e\\u0301\"", "aé🚀é", 5)]
    [InlineData("\"\\u0041suffix\"", "Asuffix", 7)]
    [InlineData("\"\\u0000\\n\\r\\t\\b\\f\\\\\\\"\\/\"", "\0\n\r\t\b\f\\\"/", 9)]
    [InlineData("\"\"", "", 0)]
    public void Jq_NativeJsonStringsDecodeToOwnedUtf8(string json, string expected, int scalars)
    {
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        using (var budget = new JqBudget(null, memory, CancellationToken.None))
        {
            byte[] input = Encoding.UTF8.GetBytes(json);
            var reader = new Utf8JsonReader(input);
            Assert.True(reader.Read());
            var value = JqString.FromJson(ref reader, budget);
            input.AsSpan().Clear();
            Assert.Equal(Encoding.UTF8.GetBytes(expected), value.Utf8.ToArray());
            Assert.Equal(scalars, value.ScalarLength);
            Assert.Equal(Encoding.UTF8.GetByteCount(expected), value.ByteLength);
        }
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData("\"\\ud800\"")]
    [InlineData("\"\\udc00\"")]
    [InlineData("\"\\ud800x\"")]
    public void Jq_NativeJsonStringsKeepStrictSurrogateValidation(string json)
    {
        using var budget = new JqBudget(CancellationToken.None);
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(json));
        Assert.True(reader.Read());
        bool failed = false;
        try { JqString.FromJson(ref reader, budget); }
        catch (JqException) { failed = true; }
        Assert.True(failed);
    }

    [Fact]
    public void Jq_NativeStringsPreserveScalarOperationsAndFreshIdentity()
    {
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        using (var budget = new JqBudget(null, memory, CancellationToken.None))
        {
            var text = JqString.FromUtf8("aé🚀b\0"u8, budget);
            Assert.Equal(5, text.ScalarLength);
            Assert.Equal(9, text.ByteLength);
            Assert.Equal(2, text.IndexOf(JqString.FromUtf8("🚀"u8, budget), 0, CancellationToken.None));
            Assert.Equal(5, text.IndexOf(JqString.FromUtf8(""u8, budget), 5, CancellationToken.None));
            Assert.Equal(-1, text.IndexOf(JqString.FromUtf8("missing"u8, budget), 0, CancellationToken.None));
            Assert.Equal("é🚀"u8.ToArray(), text.Slice(1, 2, budget).Utf8.ToArray());
            Assert.Equal(7, text.ByteOffset(3, CancellationToken.None));
            var clone = text.CloneHeader(budget);
            Assert.NotSame(text, clone);
            Assert.True(text.ContentEquals(clone, CancellationToken.None));
            Assert.Equal(text.ContentHashCode(CancellationToken.None), clone.ContentHashCode(CancellationToken.None));
            var suffix = JqString.FromUtf8("🚀"u8, budget);
            Assert.Equal("aé🚀b\0🚀"u8.ToArray(), text.Concat(suffix, budget).Utf8.ToArray());
            Assert.Equal(new[] { 97, 233, 128640, 98, 0 }, text.Scalars(CancellationToken.None));
            Assert.Equal(text.Utf8.ToArray(), JqString.FromScalars(text.Scalars(CancellationToken.None).ToArray(), budget).Utf8.ToArray());
        }
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public void Jq_NativeStringsKeepLiteralSplitAndAsciiCaseSemantics()
    {
        using var budget = new JqBudget(CancellationToken.None);
        var text = JqString.FromUtf8("aé🚀"u8, budget);
        var empty = JqString.FromUtf8(""u8, budget);
        Assert.Equal(new[] { "a", "é", "🚀" }, text.Split(empty, budget).Select(value => Encoding.UTF8.GetString(value.Utf8.Span)));
        Assert.Empty(empty.Split(empty, budget));
        Assert.Equal(new[] { "", "", "" }, JqString.FromUtf8("éé"u8, budget).Split(JqString.FromUtf8("é"u8, budget), budget)
            .Select(value => Encoding.UTF8.GetString(value.Utf8.Span)));
        var unchanged = Assert.Single(text.Split(JqString.FromUtf8("x"u8, budget), budget));
        Assert.NotSame(text, unchanged);
        Assert.True(text.ContentEquals(unchanged, CancellationToken.None));
        Assert.Equal("aé🚀"u8.ToArray(), JqString.FromUtf8("Aé🚀"u8, budget).AsciiCase(true, budget).Utf8.ToArray());
        Assert.Equal("Aé🚀"u8.ToArray(), text.AsciiCase(false, budget).Utf8.ToArray());
        Assert.NotSame(text, text.AsciiCase(true, budget));
    }

    [Fact]
    public void Jq_NativeKeysHaveDecodedContentEqualityAndUnicodeOrder()
    {
        using var budget = new JqBudget(CancellationToken.None);
        var reader = new Utf8JsonReader("{\"\\u00e9\":0}"u8);
        Assert.True(reader.Read());
        Assert.True(reader.Read());
        Assert.Equal(JsonTokenType.PropertyName, reader.TokenType);
        var escaped = JqString.FromJson(ref reader, budget);
        var literal = JqString.FromUtf8("é"u8, budget);
        Assert.True(escaped.ContentEquals(literal, CancellationToken.None));
        Assert.Equal(escaped.ContentHashCode(CancellationToken.None), literal.ContentHashCode(CancellationToken.None));
        Assert.True(JqString.FromUtf8("\uE000"u8, budget).Compare(JqString.FromUtf8("🚀"u8, budget), CancellationToken.None) < 0);
        Assert.False(JqString.FromUtf8("é"u8, budget).ContentEquals(JqString.FromUtf8("é"u8, budget), CancellationToken.None));
    }

    [Fact]
    public void Jq_NativeHostStringsReplaceLoneSurrogatesAndImplodeInvalidScalars()
    {
        using var budget = new JqBudget(CancellationToken.None);
        Assert.Equal("a�b"u8.ToArray(), JqString.FromHost("a\ud800b", budget).Utf8.ToArray());
        Assert.Equal(new[] { 65533, 65533, 65533, 0, 128640 },
            JqString.FromScalars([-1, 0xd800, 0x110000, 0, 128640], budget).Scalars(CancellationToken.None));
    }

    [Theory]
    [InlineData(65535)]
    [InlineData(65536)]
    public void Jq_NativeSearchAndJsonEscapesCrossSearchWindows(int prefix)
    {
        using var budget = new JqBudget(CancellationToken.None);
        byte[] input = Encoding.UTF8.GetBytes("\"" + new string('a', prefix) + "\\u00e9\\ud83d\\ude80b\"");
        var reader = new Utf8JsonReader(input);
        Assert.True(reader.Read());
        var value = JqString.FromJson(ref reader, budget);
        var separator = JqString.FromUtf8("é🚀"u8, budget);
        Assert.Equal(prefix + 3, value.ScalarLength);
        Assert.Equal(prefix + 7, value.ByteLength);
        Assert.Equal(prefix, value.IndexOf(separator, 0, CancellationToken.None));
        Assert.Equal(-1, value.IndexOf(separator, prefix + 1, CancellationToken.None));
        JqString[] pieces = value.Split(separator, budget).ToArray();
        Assert.Equal(2, pieces.Length);
        Assert.Equal(prefix, pieces[0].ScalarLength);
        Assert.Equal("b"u8.ToArray(), pieces[1].Utf8.ToArray());
    }

    [Fact]
    public void Jq_NativeStringReadsHonorCancellationIncludingCachedKeys()
    {
        using var budget = new JqBudget(CancellationToken.None);
        var value = JqString.FromUtf8("aé🚀"u8, budget);
        _ = value.ContentHashCode(CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => value.ContentHashCode(cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => value.ContentEquals(value, cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => value.Compare(value, cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => value.IndexOf(value, 0, cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => value.ByteOffset(0, cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => value.Scalars(cancellation.Token).ToArray());
    }

    [Fact]
    public void Jq_NativeStringsRejectMalformedUtf8()
    {
        using var budget = new JqBudget(CancellationToken.None);
        foreach (byte[] bytes in new byte[][] { [0xff], [0xc0, 0x80], [0xed, 0xa0, 0x80], [0xf4, 0x90, 0x80, 0x80], [0xe2, 0x82] })
            Assert.Throws<JqException>(() => JqString.FromUtf8(bytes, budget));
    }

    [Fact]
    public void Jq_NativeJsonStringsAvoidWholeValueUtf16Storage()
    {
        const int length = 1024 * 1024;
        byte[] input = new byte[length + 2];
        input.AsSpan().Fill((byte)'a'); input[0] = input[^1] = (byte)'"';
        using var budget = new JqBudget(CancellationToken.None);
        var reader = new Utf8JsonReader(input);
        Assert.True(reader.Read());
        long before = GC.GetAllocatedBytesForCurrentThread();
        var value = JqString.FromJson(ref reader, budget);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(length, value.ByteLength);
        Assert.InRange(allocated, length, length + 8192);
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            value.WriteTo(writer);
        Assert.Equal(input, output.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Jq_NativeStringAdmissionPrecedesConstruction(bool cancel)
    {
        var root = new BenchmarkMemoryBudget(cancel ? 128 * 1024 : 64);
        using var cancellation = new CancellationTokenSource();
        var memory = new AdmissionCallbackBudget(root, () => { if (cancel) cancellation.Cancel(); });
        using (var budget = new JqBudget(null, memory, cancellation.Token))
        {
            byte[] bytes = new byte[64 * 1024];
            bytes.AsSpan().Fill((byte)'a');
            long before = GC.GetAllocatedBytesForCurrentThread();
            if (cancel)
                Assert.Throws<OperationCanceledException>(() => JqString.FromUtf8(bytes, budget));
            else Assert.True(Assert.Throws<JqQuotaException>(() => JqString.FromUtf8(bytes, budget)).IsMemoryAdmission);
            Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 32 * 1024);
        }
        Assert.Equal(0, root.ReservedBytes);
    }
}
