using Lokad.Jq.Examples;
using Lokad.Jq.Benchmarking;
using Lokad.Jq;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json;
using System.Reflection;
using System.Runtime.Versioning;

#if NET11_0
const int expectedMajor = 11;
#else
const int expectedMajor = 10;
#endif
if (Environment.Version.Major != expectedMajor ||
    typeof(Jq).Assembly.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName != $".NETCoreApp,Version=v{expectedMajor}.0")
    throw new InvalidOperationException("Consumer must run on its target runtime and load the matching library asset.");
Console.WriteLine($"Verified actual .NET {expectedMajor} runtime and matching library asset.");

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
            await Command(["-c", program], []).ExecuteAsync(host, Utf8RegexAdapter.Instance, cancellation.Token);
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
    var memory = new BenchmarkMemoryBudget(256 * 1024);
    Check("regex scalar matching and backreferences", await Command(["-nac",
        "[ (\"🚀🚀x\"|match(\"(?<pair>.)\\\\k<pair>\")|[.offset,.length,.string]), (\"🚀\"|test(\"^[^x]$\")) ]"], [])
        .ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None) == 0
        && host.Output(JqFileDescriptor.StdOut) == "[[0,2,\"\\ud83d\\ude80\\ud83d\\ude80\"],true]\n"
        && host.Output(JqFileDescriptor.StdErr) == "" && memory.ReservedBytes == 0);
}
{
    var host = new SmokeHost();
    host.AddFile("/first", "\"x\"\n");
    host.AddFile("/later", "\"later\"\n");
    var memory = new BenchmarkMemoryBudget(1024 * 1024);
    string pattern = new string('(', 300) + "x" + new string(')', 300);
    Check("provider construction failure stays terminal", await Command(["-r", "--arg", "pattern", pattern,
        "\"prefix\", (try test($pattern) catch \"caught\")?, \"after\"", "/first", "/later"], [])
        .ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None) == 5
        && host.Output(JqFileDescriptor.StdOut) == "prefix\n"
        && host.Output(JqFileDescriptor.StdErr).StartsWith("jq: regex compilation failed: ", StringComparison.Ordinal)
        && host.ClosedDescriptors.Count == 1 && host.OpenFileCount == 0 && memory.ReservedBytes == 0);
}
foreach (string filter in new[] { "try @bogus catch .", "try format(\"bogus\") catch ." })
{
    var host = new SmokeHost();
    var memory = new BenchmarkMemoryBudget(64 * 1024);
    Check("shared format diagnostic " + filter, await Command(["-nr", filter], [])
        .ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None) == 0
        && host.Output(JqFileDescriptor.StdOut) == "bogus is not a valid format\n"
        && host.Output(JqFileDescriptor.StdErr) == "" && memory.ReservedBytes == 0);
}
{
    var host = new SmokeHost();
    var memory = new BenchmarkMemoryBudget(64 * 1024);
    Check("optional scalar path", await Command(["-nc", "1|path(.foo?)"], [])
        .ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None) == 0
        && host.Output(JqFileDescriptor.StdOut) == "" && host.Output(JqFileDescriptor.StdErr) == ""
        && memory.ReservedBytes == 0);
}
{
    var host = new SmokeHost();
    host.SetInput("\"é item-42\"");
    var memory = new BenchmarkMemoryBudget(128 * 1024);
    Check("regex example against package", await Lokad.Jq.Examples.RegexAdapterExample.RunAsync(
        host, memory, Utf8RegexAdapter.Instance, CancellationToken.None) == 0
        && host.Output(JqFileDescriptor.StdOut) == "[true,{\"offset\":2,\"length\":7,\"id\":\"42\"},{\"id\":\"42\"}]\n"
        && host.Output(JqFileDescriptor.StdErr) == "" && memory.ReservedBytes == 0 && memory.ActiveScopes == 0);
}
foreach (string path in new[] { "./tiny", "dir/./tiny", "dir//tiny" })
{
    var host = new SmokeHost();
    host.AddFile("/modules/tiny.jq", "def value:42;");
    host.AddFile("/modules/dir/tiny.jq", "def value:43;");
    Check("host-resolved module " + path, await Command(["-nc", "-L", "/modules", "import " + JsonSerializer.Serialize(path) + " as m; m::value"], [])
        .ExecuteAsync(host, Utf8RegexAdapter.Instance, CancellationToken.None) == 0
        && host.Output(JqFileDescriptor.StdOut) == (path == "./tiny" ? "42\n" : "43\n")
        && host.Output(JqFileDescriptor.StdErr) == "" && host.ClosedDescriptors.Count == 1);
}
{
    var host = new SmokeHost();
    host.AddFile("/modules/tiny.jq", "def value:42;");
    host.AddFile("/modules/dir/tiny.jq", "def value:43;");
    Check("distinct module spellings and metadata", await Command(["-nc", "-L", "/modules",
        "import \"./tiny\" as a; import \"tiny\" as b; [a::value,b::value,(\"dir/./tiny\"|modulemeta|.defs)]"], [])
        .ExecuteAsync(host, Utf8RegexAdapter.Instance, CancellationToken.None) == 0
        && host.Output(JqFileDescriptor.StdOut) == "[42,42,[\"value/0\"]]\n"
        && host.Output(JqFileDescriptor.StdErr) == "" && host.ClosedDescriptors.Count == 3);
}
{
    string directory = "/" + new string('d', 16384);
    var host = new SmokeHost();
    host.AddFile(directory + "/tiny.jq", "def value:42;");
    var memory = new BenchmarkMemoryBudget(256 * 1024);
    Check("module actual path admission", await Command(["-nc", "import \"tiny\" as m; m::value"], [new("PWD", directory)])
        .ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None) == 0
        && host.Output(JqFileDescriptor.StdOut) == "42\n" && host.Output(JqFileDescriptor.StdErr) == ""
        && host.ClosedDescriptors.Count == 1 && memory.ReservedBytes == 0);
}
foreach (string format in new[] { "base64d", "urid" })
{
    var host = new SmokeHost();
    string text = (format == "base64d" ? "!" : "%ZZ") + new string('A', 1048576);
    var memory = new BenchmarkMemoryBudget(64 * 1024 + 80 + text.Length);
    Check(format + " validates before output admission", await Command(["-nc", "--arg", "text", text, "try ($text|@" + format + ") catch \"caught\""], [])
        .ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None) == 0
        && host.Output(JqFileDescriptor.StdOut) == "\"caught\"\n" && host.Output(JqFileDescriptor.StdErr) == "" && memory.ReservedBytes == 0);
}
{
    var host = new SmokeHost();
    var memory = new BenchmarkMemoryBudget(128 * 1024 + 80 + 3 * 32768);
    Check("URI decoded capacity", await Command(["-nc", "--arg", "text", string.Concat(Enumerable.Repeat("%61", 32768)), "$text|@urid|length"], [])
        .ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None) == 0
        && host.Output(JqFileDescriptor.StdOut) == "32768\n" && host.Output(JqFileDescriptor.StdErr) == "" && memory.ReservedBytes == 0);
}
{
    var host = new SmokeHost();
    Check("host text replacement", await Command(["-nr", "--arg", "text", "\uD800", "$text,($text|test(\".\"))"], [])
        .ExecuteAsync(host, Utf8RegexAdapter.Instance, CancellationToken.None) == 0
        && host.Output(JqFileDescriptor.StdOut) == "\uFFFD\ntrue\n" && host.Output(JqFileDescriptor.StdErr) == "");
}
{
    var host = new SmokeHost();
    Check("halt text replacement status", await Command(["-n", "--arg", "text", "\uD800", "$text|halt_error(7)"], [])
        .ExecuteAsync(host, Utf8RegexAdapter.Instance, CancellationToken.None) == 7
        && host.Output(JqFileDescriptor.StdOut) == "" && host.Output(JqFileDescriptor.StdErr) == "\uFFFD");
}
{
    var host = new SmokeHost();
    host.AddFile("/\uD800", "(");
    Check("program diagnostic text replacement", await Command(["-n", "-f", "/\uD800"], [])
        .ExecuteAsync(host, Utf8RegexAdapter.Instance, CancellationToken.None) == 3
        && host.Output(JqFileDescriptor.StdOut) == ""
        && host.Output(JqFileDescriptor.StdErr) == "jq: unexpected token <end> at line 1 column 2 (file \"/\uFFFD\")\n"
        && host.ClosedDescriptors.Count == 1);
}
foreach (string transform in new[] { "ascii_downcase", "ascii_upcase", "@html", "@uri" })
{
    var host = new SmokeHost();
    var memory = new BenchmarkMemoryBudget(64 * 1024 + 80 + 131072);
    Check(transform + " borrows unchanged text", await Command(["-nr", "--arg", "text", new string('0', 131072), "$text|" + transform + "|length"], [])
        .ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None) == 0
        && host.Output(JqFileDescriptor.StdOut) == "131072\n" && host.Output(JqFileDescriptor.StdErr) == "" && memory.ReservedBytes == 0);
}
{
    var host = new SmokeHost();
    var memory = new BenchmarkMemoryBudget(256 * 1024);
    Check("URI admits changed result", await Command(["-nr", "--arg", "text", new string(' ', 32768), "$text|@uri|length"], [])
        .ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None) == 0
        && host.Output(JqFileDescriptor.StdOut) == "98304\n" && host.Output(JqFileDescriptor.StdErr) == "" && memory.ReservedBytes == 0);
}
{
    var host = new SmokeHost();
    Check("first order Bessel boundaries", await Command(["-nc", "[(copysign(0;-1)|j1|copysign(1;.)),(yn(1;0)|isinfinite)]"], [])
        .ExecuteAsync(host, Utf8RegexAdapter.Instance, CancellationToken.None) == 0
        && host.Output(JqFileDescriptor.StdOut) == "[-1,true]\n" && host.Output(JqFileDescriptor.StdErr) == "");
}
{
    string path = "/" + new string('p', 16384);
    var host = new SmokeHost();
    host.AddFile(path, "1");
    var memory = new BenchmarkMemoryBudget(64 * 1024);
    Check("canonical program path construction", await Command(["-nc", "-f", path], [])
        .ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None) == 0
        && host.Output(JqFileDescriptor.StdOut) == "1\n" && host.Output(JqFileDescriptor.StdErr) == ""
        && host.ClosedDescriptors.Count == 1 && memory.ReservedBytes == 0);
}
{
    var host = new SmokeHost();
    Check("numeric text zero signs", await Command(["-nc", "[\"-0\",\"-00\",\"-0e0\"]|map(tonumber|copysign(1;.))"], [])
        .ExecuteAsync(host, Utf8RegexAdapter.Instance, CancellationToken.None) == 0
        && host.Output(JqFileDescriptor.StdOut) == "[-1,-1,-1]\n" && host.Output(JqFileDescriptor.StdErr) == "");
}
foreach (string format in new[] { "sh", "csv" })
{
    var host = new SmokeHost();
    var memory = new BenchmarkMemoryBudget(512 * 1024);
    Check(format + " boolean row", await Command(["-nr", "--argjson", "row",
        "[" + string.Join(',', Enumerable.Repeat("true", 2500)) + "]", "$row|@" + format + "|length"], [])
        .ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None) == 0
        && host.Output(JqFileDescriptor.StdOut) == "12499\n" && host.Output(JqFileDescriptor.StdErr) == ""
        && memory.ReservedBytes == 0);
}
{
    var host = new SmokeHost();
    var memory = new BenchmarkMemoryBudget(64 * 1024 + 80 + 131072);
    Check("borrowed index key payload", await Command(["-nc", "--arg", "key", new string('k', 131072), "INDEX(0;$key)|length"], [])
        .ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None) == 0
        && host.Output(JqFileDescriptor.StdOut) == "1\n" && host.Output(JqFileDescriptor.StdErr) == ""
        && memory.ReservedBytes == 0);
}
{
    var host = new SmokeHost();
    Check("range and slice zero signs", await Command(["-nc",
        "[(first(range(copysign(0;-1);1))|copysign(1;.)),([1,2]|path(.[copysign(0;-1):])|.[0].start|copysign(1;.))]"], [])
        .ExecuteAsync(host, Utf8RegexAdapter.Instance, CancellationToken.None) == 0
        && host.Output(JqFileDescriptor.StdOut) == "[-1,-1]\n" && host.Output(JqFileDescriptor.StdErr) == "");
}
{
    var host = new SmokeHost();
    Check("signed zero range path", await Command(["-nc", "copysign(0;-1)|first(path(range(.;1)))"], [])
        .ExecuteAsync(host, Utf8RegexAdapter.Instance, CancellationToken.None) == 0
        && host.Output(JqFileDescriptor.StdOut) == "[]\n" && host.Output(JqFileDescriptor.StdErr) == "");
}
{
    var host = new SmokeHost();
    Check("range and slice upper bound storage", await Command(["-nc",
        "[1,2]|pow(2;63) as $x|[((first(range($x;infinite))|tostring)==($x|tostring)),((path(.[0:$x])|.[0].end|tostring)==($x|tostring))]"], [])
        .ExecuteAsync(host, Utf8RegexAdapter.Instance, CancellationToken.None) == 0
        && host.Output(JqFileDescriptor.StdOut) == "[true,true]\n" && host.Output(JqFileDescriptor.StdErr) == "");
}
{
    using var valid = JqCommandInvocation.CreateWithStandardDescriptors("tools/jq", [], []);
    using var distinct = JqCommandInvocation.CreateWithStandardDescriptors("tools/j\u200bq", [], []);
    Check("ordinal command identity", Jq.TryParse(valid) is not null && Jq.TryParse(distinct) is null);
}
{
    var host = new SmokeHost();
    var memory = new BenchmarkMemoryBudget(64 * 1024);
    Check("empty combinations count stream", await Command(["-nc", "[0]|[combinations(empty)]"], [])
        .ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None) == 0
        && host.Output(JqFileDescriptor.StdOut) == "[[]]\n" && host.Output(JqFileDescriptor.StdErr) == ""
        && memory.ReservedBytes == 0);
}
{
    var host = new SmokeHost();
    Check("combined combinations counts", await Command(["-nc", "[0]|combinations((1,2))"], [])
        .ExecuteAsync(host, Utf8RegexAdapter.Instance, CancellationToken.None) == 0
        && host.Output(JqFileDescriptor.StdOut) == "[0,0,0]\n" && host.Output(JqFileDescriptor.StdErr) == "");
}
{
    var host = new SmokeHost();
    Check("late combinations count error", await Command(["-nc", "try ([0]|combinations((0,error(\"late\")))) catch ."], [])
        .ExecuteAsync(host, Utf8RegexAdapter.Instance, CancellationToken.None) == 0
        && host.Output(JqFileDescriptor.StdOut) == "\"late\"\n" && host.Output(JqFileDescriptor.StdErr) == "");
}
{
    var host = new SmokeHost();
    var memory = new BenchmarkMemoryBudget(64 * 1024);
    Check("function definition capture and shadowing", await Command(["-nc", "42 as $x|def f:$x;def g:f;def f:99;[g,f]"], [])
        .ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None) == 0
        && host.Output(JqFileDescriptor.StdOut) == "[42,99]\n" && host.Output(JqFileDescriptor.StdErr) == ""
        && memory.ReservedBytes == 0);
}
{
    var host = new SmokeHost();
    Check("shared container equality", await Command(["-nc", "[nan] as $x | [$x==$x,[nan]==[nan]]"], [])
        .ExecuteAsync(host, Utf8RegexAdapter.Instance, CancellationToken.None) == 0
        && host.Output(JqFileDescriptor.StdOut) == "[true,false]\n" && host.Output(JqFileDescriptor.StdErr) == "");
}
{
    var host = new SmokeHost();
    host.SetInput("[" + string.Join(',', Enumerable.Range(0, 128)) + "]");
    var memory = new BenchmarkMemoryBudget(64 * 1024);
    Check("empty right container reuse", await Command(["-c", ". as $x | range(128) | $x + [] | empty"], [])
        .ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None) == 0
        && host.Output(JqFileDescriptor.StdOut) == "" && host.Output(JqFileDescriptor.StdErr) == ""
        && memory.ReservedBytes == 0);
}
{
    var host = new SmokeHost();
    host.SetInput(JsonSerializer.Serialize(new string('a', 8192)));
    var memory = new BenchmarkMemoryBudget(64 * 1024);
    Check("empty affix reuse", await Command(["-c", ". as $x | range(128) | $x | trimstr(\"\") | empty"], [])
        .ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None) == 0
        && host.Output(JqFileDescriptor.StdOut) == "" && host.Output(JqFileDescriptor.StdErr) == ""
        && memory.ReservedBytes == 0);
}
{
    var host = new SmokeHost();
    Check("selected path identity", await Command(["-nc", "[(0+1|path(.+0)),(true|path(not|not)),(\"abc\"|path(tostring))]"], [])
        .ExecuteAsync(host, Utf8RegexAdapter.Instance, CancellationToken.None) == 0
        && host.Output(JqFileDescriptor.StdOut) == "[[],[],[]]\n" && host.Output(JqFileDescriptor.StdErr) == "");
}
{
    var host = new SmokeHost();
    Check("affix slice paths", await Command(["-nc", "\"abc\"|path(trimstr(\"\"))"], [])
        .ExecuteAsync(host, Utf8RegexAdapter.Instance, CancellationToken.None) == 0
        && host.Output(JqFileDescriptor.StdOut) == "[{\"start\":0,\"end\":null},{\"start\":null,\"end\":3}]\n"
        && host.Output(JqFileDescriptor.StdErr) == "");
}
{
    var host = new SmokeHost();
    host.SetInput(JsonSerializer.Serialize(new { text = new string('a', 131072), needle = new string('a', 16384) + "b" + new string('a', 49152) }));
    var memory = new BenchmarkMemoryBudget(4 * 1024 * 1024);
    Check("long whole-pattern miss", await Command(["-c", ".needle as $p|.text|[index($p),rindex($p)]"], [])
        .ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None) == 0
        && host.Output(JqFileDescriptor.StdOut) == "[null,null]\n" && host.Output(JqFileDescriptor.StdErr) == ""
        && memory.ReservedBytes == 0);
}
{
    var host = new SmokeHost();
    var memory = new BenchmarkMemoryBudget(64 * 1024);
    string source = "#" + new string(' ', 256 * 1024) + "\n1";
    Check("borrowed comment source", await Command(["-nc", source], [])
        .ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None) == 0
        && host.Output(JqFileDescriptor.StdOut) == "1\n" && host.Output(JqFileDescriptor.StdErr) == ""
        && memory.ReservedBytes == 0);
}
{
    var host = new SmokeHost();
    Check("interpolation comment and literal backslash", await Command(["-nc", "[\"x\\(1 # )\n)\",\"\\u005c(1)\"]"], [])
        .ExecuteAsync(host, Utf8RegexAdapter.Instance, CancellationToken.None) == 0
        && host.Output(JqFileDescriptor.StdOut) == "[\"x1\",\"\\\\(1)\"]\n" && host.Output(JqFileDescriptor.StdErr) == "");
}
{
    var host = new SmokeHost();
    var memory = new BenchmarkMemoryBudget(64 * 1024);
    Check("count error after generator effect", await Command(["-nc", "try limit(\"2\"; (debug,99)) catch ."], [])
        .ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None) == 0
        && host.Output(JqFileDescriptor.StdOut) == "\"string (\\\"2\\\") and number (1) cannot be subtracted\"\n"
        && host.Output(JqFileDescriptor.StdErr) == "[\"DEBUG:\",null]\n" && memory.ReservedBytes == 0);
}
{
    var host = new SmokeHost();
    Check("range comparison and addition", await Command(["-nc", "[limit(3;range(\"0\";\"2\";\"1\"))]"], [])
        .ExecuteAsync(host, Utf8RegexAdapter.Instance, CancellationToken.None) == 0
        && host.Output(JqFileDescriptor.StdOut) == "[\"0\",\"01\",\"011\"]\n" && host.Output(JqFileDescriptor.StdErr) == "");
}
{
    var host = new SmokeHost();
    Check("strict source escape", await Command(["-nc", "\"\\u  41\""], [])
        .ExecuteAsync(host, Utf8RegexAdapter.Instance, CancellationToken.None) == 3
        && host.Output(JqFileDescriptor.StdOut) == "" && host.Output(JqFileDescriptor.StdErr).Contains("invalid unicode escape", StringComparison.Ordinal));
}
{
    var host = new SmokeHost();
    host.AddFile("/first", "[1,");
    host.AddFile("/second", "2]\n");
    var memory = new BenchmarkMemoryBudget(64 * 1024);
    Check("logical JSON across files", await Command(["-c", ".", "/first", "/second"], [])
        .ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None) == 0
        && host.Output(JqFileDescriptor.StdOut) == "[1,2]\n" && host.Output(JqFileDescriptor.StdErr) == ""
        && host.ClosedDescriptors.Count == 2 && host.OpenFileCount == 0 && memory.ReservedBytes == 0);
}
{
    var host = new SmokeHost();
    host.SetInput("\ufeff[1]\n");
    var memory = new BenchmarkMemoryBudget(64 * 1024);
    Check("stream input BOM", await Command(["--stream", "-c", "."], [])
        .ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None) == 0
        && host.Output(JqFileDescriptor.StdOut) == "[[0],1]\n[[0]]\n" && host.Output(JqFileDescriptor.StdErr) == ""
        && memory.ReservedBytes == 0);
}
{
    var host = new SmokeHost();
    host.SetInput(JsonSerializer.Serialize(new string('x', 65536)));
    var memory = new BenchmarkMemoryBudget(384 * 1024);
    Check("bounded operand preview", await Command(["-c", "try contains([]) catch length"], [])
        .ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None) == 0
        && host.Output(JqFileDescriptor.StdOut) == "91\n" && host.Output(JqFileDescriptor.StdErr) == ""
        && memory.ReservedBytes == 0);
}
{
    var host = new SmokeHost();
    string input = "[" + string.Join(',', Enumerable.Range(0, 128)) + "]";
    host.SetInput(input);
    var memory = new BenchmarkMemoryBudget(64 * 1024);
    Check("bounded debug tree logging", await Command(["-c", ". as $x | range(64) | $x | debug | empty"], [])
        .ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None) == 0
        && host.Output(JqFileDescriptor.StdOut) == ""
        && host.Output(JqFileDescriptor.StdErr) == string.Concat(Enumerable.Repeat("[\"DEBUG:\"," + input + "]\n", 64))
        && memory.ReservedBytes == 0);
}
{
    var host = new SmokeHost();
    Check("substring scalar offsets", await Command(["-nc", "\"🚀ababa\" | [indices(\"aba\"),index(\"aba\"),rindex(\"aba\")]"], [])
        .ExecuteAsync(host, Utf8RegexAdapter.Instance, CancellationToken.None) == 0
        && host.Output(JqFileDescriptor.StdOut) == "[[1,3],1,3]\n" && host.Output(JqFileDescriptor.StdErr) == "");
}
{
    var host = new SmokeHost();
    host.AddFile("/later", "1\n2\n");
    var memory = new BenchmarkMemoryBudget(64 * 1024);
    Check("input open recovery", await Command(["-nc", "try inputs catch \"caught\"", "/missing", "/later"], [])
        .ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None) == 2
        && host.Output(JqFileDescriptor.StdOut) == "1\n2\n"
        && host.Output(JqFileDescriptor.StdErr) == "jq: cannot open /missing: path not found\n"
        && host.ClosedDescriptors.Count == 1 && host.OpenFileCount == 0 && memory.ReservedBytes == 0);
}
{
    var host = new SmokeHost();
    var memory = new BenchmarkMemoryBudget(64 * 1024);
    Check("file argument effect order", await Command(["--rawfile", "d", "/missing", "--help"], [])
        .ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None) == 2
        && host.Output(JqFileDescriptor.StdOut) == ""
        && host.Output(JqFileDescriptor.StdErr) == "jq: cannot open /missing: path not found\n"
        && host.OpenFileCount == 0 && memory.ReservedBytes == 0);
}
{
    var host = new SmokeHost();
    host.SetInput("\u001e1 [2,\u001e3\n");
    var memory = new BenchmarkMemoryBudget(64 * 1024);
    Check("sequence complete prefix", await Command(["--seq", "-c", "."], [])
        .ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None) == 0
        && host.Output(JqFileDescriptor.StdOut) == "\u001e1\n\u001e3\n"
        && host.Output(JqFileDescriptor.StdErr) == "jq: ignoring parse error: Truncated value at line 1, column 7\n"
        && memory.ReservedBytes == 0);
}
{
    var host = new SmokeHost();
    host.AddFile("/data", "42");
    var memory = new BenchmarkMemoryBudget(128 * 1024);
    Check("intrinsic and named bindings", await Command(["-nc", "--arg", "ENV", "named",
        "--slurpfile", "ARGS", "/data", "[$ENV.COLOR,env.COLOR,$ARGS.named.ENV,$ARGS.named.ARGS]"],
        [new("COLOR", "green")]).ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None) == 0
        && host.Output(JqFileDescriptor.StdOut) == "[\"green\",\"green\",\"named\",[42]]\n"
        && host.Output(JqFileDescriptor.StdErr) == "" && host.ClosedDescriptors.Count == 1
        && host.OpenFileCount == 0 && memory.ReservedBytes == 0);
}
{
    var host = new SmokeHost();
    host.AddFile("/first", "42\n43\n");
    var memory = new BenchmarkMemoryBudget(128 * 1024);
    Check("demand-time input paths", await Command(["-nc", "first(inputs)", "/first", ""], [])
        .ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None) == 0
        && host.Output(JqFileDescriptor.StdOut) == "42\n" && host.Output(JqFileDescriptor.StdErr) == ""
        && host.ClosedDescriptors.Count == 1 && host.OpenFileCount == 0 && memory.ReservedBytes == 0);
}
{
    var host = new SmokeHost();
    host.AddFile("/m.jq", "module {version:1}; import \"missing\" as m; def f: absent;");
    var memory = new BenchmarkMemoryBudget(128 * 1024);
    Check("module metadata inspection", await Command(["-nc", "\"m\"|modulemeta|[.version,.defs,.deps[0].relpath]"], [])
        .ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None) == 0
        && host.Output(JqFileDescriptor.StdOut) == "[1,[\"f/0\"],\"missing\"]\n" && host.Output(JqFileDescriptor.StdErr) == ""
        && host.ClosedDescriptors.Count == 1 && host.OpenFileCount == 0 && memory.ReservedBytes == 0);
}
{
    var host = new SmokeHost();
    var memory = new BenchmarkMemoryBudget(128 * 1024);
    host.SetInput("{\"a\":[10,20,30],\"b\":1,\"c\":2}");
    Check("computed paths", await Command(["-c", "[path(.a[.b]),path(.a[.b:.c])]"], [])
        .ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None) == 0
        && host.Output(JqFileDescriptor.StdOut) == "[[\"a\",1],[\"a\",{\"start\":1,\"end\":2}]]\n"
        && host.Output(JqFileDescriptor.StdErr) == "" && memory.ReservedBytes == 0);
}
{
    var host = new SmokeHost();
    var memory = new BenchmarkMemoryBudget(128 * 1024);
    Check("named argument identity", await Command(["-nc", "--argjson", "data", "{\"a\":1}",
        "$ARGS.named.data | path($data)"], []).ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None) == 0
        && host.Output(JqFileDescriptor.StdOut) == "[]\n" && host.Output(JqFileDescriptor.StdErr) == ""
        && memory.ReservedBytes == 0);
}
{
    var host = new SmokeHost();
    host.SetInput("{\"items\":[{\"name\":\"a\"},{\"name\":\"b\"}]}");
    Check("JSON", await Command([".items[].name"], []).ExecuteAsync(host, Utf8RegexAdapter.Instance, CancellationToken.None) == 0
        && host.Output(JqFileDescriptor.StdOut) == "\"a\"\n\"b\"\n" && host.Output(JqFileDescriptor.StdErr) == "");
}
{
    var host = new SmokeHost();
    host.SetInput("\"abc\"");
    Check("regex", await Command(["test(\"^a\")"], []).ExecuteAsync(host, Utf8RegexAdapter.Instance, CancellationToken.None) == 0
        && host.Output(JqFileDescriptor.StdOut) == "true\n" && host.Output(JqFileDescriptor.StdErr) == "");
}
{
    var host = new SmokeHost();
    host.AddFile("/data", "[1,2]");
    Check("owned file", await Command([".[]", "/data"], []).ExecuteAsync(host, Utf8RegexAdapter.Instance, CancellationToken.None) == 0
        && host.Output(JqFileDescriptor.StdOut) == "1\n2\n" && host.Output(JqFileDescriptor.StdErr) == ""
        && host.ClosedDescriptors.Count == 1 && host.OpenFileCount == 0);
}
{
    var host = new SmokeHost();
    Check("open failure", await Command([".", "/missing"], []).ExecuteAsync(host, Utf8RegexAdapter.Instance, CancellationToken.None) == 2
        && host.Output(JqFileDescriptor.StdOut) == ""
        && host.Output(JqFileDescriptor.StdErr) == "jq: cannot open /missing: path not found\n"
        && host.ClosedDescriptors.Count == 0 && host.OpenFileCount == 0);
}
{
    var host = new SmokeHost();
    host.AddFile("/modules/values.jq", "def twice: . * 2;");
    Check("hosted module", await Command(["-n", "-L", "/modules", "import \"values\" as v; 3 | v::twice"], [])
        .ExecuteAsync(host, Utf8RegexAdapter.Instance, CancellationToken.None) == 0
        && host.Output(JqFileDescriptor.StdOut) == "6\n" && host.Output(JqFileDescriptor.StdErr) == ""
        && host.ClosedDescriptors.Count == 1 && host.OpenFileCount == 0);
}
{
    var host = new SmokeHost();
    var invocation = JqCommandInvocation.CreateWithStandardDescriptors("jq",
        ["-n", "-c", "[$ENV.REGION, env.REGION, now]"], [new JqEnvironmentVariable("REGION", "example")],
        new JqClock(new EpochClock(), TimeZoneInfo.Utc));
    var command = Jq.TryParse(invocation) ?? throw new InvalidOperationException("Expected a jq command.");
    Check("explicit environment and clock", await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, CancellationToken.None) == 0
        && host.Output(JqFileDescriptor.StdOut) == "[\"example\",\"example\",0]\n" && host.Output(JqFileDescriptor.StdErr) == "");
}
{
    var host = new SmokeHost();
    host.SetInput("\"nan\"");
    Check("non-finite parsing", await Command(["fromjson | isnan"], []).ExecuteAsync(host, Utf8RegexAdapter.Instance, CancellationToken.None) == 0
        && host.Output(JqFileDescriptor.StdOut) == "true\n" && host.Output(JqFileDescriptor.StdErr) == "");
}
{
    var host = new SmokeHost();
    var memory = new BenchmarkMemoryBudget(64 * 1024);
    Check("terminal string admission", await Command(["-nr", "\"ok\", (\"x\" * 1000000)"], []).ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None) == 5
        && host.Output(JqFileDescriptor.StdOut) == "ok\n" && host.Output(JqFileDescriptor.StdErr) == "jq: memory budget exceeded\n"
        && memory.ReservedBytes == 0);
}
{
    var host = new SmokeHost();
    Check("provider regex matching", await Command(["-n",
        "\"x\" | test(\"x\" * 17000)"], [])
        .ExecuteAsync(host, Utf8RegexAdapter.Instance, CancellationToken.None) == 0
        && host.Output(JqFileDescriptor.StdOut) == "false\n" && host.Output(JqFileDescriptor.StdErr) == "");
}
{
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    var host = new SmokeHost();
    bool cancelled = false;
    try
    {
        await Command(["-n", "1"], []).ExecuteAsync(host, Utf8RegexAdapter.Instance, cancellation.Token);
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
    Check("borrowed descriptors", await Command(["."], []).ExecuteAsync(host, Utf8RegexAdapter.Instance, CancellationToken.None) == 0
        && host.Output(JqFileDescriptor.StdOut) == "7\n" && host.ClosedDescriptors.Count == 0);
}
{
    var memory = new BenchmarkMemoryBudget(64 * 1024);
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
            reused &= await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, CancellationToken.None) == 0
                && host.Output(JqFileDescriptor.StdOut) == "sample\n"
                && host.Output(JqFileDescriptor.StdErr) == ""
                && memory.ReservedBytes == retained;
        }
        invocation.Dispose();
        Check("governed snapshot and sequential reuse", reused && memory.ReservedBytes == 0);
    }
}
{
    var memory = new BenchmarkMemoryBudget(8);
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
    var memory = new BenchmarkMemoryBudget(limit);
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
        Task<int> firstCall = command.ExecuteAsync(first, Utf8RegexAdapter.Instance, cancellation.Token);
        Task<int> secondCall = command.ExecuteAsync(second, Utf8RegexAdapter.Instance, CancellationToken.None);
        var pending = await Task.WhenAll(first.Ready.Task, second.Ready.Task).WaitAsync(TimeSpan.FromSeconds(10));
        if (!memory.TryReserve(limit - memory.ReservedBytes, out var pressure))
            throw new InvalidOperationException("Expected competing caller admission.");
        using (pressure)
        {
            var refused = new SmokeHost();
            Check("overlapping commands share root", await command.ExecuteAsync(refused, Utf8RegexAdapter.Instance, CancellationToken.None) == 5
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
    var memory = new BenchmarkMemoryBudget(64 * 1024);
    Check("discarded evaluation keeps shared admission bounded", await Command(["-n", "range(100000) | empty"], []).ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None) == 0
        && host.Output(JqFileDescriptor.StdOut) == ""
        && host.Output(JqFileDescriptor.StdErr) == "" && memory.ReservedBytes == 0 && memory.PeakBytes <= 64 * 1024);
}
{
    const long limit = 64 * 1024;
    var memory = new BenchmarkMemoryBudget(limit);
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
            int status = await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, CancellationToken.None);
            bool stopped = status == 5 && host.Output(JqFileDescriptor.StdOut) == "x\n"
                && host.Output(JqFileDescriptor.StdErr) == "jq: memory budget exceeded\n"
                && pressure is not null && memory.ReservedBytes == snapshot + pressure.Bytes;
            pressure?.Dispose();
            var later = new SmokeHost();
            Check("governed padding refusal stops and permits later reuse", stopped
                && await command.ExecuteAsync(later, Utf8RegexAdapter.Instance, CancellationToken.None) == 0
                && later.Output(JqFileDescriptor.StdOut) == "x\nxx\nxxx\n"
                && later.Output(JqFileDescriptor.StdErr) == "" && memory.ReservedBytes == snapshot);
        }
        finally { pressure?.Dispose(); }
    }
    if (memory.ReservedBytes != 0) throw new InvalidOperationException("Invocation storage was not refunded.");
}
foreach (bool refuse in new[] { false, true })
{
    string filter = refuse
        ? "\"prefix\", try (\"x\" * 1000000) catch \"caught\", \"later\""
        : "\"prefix\", (\"note\" | stderr | empty), (\"x\" * 1000000)";
    var memory = new BenchmarkMemoryBudget(64 * 1024);
    if (!JqCommandInvocation.TryCreateWithStandardDescriptors("jq", ["-r",
        filter,
        "/first", "/later"], [], memory, out var invocation))
        throw new InvalidOperationException("Expected governed snapshot admission.");
    using (invocation)
    {
        long snapshot = memory.ReservedBytes;
        var host = new SmokeHost { StderrResult = JqAppendResult.Closed };
        host.AddFile("/first", "0\n");
        host.AddFile("/later", "1\n");
        var command = Jq.TryParse(invocation) ?? throw new InvalidOperationException("Expected a jq command.");
        Check(refuse ? "governed refusal keeps failure after stderr closure" : "diagnostic closure stops later work",
            await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, CancellationToken.None) == (refuse ? 5 : 0)
            && host.Output(JqFileDescriptor.StdOut) == "prefix\n" && host.Output(JqFileDescriptor.StdErr) == (refuse ? "jq: memory budget exceeded\n" : "note")
            && host.ClosedDescriptors.Count == 1 && host.OpenFileCount == 0 && memory.ReservedBytes == snapshot);
    }
    if (memory.ReservedBytes != 0) throw new InvalidOperationException("Invocation storage was not refunded.");
}
{
    using var cancellation = new CancellationTokenSource();
    var engine = new SmokeCooperatingRegex(cancellation);
    var memory = new BenchmarkMemoryBudget(64 * 1024);
    var host = new SmokeHost();
    host.AddFile("/data", "\"x\"\n");
    bool cancelled = false;
    try
    {
        await Command(["-r", "\"prefix\", (try test(\"x\") catch \"caught\")", "/data"], [])
            .ExecuteAsync(host, engine, memory, cancellation.Token);
    }
    catch (OperationCanceledException error) { cancelled = error.CancellationToken == cancellation.Token; }
    Check("public cooperating regex adapter", cancelled && engine.Disposed
        && host.Output(JqFileDescriptor.StdOut) == "prefix\n" && host.Output(JqFileDescriptor.StdErr) == ""
        && host.ClosedDescriptors.Count == 1 && host.OpenFileCount == 0 && memory.ReservedBytes == 0);
}
{
    using var cancellation = new CancellationTokenSource();
    var engine = new SmokeCooperatingRegex(cancellation) { CancelOnSearch = false };
    var memory = new BenchmarkMemoryBudget(64 * 1024);
    var host = new SmokeHost();
    int status = await Command(["-nr", "\"x\" | match(\"x\") | .string"], [])
        .ExecuteAsync(host, engine, memory, CancellationToken.None);
    Check("public provider-owned capture result", status == 0 && engine.Disposed
        && host.Output(JqFileDescriptor.StdOut) == "x\n" && host.Output(JqFileDescriptor.StdErr) == ""
        && memory.ReservedBytes == 0);
}
{
    var host = new SmokeHost { CloseFailure = new JqOperationError(JqOperationErrorKind.TransportFailure, "storage unavailable") };
    host.AddFile("/first", "42\n");
    host.AddFile("/later", "99\n");
    var memory = new BenchmarkMemoryBudget(64 * 1024);
    Check("terminal structured close", await Command(["-c", ".", "/first", "/later"], [])
        .ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None) == 5
        && host.Output(JqFileDescriptor.StdOut) == "42\n"
        && host.Output(JqFileDescriptor.StdErr) == "jq: error closing /first: storage unavailable\n"
        && host.ClosedDescriptors.Count == 1 && host.OpenFileCount == 0 && memory.ReservedBytes == 0);
}
{
    var memory = new BenchmarkMemoryBudget(64 * 1024);
    var host = new SmokeHost();
    host.AddFile("/records", string.Concat(Enumerable.Range(0, 4096).Select(i => $"p{i:D5}\n")));
    Check("distinct regex stream retains bounded storage", await Command(["-Rc",
        ". as $p | \"x\" | test($p) | empty", "/records"], [])
        .ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None) == 0
        && host.Output(JqFileDescriptor.StdOut) == "" && host.Output(JqFileDescriptor.StdErr) == ""
        && host.ClosedDescriptors.Count == 1 && host.OpenFileCount == 0 && memory.ReservedBytes == 0);
}
{
    var host = new SmokeHost();
    Check("empty value arguments precede invalid input", await Command(["-nc",
        "[bsearch(empty)], [sub(empty; \"x\")], [gsub(\"a\"; \"x\"; empty)]"], [])
        .ExecuteAsync(host, Utf8RegexAdapter.Instance, CancellationToken.None) == 0
        && host.Output(JqFileDescriptor.StdOut) == "[]\n[]\n[]\n"
        && host.Output(JqFileDescriptor.StdErr) == "");
}
{
    var host = new SmokeHost();
    Check("argument halt status", await Command(["-nc", "try bsearch(halt_error(7)) catch ."], [])
        .ExecuteAsync(host, Utf8RegexAdapter.Instance, CancellationToken.None) == 7
        && host.Output(JqFileDescriptor.StdOut) == "" && host.Output(JqFileDescriptor.StdErr) == "");
}
{
    var host = new SmokeHost();
    Check("zero-width transpose rows", await Command(["-nc", "[0,\"\",{},null] | transpose"], [])
        .ExecuteAsync(host, Utf8RegexAdapter.Instance, CancellationToken.None) == 0
        && host.Output(JqFileDescriptor.StdOut) == "[]\n" && host.Output(JqFileDescriptor.StdErr) == "");
}
{
    var memory = new BenchmarkMemoryBudget(64 * 1024);
    var host = new SmokeHost();
    Check("information tails need no retained setup", await Command(["--version", .. Enumerable.Repeat("ignored", 2048)], [])
        .ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None) == 0
        && host.Output(JqFileDescriptor.StdOut) == "Lokad jq\n" && host.Output(JqFileDescriptor.StdErr) == ""
        && memory.ReservedBytes == 0);
}
{
    var memory = new BenchmarkMemoryBudget(64 * 1024);
    var host = new SmokeHost();
    Check("duplicate bindings retain one value", await Command(["-nr",
        .. Enumerable.Range(0, 2048).SelectMany(_ => new[] { "--arg", "x", "value" }), "$x"], [])
        .ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None) == 0
        && host.Output(JqFileDescriptor.StdOut) == "value\n" && host.Output(JqFileDescriptor.StdErr) == ""
        && memory.ReservedBytes == 0);
}
{
    var memory = new BenchmarkMemoryBudget(64 * 1024 + 80 + 131072);
    var host = new SmokeHost();
    Check("literal split borrows unchanged payload", await Command(["-nc", "--arg", "s", new string('x', 131072),
        "[($s | split(\"z\") | length), ($s / \"z\" | length)]"], [])
        .ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None) == 0
        && host.Output(JqFileDescriptor.StdOut) == "[1,1]\n" && host.Output(JqFileDescriptor.StdErr) == ""
        && memory.ReservedBytes == 0);
}
{
    var host = new SmokeHost();
    Check("math signs and error-function tails", await Command(["-nc", "(-1*0) as $x | "
        + "[copysign(1;$x|log1p),copysign(1;$x|expm1),(-1|modf|.[0]|copysign(1;.)),(6|erfc>0)]"], [])
        .ExecuteAsync(host, Utf8RegexAdapter.Instance, CancellationToken.None) == 0
        && host.Output(JqFileDescriptor.StdOut) == "[-1,-1,-1,true]\n" && host.Output(JqFileDescriptor.StdErr) == "");
}
{
    var host = new SmokeHost();
    Check("near-zero kernels retain tiny inputs", await Command(["-nc",
        "[(1e-20|expm1 != 0),(-1e-20|log1p != 0)]"], [])
        .ExecuteAsync(host, Utf8RegexAdapter.Instance, CancellationToken.None) == 0
        && host.Output(JqFileDescriptor.StdOut) == "[true,true]\n" && host.Output(JqFileDescriptor.StdErr) == "");
}
{
    var host = new SmokeHost();
    host.SetInput("\u001etruex\n\u001e[9]\n");
    var memory = new BenchmarkMemoryBudget(64 * 1024);
    Check("stream sequence warning progress", await Command(["-c", "--seq", "--stream", "."], [])
        .ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None) == 0
        && host.Output(JqFileDescriptor.StdOut) == "\u001e[[0],9]\n\u001e[[0]]\n"
        && host.Output(JqFileDescriptor.StdErr) == "jq: ignoring parse error: Invalid literal at line 2, column 0 (need RS to resync)\n"
        && memory.ReservedBytes == 0);
}
{
    var host = new SmokeHost();
    host.SetInput("\u001e[1\u001e[9]\n");
    var memory = new BenchmarkMemoryBudget(64 * 1024);
    Check("stream sequence error path", await Command(["-c", "--seq", "--stream-errors", "."], [])
        .ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None) == 0
        && host.Output(JqFileDescriptor.StdOut) == "\u001e[\"Truncated value at line 1, column 4\",[0]]\n\u001e[[0],9]\n\u001e[[0]]\n"
        && host.Output(JqFileDescriptor.StdErr) == "" && memory.ReservedBytes == 0);
}
{
    var host = new SmokeHost();
    host.SetInput("[1, x, 2]");
    var memory = new BenchmarkMemoryBudget(64 * 1024);
    Check("stream literal diagnostic", await Command(["-c", "--stream-errors", "."], [])
        .ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None) == 0
        && host.Output(JqFileDescriptor.StdOut) == "[[0],1]\n[\"Invalid numeric literal at line 1, column 6\",[1]]\n"
        && host.Output(JqFileDescriptor.StdErr) == "" && memory.ReservedBytes == 0);
}
foreach (bool missingFile in new[] { false, true })
{
    var host = new SmokeHost();
    var memory = new BenchmarkMemoryBudget(64 * 1024);
    string text = new('x', 1048576);
    string[] arguments = missingFile ? ["--rawfile", "v", "/" + text, "-nc", "1"] : ["--" + text];
    Check(missingFile ? "missing-path diagnostic admission" : "unknown-option diagnostic admission",
        await Command(arguments, []).ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None) == 5
        && host.Output(JqFileDescriptor.StdOut) == "" && host.Output(JqFileDescriptor.StdErr) == "jq: memory budget exceeded\n"
        && memory.ReservedBytes == 0);
}
{
    var host = new SmokeHost();
    long nativeBindings = Enumerable.Range(0, 512).Sum(index =>
        80L + 1 + 80 + ("v" + index.ToString(System.Globalization.CultureInfo.InvariantCulture)).Length);
    var memory = new BenchmarkMemoryBudget(128 * 1024 + nativeBindings);
    Check("one named-binding table", await Command(["-nc", .. Enumerable.Range(0, 512).SelectMany(index =>
        new[] { "--arg", "v" + index.ToString(System.Globalization.CultureInfo.InvariantCulture), "x" }), "$ARGS.named|length"], [])
        .ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None) == 0
        && host.Output(JqFileDescriptor.StdOut) == "512\n" && host.Output(JqFileDescriptor.StdErr) == "" && memory.ReservedBytes == 0);
}
{
    var host = new SmokeHost();
    var memory = new BenchmarkMemoryBudget(64 * 1024);
    Check("borrowed indent padding", await Command(["--indent", new string('0', 262144) + "2", "-n", "[1]"], [])
        .ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None) == 0
        && host.Output(JqFileDescriptor.StdOut) == "[\n  1\n]\n" && host.Output(JqFileDescriptor.StdErr) == "" && memory.ReservedBytes == 0);
}
foreach (bool separator in new[] { false, true })
{
    var host = new SmokeHost();
    var memory = new BenchmarkMemoryBudget(96 * 1024);
    string expression = separator ? "[$text,\"y\"]|join(1)" : "[$text,{}]|join(\"\")";
    Check(separator ? "bounded join separator preview" : "bounded join item preview",
        await Command(["-nr", "--arg", "text", new string('x', 32768), "try (" + expression + ") catch ."], [])
            .ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None) == 0
        && host.Output(JqFileDescriptor.StdOut) == "string (\"xxxxxxxxxxxxxxxxxxxxxxxx...\") and "
            + (separator ? "number (1)" : "object ({})") + " cannot be added\n"
        && host.Output(JqFileDescriptor.StdErr) == "" && memory.ReservedBytes == 0);
}
{
    var host = new SmokeHost();
    var memory = new BenchmarkMemoryBudget(96 * 1024);
    Check("successful join still preflights full output", await Command(["-nr", "--arg", "text", new string('x', 32768),
        "try ([$text]|join(\"\")) catch \"caught\""], []).ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None) == 5
        && host.Output(JqFileDescriptor.StdOut) == "" && host.Output(JqFileDescriptor.StdErr) == "jq: memory budget exceeded\n"
        && memory.ReservedBytes == 0);
}
{
    var host = new SmokeHost();
    var memory = new BenchmarkMemoryBudget(4 * 1024 * 1024);
    Check("growing JSON serialization", await Command(["-nc", "[range(10000)]|tojson|length"], [])
        .ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None) == 0
        && host.Output(JqFileDescriptor.StdOut) == "48891\n" && host.Output(JqFileDescriptor.StdErr) == "" && memory.ReservedBytes == 0);
}
{
    var host = new SmokeHost();
    var memory = new BenchmarkMemoryBudget(64 * 1024);
    Check("composite date construction", await Command(["-nr", "--arg", "format", string.Concat(Enumerable.Repeat("%c", 500)),
        "0|strftime($format)|length"], []).ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None) == 0
        && host.Output(JqFileDescriptor.StdOut) == "12000\n" && host.Output(JqFileDescriptor.StdErr) == "" && memory.ReservedBytes == 0);
}
{
    var host = new SmokeHost();
    var memory = new BenchmarkMemoryBudget(64 * 1024 + 80 + 40000 + 80 + 20000);
    Check("repeated date directive parsing", await Command(["-nc", "--arg", "text", string.Concat(Enumerable.Repeat("1970", 10000)),
        "--arg", "format", string.Concat(Enumerable.Repeat("%Y", 10000)), "$text|strptime($format)"], [])
        .ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None) == 0
        && host.Output(JqFileDescriptor.StdOut) == "[1970,0,0,0,0,0,8,367]\n" && host.Output(JqFileDescriptor.StdErr) == "" && memory.ReservedBytes == 0);
}
{
    string value = "[" + string.Join(',', Enumerable.Range(0, 10000)) + "]";
    var host = new SmokeHost(); host.SetInput("\u001e" + value + "\n");
    var memory = new BenchmarkMemoryBudget(2 * 1024 * 1024);
    Check("growing sequence output framing", await Command(["-c", "--seq", "."], [])
        .ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None) == 0
        && host.Output(JqFileDescriptor.StdOut) == "\u001e" + value + "\n"
        && host.Output(JqFileDescriptor.StdErr) == "" && memory.ReservedBytes == 0);
}
{
    var host = new SmokeHost(); host.SetInput("null" + new string(' ', 65532));
    var memory = new BenchmarkMemoryBudget(1024 * 1024);
    Check("repeated native fromjson", await Command(["-Rrs",
        "\"ready\", (. as $t | range(0;32) | $t | fromjson)"], []).ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None) == 0
        && host.Output(JqFileDescriptor.StdOut) == "ready\n" + string.Concat(Enumerable.Repeat("null\n", 32))
        && host.Output(JqFileDescriptor.StdErr) == "" && memory.ReservedBytes == 0 && memory.ActiveScopes == 0);
}
{
    var host = new SmokeHost();
    var memory = new BenchmarkMemoryBudget(128 * 1024);
    Check("fromjson decoded value ownership", await Command(["-nr", "--arg", "text",
        "{\"x\":\"ok\"}" + new string(' ', 65526), "\"ready\", ($text|fromjson|.x)"], [])
        .ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None) == 0
        && host.Output(JqFileDescriptor.StdOut) == "ready\nok\n" && host.Output(JqFileDescriptor.StdErr) == ""
        && memory.ReservedBytes == 0 && memory.ActiveScopes == 0);
}
{
    var host = new SmokeHost();
    var memory = new BenchmarkMemoryBudget(96 * 1024);
    Check("fromjson reads native input without a conversion buffer", await Command(["-nr", "--arg", "text", "null" + new string(' ', 65532),
        "\"ready\", (try ($text|fromjson) catch \"caught\")"], []).ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None) == 0
        && host.Output(JqFileDescriptor.StdOut) == "ready\nnull\n" && host.Output(JqFileDescriptor.StdErr) == ""
        && memory.ReservedBytes == 0 && memory.ActiveScopes == 0);
}
{
    var host = new SmokeHost(); host.SetInput("[" + string.Join(',', Enumerable.Range(0, 10000)) + "]");
    var memory = new BenchmarkMemoryBudget(1024 * 1024);
    Check("unsupported format avoids conversion", await Command(["-cr", "\"ready\", (try @missing catch \"caught\")"], [])
        .ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None) == 0
        && host.Output(JqFileDescriptor.StdOut) == "ready\ncaught\n" && host.Output(JqFileDescriptor.StdErr) == ""
        && memory.ReservedBytes == 0 && memory.ActiveScopes == 0);
}
foreach (var (filter, output) in new[] { ("@missing \"literal\"", "literal\n"), ("@missing \"\\(empty)\"", "") })
{
    var host = new SmokeHost();
    Check("unused format remains lazy " + filter, await Command(["-nr", filter], []).ExecuteAsync(host, Utf8RegexAdapter.Instance, CancellationToken.None) == 0
        && host.Output(JqFileDescriptor.StdOut) == output && host.Output(JqFileDescriptor.StdErr) == "");
}
foreach (string expression in new[] { "\"x\" * 1048576", "$text" })
{
    var host = new SmokeHost();
    var memory = new BenchmarkMemoryBudget(16 * 1024 * 1024);
    using var cancellation = new CancellationTokenSource();
    bool armed = false;
    long before = 0;
    host.BeforeAppend = descriptor =>
    {
        if (descriptor != JqFileDescriptor.StdOut) return;
        before = GC.GetAllocatedBytesForCurrentThread();
        armed = true;
    };
    var hooked = new SmokeAdmissionBudget(memory, () => { if (armed && memory.ReservedBytes >= 1024 * 1024) cancellation.Cancel(); });
    var command = Command(["-nr", "--arg", "text", new string('x', 1024 * 1024), "\"ready\", (" + expression + ")"], []);
    bool canceled = false;
    try { await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, hooked, cancellation.Token); }
    catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { canceled = true; }
    long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
    Check("post-admission cancellation " + expression, canceled && allocated is >= 0 and <= 256 * 1024
        && host.Output(JqFileDescriptor.StdOut) == "ready\n" && host.Output(JqFileDescriptor.StdErr) == ""
        && memory.ReservedBytes == 0 && memory.ActiveScopes == 0);
}
{
    var host = new SmokeHost();
    var memory = new BenchmarkMemoryBudget(64 * 1024);
    using var cancellation = new CancellationTokenSource();
    bool armed = false;
    host.BeforeAppend = descriptor => { if (descriptor == JqFileDescriptor.StdOut) armed = true; };
    var hooked = new SmokeAdmissionBudget(memory, () => { if (armed) cancellation.Cancel(); });
    Check("refusal takes precedence over cancellation", await Command(["-nr", "\"ready\", (try (\"x\" * 1048576) catch \"caught\")"], [])
        .ExecuteAsync(host, Utf8RegexAdapter.Instance, hooked, cancellation.Token) == 5
        && cancellation.IsCancellationRequested && host.Output(JqFileDescriptor.StdOut) == "ready\n"
        && host.Output(JqFileDescriptor.StdErr) == "" && memory.ReservedBytes == 0 && memory.ActiveScopes == 0);
}
{
    var host = new SmokeHost();
    var memory = new BenchmarkMemoryBudget(256 * 1024);
    Check("formatted literal reuse", await Command(["-nr", "range(64)|@html \"" + new string('x', 8192) + "\"|length"], [])
        .ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None) == 0
        && host.Output(JqFileDescriptor.StdOut) == string.Concat(Enumerable.Repeat("8192\n", 64))
        && host.Output(JqFileDescriptor.StdErr) == "" && memory.ReservedBytes == 0 && memory.ActiveScopes == 0);
}
foreach (string format in new[] { "text", "html", "missing" })
{
    var host = new SmokeHost();
    Check("constant formatted metadata " + format,
        await Command(["-nc", "module {doc:@" + format + " \"literal\"};1"], []).ExecuteAsync(host, Utf8RegexAdapter.Instance, CancellationToken.None) == 0
        && host.Output(JqFileDescriptor.StdOut) == "1\n" && host.Output(JqFileDescriptor.StdErr) == "");
}
{
    var host = new SmokeHost(); host.AddFile("/lib/tiny.jq", "module {doc:@missing \"<&>\"}; def value:42;");
    var memory = new BenchmarkMemoryBudget(128 * 1024);
    Check("formatted import and module metadata", await Command(["-nc", "-L", "/lib",
        "import \"tiny\" as m {doc:@html \"literal\"}; [(\"tiny\"|modulemeta).doc,m::value]"], [])
        .ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None) == 0
        && host.Output(JqFileDescriptor.StdOut) == "[\"<&>\",42]\n" && host.Output(JqFileDescriptor.StdErr) == ""
        && host.ClosedDescriptors.Count == 2 && memory.ReservedBytes == 0 && memory.ActiveScopes == 0);
}
{
    var host = new SmokeHost();
    Check("formatted constant path identity", await Command(["-nc", "def f:@missing \"x\";f as $x|$x|path(f)"], [])
        .ExecuteAsync(host, Utf8RegexAdapter.Instance, CancellationToken.None) == 0
        && host.Output(JqFileDescriptor.StdOut) == "[]\n" && host.Output(JqFileDescriptor.StdErr) == "");
}
{
    var host = new SmokeHost();
    var memory = new BenchmarkMemoryBudget(512 * 1024);
    Check("base64 roundtrip active allowance", await Command(["-nr", "\"ready\", ((\"x\"*65536)|@base64|@base64d|length)"], [])
        .ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None) == 0
        && host.Output(JqFileDescriptor.StdOut) == "ready\n65536\n" && host.Output(JqFileDescriptor.StdErr) == ""
        && memory.ReservedBytes == 0 && memory.ActiveScopes == 0);
}
{
    var counts = new List<int>();
    foreach (bool encode in new[] { false, true })
    {
        int calls = 0;
        var host = new SmokeHost(); host.SetInput(string.Concat(Enumerable.Repeat("x\n", 2500)));
        var memory = new BenchmarkMemoryBudget(128 * 1024);
        var hooked = new SmokeAdmissionBudget(memory, () => calls++);
        Check("small encode stream " + encode, await Command(["-Rr", encode ? "@base64" : "."], [])
            .ExecuteAsync(host, Utf8RegexAdapter.Instance, hooked, CancellationToken.None) == 0
            && host.Output(JqFileDescriptor.StdOut) == string.Concat(Enumerable.Repeat(encode ? "eA==\n" : "x\n", 2500))
            && host.Output(JqFileDescriptor.StdErr) == "" && memory.ReservedBytes == 0 && memory.ActiveScopes == 0);
        counts.Add(calls);
    }
    Check("small encodes retain admission amortization", counts[1] <= counts[0] + 64);
}
{
    var host = new SmokeHost();
    var memory = new BenchmarkMemoryBudget(512 * 1024);
    using var cancellation = new CancellationTokenSource();
    bool armed = false;
    host.BeforeAppend = descriptor => { if (descriptor == JqFileDescriptor.StdOut) armed = true; };
    var hooked = new SmokeAdmissionBudget(memory, () => { if (armed) cancellation.Cancel(); });
    var command = Command(["-nr", "--arg", "text", new string('x', 65536), "\"ready\", ($text|@base64)"], []);
    bool canceled = false;
    long before = GC.GetAllocatedBytesForCurrentThread();
    try { await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, hooked, cancellation.Token); }
    catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { canceled = true; }
    Check("native encoding output admission cancellation", canceled && GC.GetAllocatedBytesForCurrentThread() - before is >= 0 and <= 256 * 1024
        && host.Output(JqFileDescriptor.StdOut) == "ready\n" && host.Output(JqFileDescriptor.StdErr) == ""
        && memory.ReservedBytes == 0 && memory.ActiveScopes == 0);
}
{
    const long limit = 512 * 1024;
    var host = new SmokeHost();
    var memory = new BenchmarkMemoryBudget(limit);
    IMemoryReservation? pressure = null;
    host.BeforeAppend = descriptor =>
    {
        if (descriptor == JqFileDescriptor.StdOut && pressure is null
            && !memory.TryReserve(limit - memory.ReservedBytes - 87463, out pressure))
            throw new InvalidOperationException("Expected pressure admission.");
    };
    int status = await Command(["-nr", "--arg", "text", new string('x', 65536), "\"ready\", (try ($text|@base64) catch \"caught\")"], [])
        .ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None);
    bool retained = pressure is not null && memory.ReservedBytes == pressure.Bytes;
    pressure?.Dispose();
    Check("encoding output refusal stays terminal", status == 5 && retained
        && host.Output(JqFileDescriptor.StdOut) == "ready\n" && host.Output(JqFileDescriptor.StdErr) == "jq: memory budget exceeded\n"
        && memory.ReservedBytes == 0 && memory.ActiveScopes == 0);
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

