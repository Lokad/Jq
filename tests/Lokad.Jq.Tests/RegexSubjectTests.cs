using System.Diagnostics.CodeAnalysis;
using System.Text;
using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed class RegexSubjectTests
{
    [Fact]
    public void EmptyNameMetadataAdmitsItsMapWithoutSameSizeResizes()
    {
        var root = new BenchmarkMemoryBudget(64 * 1024);
        var memory = new CountingMemoryBudget(root);
        using (var context = new JqContext(new Dictionary<string, JqValue?>(), new JqBudget(new FixtureEngine(), memory, CancellationToken.None)))
        {
            JqString input = JqString.FromUtf8("é"u8, context.Budget);
            JqString pattern = JqString.FromUtf8("."u8, context.Budget);
            Assert.Single(JqMatch.Search(context, input, pattern, null));
            Assert.Single(JqMatch.Search(context, input, pattern, null));
            Assert.Equal(0, memory.Counts.SameSizeResizes);
        }
        Assert.Equal(0, root.ReservedBytes);
    }

    [Fact]
    public async Task RuntimeBorrowsLargeNativeSubjectsWithinFundedRecordLifetime()
    {
        const long limit = 2 * 1024 * 1024;
        var memory = new BenchmarkMemoryBudget(limit);
        var engine = new FixtureEngine();
        var host = new MockFileSystem();
        IMemoryReservation? pressure = null;
        host.BeforeByteAppend = () =>
        {
            if (pressure is null)
                Assert.True(memory.TryReserve(limit - memory.ReservedBytes - 8192, out pressure));
        };
        using var invocation = JqCommandInvocation.CreateWithStandardDescriptors("jq",
            ["-nr", "--arg", "text", new string('a', 1024 * 1024), "\"ready\", ($text|test(\".\"))"], []);
        Assert.Equal(0, await Assert.IsType<Jq>(Jq.TryParse(invocation)).ExecuteAsync(host, engine, memory, CancellationToken.None));
        Assert.Equal("ready\ntrue\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(1, engine.SubjectsCreated);
        Assert.Equal(1, engine.SubjectsDisposed);
        Assert.NotNull(pressure);
        Assert.Equal(pressure.Bytes, memory.ReservedBytes);
        pressure.Dispose();
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public void NativeCaptureNamesSurviveProviderStorageRetirement()
    {
        var memory = new BenchmarkMemoryBudget(128 * 1024);
        var engine = new FixtureEngine { NameUtf8 = "café名"u8.ToArray(), ScrubNamesOnDispose = true };
        using (var context = new JqContext(new Dictionary<string, JqValue?>(), new JqBudget(engine, memory, CancellationToken.None)))
        {
            JqString input = JqString.FromUtf8("aé"u8, context.Budget);
            JqString pattern = JqString.FromUtf8("(?<x>.)"u8, context.Budget);
            JqString flags = JqString.FromUtf8("g"u8, context.Budget);
            var results = JqMatch.Search(context, input, pattern, flags);
            Assert.Equal(2, results.Count);
            _ = JqMatch.Search(context, input, pattern, flags);
            context.Regexes.Dispose();
            Assert.Equal(1, engine.ProgramsDisposed);
            Assert.Equal(1, engine.MetadataReads);
            foreach (var result in results)
            {
                var captures = Assert.IsType<JqArray>(result["captures"]);
                var capture = Assert.IsType<JqObject>(captures[0]);
                Assert.Equal("café名"u8.ToArray(), Assert.IsType<JqString>(capture["name"]).Utf8.ToArray());
            }
        }
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public void MissingProviderDoesNotCopyThePattern()
    {
        var memory = new BenchmarkMemoryBudget(0);
        using var budget = new JqBudget(null, memory, CancellationToken.None);
        using var cache = new JqRegexCache(budget);
        var error = Assert.Throws<JqException>(() => cache.Rent(new byte[1024 * 1024], JqRegexCompileOptions.None));
        Assert.Equal("regex engine is not configured", error.Message);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public async Task RetainedRangesKeepSubjectAndProgramAliveAcrossSuspension()
    {
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        var engine = new FixtureEngine();
        using var budget = new JqBudget(engine, memory, CancellationToken.None);
        using var cache = new JqRegexCache(budget);
        using var pattern = cache.Rent("(?<x>.)"u8.ToArray(), JqRegexCompileOptions.None);
        Assert.Equal(1, JqRegexCache.GetCaptureCount(pattern.Value));
        Assert.Equal("x"u8.ToArray(), JqRegexCache.GetGroupNames(pattern.Value)[1].ToArray());
        using var subject = PrepareNative(cache, pattern.Value, "aé🚀", memory);
        using var cursor = subject.CreateCursor(JqRegexMatchOptions.None, CancellationToken.None);
        Assert.True(cursor.MoveNext(CancellationToken.None));
        var retained = cursor.RetainCurrent(CancellationToken.None);
        Assert.Equal(new JqRegexCapture(true, 0, 1), retained.Value);
        Assert.True(cursor.MoveNext(CancellationToken.None));
        Assert.Equal(new JqRegexCapture(true, 1, 3), cursor.Current.Value);
        Assert.True(cursor.MoveNext(CancellationToken.None));
        Assert.Equal(new JqRegexCapture(true, 3, 7), cursor.Current.Value);
        cache.Dispose();
        pattern.Dispose();
        subject.Dispose();
        cursor.Dispose();
        Assert.Equal(0, engine.SubjectsDisposed);
        Assert.Equal(0, engine.ProgramsDisposed);
        Assert.True(memory.ReservedBytes > 0);
        await Task.Yield();
        Assert.Equal(new JqRegexCapture(true, 0, 1), retained.Value);
        Assert.Equal(2, retained.CaptureSlotCount);
        retained.Dispose();
        retained.Dispose();
        Assert.Equal(1, engine.SubjectsDisposed);
        Assert.Equal(1, engine.ProgramsDisposed);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public void EveryAdvanceUsesItsCurrentToken()
    {
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        var engine = new FixtureEngine { ObserveTokens = true };
        using var budget = new JqBudget(engine, memory, CancellationToken.None);
        using var cache = new JqRegexCache(budget);
        using var pattern = cache.Rent("."u8.ToArray(), JqRegexCompileOptions.None);
        using var subject = PrepareNative(cache, pattern.Value, "abc", memory);
        using var creation = new CancellationTokenSource();
        using var first = new CancellationTokenSource();
        using var second = new CancellationTokenSource();
        using var cursor = subject.CreateCursor(JqRegexMatchOptions.None, creation.Token);
        creation.Cancel();
        Assert.True(cursor.MoveNext(first.Token));
        Assert.True(cursor.MoveNext(second.Token));
        Assert.Equal([first.Token, second.Token], engine.AdvanceTokens);
        second.Cancel();
        var failure = Assert.Throws<OperationCanceledException>(() => cursor.MoveNext(second.Token));
        Assert.Equal(second.Token, failure.CancellationToken);
        Assert.Equal(2, engine.AdvanceTokens.Count);
    }

    [Theory]
    [InlineData("[match(\"\";\"g\")|.offset]", "[0,1,2,3]\n")]
    [InlineData("[match(\"(?<x>.)\";\"g\")|[.offset,.length,.captures[0].name]]", "[[0,1,\"x\"],[1,1,\"x\"],[2,1,\"x\"]]\n")]
    [InlineData("test(\".\")", "true\n")]
    [InlineData("gsub(\"(?<x>.)\"; .x|gsub(\".\";\"x\"))", "\"xxx\"\n")]
    public async Task RuntimeUsesPreparedSubjectsForGlobalAndNestedOperations(string filter, string expected)
    {
        var memory = new BenchmarkMemoryBudget(128 * 1024);
        var engine = new FixtureEngine();
        var host = new MockFileSystem();
        host.SetStandardInput("\"aé🚀\"");
        using var invocation = JqCommandInvocation.CreateWithStandardDescriptors("jq", ["-c", filter], []);
        Assert.Equal(0, await Assert.IsType<Jq>(Jq.TryParse(invocation)).ExecuteAsync(host, engine, memory, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(engine.ProgramsCreated, engine.ProgramsDisposed);
        Assert.Equal(engine.SubjectsCreated, engine.SubjectsDisposed);
        Assert.Equal(0, memory.ReservedBytes);
        Assert.Equal(0, memory.ActiveScopes);
    }

    [Fact]
    public void BooleanSearchDoesNotCreateCaptureStorage()
    {
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        var engine = new FixtureEngine();
        using (var budget = new JqBudget(engine, memory, CancellationToken.None))
        using (var cache = new JqRegexCache(budget))
        using (var pattern = cache.Rent("(?<x>.)"u8.ToArray(), JqRegexCompileOptions.None))
        using (var subject = PrepareNative(cache, pattern.Value, "é", memory))
        {
            Assert.True(subject.IsMatch(JqRegexMatchOptions.None, CancellationToken.None));
            Assert.Equal(0, engine.CursorsCreated);
        }
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public void FailedRetentionReleasesEveryExistingBorrower()
    {
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        var engine = new FixtureEngine { RefuseAt = "retain" };
        using (var budget = new JqBudget(engine, memory, CancellationToken.None))
        using (var cache = new JqRegexCache(budget))
        using (var pattern = cache.Rent("."u8.ToArray(), JqRegexCompileOptions.None))
        using (var subject = PrepareNative(cache, pattern.Value, "x", memory))
        using (var cursor = subject.CreateCursor(JqRegexMatchOptions.None, CancellationToken.None))
        {
            Assert.True(cursor.MoveNext(CancellationToken.None));
            var error = Assert.Throws<JqQuotaException>(() => cursor.RetainCurrent(CancellationToken.None));
            Assert.True(error.IsMemoryAdmission);
        }
        Assert.Equal(1, engine.ProgramsDisposed);
        Assert.Equal(1, engine.SubjectsDisposed);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public void CacheOwnsUtf8ContentAndReusesEqualPatterns()
    {
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        var engine = new FixtureEngine();
        using (var budget = new JqBudget(engine, memory, CancellationToken.None))
        using (var cache = new JqRegexCache(budget))
        {
            byte[] bytes = "(?<x>.)"u8.ToArray();
            using var first = cache.Rent(bytes, JqRegexCompileOptions.None);
            bytes.AsSpan().Clear();
            using var equal = cache.Rent("(?<x>.)"u8.ToArray(), JqRegexCompileOptions.None);
            Assert.Same(first.Value, equal.Value);
            Assert.Equal(1, engine.ProgramsCreated);
            Assert.Equal(1, first.Value.Compiled.CaptureCount);
            var name = Assert.Single(first.Value.Compiled.GroupNames);
            Assert.Equal(1, name.GroupNumber);
            Assert.Equal("x"u8.ToArray(), name.NameUtf8.ToArray());
        }
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public void BorrowedAdvancesDoNotReadmitReusableBuffers()
    {
        var root = new BenchmarkMemoryBudget(128 * 1024);
        var memory = new CountingMemoryBudget(root);
        using (var budget = new JqBudget(new FixtureEngine(), memory, CancellationToken.None))
        using (var cache = new JqRegexCache(budget))
        using (var pattern = cache.Rent("."u8.ToArray(), JqRegexCompileOptions.None))
        using (var subject = PrepareNative(cache, pattern.Value, new string('é', 10000), memory))
        using (var cursor = subject.CreateCursor(JqRegexMatchOptions.None, CancellationToken.None))
        {
            var before = memory.Counts;
            int count = 0;
            while (cursor.MoveNext(CancellationToken.None))
            {
                Assert.Equal(new JqRegexCapture(true, 2 * count, 2 * count + 2), cursor.Current.Value);
                count++;
            }
            Assert.Equal(10000, count);
            Assert.Equal(before, memory.Counts);
        }
        Assert.Equal(0, root.ReservedBytes);
    }

    [Fact]
    public void NativeSubjectsBorrowFundedBytesWithoutWholeValueConversion()
    {
        const int length = 1024 * 1024;
        var memory = new BenchmarkMemoryBudget(length + 64 * 1024);
        using (var budget = new JqBudget(new FixtureEngine(), memory, CancellationToken.None))
        using (var cache = new JqRegexCache(budget))
        using (var pattern = cache.Rent("."u8.ToArray(), JqRegexCompileOptions.None))
        {
            Assert.True(memory.TryReserve(length + 32, out var owner));
            byte[] bytes = new byte[length];
            bytes.AsSpan().Fill((byte)'a');
            long before = GC.GetAllocatedBytesForCurrentThread();
            using var subject = cache.Prepare(pattern.Value, bytes, owner);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.InRange(allocated, 0, 16 * 1024);
            Assert.True(subject.IsMatch(JqRegexMatchOptions.None, CancellationToken.None));
            Assert.True(memory.ReservedBytes >= length);
        }
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnedCursorSurvivesReplacementInputSuspension(bool cancel)
    {
        var memory = new BenchmarkMemoryBudget(128 * 1024);
        var engine = new FixtureEngine();
        var inner = new MockFileSystem();
        inner.SetStandardInput("\"x\"\n\"y\"\n");
        var host = new SuspendedReadHost(inner);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var invocation = JqCommandInvocation.CreateWithStandardDescriptors("jq",
            ["-nr", "\"aé\" | gsub(\"(?<x>.)\";input)"], []);
        Task<int> execution = Assert.IsType<Jq>(Jq.TryParse(invocation)).ExecuteAsync(host, engine, memory, cancellation.Token);
        await host.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.False(execution.IsCompleted);
            Assert.Equal(1, engine.SubjectsCreated);
            Assert.Equal(0, engine.SubjectsDisposed);
            Assert.Equal(0, engine.ProgramsDisposed);
            Assert.True(memory.ReservedBytes > 0);
            if (cancel) cancellation.Cancel();
            Assert.False(execution.IsCompleted);
        }
        finally { host.Release.TrySetResult(); }
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        else Assert.Equal(0, await execution);
        Assert.Equal(cancel ? "" : "xy\n", inner.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(inner.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(engine.SubjectsCreated, engine.SubjectsDisposed);
        Assert.Equal(engine.ProgramsCreated, engine.ProgramsDisposed);
        Assert.Equal(0, memory.ReservedBytes);
        Assert.Equal(0, memory.ActiveScopes);
    }

    private static JqRegexCache.SubjectLease PrepareNative(JqRegexCache cache,
        JqRegexCache.Pattern pattern, string text, IMemoryBudget memory)
    {
        int length = Encoding.UTF8.GetByteCount(text);
        Assert.True(memory.TryReserve(32L + length, out var owner));
        byte[] bytes;
        try { bytes = Encoding.UTF8.GetBytes(text); }
        catch { owner.Dispose(); throw; }
        return cache.Prepare(pattern, bytes, owner);
    }

    private sealed class SuspendedReadHost(MockFileSystem inner) : IJqHost
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<JqByteReadResult> ReadBytesAsync(JqFileDescriptor descriptor, Memory<byte> buffer, CancellationToken token)
        {
            Entered.TrySetResult();
            await Release.Task.ConfigureAwait(false);
            return await inner.ReadBytesAsync(descriptor, buffer, token).ConfigureAwait(false);
        }
        public Task<JqAppendResult> AppendWhileOpenAsync(JqFileDescriptor descriptor, ReadOnlyMemory<byte> bytes, CancellationToken token) =>
            inner.AppendWhileOpenAsync(descriptor, bytes, token);
        public Task<JqOpenedFile> OpenReadAsync(JqPathRequest path, CancellationToken token) => inner.OpenReadAsync(path, token);
        public Task<JqCloseResult> CloseDescriptorAsync(JqFileDescriptor descriptor, CancellationToken token) => inner.CloseDescriptorAsync(descriptor, token);
    }

    [Fact]
    public void LongIterationReusesLiveStorage()
    {
        var memory = new BenchmarkMemoryBudget(128 * 1024);
        var engine = new FixtureEngine();
        using (var budget = new JqBudget(engine, memory, CancellationToken.None))
        using (var cache = new JqRegexCache(budget))
        using (var pattern = cache.Rent("."u8.ToArray(), JqRegexCompileOptions.None))
        using (var subject = PrepareNative(cache, pattern.Value, new string('é', 10000), memory))
        using (var cursor = subject.CreateCursor(JqRegexMatchOptions.None, CancellationToken.None))
        {
            long live = memory.ReservedBytes;
            int count = 0;
            while (cursor.MoveNext(CancellationToken.None))
            {
                using var result = cursor.RetainCurrent(CancellationToken.None);
                Assert.Equal(new JqRegexCapture(true, 2 * count, 2 * count + 2), result.Value);
                count++;
                Assert.InRange(memory.ReservedBytes, live, live + 128);
            }
            Assert.Equal(10000, count);
            Assert.Equal(live, memory.ReservedBytes);
            Assert.True(memory.PeakBytes < 24 * 1024);
        }
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData("compile")]
    [InlineData("prepare")]
    [InlineData("cursor")]
    [InlineData("advance")]
    public async Task ProviderRefusalRemainsTerminalAtEveryOperationBoundary(string stage)
    {
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        var engine = new FixtureEngine { RefuseAt = stage };
        var host = new MockFileSystem();
        host.AddFile("/input", "\"a\"");
        using var invocation = JqCommandInvocation.CreateWithStandardDescriptors("jq",
            ["-r", "\"prefix\", (try match(\".\";\"g\") catch \"caught\"), \"later\"", "/input"], []);
        Assert.Equal(5, await Assert.IsType<Jq>(Jq.TryParse(invocation)).ExecuteAsync(host, engine, memory, CancellationToken.None));
        Assert.Equal("prefix\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("jq: memory budget exceeded\n", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Single(host.ClosedDescriptors);
        Assert.Equal(engine.ProgramsCreated, engine.ProgramsDisposed);
        Assert.Equal(engine.SubjectsCreated, engine.SubjectsDisposed);
        Assert.Equal(0, memory.ReservedBytes);
        Assert.Equal(0, memory.ActiveScopes);
    }

    // A deliberately narrow provider fixture: only scalar, named-scalar and
    // empty patterns. It models the actual storage it owns, reuses cursor
    // capture slots and makes every operation/lifetime boundary observable.
    private sealed class FixtureEngine : IJqRegexEngine
    {
        internal string? RefuseAt { get; init; }
        internal bool ObserveTokens { get; init; }
        internal ReadOnlyMemory<byte> NameUtf8 { get; init; } = "x"u8.ToArray();
        internal bool ScrubNamesOnDispose { get; init; }
        internal int MetadataReads { get; private set; }
        internal int ProgramsCreated { get; private set; }
        internal int ProgramsDisposed { get; private set; }
        internal int SubjectsCreated { get; private set; }
        internal int SubjectsDisposed { get; private set; }
        internal int CursorsCreated { get; private set; }
        internal List<CancellationToken> AdvanceTokens { get; } = [];

        private IMemoryReservation? Admit(IMemoryBudget? budget, long bytes, string stage)
        {
            if (RefuseAt == stage) throw new JqRegexResourceException("fixture refusal", true);
            if (budget is null) return null;
            if (!budget.TryReserve(bytes, out var memory)) throw new JqRegexResourceException("shared refusal", true);
            return memory;
        }

        public bool TryCompile(ReadOnlyMemory<byte> pattern, JqRegexCompileOptions options, IMemoryBudget? budget,
            CancellationToken token, [NotNullWhen(true)] out IJqCompiledRegex? regex, out string? error)
        {
            token.ThrowIfCancellationRequested();
            if (!pattern.IsEmpty && !pattern.Span.SequenceEqual("."u8) && !pattern.Span.SequenceEqual("(?<x>.)"u8))
            {
                regex = null;
                error = "unsupported fixture pattern";
                return false;
            }
            var memory = Admit(budget, 128L + NameUtf8.Length, "compile");
            regex = new Compiled(this, pattern, memory);
            ProgramsCreated++;
            error = null;
            return true;
        }

        private sealed class Compiled(FixtureEngine engine, ReadOnlyMemory<byte> pattern, IMemoryReservation? memory) : IJqCompiledRegex
        {
            private readonly byte[] _name = engine.NameUtf8.ToArray();
            private bool _disposed;
            public int CaptureCount => pattern.Span.SequenceEqual("(?<x>.)"u8) ? 1 : 0;
            public IReadOnlyList<JqRegexGroupName> GroupNames
            {
                get
                {
                    engine.MetadataReads++;
                    return CaptureCount == 0 ? Array.Empty<JqRegexGroupName>()
                        : Array.AsReadOnly(new[] { new JqRegexGroupName(1, _name) });
                }
            }

            public IJqRegexSubject Prepare(ReadOnlyMemory<byte> bytes, IMemoryBudget? budget, CancellationToken token)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                token.ThrowIfCancellationRequested();
                var owner = engine.Admit(budget, 64, "prepare");
                engine.SubjectsCreated++;
                return new Subject(engine, pattern.Length == 0, CaptureCount, bytes, budget, owner);
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                engine.ProgramsDisposed++;
                if (engine.ScrubNamesOnDispose) Array.Fill(_name, (byte)'?');
                memory?.Dispose();
            }
        }

        private sealed class Subject(FixtureEngine engine, bool empty, int captureCount,
            ReadOnlyMemory<byte> bytes, IMemoryBudget? budget, IMemoryReservation? memory) : IJqRegexSubject
        {
            private bool _disposed;

            public bool IsMatch(JqRegexMatchOptions options, CancellationToken token)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                token.ThrowIfCancellationRequested();
                return empty ? (options & JqRegexMatchOptions.NotEmpty) == 0 : bytes.Length > 0;
            }

            public IJqRegexCursor CreateCursor(JqRegexMatchOptions options, CancellationToken token)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                token.ThrowIfCancellationRequested();
                var owner = engine.Admit(budget, 64 + 12L * (captureCount + 1), "cursor");
                engine.CursorsCreated++;
                return new Cursor(engine, empty, captureCount, bytes, options, budget, owner);
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                engine.SubjectsDisposed++;
                memory?.Dispose();
            }
        }

        private sealed class Cursor(FixtureEngine engine, bool empty, int captureCount,
            ReadOnlyMemory<byte> bytes, JqRegexMatchOptions options, IMemoryBudget? budget,
            IMemoryReservation? memory) : IJqRegexCursor
        {
            private readonly JqRegexCapture[] _captures = new JqRegexCapture[captureCount + 1];
            private int _next;
            private bool _hasCurrent;
            private bool _disposed;

            public bool MoveNext(CancellationToken token)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (engine.ObserveTokens) engine.AdvanceTokens.Add(token);
                token.ThrowIfCancellationRequested();
                if (engine.RefuseAt == "advance") throw new JqRegexResourceException("fixture refusal", true);
                _hasCurrent = false;
                if (_next > bytes.Length || (!empty && _next == bytes.Length)
                    || empty && (options & JqRegexMatchOptions.NotEmpty) != 0) return false;
                int start = _next;
                int width = 0;
                if (_next < bytes.Length)
                {
                    Rune.DecodeFromUtf8(bytes.Span[_next..], out _, out width);
                    _next += width;
                }
                else _next++;
                var capture = new JqRegexCapture(true, start, empty ? start : start + width);
                _captures.AsSpan().Fill(capture);
                _hasCurrent = true;
                return true;
            }

            public JqRegexMatch Current => _hasCurrent && !_disposed ? new(_captures, null)
                : throw new InvalidOperationException("No current match.");

            public JqRegexMatch RetainCurrent(CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                _ = Current;
                var owner = engine.Admit(budget, 32 + 12L * _captures.Length, "retain");
                return new JqRegexMatch(_captures.ToArray(), owner);
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                _hasCurrent = false;
                memory?.Dispose();
            }
        }
    }
}
