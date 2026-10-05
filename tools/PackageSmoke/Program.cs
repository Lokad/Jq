using Lokad.Jq;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json;

if (args.Length != 0)
{
    if (args.Length != 3 || args[0] != "--vectors" || !int.TryParse(args[2], out int expectedMisses) || expectedMisses < 0)
        throw new ArgumentException("Use --vectors <JSONL file> <expected misses>.");

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
    var policy = new JqExecutionPolicy { MaximumRegexWork = 1 };
    Check("terminal regex policy", await Command(["-n", "\"x\" | try test(\"x\") catch \"caught\""], [])
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
        cancellationToken.ThrowIfCancellationRequested();
        if (!_outputs.TryGetValue(descriptor.Id, out var output))
            _outputs[descriptor.Id] = output = new MemoryStream();
        output.Write(content.Span);
        return Task.FromResult(JqAppendResult.Open);
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
