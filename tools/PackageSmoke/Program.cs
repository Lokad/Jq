using Lokad.Jq;
using Lokad.Jq.Tests;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json;

if (args.Length != 0)
{
    if (args.Length == 4 && (args[0] == "--symbols" || args[0] == "--symbols-hosted"))
    {
        await PackageSymbols.VerifyAsync(args[1], args[2], args[3], args[0] == "--symbols-hosted").ConfigureAwait(false);
        return 0;
    }
    if (args.Length != 3 || args[0] != "--vectors" || !int.TryParse(args[2], out int expectedMisses) || expectedMisses < 0)
        throw new ArgumentException("Use --vectors <JSONL file> <expected misses> or --symbols/--symbols-hosted <snupkg> <source checkout> <commit>.");

    // This mode consumes caller-supplied triples; it never reads an inspection checkout.
    int passed = 0, missed = 0, escapes = 0;
    foreach (string line in File.ReadLines(args[1]))
    {
        using var vector = JsonDocument.Parse(line);
        string program = vector.RootElement.GetProperty("prog").GetString()
            ?? throw new ArgumentException("Missing vector program.");
        string input = vector.RootElement.GetProperty("input").GetString()
            ?? throw new ArgumentException("Missing vector input.");
        string[] expected = vector.RootElement.GetProperty("expected").EnumerateArray()
            .Select(value => value.GetString() ?? throw new ArgumentException("Missing expected value.")).ToArray();
        var host = new SmokeHost();
        host.SetInput(input);
        try
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await Command(["-c", program], []).ExecuteAsync(host, cancellation.Token);
            if (ValuesMatch(host.Output(JqFileDescriptor.StdOut), expected))
                passed++;
            else
            {
                missed++;
                Console.WriteLine("MISS " + program + " | " + host.Output(JqFileDescriptor.StdErr).Trim());
            }
        }
        catch (Exception error)
        {
            escapes++;
            Console.WriteLine("ESCAPE " + program + " | " + error.GetType().Name);
        }
    }
    if (ValuesMatch("1\n", ["2"]) || ValuesMatch("1\n2\n", ["2", "1"])
        || ValuesMatch("1\n2\n", ["1"])
        || ValuesMatch("0.12345678901234568\n", ["0.12345678901234567890123456789"]))
        throw new InvalidOperationException("Corrupted value, order, extra-output and decimal controls must fail.");
    Console.WriteLine($"Vectors: {passed} pass, {missed} miss, {escapes} escapes/timeouts; expected misses: {expectedMisses}.");
    return missed == expectedMisses && escapes == 0 ? 0 : 1;
}