// A host admission callback covers both initial reservations and later growth.
sealed class SmokeAdmissionBudget(IMemoryBudget inner, Action callback) : IMemoryBudget
{
    public IMemoryScope CreateScope() => new Scope(inner.CreateScope(), callback);
    public bool TryReserve(long bytes, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IMemoryReservation? reservation) =>
        Reserve(inner, callback, bytes, out reservation);

    private static bool Reserve(IMemoryBudget budget, Action callback, long bytes,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IMemoryReservation? reservation)
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
        public bool TryReserve(long bytes, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IMemoryReservation? reservation) =>
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

sealed class SmokeCooperatingRegex(CancellationTokenSource cancellation) : IJqRegexEngine
{
    internal bool Disposed { get; private set; }
    internal bool CancelOnSearch { get; init; } = true;
    public bool TryCompile(ReadOnlyMemory<byte> patternUtf8, JqRegexCompileOptions options, IMemoryBudget? memoryBudget,
        CancellationToken cancellationToken,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IJqCompiledRegex? regex, out string? error)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (memoryBudget is null) throw new InvalidOperationException("Adapter budget was not forwarded.");
        if (!memoryBudget.TryReserve(64, out var reservation))
            throw new JqRegexResourceException("adapter admission refused", true);
        regex = new Compiled(this, memoryBudget, reservation, cancellation);
        error = null;
        return true;
    }

