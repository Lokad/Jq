using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text;
using Lokad.Jq;
using Lokad.Jq.Benchmarking;

int checks = 0;
#if NET11_0
const int expectedMajor = 11;
#else
const int expectedMajor = 10;
#endif
Check("actual runtime target", Environment.Version.Major == expectedMajor);
Check("matching library asset", typeof(Jq).Assembly.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName
    == $".NETCoreApp,Version=v{expectedMajor}.0");
Check("no provider assembly references", !typeof(Jq).Assembly.GetReferencedAssemblies().Any(name =>
    name.Name is { } text && (text.StartsWith("Lokad.Utf8Regex", StringComparison.Ordinal)
        || text == "System.Text.RegularExpressions")));
Check("no bundled provider type", typeof(Jq).Assembly.GetType("Lokad.Jq.JqUtf8RegexEngine") is null);
await RunAsync("[range(3)]", "[0,1,2]\n", "", 0, null);
await RunAsync("if false then test(\"x\") else 42 end", "42\n", "", 0, null);
await RunAsync("try test(\"x\") catch .", "\"regex engine is not configured\"\n", "", 0, null);
await RunAsync("test(\"x\")?", "", "", 0, null);
await RunAsync("\"ready\",(try (\"x\"*65536) catch \"caught\")", "\"ready\"\n", "jq: memory budget exceeded\n", 5, null);
var engine = new DecliningEngine();
await RunAsync("try test(\"x\") catch .", "\"application regex adapter\"\n", "", 0, engine);
Check("explicit application adapter invoked", engine.Compilations == 1);
Check("no provider assemblies loaded", !AppDomain.CurrentDomain.GetAssemblies().Any(assembly =>
    assembly.GetName().Name is { } name && name.StartsWith("Lokad.Utf8Regex", StringComparison.Ordinal)));
Console.WriteLine($"Core-only package smoke: {checks} checks pass.");

async Task RunAsync(string filter, string output, string error, int expectedStatus, IJqRegexEngine? regexEngine)
{
    var memory = new BenchmarkMemoryBudget(128 * 1024);
    var host = new CoreHost();
    if (!JqCommandInvocation.TryCreateWithStandardDescriptors("jq", ["-c", filter], [], memory, out var invocation))
        throw new InvalidOperationException("Invocation admission failed.");
    using (invocation)
    {
        var command = Jq.TryParse(invocation) ?? throw new InvalidOperationException("Expected jq.");
        int status = regexEngine is null
            ? await command.ExecuteAsync(host, CancellationToken.None)
            : await command.ExecuteAsync(host, regexEngine, CancellationToken.None);
        Check(filter, status == expectedStatus && host.Output(JqFileDescriptor.StdOut) == output
            && host.Output(JqFileDescriptor.StdErr) == error);
    }
    Check(filter + " refunds", memory.ReservedBytes == 0 && memory.ActiveScopes == 0);
}

void Check(string name, bool success)
{
    if (!success) throw new InvalidOperationException("Core-only package smoke failed: " + name);
    checks++;
}

sealed class DecliningEngine : IJqRegexEngine
{
    public int Compilations { get; private set; }
    public bool TryCompile(string pattern, JqRegexCompileOptions options, IMemoryBudget? memoryBudget,
        CancellationToken cancellationToken, [NotNullWhen(true)] out IJqCompiledRegex? regex, out string? error)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Compilations++;
        regex = null;
        error = "application regex adapter";
        return false;
    }
}

sealed class CoreHost : IJqHost
{
    private readonly byte[] _input = "\"x\"\n"u8.ToArray();
    private readonly MemoryStream _output = new();
    private readonly MemoryStream _error = new();
    private int _position;

    public string Output(JqFileDescriptor descriptor) => Encoding.UTF8.GetString(
        (descriptor == JqFileDescriptor.StdOut ? _output : _error).ToArray());

    public ValueTask<JqByteReadResult> ReadBytesAsync(JqFileDescriptor descriptor, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int count = Math.Min(buffer.Length, _input.Length - _position);
        _input.AsMemory(_position, count).CopyTo(buffer);
        _position += count;
        return ValueTask.FromResult(JqByteReadResult.Success(count, _position == _input.Length));
    }

    public Task<JqOpenedFile> OpenReadAsync(JqPath path, CancellationToken cancellationToken) =>
        Task.FromResult(JqOpenedFile.Failure(new JqOperationError(JqOperationErrorKind.NotFound, "path not found")));

    public Task<JqAppendResult> AppendWhileOpenAsync(JqFileDescriptor descriptor, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        (descriptor == JqFileDescriptor.StdOut ? _output : _error).Write(content.Span);
        return Task.FromResult(JqAppendResult.Open);
    }

    public Task<JqCloseResult> CloseDescriptorAsync(JqFileDescriptor descriptor, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Standard descriptors are borrowed.");
}