int checks = 0;
{
    var host = new SmokeHost();
    host.SetInput("{\"items\":[{\"name\":\"a\"},{\"name\":\"b\"}]}");
    Check("JSON", await Command([".items[].name"], []).ExecuteAsync(host, CancellationToken.None) == 0
        && host.Output(JqFileDescriptor.StdOut) == "\"a\"\n\"b\"\n" && host.Output(JqFileDescriptor.StdErr) == "");
}
{
    var host = new SmokeHost();
    host.SetInput("\"abc\"");
    Check("regex", await Command(["test(\"^a\")"], []).ExecuteAsync(host, CancellationToken.None) == 0
        && host.Output(JqFileDescriptor.StdOut) == "true\n" && host.Output(JqFileDescriptor.StdErr) == "");
}
{
    var host = new SmokeHost();
    host.AddFile("/data", "[1,2]");
    Check("owned file", await Command([".[]", "/data"], []).ExecuteAsync(host, CancellationToken.None) == 0
        && host.Output(JqFileDescriptor.StdOut) == "1\n2\n" && host.Output(JqFileDescriptor.StdErr) == ""
        && host.ClosedDescriptors.Count == 1 && host.OpenFileCount == 0);
}
{
    var host = new SmokeHost();
    host.AddFile("/modules/values.jq", "def twice: . * 2;");
    Check("hosted module", await Command(["-n", "-L", "/modules", "import \"values\" as v; 3 | v::twice"], [])
        .ExecuteAsync(host, CancellationToken.None) == 0
        && host.Output(JqFileDescriptor.StdOut) == "6\n" && host.Output(JqFileDescriptor.StdErr) == ""
        && host.ClosedDescriptors.Count == 1 && host.OpenFileCount == 0);
}
{
    var host = new SmokeHost();
    var invocation = JqCommandInvocation.CreateWithStandardDescriptors("jq",
        ["-n", "-c", "[$ENV.REGION, env.REGION, now]"], [new JqEnvironmentVariable("REGION", "example")],
        new JqClock(new EpochClock(), TimeZoneInfo.Utc));
    var command = Jq.TryParse(invocation) ?? throw new InvalidOperationException("Expected a jq command.");
    Check("explicit environment and clock", await command.ExecuteAsync(host, CancellationToken.None) == 0
        && host.Output(JqFileDescriptor.StdOut) == "[\"example\",\"example\",0]\n" && host.Output(JqFileDescriptor.StdErr) == "");
}
{
    var host = new SmokeHost();
    host.SetInput("\"nan\"");
    Check("non-finite parsing", await Command(["fromjson | isnan"], []).ExecuteAsync(host, CancellationToken.None) == 0
        && host.Output(JqFileDescriptor.StdOut) == "true\n" && host.Output(JqFileDescriptor.StdErr) == "");
}
{
    var host = new SmokeHost();
    var policy = new JqExecutionPolicy { MaximumOutputBytes = 2 };
    Check("output policy", await Command(["-n", "1,2"], []).ExecuteAsync(host, policy, CancellationToken.None) == 5
        && host.Output(JqFileDescriptor.StdOut) == "1\n" && host.Output(JqFileDescriptor.StdErr) == "jq: output exceeds the 2-byte limit\n");
}
{
    var host = new SmokeHost();
    var policy = new JqExecutionPolicy { MaximumRegexWork = 32 };
    Check("terminal regex policy", await Command(["-n", "(\"a\" * 32 + \"b\") | try test(\"(a+)+$\") catch \"caught\""], [])
        .ExecuteAsync(host, policy, CancellationToken.None) == 5
        && host.Output(JqFileDescriptor.StdOut) == "" && host.Output(JqFileDescriptor.StdErr) == "jq: regex work limit exceeded\n");
}
{
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    var host = new SmokeHost();
    bool cancelled = false;
    try
    {
        await Command(["-n", "1"], []).ExecuteAsync(host, cancellation.Token);
    }
    catch (OperationCanceledException)
    {
        cancelled = true;
    }
    Check("cancellation", cancelled && host.Output(JqFileDescriptor.StdOut) == "" && host.Output(JqFileDescriptor.StdErr) == "");
}
{
    var host = new SmokeHost();
    host.SetInput("7");
    Check("borrowed descriptors", await Command(["."], []).ExecuteAsync(host, CancellationToken.None) == 0
        && host.Output(JqFileDescriptor.StdOut) == "7\n" && host.ClosedDescriptors.Count == 0);
}
{
    var memory = new TestMemoryBudget(64 * 1024);
    if (!JqCommandInvocation.TryCreateWithStandardDescriptors("jq",
        ["-n", "-r", "--arg", "name", "sample", "$name"], [], memory, out var invocation))
        throw new InvalidOperationException("Expected governed snapshot admission.");
    using (invocation)
    {
        long retained = memory.ReservedBytes;
        var command = Jq.TryParse(invocation) ?? throw new InvalidOperationException("Expected a jq command.");
        bool reused = retained > 0;
        for (int run = 0; run < 3; run++)
        {
            var host = new SmokeHost();
            reused &= await command.ExecuteAsync(host, CancellationToken.None) == 0
                && host.Output(JqFileDescriptor.StdOut) == "sample\n"
                && host.Output(JqFileDescriptor.StdErr) == ""
                && memory.ReservedBytes == retained;
        }
        invocation.Dispose();
        Check("governed snapshot and sequential reuse", reused && memory.ReservedBytes == 0);
    }
}
{
    var memory = new TestMemoryBudget(8);
    if (!memory.TryReserve(4, out var retained))
        throw new InvalidOperationException("Expected caller admission.");
    using (retained)
    {
        Check("snapshot refusal preserves caller", !JqCommandInvocation.TryCreateWithStandardDescriptors(
            "jq", ["."], [], memory, out var invocation) && invocation is null
            && retained.Bytes == 4 && memory.ReservedBytes == 4);
    }
}
{
    const long limit = 128 * 1024;
    var memory = new TestMemoryBudget(limit);
    using var caller = memory.CreateScope();
    if (!JqCommandInvocation.TryCreateWithStandardDescriptors("jq", [".", "/data"], [], caller, out var invocation))
        throw new InvalidOperationException("Expected governed snapshot admission.");
    using (invocation)
    using (var cancellation = new CancellationTokenSource())
    {
        long snapshot = memory.ReservedBytes;
        var command = Jq.TryParse(invocation) ?? throw new InvalidOperationException("Expected a jq command.");
        var firstInner = new SmokeHost();
        var secondInner = new SmokeHost();
        firstInner.AddFile("/data", "1\n");
        secondInner.AddFile("/data", "1\n");
        var first = new SmokeBorrowHost(firstInner);
        var second = new SmokeBorrowHost(secondInner);
        Task<int> firstCall = command.ExecuteAsync(first, cancellation.Token);
        Task<int> secondCall = command.ExecuteAsync(second, CancellationToken.None);
        var pending = await Task.WhenAll(first.Ready.Task, second.Ready.Task).WaitAsync(TimeSpan.FromSeconds(10));
        if (!memory.TryReserve(limit - memory.ReservedBytes, out var pressure))
            throw new InvalidOperationException("Expected competing caller admission.");
        using (pressure)
        {
            var refused = new SmokeHost();
            Check("overlapping commands share root", await command.ExecuteAsync(refused, CancellationToken.None) == 5
                && refused.Output(JqFileDescriptor.StdOut) == ""
                && refused.Output(JqFileDescriptor.StdErr) == "jq: memory budget exceeded\n"
                && memory.ReservedBytes == limit && pending.All(bytes => bytes.Span.SequenceEqual("1\n"u8)));
            cancellation.Cancel();
            first.Release.SetResult();
            second.Release.SetResult();
            await Task.WhenAll(first.Closing.Task, second.Closing.Task).WaitAsync(TimeSpan.FromSeconds(10));
            Check("governed cleanup retains pending storage", !firstCall.IsCompleted && !secondCall.IsCompleted
                && memory.ReservedBytes >= pressure.Bytes + snapshot + pending.Sum(bytes => bytes.Length)
                && memory.ReservedBytes <= limit);
            first.CloseRelease.SetResult();
            second.CloseRelease.SetResult();
            bool cancelled = false;
            try { await firstCall; }
            catch (OperationCanceledException) { cancelled = true; }
            Check("governed cancellation and refunds", cancelled && await secondCall == 0
                && firstInner.Output(JqFileDescriptor.StdOut) == ""
                && secondInner.Output(JqFileDescriptor.StdOut) == "1\n"
                && firstInner.ClosedDescriptors.Count == 1 && firstInner.OpenFileCount == 0
                && secondInner.ClosedDescriptors.Count == 1 && secondInner.OpenFileCount == 0
                && memory.ReservedBytes == pressure.Bytes + snapshot);
        }
        invocation.Dispose();
        if (memory.ReservedBytes != 0 || !caller.TryReserve(limit, out var available))
            throw new InvalidOperationException("The borrowed root was not reusable.");
        available.Dispose();
    }
}
{
    var host = new SmokeHost();
    var policy = new JqExecutionPolicy { MaximumAllocationWorkBytes = 3 };
    Check("allocation work is independent", await Command(["-n", "1"], []).ExecuteAsync(host, policy, CancellationToken.None) == 5
        && host.Output(JqFileDescriptor.StdOut) == ""
        && host.Output(JqFileDescriptor.StdErr) == "jq: allocation work allowance exceeded\n");
}
{
    const long limit = 64 * 1024;
    var memory = new TestMemoryBudget(limit);
    if (!JqCommandInvocation.TryCreateWithStandardDescriptors("jq", ["-nr", "\"x\",\"xx\",\"xxx\""],
        [], memory, out var invocation))
        throw new InvalidOperationException("Expected governed snapshot admission.");
    using (invocation)
    {
        long snapshot = memory.ReservedBytes;
        IMemoryReservation? pressure = null;
        var host = new SmokeHost();
        host.BeforeAppend = descriptor =>
        {
            if (descriptor == JqFileDescriptor.StdOut && pressure is null
                && !memory.TryReserve(limit - memory.ReservedBytes - 3, out pressure))
                throw new InvalidOperationException("Expected competing caller admission.");
        };
        try
        {
            var command = Jq.TryParse(invocation) ?? throw new InvalidOperationException("Expected a jq command.");
            int status = await command.ExecuteAsync(host, CancellationToken.None);
            bool stopped = status == 5 && host.Output(JqFileDescriptor.StdOut) == "x\n"
                && host.Output(JqFileDescriptor.StdErr) == "jq: memory budget exceeded\n"
                && pressure is not null && memory.ReservedBytes == snapshot + pressure.Bytes;
            pressure?.Dispose();
            var later = new SmokeHost();
            Check("governed padding refusal stops and permits later reuse", stopped
                && await command.ExecuteAsync(later, CancellationToken.None) == 0
                && later.Output(JqFileDescriptor.StdOut) == "x\nxx\nxxx\n"
                && later.Output(JqFileDescriptor.StdErr) == "" && memory.ReservedBytes == snapshot);
        }
        finally { pressure?.Dispose(); }
    }
    if (memory.ReservedBytes != 0) throw new InvalidOperationException("Invocation storage was not refunded.");
}
{
    var memory = new TestMemoryBudget(64 * 1024);
    if (!JqCommandInvocation.TryCreateWithStandardDescriptors("jq", ["-r",
        "\"prefix\", (\"note\" | stderr | empty), try (\"x\" * 1000000) catch \"caught\", \"later\"",
        "/first", "/later"], [], memory, out var invocation))
        throw new InvalidOperationException("Expected governed snapshot admission.");
    using (invocation)
    {
        long snapshot = memory.ReservedBytes;
        var host = new SmokeHost { StderrResult = JqAppendResult.Closed };
        host.AddFile("/first", "0\n");
        host.AddFile("/later", "1\n");
        var command = Jq.TryParse(invocation) ?? throw new InvalidOperationException("Expected a jq command.");
        Check("governed refusal keeps failure after stderr closure", await command.ExecuteAsync(host, CancellationToken.None) == 5
            && host.Output(JqFileDescriptor.StdOut) == "prefix\n" && host.Output(JqFileDescriptor.StdErr) == "note"
            && host.ClosedDescriptors.Count == 1 && host.OpenFileCount == 0 && memory.ReservedBytes == snapshot);
    }
    if (memory.ReservedBytes != 0) throw new InvalidOperationException("Invocation storage was not refunded.");
}
Console.WriteLine($"Package smoke: {checks} checks pass.");
return 0;

