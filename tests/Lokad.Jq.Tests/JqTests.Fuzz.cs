using Lokad.Jq;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    private const int FuzzSeed = 20261020;
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
        """{a: (1, 2), b: .}""",
        """[.[] | {k: ., v: 1}]""",
        """. | .""",
        """., .""",
        """select(type == "number")""",
        """if . then 1 else 2 end""",
        """[if 1,null,2 then 3 else 4 end]""",
        """with_entries(.key |= "K" + .)""",
        """try . catch 0""",
        """try .a // 1""",
        """type, length""",
        """[10 > 0, 10 > 10, 10 > 20, 10 < 0, 10 < 10, 10 < 20]""",
        """.[] //= .[0]""",
        """[., arrays, objects, iterables, scalars, numbers, strings, values, booleans, nulls]""",
        """keys""",
        """has("a")""",
        """map(.)""",
        """map_values(.)""",
        """add""",
        """any(true, error; .)""",
        """all(false, error; .)""",
        """sort""",
        """reverse""",
        """join(",")""",
        """split(",")""",
        """tostring""",
        """tonumber""",
        """test("a")""",
        """match("a")""",
        """range(3)""",
        """[range(0,1;4,5;1,2)]""",
        """[range(0,1,2;4,3,2;2,3)]""",
        """limit(2; .[])""",
        """first(.[])""",
        """isempty(.[])""",
        """[inputs]""",
        """input""",
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
        """try flatten(\"a\") catch .""",
        """.a //= empty""",
        """combinations""",
        """combinations(0)""",
        """combinations(2)""",
        """[range(0;5) | [pow(2;.), log2]]""",
        """transpose""",
        """bsearch(1)""",
        """contains(1)""",
        """[contains(""), contains("\u0000")]""",
        """[contains("cd"), contains("b\u0000")]""",
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
        """try .a[] catch .""",
        """.a[]?""",
        """[.[]|try if . == 0 then error("foo") else . end catch .]""",
        """last(range(20)|.+86400|gmtime)""",
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
        """.[999999999999]""",
        """[null < 1, 1 < null, null == null]""",
        """.[1.5]""",
        """tonumber""",
        """toboolean""",
        """implode""",
        """strftime("%Y-%m-%d")""",
        """mktime""",
        """gmtime""",
        """sort_by(.)""",
        """group_by(.)""",
        """unique""",
        """reverse""",
        """keys""",
        """recurse""",
        """[limit(3; repeat(1))]""",
        """min""",
        """max""",
        """skip(1; .[])""",
        """pick(.a)""",
        """indices("")""",
        """index("")""",
        """rindex("")""",
        """indices("a")""",
        """index("a")""",
        """rindex("a")""",
        """.[null]""",
        """.[[0]]""",
        """.[[1,2]]""",
        """.[{"start":1}]""",
        """.[{"start":1,"end":2}]""",
        """path(.[[0]])""",
        """path(.[{"start":0}])""",
        """.[{"start":0}] = [0]""",
        """del(.[{"start":0}])""",
        """delpaths([[-0.5]])""",
        """path(.[-0.5])""",
        """setpath([-0.5]; 0)""",
        """.["x":]?""",
        """.[error("x")]?""",
        """getpath([[0]])""",
        """path(.[[0]])""",
        """.["x":] = [0]""",
        """ascii_upcase""",
        """ascii_downcase""",
        """trim""",
        """ltrimstr("a")""",
        """@base64""",
        """@uri""",
        """max_by(.)""",
        """min_by(.)""",
        """tostream""",
        """[tostream] | fromstream""",
        """. as $dot | fromstream($dot | tostream) | . == $dot""",
        """[tostream] | truncate_stream(1) | fromstream""",
        """[input_filename, input_line_number]""",
        """del(.a)""",
        """. as {a:$a} ?// {a:$a} ?// {a:$a} | $a""",
        """. as $a ?// {a:$a} ?// {a:$a} | $a""",
        """try .a catch .""",
        """try fromjson catch .""",
        """path(.a)""",
        """-.""",
        """try -. catch .""",
        """strptime("%Y")""",
        """strftime("%Y")""",
        """@urid""",
        """@base64d""",
        """foreach .[] as $x (0, 1; . + $x)""",
        """foreach (1,2,3) as $x (0; . + $x; . * 10)""",
        """sort_by(.a)""",
        """[nan,nan] | unique""",
        """[nan,null] | group_by(.)""",
        """[0.0, -0.0] | sort""",
        """[2,nan,1] | sort""",
        """debug""",
        """$ARGS""",
        """$ENV""",
        """stderr""",
        """reduce (1, 2) as $x (0; (., . + $x))""",
        """foreach (1, 2) as $x (0; (., . + $x))""",
        """reduce (1, 2) as $x (0; empty)""",
        """reduce (1, 2, 3) as $x (0; select($x > 1) | . + $x)""",
        """foreach (1, 2, 3) as $x (0; select($x > 1) | . + $x)""",
        """foreach (1, 2) as $x (0; empty)""",
        """reduce (1, 2) as $x (0; select($x > 10) | . + $x)""",
        """[foreach .[] as $x (0; ($x, -$x))]""",
        """[walk(if type == "number" then (., .) else . end)]""",
        """walk(if type == "number" then (., . + 1) else . end)""",
        """[walk(if type == "array" then empty else . end)]""",
        """walk(if type == "object" then {} else . end)""",
        """[walk(if type == "number" then (., . + 10) else . end)]""",
        """. as $x | $x""",
        """try @base64d catch .""",
        """try @urid catch .""",
        """getpath(path(.a))""",
        """setpath(path(.a); 1)""",
        """try (.a, error("x")) catch .""",
        """try (., error("x"), .) catch 99""",
        """. and error("x")""",
        """[(1,2,3) | ((. > 1) and (. < 3))]""",
        """[(1,2,3) | ((. > 2) or (. < 2))]""",
        """. or error("x")""",
        """. + 1e+0""",
        """1e+0+0.001e3""",
        """map(try .a[] catch ., .a[]?)""",
        """[.[]|try if . == 0 then error("foo") else . end catch .]""",
        """reduce range(100) as $i ([]; .[$i] = $i)""",
        """last(range(50)|.+86400|gmtime)""",
        """try strflocaltime("%Y") catch .""",
        """try 0[implode] catch .""",
        """. * 1000000""",
        """1 / 1e-17""",
        """9E999999999, 9999999999E999999990, 1E-999999999, 0.000000001E-999999990""",
        """(1e999999999, 10e999999999) > (1e-1147483646, 0.1e-1147483646)""",
        """try (. * 1000000000) catch .""",
        """try ("very-long-long-long-long-string" | -.) catch .""",
        """[13911860366432393] | .[0] | tostring | . == if have_decnum then "13911860366432393" else "13911860366432392" end""",
        """13911860366432393 | -. | tojson == if have_decnum then "-13911860366432393" else "-13911860366432392" end""",
        """0 | strflocaltime("" | ., @uri)""",
        """12345678909876543212345 | [., tojson] == if have_decnum then [12345678909876543212345,"12345678909876543212345"] else [12345678909876543000000,"12345678909876543000000"] end""",
        """[1234567890987654321,-1234567890987654321 | tojson] == if have_decnum then ["1234567890987654321","-1234567890987654321"] else ["1234567890987654400","-1234567890987654400"] end""",
        """[1E+1000,-1E+1000 | tojson] == if have_decnum then ["1E+1000","-1E+1000"] else ["1.7976931348623157e+308","-1.7976931348623157e+308"] end""",
        """[1E+1000,-1E+1000 | length | tojson] | unique == if have_decnum then ["1E+1000"] else ["1.7976931348623157e+308"] end""",
        """[2015,13,1,0,0,0] | mktime""",
        """[null + 5, "a" + null]""",
        """[null < 1, 1 < null, null == null]""",
        """(-1) | sqrt | isnan""",
        """1e1000 | isinfinite""",
        """0 | log""",
        """1e1000 | isfinite""",
        """[nan % 1, 1 % nan | isnan]""",
        """[(infinite, -infinite) % (1, -1, infinite)]""",
        """0.1 + 0.2 == 0.3""",
        """fmod(5.5; 2)""",
        """[strptime("%Y-%m-%dT%H:%M:%SZ")|(.,mktime)]""",
        """try strftime("%Y-%m-%dT%H:%M:%SZ") catch .""",
        """try mktime catch .""",
        """["2015-W10-4"] | .[] | strptime("%G-W%V-%u") | mktime""",
        """["2015 09 4"] | .[] | strptime("%Y %U %w") | mktime""",
        """["2015 064"] | .[] | strptime("%Y %j") | mktime""",
        """try error(0) // 1""",
        """try error catch .""",
        """try ["OK", (.[] | error)] catch ["KO", .]""",
        """[label $o | [1,2,3][] | if . > 1 then break $o else . end]""",
        """.[] | . as {a:$a} ?// {a:$a} | $a""",
        """. as {(true):$foo} | $foo""",
        """. as {(0):$foo} | $foo""",
        """def f: if . == 1 then 1 else . * (. - 1 | f) end; 5 | f""",
        """trim, ltrim, rtrim""",
        """try trim catch ., try ltrim catch ., try rtrim catch .""",
        """[match("a"; "gi")]""",
        """try capture("(?<x>a)?") catch .""",
        """map(abs)""",
        """try abs catch .""",
        """try fromdate catch .""",
        """try .[] catch .""",
        """try from_entries catch .""",
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
        """"a\ud83d\ude80b"""",
        """"a\u0304b"""",
        """"ab\u0000cd"""",
        """{"a":1,"a":2}""",
        """""2015-03-05T23:51:47Z""""",
        """""abc%""""",
        """""QUJDa""""",
        """""1E9999999999""""",
        """13911860366432393""",
        """1E-999999999""",
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

    // Malformed inputs cycle through shapes (bad token, trailing comma,
    // missing separator) so strictness paths stay under fuzz.
    private static string FuzzBadInput(Random rng) => rng.Next(3) switch
    {
        0 => "{bad",
        1 => "[1,]",
        _ => "[1 2]",
    };

    // Deep values built at runtime bypass the ingress depth cap, so every
    // operation must either compute or fail staged. Builders stay narrow and
    // bounded (a few hundred levels) to keep the sweep fast.
    private static readonly string[] DeepBuilders =
    [
        """setpath([range(200)|"a"]; 1)""",
        """setpath([range(200)|0]; 0)""",
    ];

    private static readonly string[] DeepProbes =
    [
        """type""",
        """length""",
        """keys""",
        """has("a")""",
        """.. | length""",
        """[paths] | length""",
        """walk(true)""",
        """tojson""",
        """tostring""",
        """flatten""",
        """sort""",
        """getpath([])""",
        """setpath(["b"]; 1)""",
        """delpaths([["a"]])""",
        """to_entries""",
        """map(.)""",
        """select(.)""",
        """isempty(..)""",
        """path(..)""",
        """unique""",
    ];

    [Fact]
    public async Task Jq_DeepValuesSettleOnStagedExits()
    {
        foreach (string builder in DeepBuilders)
        {
            foreach (string probe in DeepProbes)
            {
                string filter = builder + " | " + probe;
                var host = new MockFileSystem();
                host.SetStandardInput("null");
                var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", filter)));
                int exit;
                try
                {
                    exit = await tool.ExecuteAsync(host, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    Assert.Fail("Deep case escaped with " + ex.GetType().Name + ": " + filter);
                    throw new InvalidOperationException("Unreachable deep failure.");
                }
                Assert.True(exit is 0 or 5, "Deep case gave exit " + exit + ": " + filter);
                if (exit != 0)
                    Assert.StartsWith("jq:", host.GetOutput(JqFileDescriptor.StdErr), StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public async Task Jq_SeededFuzzSettlesOnStagedExits()
    {
        var rng = new Random(FuzzSeed);
        for (int index = 0; index < FuzzCases; index++)
        {
            string filter = FuzzFilters[rng.Next(FuzzFilters.Length)];
            string input = rng.Next(10) == 0 ? FuzzBadInput(rng) : FuzzValue(rng, 3);
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

    // Flag-dimension crash-freedom: framing and input modes compose with
    // every fuzz filter over hostile values. A separate seed keeps the
    // base campaign reproducible; CLI failures join the staged set.
    private const int CliFuzzSeed = 20261021;
    private const int CliFuzzCases = 120;

    private static readonly string[][] CliFlagSets =
    [
        [],
        ["-c"],
        ["-n"],
        ["-e"],
        ["-s"],
        ["-R"],
        ["-r"],
        ["-j"],
        ["--stream"],
        ["--seq"],
        ["-c", "-e"],
        ["-s", "-c"],
        ["-R", "-n"],
        ["-n", "-e"],
        ["-s", "--stream"],
        ["--seq", "-c"],
        ["--stream-errors"],
        ["-a"],
        ["-S"],
        ["-s", "-e"],
        ["-R", "-c"],
        ["--seq", "-s"],
        ["-n", "-r"],
        ["--stream-errors", "-c"],
    ];

    [Fact]
    public async Task Jq_SeededCliFuzzSettlesOnStagedExits()
    {
        var rng = new Random(CliFuzzSeed);
        for (int index = 0; index < CliFuzzCases; index++)
        {
            string[] flags = CliFlagSets[rng.Next(CliFlagSets.Length)];
            string filter = FuzzFilters[rng.Next(FuzzFilters.Length)];
            string input = rng.Next(10) == 0 ? FuzzBadInput(rng) : FuzzValue(rng, 2);
            var host = new MockFileSystem();
            host.SetStandardInput(input);
            var arguments = new List<string>(flags) { filter };
            var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", arguments.ToArray())));
            int exit;
            try
            {
                exit = await tool.ExecuteAsync(host, CancellationToken.None);
            }
            catch (Exception ex)
            {
                Assert.Fail("CLI seed " + CliFuzzSeed + " case " + index + " escaped with " + ex.GetType().Name + ": [" + string.Join(" ", arguments) + "] on " + input);
                throw new InvalidOperationException("Unreachable CLI fuzz failure.");
            }
            Assert.True(exit is 0 or 1 or 2 or 3 or 4 or 5, "CLI seed " + CliFuzzSeed + " case " + index + " gave exit " + exit + ": [" + string.Join(" ", arguments) + "] on " + input);
            if (exit is 2 or 3 or 5)
                Assert.StartsWith("jq:", host.GetOutput(JqFileDescriptor.StdErr), StringComparison.Ordinal);
        }
    }

    // Multi-input crash-freedom.
    private const int MultiFuzzSeed = 20261022;
    private const int MultiFuzzCases = 100;
    private static readonly string[] MultiFuzzFilters =
    [
        """.""",
        """., .""",
        """add""",
        """length""",
        """type""",
        """map(.)""",
        """first(.)""",
        """limit(1; .)""",
        """try . catch 0""",
        """1""",
        """null""",
        """.a""",
        """tojson""",
        """tostring""",
        """.[]""",
        """select(.)""",
        """empty""",
    ];
    private static readonly string[][] MultiFuzzFlagSets =
    [
        [],
        ["-e"],
        ["-s"],
        ["-c"],
    ];

    [Fact]
    public async Task Jq_SeededMultiInputFuzzSettlesOnStagedExits()
    {
        var rng = new Random(MultiFuzzSeed);
        for (int index = 0; index < MultiFuzzCases; index++)
        {
            string[] flags = MultiFuzzFlagSets[rng.Next(MultiFuzzFlagSets.Length)];
            string filter = MultiFuzzFilters[rng.Next(MultiFuzzFilters.Length)];
            var parts = new List<string>();
            int count = 1 + rng.Next(3);
            for (int part = 0; part < count; part++)
                parts.Add(rng.Next(8) == 0 ? FuzzBadInput(rng) : FuzzValue(rng, 2));
            string input = string.Join("\n", parts) + "\n";
            var host = new MockFileSystem();
            host.SetStandardInput(input);
            var arguments = new List<string>(flags) { filter };
            var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", arguments.ToArray())));
            int exit;
            try
            {
                exit = await tool.ExecuteAsync(host, CancellationToken.None);
            }
            catch (Exception ex)
            {
                Assert.Fail("Multi seed " + MultiFuzzSeed + " case " + index + " escaped: " + ex.GetType().Name);
                throw new InvalidOperationException("Unreachable multi-input fuzz failure.");
            }
            Assert.True(exit is 0 or 1 or 4 or 5, "Multi seed " + MultiFuzzSeed + " case " + index + " gave exit " + exit);
            if (exit == 5)
                Assert.StartsWith("jq:", host.GetOutput(JqFileDescriptor.StdErr), StringComparison.Ordinal);
        }
    }

    // Path-mode crash-freedom: path enumeration, deletion, assignment,
    // and update targets over hostile values must settle on staged exits.
    // Filters stay fixed and valid; paths that resolve nowhere fail with
    // catchable diagnostics instead of escaping.
    private const int PathFuzzSeed = 20261023;
    private const int PathFuzzCases = 120;

    private static readonly string[] PathFuzzFilters =
    [
        """path(.)""",
        """path(.a)""",
        """path(.[])""",
        """path(..)""",
        """path(.a | map(.))""",
        """path(.a | .b)""",
        """del(.a)""",
        """delpaths([["a"]])""",
        """pick(.a)""",
        """getpath(["a"])""",
        """setpath(["a"]; 1)""",
        """.a = 1""",
        """.a[2].b = 1""",
        """.[] = 1""",
        """.a |= . + 1""",
        """(.a | map(.)) = 1""",
        """[paths]""",
        """limit(1; path(.a))""",
        """isempty(path(.a))""",
        """path(. as $x | $x)""",
        """try path(.a as $x | $x) catch .""",
        """path(1 as $x | $x)""",
        """path((.a as $x | .b))""",
        """(.a as $x | $x) = 1""",
        """(.a as $x | .b) = 1""",
        """(.. | select(type == "object" and has("b")) | .b) |= 0""",
        """path(try .a[])""",
        """try del(.a[]) catch .""",
        """.a[]?""",
        """delpaths([paths(type == "number")])""",
        // Missing-deep and invalid-segment deletions: unchanged subtrees detach, invalid fails staged.
        """delpaths([["a","x","y"]])""",
        """delpaths([["a",null]])""",
        """delpaths([[null]])""",
        """delpaths([["a","x"],["a","y"]])""",
        // NaN and empty path arguments: invalid-segment failures stay staged, empties detach.
        """pick(nan)""",
        """del(nan)""",
        """pick(empty)""",
        """del(empty)""",
        """delpaths([[nan]])""",
        """delpaths([[{start:0,end:1}]])""",
        """delpaths([[0,"a"]])""",
        """delpaths([[0]])""",
    ];

    [Fact]
    public async Task Jq_SeededPathFuzzSettlesOnStagedExits()
    {
        var rng = new Random(PathFuzzSeed);
        for (int index = 0; index < PathFuzzCases; index++)
        {
            string filter = PathFuzzFilters[rng.Next(PathFuzzFilters.Length)];
            string input = rng.Next(10) == 0 ? FuzzBadInput(rng) : FuzzValue(rng, 2);
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
                Assert.Fail("Path seed " + PathFuzzSeed + " case " + index + " escaped with " + ex.GetType().Name + ": " + filter + " on " + input);
                throw new InvalidOperationException("Unreachable path fuzz failure.");
            }
            Assert.True(exit is 0 or 5, "Path seed " + PathFuzzSeed + " case " + index + " gave exit " + exit);
            if (exit != 0)
                Assert.StartsWith("jq:", host.GetOutput(JqFileDescriptor.StdErr), StringComparison.Ordinal);
        }
    }
}