    private sealed class Compiled(SmokeCooperatingRegex owner, IMemoryBudget budget, IMemoryReservation memory,
        CancellationTokenSource cancellation) : IJqCompiledRegex
    {
        public IReadOnlyDictionary<int, string> GroupNames { get; } = new Dictionary<int, string>();
        public bool IsMatch(ReadOnlySpan<byte> input, JqRegexMatchOptions options, CancellationToken cancellationToken)
        {
            if (owner.CancelOnSearch) cancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return input.SequenceEqual("x"u8);
        }
        public JqRegexMatch Match(ReadOnlySpan<byte> input, int start, JqRegexMatchOptions options,
            CancellationToken cancellationToken)
        {
            if (start != 0 || !IsMatch(input, options, cancellationToken)) return default;
            if (!budget.TryReserve(32, out var resultMemory))
                throw new JqRegexResourceException("adapter result admission refused", true);
            JqRegexCapture[] captures = [new(true, 0, 1)];
            return new JqRegexMatch(captures, resultMemory);
        }
        public void Dispose() { memory.Dispose(); owner.Disposed = true; }
    }
}

sealed class SmokeHost : IJqHost
{
    public JqOperationError? CloseFailure { get; init; }
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

    // This in-memory host has no links and owns its namespace traversal policy.
    private static string? Lookup(JqPathRequest request)
    {
        if (JqPath.IsValid(request.Path)) return request.Path;
        var components = new List<string>();
        string path = request.Path.StartsWith('/') ? request.Path : request.CurrentDirectory.Path + "/" + request.Path;
        foreach (string component in path.Split('/'))
        {
            if (component is "" or ".") continue;
            if (component == "..")
            {
                if (components.Count == 0) return null;
                components.RemoveAt(components.Count - 1);
            }
            else components.Add(component);
        }
        return "/" + string.Join('/', components);
    }