void Check(string name, bool success)
{
    if (!success) throw new InvalidOperationException("Package smoke failed: " + name);
    checks++;
    Console.WriteLine("ok " + name);
}

static Jq Command(string[] arguments, JqEnvironmentVariable[] environment) =>
    Jq.TryParse(JqCommandInvocation.CreateWithStandardDescriptors("jq", arguments, environment))
        ?? throw new InvalidOperationException("Expected a jq command.");

static bool ValuesMatch(string output, string[] expected)
{
    string[] actual = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
    return actual.Length == expected.Length && actual.Zip(expected)
        .All(pair => JsonNode.DeepEquals(JsonNode.Parse(pair.First), JsonNode.Parse(pair.Second)));
}

sealed class EpochClock : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch;
}

sealed class SmokeHost : IJqHost
{
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);
    private readonly Dictionary<int, byte[]> _inputs = new();
    private readonly Dictionary<int, MemoryStream> _outputs = new();
    private int _nextDescriptor = 10;
    public List<JqFileDescriptor> ClosedDescriptors { get; } = [];
    public int OpenFileCount => _inputs.Keys.Count(id => id >= 10);
    internal Action<JqFileDescriptor>? BeforeAppend { get; set; }
    internal JqAppendResult? StderrResult { get; init; }

    public void SetInput(string text) => _inputs[JqFileDescriptor.StdIn.Id] = Encoding.UTF8.GetBytes(text);
    public void AddFile(string path, string text) => _files[path] = Encoding.UTF8.GetBytes(text);
    public string Output(JqFileDescriptor descriptor) =>
        _outputs.TryGetValue(descriptor.Id, out var output) ? Encoding.UTF8.GetString(output.ToArray()) : "";

    public ValueTask<JqByteReadResult> ReadBytesAsync(JqFileDescriptor descriptor, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_inputs.TryGetValue(descriptor.Id, out var bytes) || bytes.Length == 0)
            return ValueTask.FromResult(JqByteReadResult.EndOfFile);
        int length = Math.Min(bytes.Length, buffer.Length);
        bytes.AsSpan(0, length).CopyTo(buffer.Span);
        _inputs[descriptor.Id] = bytes[length..];
        return ValueTask.FromResult(JqByteReadResult.Success(length, length == bytes.Length));
    }

    public Task<JqAppendResult> AppendWhileOpenAsync(JqFileDescriptor descriptor, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        BeforeAppend?.Invoke(descriptor);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_outputs.TryGetValue(descriptor.Id, out var output))
            _outputs[descriptor.Id] = output = new MemoryStream();
        output.Write(content.Span);
        return Task.FromResult(descriptor == JqFileDescriptor.StdErr && StderrResult is { } result ? result : JqAppendResult.Open);
    }

    public Task<JqOpenedFile> OpenReadAsync(JqPath path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_files.TryGetValue(path.Path, out var bytes))
            return Task.FromResult(JqOpenedFile.Failure("Missing virtual file."));
        var descriptor = new JqFileDescriptor(_nextDescriptor++);
        _inputs[descriptor.Id] = bytes;
        return Task.FromResult(JqOpenedFile.Success(descriptor));
    }

    public Task<int> CloseDescriptorAsync(JqFileDescriptor descriptor, CancellationToken cancellationToken)
    {
        if (descriptor.Id < 10) throw new InvalidOperationException("Borrowed descriptor was closed.");
        ClosedDescriptors.Add(descriptor);
        _inputs.Remove(descriptor.Id);
        return Task.FromResult(0);
    }
}

