using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Lokad.Jq.Benchmarking;

internal sealed class LocalWorkload
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private readonly Jq _command;
    private readonly byte[] _input;
    private readonly OutputDigest _expected;

    private LocalWorkload(string[] arguments, byte[] input, string output)
    {
        Arguments = arguments;
        _command = BenchmarkExecution.Bind(arguments);
        _input = input;
        _expected = OutputDigest.FromBytes(Encoding.UTF8.GetBytes(output));
    }

    public string[] Arguments { get; }

    public async Task<long> ExecuteAsync(bool bind)
    {
        using var host = new BenchmarkHost(_input, false);
        int exit = await (bind ? BenchmarkExecution.Bind(Arguments) : _command)
            .ExecuteAsync(host, CancellationToken.None).ConfigureAwait(false);
        var result = host.Finish(exit);
        BenchmarkExecution.Verify(result, _expected);
        return result.Digest.Length;
    }

    public static LocalWorkload Range() => Generated("range(0;1000) | . * 2",
        string.Concat(Enumerable.Range(0, 1000).Select(i => (i * 2).ToString(CultureInfo.InvariantCulture) + "\n")));

    public static LocalWorkload Scalar() => Generated("[range(0;500) | {name: .}] | .[].name",
        string.Concat(Enumerable.Range(0, 500).Select(i => i.ToString(CultureInfo.InvariantCulture) + "\n")));

    public static LocalWorkload Construction() => Generated("[{a: range(0;20), b: range(0;20)}]",
        Serialize(Enumerable.Range(0, 20).SelectMany(a => Enumerable.Range(0, 20).Select(b => new { a, b }))) + "\n");

    public static LocalWorkload Reduction() => Generated("reduce range(0;1000) as $x (0; . + $x)", "499500\n");
    public static LocalWorkload SortGroup() => Generated(
        "[range(0;500) | {k: . % 7, v: .}] | sort_by(.k) | group_by(.k) | map(length)",
        "[72,72,72,71,71,71,71]\n");
    public static LocalWorkload Unicode() => Generated("[\"é🚀\" * 100] | map(explode | implode) | length", "1\n");
    public static LocalWorkload Walk() => Generated(
        "[{a: {b: [range(0;50)]}}] | walk(if type == \"number\" then . + 1 else . end) | length", "1\n");
    public static LocalWorkload Paths() => Generated("[range(0;200) | {v: .}] | [paths] | length", "400\n");
    public static LocalWorkload ParseAndExecute() => Generated("range(0;100) | {value:.}",
        string.Concat(Enumerable.Range(0, 100).Select(value => Serialize(new { value }) + "\n")));
    public static LocalWorkload InputTransform() => new(["-c", "map(.v * 2)"],
        Encoding.UTF8.GetBytes(Serialize(Enumerable.Range(0, 1000).Select(v => new { v }))),
        Serialize(Enumerable.Range(0, 1000).Select(v => v * 2)) + "\n");
    public static LocalWorkload UnicodeTransform() => new(["-c", "map(.t | explode | implode)"],
        Encoding.UTF8.GetBytes(Serialize(Enumerable.Range(0, 200).Select(i => new { t = $"café 🚀 item-{i}" }))),
        "[" + string.Join(',', Enumerable.Range(0, 200).Select(i => $"\"café \\ud83d\\ude80 item-{i}\"")) + "]\n");

    private static LocalWorkload Generated(string filter, string output) => new(["-n", "-c", filter], [], output);
    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);
}