    public Task<JqOpenedFile> OpenReadAsync(JqPathRequest path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Lookup(path) is not { } location || !_files.TryGetValue(location, out var bytes))
            return Task.FromResult(JqOpenedFile.Failure(new JqOperationError(JqOperationErrorKind.NotFound, "path not found")));
        var descriptor = new JqFileDescriptor(_nextDescriptor++);
        _inputs[descriptor.Id] = bytes;
        return Task.FromResult(JqOpenedFile.Success(descriptor));
    }

    public Task<JqCloseResult> CloseDescriptorAsync(JqFileDescriptor descriptor, CancellationToken cancellationToken)
    {
        if (descriptor.Id < 10) throw new InvalidOperationException("Borrowed descriptor was closed.");
        ClosedDescriptors.Add(descriptor);
        _inputs.Remove(descriptor.Id);
        return Task.FromResult(CloseFailure is { } error ? JqCloseResult.Failure(error) : JqCloseResult.Success);
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

    public Task<JqOpenedFile> OpenReadAsync(JqPathRequest path, CancellationToken cancellationToken) =>
        inner.OpenReadAsync(path, cancellationToken);

    public async Task<JqCloseResult> CloseDescriptorAsync(JqFileDescriptor descriptor, CancellationToken cancellationToken)
    {
        Closing.TrySetResult();
        await CloseRelease.Task.ConfigureAwait(false);
        return await inner.CloseDescriptorAsync(descriptor, cancellationToken).ConfigureAwait(false);
    }
}