// Cancellation cannot end a borrow before the host completes its operation.
sealed class SmokeBorrowHost(SmokeHost inner) : IJqHost
{
    internal TaskCompletionSource<ReadOnlyMemory<byte>> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource Closing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource CloseRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ValueTask<JqByteReadResult> ReadBytesAsync(JqFileDescriptor descriptor,
        Memory<byte> buffer, CancellationToken cancellationToken) =>
        inner.ReadBytesAsync(descriptor, buffer, cancellationToken);

    public async Task<JqAppendResult> AppendWhileOpenAsync(JqFileDescriptor descriptor,
        ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        if (descriptor == JqFileDescriptor.StdOut && !Ready.Task.IsCompleted)
        {
            Ready.SetResult(content);
            await Release.Task.ConfigureAwait(false);
        }
        return await inner.AppendWhileOpenAsync(descriptor, content, cancellationToken).ConfigureAwait(false);
    }

    public Task<JqOpenedFile> OpenReadAsync(JqPath path, CancellationToken cancellationToken) =>
        inner.OpenReadAsync(path, cancellationToken);

    public async Task<int> CloseDescriptorAsync(JqFileDescriptor descriptor, CancellationToken cancellationToken)
    {
        Closing.TrySetResult();
        await CloseRelease.Task.ConfigureAwait(false);
        return await inner.CloseDescriptorAsync(descriptor, cancellationToken).ConfigureAwait(false);
    }
}
