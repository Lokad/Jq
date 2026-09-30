using Lokad.Jq;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    private const int FuzzSeed = 20260930;
    private const int FuzzCases = 200;

    private static readonly string[] FuzzFilters =
    [
        """.""",
        """.[]""",
        """.a""",
        """.["a"]""",
        """.[0]""",
        """.[-1]""",
        """.[0:2]""",
        """[.]""",
        """{a: .}""",
        """. | .""",
        """., .""",
        """select(type == "number")""",
        """if . then 1 else 2 end""",
        """try . catch 0""",
        """type, length""",
        """keys""",
        """has("a")""",
        """map(.)""",
        """map_values(.)""",
        """add""",
        """sort""",
        """reverse""",
        """join(",")""",
        """split(",")""",
        """tostring""",
        """tonumber""",
        """test("a")""",
        """match("a")""",
        """range(3)""",
        """limit(2; .[])""",
        """first(.[])""",
        """isempty(.[])""",
        """paths""",
        """.. | type""",
        """1 + 2 * 3""",
        """. == .""",
        """while(false; .)""",
        """until(true; .)""",
        """def f: 1; f""",
        """def f(x): x + 1; f(41)""",
        """def f: f; f""",
        """\(.)""",
        """@json""",
        """to_entries""",
        """flatten""",
        """combinations""",
        """transpose""",
        """bsearch(1)""",
        """contains(1)""",
        """inside([1])""",
        """index(1)""",
        """indices(1)""",
        """startswith("a")""",
        """explode | implode""",
        """utf8bytelength""",
        """tojson | fromjson""",
        """floor""",
        """not""",
        """empty""",
        """gsub("a"; "b")""",
        """sub("a"; "b")""",
        """capture("(?<x>.)")""",
        """scan("a")""",
        """walk(true)""",
        """delpaths([["a"]])""",
        """getpath(["a"])""",
        """IN([1, 2])""",
        """paths((true, true))""",
        """INDEX(.[]; .)""",
        """JOIN({"a": 1}; "a")""",
        """def f: "\(.+1)"; f""",
        """builtins | length""",
        """format("text")""",
        """_strindices("a")""",
        """-.""",
        """path(first)""",
        """path(last)""",
        """pick(first)""",
        """setpath([1]; 1)""",
        """.[1e18]""",
        """.[1.5]""",
        """tonumber""",
        """toboolean""",
        """implode""",
    ];

    private static readonly string[] FuzzAtoms =
    [
        """null""",
        """true""",
        """false""",
        """0""",
        """1""",
        """-1""",
        """-0""",
        """1.5""",
        """1e3""",
        """nan""",
        """Infinity""",
        "\"\"",
        "\"a\"",
        "\"foo bar\"",
        "\"a\\\"b\"",
        """[]""",
        """{}""",
        """[1, 2]""",
        """[null]""",
        """{"a": 1}""",
        """{"a": {"b": [1]}}""",
    ];

    private static readonly string[] FuzzKeys = ["a", "b", "c", "x y"];

    private static string FuzzValue(Random rng, int depth)
    {
        if (depth <= 0 || rng.Next(3) == 0)
            return FuzzAtoms[rng.Next(FuzzAtoms.Length)];
        if (rng.Next(2) == 0)
        {
            var parts = new List<string>();
            int count = rng.Next(4);
            for (int i = 0; i < count; i++)
                parts.Add(FuzzValue(rng, depth - 1));
            return "[" + string.Join(",", parts) + "]";
        }
        var props = new List<string>();
        int fields = rng.Next(4);
        for (int i = 0; i < fields; i++)
            props.Add("\"" + FuzzKeys[rng.Next(FuzzKeys.Length)] + "\":" + FuzzValue(rng, depth - 1));
        return "{" + string.Join(",", props) + "}";
    }

    [Fact]
    public async Task Jq_SeededFuzzSettlesOnStagedExits()
    {
        var rng = new Random(FuzzSeed);
        for (int index = 0; index < FuzzCases; index++)
        {
            string filter = FuzzFilters[rng.Next(FuzzFilters.Length)];
            string input = rng.Next(10) == 0 ? "{bad" : FuzzValue(rng, 3);
            var host = new MockFileSystem();
            host.SetStandardInput(input);
            var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", filter)));
            int exit;
            try
            {
                exit = await tool.ExecuteAsync(host, CancellationToken.None);
            }
            catch (Exception ex)
            {
                Assert.Fail("Seed " + FuzzSeed + " case " + index + " escaped with " + ex.GetType().Name + ": " + filter + " on " + input);
                throw new InvalidOperationException("Unreachable fuzz failure.");
            }
            Assert.True(exit is 0 or 3 or 4 or 5, "Seed " + FuzzSeed + " case " + index + " gave exit " + exit + ": " + filter + " on " + input);
            if (exit != 0)
                Assert.StartsWith("jq:", host.GetOutput(JqFileDescriptor.StdErr), StringComparison.Ordinal);
        }
    }
}
