using Lokad.Jq;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("{\"b\": 1, \"a\": 2}", "keys", "[\n  \"a\",\n  \"b\"\n]\n")]
    [InlineData("{\"b\": 1, \"a\": 2}", "keys_unsorted", "[\n  \"b\",\n  \"a\"\n]\n")]
    // Duplicate keys coalesce last-wins at parse like jv_object_set, so counts and orders see one entry.
    [InlineData("{\"b\":1,\"a\":2,\"b\":3}", "keys_unsorted", "[\n  \"b\",\n  \"a\"\n]\n")]
    [InlineData("{\"b\":1,\"a\":2,\"b\":3}", "length", "2\n")]
    [InlineData("[42, 3, 35]", "keys", "[\n  0,\n  1,\n  2\n]\n")]
    [InlineData("[{\"foo\": 42}, {}]", "map(has(\"foo\"))", "[\n  true,\n  false\n]\n")]
    [InlineData("[[0, 1], [\"a\", \"b\", \"c\"]]", "map(has(2))", "[\n  false,\n  true\n]\n")]
    [InlineData("{\"a\": 1, \"b\": 2}", "to_entries", "[\n  {\n    \"key\": \"a\",\n    \"value\": 1\n  },\n  {\n    \"key\": \"b\",\n    \"value\": 2\n  }\n]\n")]
    [InlineData("[{\"key\": \"a\", \"value\": 1}, {\"Key\": \"b\", \"Value\": 2}, {\"name\": \"c\", \"value\": 3}, {\"Name\": \"d\", \"Value\": 4}]", "from_entries", "{\n  \"a\": 1,\n  \"b\": 2,\n  \"c\": 3,\n  \"d\": 4\n}\n")]
    [InlineData("{\"a\": 1, \"b\": 2}", "with_entries(.key |= \"KEY_\" + .)", "{\n  \"KEY_a\": 1,\n  \"KEY_b\": 2\n}\n")]
    [InlineData("[{\"key\":\"a\", \"value\":1}, {\"key\":\"a\", \"value\":2}]", "from_entries", "{\n  \"a\": 2\n}\n")]
    [InlineData("[]", "to_entries", "[]\n")]
    // Entries roundtrip fixes every object; duplicate keys coalesce last-wins and stay visible as one entry.
    [InlineData("{\"a\":1,\"a\":2}", "to_entries", "[\n  {\n    \"key\": \"a\",\n    \"value\": 2\n  }\n]\n")]
    [InlineData("{\"b\":1,\"a\":2}", "(to_entries|from_entries) == .", "true\n")]
    [InlineData("{\"a\":1,\"a\":2}", "(to_entries|from_entries) == .", "true\n")]
    [InlineData("{\"a\":1,\"b\":2}", "with_entries(select(.key != \"a\"))", "{\n  \"b\": 2\n}\n")]
    [InlineData("[1, 2]", "to_entries", "[\n  {\n    \"key\": 0,\n    \"value\": 1\n  },\n  {\n    \"key\": 1,\n    \"value\": 2\n  }\n]\n")]
    [InlineData("[{\"key\": \"a\", \"value\": 1}, {\"key\": \"a\", \"value\": 2}]", "from_entries", "{\n  \"a\": 2\n}\n")]
    [InlineData("[1, \"a\", true, null, [], {}]", "map(type)", "[\n  \"number\",\n  \"string\",\n  \"boolean\",\n  \"null\",\n  \"array\",\n  \"object\"\n]\n")]
    [InlineData("[1, \"a\", true, null, [], {}]", "[(.[] | arrays), (.[] | objects), (.[] | numbers)]", "[\n  [],\n  {},\n  1\n]\n")]
    [InlineData("[0, 1, 2]", "has(-1 | sqrt)", "false\n")]
    [InlineData("[0,1,2]", "has(nan)", "false\n")]
    // Fractional indices truncate like the reference array get; the object number-key diagnostic matches jv_has byte-exact.
    [InlineData("[1,2]", "has(1.5)", "true\n")]
    [InlineData("{\"a\":1,\"a\":2}", ".", "{\n  \"a\": 2\n}\n")]
    [InlineData("1", "in([1,2])", "true\n")]
    // IN compares by equality, so NaN never matches, unlike INDEX tostring keys.
    [InlineData("nan", "in([nan])", "false\n")]
    [InlineData("{\"a\":1,\"b\":2}", "pick(.a)", "{\n  \"a\": 1\n}\n")]
    [InlineData("{\"a\":1,\"b\":2}", "delpaths([[\"a\"]])", "{\n  \"b\": 2\n}\n")]
    [InlineData("[{}, {\"abcd\":1,\"abc\":2,\"abcde\":3}, {\"x\":1, \"z\": 3, \"y\":2}]", "map(keys)", "[\n  [],\n  [\n    \"abc\",\n    \"abcd\",\n    \"abcde\"\n  ],\n  [\n    \"x\",\n    \"y\",\n    \"z\"\n  ]\n]\n")]
    [InlineData("[[], [1,2,3], [\"a\",\"b\",\"c\"], [[3],[4,5],[6]], [{\"a\":1}, {\"b\":2}, {\"a\":3}]]", "map(add)", "[\n  null,\n  6,\n  \"abc\",\n  [\n    3,\n    4,\n    5,\n    6\n  ],\n  {\n    \"a\": 3,\n    \"b\": 2\n  }\n]\n")]
    [InlineData("[0,1,2]", "map_values(.+1)", "[\n  1,\n  2,\n  3\n]\n")]
    [InlineData("[1, 2, 3]", "[ .[] | . * 2]", "[\n  2,\n  4,\n  6\n]\n")]
    [InlineData("[1,2,3]", "map(.+1)", "[\n  2,\n  3,\n  4\n]\n")]
    [InlineData("[1,2]", "map(., .)", "[\n  1,\n  1,\n  2,\n  2\n]\n")]
    [InlineData("{\"a\":1,\"b\":2}", "map(.)", "[\n  1,\n  2\n]\n")]
    [InlineData("{\"a\":1,\"b\":2}", "map_values(.)", "{\n  \"a\": 1,\n  \"b\": 2\n}\n")]
    [InlineData("{\"a\":1}", "map(., .)", "[\n  1,\n  1\n]\n")]
    [InlineData("[\"a\",\"a\",\"b\",\"a\",\"d\",\"b\",\"d\",\"a\",\"d\"]", "add({(.[]):1}) | keys", "[\n  \"a\",\n  \"b\",\n  \"d\"\n]\n")]
    // has() follows the reference kinds: null never has, mismatches error, float indices truncate.
    [InlineData("null", "has(\"a\")", "false\n")]
    [InlineData("1", "try has(\"a\") catch .", "\"Cannot check whether number has a string key\"\n")]
    [InlineData("\"a\"", "try has(0) catch .", "\"Cannot check whether string has a number key\"\n")]
    [InlineData("[1]", "try has(\"a\") catch .", "\"Cannot check whether array has a string key\"\n")]
    [InlineData("{\"a\":1}", "try has(0) catch .", "\"Cannot check whether object has a number key\"\n")]
    [InlineData("[1,2,3]", "has(1.9)", "true\n")]
    [InlineData("1", "try keys catch .", "\"number (1) has no keys\"\n")]
    public async Task Jq_StructuralBuiltins(string input, string filter, string expected)
    {
        var host = new MockFileSystem();
        host.SetStandardInput(input);
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("5", "keys", "number (5) has no keys")]
    [InlineData("\"ab\"", "to_entries", "string (\"ab\") has no keys")]
    [InlineData("{\"a\":1}", "has(0)", "Cannot check whether object has a number key")]
    // Scalar combinations fail at native iteration rather than the desugar length/index steps; wording follows the structured-error policy.
    [InlineData("1", "combinations", "cannot iterate over number")]
    [InlineData("\"a\"", "combinations", "cannot iterate over string")]
    [InlineData("{\"a\":1}", "combinations", "cannot iterate over object")]
    [InlineData("\"abcdef\"", ".[\"a\":]", "Array/string slice indices must be integers")]
    [InlineData("[[1]]", "combinations(\"a\")", "Range bounds must be numeric")]
    [InlineData("[{}]", "from_entries", "Cannot use null (null) as object key")]
    [InlineData("[{\"a\":1}]", "from_entries", "Cannot use null (null) as object key")]
    [InlineData("null", "flatten", "cannot iterate over null")]
    [InlineData("\"ab\"", "reverse", "cannot reverse string")]
    [InlineData("5", "reverse", "cannot reverse number")]
    public async Task Jq_StructuralFailures(string input, string filter, string diagnostic)
    {
        var host = new MockFileSystem();
        host.SetStandardInput(input);
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", filter)));

        Assert.Equal(5, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Contains(diagnostic, host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }

    [Theory]
    [InlineData("[1, 2, 3]", "contains([2, 3])", "true\n")]
    [InlineData("{\"a\": {\"b\": [1, 2]}}", "contains({\"a\": {\"b\": [2]}})", "true\n")]
    [InlineData("\"abc\"", "contains(\"\")", "true\n")]
[InlineData("\"ab\\u0000cd\"", "[contains(\"\"), contains(\"a\"), contains(\"ab\"), contains(\"c\"), contains(\"d\")]", "[\n  true,\n  true,\n  true,\n  true,\n  true\n]\n")]
[InlineData("\"ab\\u0000cd\"", "[contains(\"cd\"), contains(\"b\\u0000\"), contains(\"ab\\u0000\")]", "[\n  true,\n  true,\n  true\n]\n")]
[InlineData("\"ab\\u0000cd\"", "[contains(\"@\"), contains(\"\\u0000@\"), contains(\"\\u0000what\")]", "[\n  false,\n  false,\n  false\n]\n")]
[InlineData("null", "[({foo: 12, bar:13} | contains({foo: 12})), ({foo: 12} | contains({})), ({foo: 12, bar:13} | contains({baz:14}))]", "[\n  true,\n  true,\n  false\n]\n")]
[InlineData("null", "{foo: {baz: 12, blap: {bar: 13}}, bar: 14} | contains({bar: 14, foo: {blap: {}}})", "true\n")]
    [InlineData("\"ab\"", "inside(\"xaby\")", "true\n")]
    [InlineData("[]", "contains([])", "true\n")]
    [InlineData("{}", "contains({})", "true\n")]
    [InlineData("\"\"", "contains(\"a\")", "false\n")]
    [InlineData("[0, 1, 2, 3, 1, 4, 2, 5, 1, 2, 6, 7]", "indices([1, 2])", "[\n  1,\n  8\n]\n")]
    [InlineData("\"🇬🇧oo\"", "indices(\"o\")", "[\n  2,\n  3\n]\n")]
    [InlineData("[0, [1], [[2]], [[[3]]]]", "flatten", "[\n  0,\n  1,\n  2,\n  3\n]\n")]
    [InlineData("[0, [1], [[2]], [[[3]]]]", "flatten(0)", "[\n  0,\n  [\n    1\n  ],\n  [\n    [\n      2\n    ]\n  ],\n  [\n    [\n      [\n        3\n      ]\n    ]\n  ]\n]\n")]
    [InlineData("[0, [1], [[2]], [[[3]]]]", "flatten(2)", "[\n  0,\n  1,\n  2,\n  [\n    3\n  ]\n]\n")]
    [InlineData("[0, [1, [2]], [1, [[3], 2]]]", "flatten(2)", "[\n  0,\n  1,\n  2,\n  1,\n  [\n    3\n  ],\n  2\n]\n")]
    [InlineData("[[1]]", "flatten(0.5)", "[\n  1\n]\n")]
    // Non-numeric depths follow the desugar: strings and containers never equal zero, so they flatten fully.
    [InlineData("[1,[2,[3]]]", "flatten(\"a\")", "[\n  1,\n  2,\n  3\n]\n")]
    [InlineData("[1,[2,[3]]]", "flatten(\"1\")", "[\n  1,\n  2,\n  3\n]\n")]
    [InlineData("[1,[2,[3]]]", "flatten([])", "[\n  1,\n  2,\n  3\n]\n")]
    [InlineData("[1,[2]]", "_flatten(null)", "[\n  1,\n  2\n]\n")]
    [InlineData("{\"a\":1}", "flatten", "[\n  1\n]\n")]
    [InlineData("\"\"", "reverse", "[]\n")]
    [InlineData("{}", "reverse", "[]\n")]
    [InlineData("null", "reverse", "[]\n")]
    [InlineData("[3,1,2]", "reverse", "[\n  2,\n  1,\n  3\n]\n")]
    [InlineData("{\"arr\": [1, 2, 3]}", ".sum = add(.arr[])", "{\n  \"arr\": [\n    1,\n    2,\n    3\n  ],\n  \"sum\": 6\n}\n")]
    [InlineData("[[1], [2, 3]]", "transpose", "[\n  [\n    1,\n    2\n  ],\n  [\n    null,\n    3\n  ]\n]\n")]
    [InlineData("[]", "transpose", "[]\n")]
    [InlineData("[[]]", "transpose", "[]\n")]
    [InlineData("{\"a\": [1, 2]}", "transpose", "[\n  [\n    1\n  ],\n  [\n    2\n  ]\n]\n")]
    [InlineData("[[],[1]]", "transpose", "[\n  [\n    null,\n    1\n  ]\n]\n")]
    // Null rows contribute zero width like empty rows; present nulls pad.
    [InlineData("[null]", "transpose", "[]\n")]
    [InlineData("[[1],null]", "transpose", "[\n  [\n    1,\n    null\n  ]\n]\n")]
    [InlineData("[[1, 2], [3]]", "combinations", "[\n  1,\n  3\n]\n[\n  2,\n  3\n]\n")]
    [InlineData("[1, 2]", "combinations(2)", "[\n  1,\n  1\n]\n[\n  1,\n  2\n]\n[\n  2,\n  1\n]\n[\n  2,\n  2\n]\n")]
    [InlineData("[1, 2]", "combinations(0)", "[]\n")]
    [InlineData("[1, 2]", "combinations(-1)", "[]\n")]
    // Numeric-string counts coerce like other count positions; other kinds fail with the reference range diagnostic.
    [InlineData("[[1,2],[3,4]]", "combinations(\"2\")", "[\n  [\n    1,\n    2\n  ],\n  [\n    1,\n    2\n  ]\n]\n[\n  [\n    1,\n    2\n  ],\n  [\n    3,\n    4\n  ]\n]\n[\n  [\n    3,\n    4\n  ],\n  [\n    1,\n    2\n  ]\n]\n[\n  [\n    3,\n    4\n  ],\n  [\n    3,\n    4\n  ]\n]\n")]
    [InlineData("[]", "combinations", "[]\n")]
    // Length-zero inputs yield one empty combination like the reference length check.
    [InlineData("null", "combinations", "[]\n")]
    [InlineData("\"\"", "combinations", "[]\n")]
    [InlineData("{}", "combinations", "[]\n")]
    [InlineData("0", "combinations", "[]\n")]
    // An empty row kills the cartesian product per the recursive desugar.
    [InlineData("[[]]", "combinations", "")]
    [InlineData("[[],[1]]", "combinations", "")]
    [InlineData("[1, 2, 3]", "bsearch(0, 1, 2, 3, 4)", "-1\n0\n1\n2\n-4\n")]
    [InlineData("[]", "bsearch(1)", "-1\n")]
    [InlineData("[[0, [1]]]", "flatten((1, 0))", "[\n  0,\n  [\n    1\n  ]\n]\n[\n  [\n    0,\n    [\n      1\n    ]\n  ]\n]\n")]
    [InlineData("{\"a\": [1]}", "flatten", "[\n  1\n]\n")]
    [InlineData("{\"a\": [1, [2]]}", "flatten(1)", "[\n  1,\n  [\n    2\n  ]\n]\n")]
    [InlineData("[0, [1], [[2]], [[[3]]]]", "flatten(3,2,1)", "[\n  0,\n  1,\n  2,\n  3\n]\n[\n  0,\n  1,\n  2,\n  [\n    3\n  ]\n]\n[\n  0,\n  1,\n  [\n    2\n  ],\n  [\n    [\n      3\n    ]\n  ]\n]\n")]
    [InlineData("\"a,b|c,d,e||f,g,h,|,|,i,j\"", "[(index(\",\",\"|\"), rindex(\",\",\"|\")), indices(\",\",\"|\")]", "[\n  1,\n  3,\n  22,\n  19,\n  [\n    1,\n    5,\n    7,\n    12,\n    14,\n    16,\n    18,\n    20,\n    22\n  ],\n  [\n    3,\n    9,\n    10,\n    17,\n    19\n  ]\n]\n")]
    [InlineData("[\"a\",\"b\",\"c\",\"d\"]", "join(\",\",\"/\")", "\"a,b,c,d\"\n\"a/b/c/d\"\n")]
    [InlineData("\"xababababax\"", "[ index(\"aba\"), rindex(\"aba\"), indices(\"aba\") ]", "[\n  1,\n  7,\n  [\n    1,\n    3,\n    5,\n    7\n  ]\n]\n")]
    [InlineData("\"a,bc,def,ghij,klmno\"", "[(index(\",\"), rindex(\",\")), indices(\",\")]", "[\n  1,\n  13,\n  [\n    1,\n    4,\n    8,\n    13\n  ]\n]\n")]
    [InlineData("[0,1,1,2,3,4,1,5]", "indices(1)", "[\n  1,\n  2,\n  6\n]\n")]
    [InlineData("[0,1,2,3,1,4,2,5,1,2,6,7]", "indices([1,2])", "[\n  1,\n  8\n]\n")]
    [InlineData("[1]", "indices([1,2])", "[]\n")]
    // Like the reference jv_array_indexes, an empty array needle matches
    // nowhere (the string branch already yields nothing for empty needles).
    [InlineData("[1,2]", "indices([])", "[]\n")]
    [InlineData("[1,2]", "index([])", "null\n")]
    [InlineData("[1,2]", "rindex([])", "null\n")]
    [InlineData("\"a,b, cd,e, fgh, ijkl\"", "indices(\", \")", "[\n  3,\n  9,\n  14\n]\n")]
    [InlineData("\"здравствуй мир!\"", "index(\"!\")", "14\n")]
    [InlineData("\"ƒoo\"", "indices(\"o\")", "[\n  1,\n  2\n]\n")]
    [InlineData("[\"a, bc, def, ghij, jklmn, a,b, c,d, e,f\", \"a,b,c,d, e,f,g,h\"]", "[.[]|split(\",\")]", "[\n  [\n    \"a\",\n    \" bc\",\n    \" def\",\n    \" ghij\",\n    \" jklmn\",\n    \" a\",\n    \"b\",\n    \" c\",\n    \"d\",\n    \" e\",\n    \"f\"\n  ],\n  [\n    \"a\",\n    \"b\",\n    \"c\",\n    \"d\",\n    \" e\",\n    \"f\",\n    \"g\",\n    \"h\"\n  ]\n]\n")]
    [InlineData("[[],[\"\"],[\"\",\"\"],[\"\",\"\",\"\"]]", "[.[]|join(\"a\")]", "[\n  \"\",\n  \"\",\n  \"a\",\n  \"aa\"\n]\n")]
    [InlineData("[{\"x\": 0}, {\"x\": 1}, {\"x\": 2}]", "bsearch({\"x\": 1})", "1\n")]
    [InlineData("0", "range(3; 0; -1)", "3\n2\n1\n")]
    [InlineData("0", "range(0; 1; 0.5)", "0\n0.5\n")]
    [InlineData("0", "range(0; 2; 0.5)", "0\n0.5\n1\n1.5\n")]
    [InlineData("0", "range(2; 0; -0.5)", "2\n1.5\n1\n0.5\n")]
    [InlineData("0", "range(0.5; 2)", "0.5\n1.5\n")]
    [InlineData("0", "range(0; 1; 0)", "")]
    [InlineData("0", "range(0; 1; nan)", "")]
    // Non-numeric bounds fail with the reference range diagnostic while numeric strings keep the recorded leniency.
    [InlineData("0", "try range(\"a\") catch .", "\"Range bounds must be numeric\"\n")]
    [InlineData("0", "try range(0;\"a\") catch .", "\"Range bounds must be numeric\"\n")]
    [InlineData("0", "[range(\"2\";4)]", "[\n  2,\n  3\n]\n")]
    [InlineData("[1,2,3]", "map(if . > 1 then . end)", "[\n  1,\n  2,\n  3\n]\n")]
    [InlineData("null", "{x: try 1 | . + 1}", "{\n  \"x\": 2\n}\n")]
    [InlineData("null", "{x: null // 1 | . + 1}", "{\n  \"x\": 2\n}\n")]
    [InlineData("null", "{x: [1, def f: 2; f]}", "{\n  \"x\": [\n    1,\n    2\n  ]\n}\n")]
    [InlineData("[\"baz\", \"bar\"]", "inside([\"foobar\", \"foobaz\", \"blarp\"])", "true\n")]
    [InlineData("[\"bazzzzz\", \"bar\"]", "inside([\"foobar\", \"foobaz\", \"blarp\"])", "false\n")]
    [InlineData("{\"foo\": 12, \"bar\": [{\"barp\": 12}]}", "inside({\"foo\": 12, \"bar\":[1,2,{\"barp\":12, \"blip\":13}]})", "true\n")]
    [InlineData("{\"foo\": 12, \"bar\": [{\"barp\": 15}]}", "inside({\"foo\": 12, \"bar\":[1,2,{\"barp\":12, \"blip\":13}]})", "false\n")]
    [InlineData("[2,4,4,4,5,5,7,9]", "(add / length) as $m | map((. - $m) as $d | $d * $d) | add / length | sqrt", "2\n")]
    [InlineData("[[[],[]], [[1,2,3], [1,2]], [[1,2,3], [3,1]], [[1,2,3], [4]], [[1,2,3], [1,4]]]", "map(.[1] as $needle | .[0] | contains($needle))", "[\n  true,\n  true,\n  true,\n  false,\n  false\n]\n")]
    [InlineData("[[[\"foobar\", \"foobaz\"], [\"baz\", \"bar\"]], [[\"foobar\", \"foobaz\"], [\"foo\"]], [[\"foobar\", \"foobaz\"], [\"blap\"]]]", "map(.[1] as $needle | .[0] | contains($needle))", "[\n  true,\n  true,\n  false\n]\n")]
    [InlineData("[1,2,3]", "bsearch(4) as $ix | if $ix < 0 then .[-(1+$ix)] = 4 else . end", "[\n  1,\n  2,\n  3,\n  4\n]\n")]
    [InlineData("null", "{x: (1,2)},{x:3} | .x", "1\n2\n3\n")]
    [InlineData("[\"a\",\"b\"]", "[(.,1),((.,.[]),(2,3))]", "[\n  [\n    \"a\",\n    \"b\"\n  ],\n  1,\n  [\n    \"a\",\n    \"b\"\n  ],\n  \"a\",\n  \"b\",\n  2,\n  3\n]\n")]
    [InlineData("1", "{x:-1},{x:-.},{x:-.|abs}", "{\n  \"x\": -1\n}\n{\n  \"x\": -1\n}\n{\n  \"x\": 1\n}\n")]
    [InlineData("null", "[1,2,empty,3,empty,4]", "[\n  1,\n  2,\n  3,\n  4\n]\n")]
    [InlineData("\"abc\"", ". * 100000 | [.[:10],.[-10:]]", "[\n  \"abcabcabca\",\n  \"cabcabcabc\"\n]\n")]
    [InlineData("{\"user\":\"stedolan\", \"projects\": [\"jq\", \"wikiflow\"]}", "[.user, .projects[]]", "[\n  \"stedolan\",\n  \"jq\",\n  \"wikiflow\"\n]\n")]
    [InlineData("{\"user\":\"stedolan\",\"titles\":[\"JQ Primer\", \"More JQ\"]}", "{user, title: .titles[]}", "{\n  \"user\": \"stedolan\",\n  \"title\": \"JQ Primer\"\n}\n{\n  \"user\": \"stedolan\",\n  \"title\": \"More JQ\"\n}\n")]
    [InlineData("[\"foo\", \"bar\"]", ".[] | in({\"foo\": 42})", "true\nfalse\n")]
    [InlineData("\"asdfasdf\"", "{\"a\":1} + {\"b\":2} + {\"c\":3}", "{\n  \"a\": 1,\n  \"b\": 2,\n  \"c\": 3\n}\n")]
    [InlineData("{\"k\": {\"a\": 0,\"c\": 3}}", "{\"k\": {\"a\": 1, \"b\": 2}} * .", "{\n  \"k\": {\n    \"a\": 0,\n    \"b\": 2,\n    \"c\": 3\n  }\n}\n")]
    [InlineData("{\"k\": {\"a\": 0,\"c\": 3}, \"hello\": 1}", "{\"k\": {\"a\": 1, \"b\": 2}, \"hello\": {\"x\": 1}} * .", "{\n  \"k\": {\n    \"a\": 0,\n    \"b\": 2,\n    \"c\": 3\n  },\n  \"hello\": 1\n}\n")]
    [InlineData("{\"k\": {\"a\": 0,\"c\": 3}, \"hello\": {\"x\": 1}}", "{\"k\": {\"a\": 1, \"b\": 2}, \"hello\": 1} * .", "{\n  \"k\": {\n    \"a\": 0,\n    \"b\": 2,\n    \"c\": 3\n  },\n  \"hello\": {\n    \"x\": 1\n  }\n}\n")]
    [InlineData("{\"a\": {\"b\": 2}, \"c\": {\"d\": 3, \"f\": 9}}", "{\"a\": {\"b\": 1}, \"c\": {\"d\": 2}, \"e\": 5} * .", "{\n  \"a\": {\n    \"b\": 2\n  },\n  \"c\": {\n    \"d\": 3,\n    \"f\": 9\n  },\n  \"e\": 5\n}\n")]
    [InlineData("null", "[add(null), add(range(range(10))), add(empty), add(10,range(10))]", "[\n  null,\n  120,\n  null,\n  55\n]\n")]
    [InlineData("null", "range(10;12)|IN(range(10))", "false\nfalse\n")]
    [InlineData("{}", "[(\"foo\" | contains(\"foo\")), (\"foobar\" | contains(\"foo\")), (\"foo\" | contains(\"foobar\"))]", "[\n  true,\n  true,\n  false\n]\n")]
    [InlineData("\"\\u0000\"", "[contains(\"\"), contains(\"\\u0000\")]", "[\n  true,\n  true\n]\n")]
    [InlineData("\"ab\\u0000cd\"", "[contains(\"b\\u0000c\"), contains(\"b\\u0000cd\"), contains(\"b\\u0000cd\")]", "[\n  true,\n  true,\n  true\n]\n")]
    [InlineData("{}", "[({foo: 12, bar:13} | contains({foo: 12})), ({foo: 12} | contains({})), ({foo: 12, bar:13} | contains({baz:14}))]", "[\n  true,\n  true,\n  false\n]\n")]
    [InlineData("{}", "{foo: {baz: 12, blap: {bar: 13}}, bar: 14} | contains({bar: 14, foo: {blap: {}}})", "true\n")]
    [InlineData("{}", "{foo: {baz: 12, blap: {bar: 13}}, bar: 14} | contains({bar: 14, foo: {blap: {bar: 14}}})", "false\n")]
    [InlineData("\"foobar\"", "contains(\"bar\")", "true\n")]
    [InlineData("\"bar\"", "inside(\"foobar\")", "true\n")]
    [InlineData("[\"foobar\", \"foobaz\", \"blarp\"]", "contains([\"baz\", \"bar\"])", "true\n")]
    [InlineData("[\"foobar\", \"foobaz\", \"blarp\"]", "contains([\"bazzzzz\", \"bar\"])", "false\n")]
    [InlineData("{\"foo\": 12, \"bar\":[1,2,{\"barp\":12, \"blip\":13}]}", "contains({foo: 12, bar: [{barp: 12}]})", "true\n")]
    [InlineData("{\"foo\": 12, \"bar\":[1,2,{\"barp\":12, \"blip\":13}]}", "contains({foo: 12, bar: [{barp: 15}]})", "false\n")]
    [InlineData("[0,1,2,1,3,1,4]", "indices(1)", "[\n  1,\n  3,\n  5\n]\n")]
    [InlineData("\"a,b, cd, efg, hijk\"", "indices(\", \")", "[\n  3,\n  7,\n  12\n]\n")]
    [InlineData("\"a,b, cd, efg, hijk\"", "index(\", \")", "3\n")]
    [InlineData("[0,1,2,1,3,1,4]", "index(1)", "1\n")]
    [InlineData("[0,1,2,3,1,4,2,5,1,2,6,7]", "index([1,2])", "1\n")]
    [InlineData("\"a,b, cd, efg, hijk\"", "rindex(\", \")", "12\n")]
    [InlineData("[2, 0]", "map(in([0,1]))", "[\n  false,\n  true\n]\n")]
    [InlineData("[{\"a\":3}, {\"a\":5}, {\"b\":6}]", "add(.[].a)", "8\n")]
    // NaN targets use the strict null-first order, proving the ordering fix flows into bsearch.
    [InlineData("[1,2]", "bsearch(nan)", "-1\n")]
    [InlineData("[null,1]", "bsearch(nan)", "-2\n")]
    [InlineData("[nan,1]", "bsearch(null)", "-1\n")]
    // NaN never equals, so equality search misses, but it keeps its null-first order slot for bsearch.
    [InlineData("[1,nan,2]", "index(nan)", "null\n")]
    [InlineData("[1,nan,2]", "indices(nan)", "[]\n")]
    [InlineData("[1,nan,2]", "rindex(nan)", "null\n")]
    [InlineData("nan", "contains(nan)", "false\n")]
    [InlineData("[1,2]", "inside([1,nan,2])", "true\n")]
    [InlineData("{\"a\":nan}", "contains({a:nan})", "false\n")]
    [InlineData("[nan]", "inside([nan])", "false\n")]
    [InlineData("[nan]", "bsearch(nan)", "0\n")]
    public async Task Jq_CollectionSearchFold(string input, string filter, string expected)
    {
        var host = new MockFileSystem();
        host.SetStandardInput(input);
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("try ([1] | contains(2)) catch .", "\"array ([1]) and number (2) cannot have their containment checked\"\n")]
    [InlineData("\"aa\" | try [\"OK\", bsearch(0)] catch [\"KO\",.]", "[\n  \"KO\",\n  \"string (\\\"aa\\\") cannot be searched from\"\n]\n")]
[InlineData("try ([range(3)] | .[nan] = 9) catch .", "\"Cannot set array element at NaN index\"\n")]
    [InlineData("try flatten(-1) catch .", "\"flatten depth must not be negative\"\n")]
    // Null and booleans sort below zero, so they fail the same guard.
    [InlineData("try flatten(null) catch .", "\"flatten depth must not be negative\"\n")]
    [InlineData("try flatten(false) catch .", "\"flatten depth must not be negative\"\n")]
    [InlineData("try (5 | flatten) catch .", "\"cannot iterate over number\"\n")]
    [InlineData("try (5 | add) catch .", "\"cannot iterate over number\"\n")]
    [InlineData("try (5 | bsearch(0)) catch .", "\"number (5) cannot be searched from\"\n")]
    public async Task Jq_CollectionFailures(string filter, string expected)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("[42, [2, 5, 3, 11], 10, {\"a\": 42, \"b\": 2}, {\"a\": 42}, true, 2, [2, 6], \"hello\", null, [2, 5, 6], {\"a\": [], \"b\": 1}, \"abc\", \"ab\", [3, 10], {}, false, \"abcd\", null]", "sort", "[\n  null,\n  null,\n  false,\n  true,\n  2,\n  10,\n  42,\n  \"ab\",\n  \"abc\",\n  \"abcd\",\n  \"hello\",\n  [\n    2,\n    5,\n    3,\n    11\n  ],\n  [\n    2,\n    5,\n    6\n  ],\n  [\n    2,\n    6\n  ],\n  [\n    3,\n    10\n  ],\n  {},\n  {\n    \"a\": 42\n  },\n  {\n    \"a\": 42,\n    \"b\": 2\n  },\n  {\n    \"a\": [],\n    \"b\": 1\n  }\n]\n")]
    [InlineData("[1, 2, 5, 3, 5, 3, 1, 3]", "unique", "[\n  1,\n  2,\n  3,\n  5\n]\n")]
    [InlineData("[]", "unique", "[]\n")]
    [InlineData("0", "[nan, nan] | unique", "[\n  null,\n  null\n]\n")]
    [InlineData("0", "[nan, 1] | group_by(.)", "[\n  [\n    null\n  ],\n  [\n    1\n  ]\n]\n")]
    [InlineData("[[4, 2, \"a\"], [3, 1, \"a\"], [2, 4, \"a\"], [1, 3, \"a\"]]", "[min, max, min_by(.[1]), max_by(.[1]), min_by(.[2]), max_by(.[2])]", "[\n  [\n    1,\n    3,\n    \"a\"\n  ],\n  [\n    4,\n    2,\n    \"a\"\n  ],\n  [\n    3,\n    1,\n    \"a\"\n  ],\n  [\n    2,\n    4,\n    \"a\"\n  ],\n  [\n    4,\n    2,\n    \"a\"\n  ],\n  [\n    1,\n    3,\n    \"a\"\n  ]\n]\n")]
    [InlineData("[]", "[min, max, min_by(.), max_by(.)]", "[\n  null,\n  null,\n  null,\n  null\n]\n")]
    // Like the reference minmax_by fold, ties keep the first minimum and the last maximum.
    [InlineData("[0,-0.0]", "[min,max]", "[\n  0,\n  -0\n]\n")]
    [InlineData("[{\"a\":1,\"i\":0},{\"a\":2,\"i\":0}]", "[min_by(.i),max_by(.i)]", "[\n  {\n    \"a\": 1,\n    \"i\": 0\n  },\n  {\n    \"a\": 2,\n    \"i\": 0\n  }\n]\n")]
    [InlineData("[{\"a\": 1, \"b\": 4, \"c\": 14}, {\"a\": 4, \"b\": 1, \"c\": 3}, {\"a\": 1, \"b\": 4, \"c\": 3}, {\"a\": 0, \"b\": 2, \"c\": 43}]", "(sort_by(.b) | sort_by(.a)), sort_by(.a, .b)", "[\n  {\n    \"a\": 0,\n    \"b\": 2,\n    \"c\": 43\n  },\n  {\n    \"a\": 1,\n    \"b\": 4,\n    \"c\": 14\n  },\n  {\n    \"a\": 1,\n    \"b\": 4,\n    \"c\": 3\n  },\n  {\n    \"a\": 4,\n    \"b\": 1,\n    \"c\": 3\n  }\n]\n[\n  {\n    \"a\": 0,\n    \"b\": 2,\n    \"c\": 43\n  },\n  {\n    \"a\": 1,\n    \"b\": 4,\n    \"c\": 14\n  },\n  {\n    \"a\": 1,\n    \"b\": 4,\n    \"c\": 3\n  },\n  {\n    \"a\": 4,\n    \"b\": 1,\n    \"c\": 3\n  }\n]\n")]
    [InlineData("[{\"a\": 1, \"b\": 4, \"c\": 14}, {\"a\": 4, \"b\": 1, \"c\": 3}, {\"a\": 1, \"b\": 4, \"c\": 3}, {\"a\": 0, \"b\": 2, \"c\": 43}]", "group_by(.b)", "[\n  [\n    {\n      \"a\": 4,\n      \"b\": 1,\n      \"c\": 3\n    }\n  ],\n  [\n    {\n      \"a\": 0,\n      \"b\": 2,\n      \"c\": 43\n    }\n  ],\n  [\n    {\n      \"a\": 1,\n      \"b\": 4,\n      \"c\": 14\n    },\n    {\n      \"a\": 1,\n      \"b\": 4,\n      \"c\": 3\n    }\n  ]\n]\n")]
    [InlineData("[{\"foo\": 1, \"bar\": 10}, {\"foo\": 3, \"bar\": 100}, {\"foo\": 1, \"bar\": 1}]", "group_by(.foo)", "[\n  [\n    {\n      \"foo\": 1,\n      \"bar\": 10\n    },\n    {\n      \"foo\": 1,\n      \"bar\": 1\n    }\n  ],\n  [\n    {\n      \"foo\": 3,\n      \"bar\": 100\n    }\n  ]\n]\n")]
    [InlineData("[{\"foo\": 1, \"bar\": 14}, {\"foo\": 2, \"bar\": 3}]", "max_by(.foo)", "{\n  \"foo\": 2,\n  \"bar\": 3\n}\n")]
    [InlineData("[{\"a\": 1, \"b\": 4, \"c\": 14}, {\"a\": 4, \"b\": 1, \"c\": 3}, {\"a\": 1, \"b\": 4, \"c\": 3}, {\"a\": 0, \"b\": 2, \"c\": 43}]", "(sort_by(.b) | sort_by(.a)), sort_by(.a, .b), sort_by(.b, .c), group_by(.b), group_by(.a + .b - .c == 2)", "[\n  {\n    \"a\": 0,\n    \"b\": 2,\n    \"c\": 43\n  },\n  {\n    \"a\": 1,\n    \"b\": 4,\n    \"c\": 14\n  },\n  {\n    \"a\": 1,\n    \"b\": 4,\n    \"c\": 3\n  },\n  {\n    \"a\": 4,\n    \"b\": 1,\n    \"c\": 3\n  }\n]\n[\n  {\n    \"a\": 0,\n    \"b\": 2,\n    \"c\": 43\n  },\n  {\n    \"a\": 1,\n    \"b\": 4,\n    \"c\": 14\n  },\n  {\n    \"a\": 1,\n    \"b\": 4,\n    \"c\": 3\n  },\n  {\n    \"a\": 4,\n    \"b\": 1,\n    \"c\": 3\n  }\n]\n[\n  {\n    \"a\": 4,\n    \"b\": 1,\n    \"c\": 3\n  },\n  {\n    \"a\": 0,\n    \"b\": 2,\n    \"c\": 43\n  },\n  {\n    \"a\": 1,\n    \"b\": 4,\n    \"c\": 3\n  },\n  {\n    \"a\": 1,\n    \"b\": 4,\n    \"c\": 14\n  }\n]\n[\n  [\n    {\n      \"a\": 4,\n      \"b\": 1,\n      \"c\": 3\n    }\n  ],\n  [\n    {\n      \"a\": 0,\n      \"b\": 2,\n      \"c\": 43\n    }\n  ],\n  [\n    {\n      \"a\": 1,\n      \"b\": 4,\n      \"c\": 14\n    },\n    {\n      \"a\": 1,\n      \"b\": 4,\n      \"c\": 3\n    }\n  ]\n]\n[\n  [\n    {\n      \"a\": 1,\n      \"b\": 4,\n      \"c\": 14\n    },\n    {\n      \"a\": 0,\n      \"b\": 2,\n      \"c\": 43\n    }\n  ],\n  [\n    {\n      \"a\": 4,\n      \"b\": 1,\n      \"c\": 3\n    },\n    {\n      \"a\": 1,\n      \"b\": 4,\n      \"c\": 3\n    }\n  ]\n]\n")]
    [InlineData("[{\"foo\": 1, \"bar\": 2}, {\"foo\": 1, \"bar\": 3}, {\"foo\": 4, \"bar\": 5}]", "unique_by(.foo)", "[\n  {\n    \"foo\": 1,\n    \"bar\": 2\n  },\n  {\n    \"foo\": 4,\n    \"bar\": 5\n  }\n]\n")]
    [InlineData("[\"chunky\", \"bacon\", \"kitten\", \"cicada\", \"asparagus\"]", "unique_by(length)", "[\n  \"bacon\",\n  \"chunky\",\n  \"asparagus\"\n]\n")]
    [InlineData("[{\"foo\":4, \"bar\":10}, {\"foo\":3, \"bar\":10}, {\"foo\":2, \"bar\":1}]", "sort_by(.foo)", "[\n  {\n    \"foo\": 2,\n    \"bar\": 1\n  },\n  {\n    \"foo\": 3,\n    \"bar\": 10\n  },\n  {\n    \"foo\": 4,\n    \"bar\": 10\n  }\n]\n")]
    [InlineData("[{\"foo\":4, \"bar\":10}, {\"foo\":3, \"bar\":20}, {\"foo\":2, \"bar\":1}, {\"foo\":3, \"bar\":10}]", "sort_by(.foo, .bar)", "[\n  {\n    \"foo\": 2,\n    \"bar\": 1\n  },\n  {\n    \"foo\": 3,\n    \"bar\": 10\n  },\n  {\n    \"foo\": 3,\n    \"bar\": 20\n  },\n  {\n    \"foo\": 4,\n    \"bar\": 10\n  }\n]\n")]
    [InlineData("[]", "sort", "[]\n")]
    [InlineData("[]", "group_by(.)", "[]\n")]
    [InlineData("[]", "min", "null\n")]
    [InlineData("[]", "max", "null\n")]
    [InlineData("[nan, null]", "sort | map(type)", "[\n  \"null\",\n  \"number\"\n]\n")]
    [InlineData("[null, nan]", "sort | map(type)", "[\n  \"null\",\n  \"number\"\n]\n")]
    // Signed zeros compare equal in ordering (stable, deduped together)
    // while staying bitwise distinct for path tracking.
    [InlineData("[0.0, -0.0]", "sort", "[\n  0,\n  -0\n]\n")]
    [InlineData("[-0.0, 0.0]", "unique", "[\n  -0\n]\n")]
    [InlineData("[nan, null]", "min | type", "\"null\"\n")]
    [InlineData("[null, nan]", "max | type", "\"number\"\n")]
    // Key expressions collect every output per element like map([f]), then compare lexically.
    [InlineData("[[2,1],[1]]", "sort_by(.[])", "[\n  [\n    1\n  ],\n  [\n    2,\n    1\n  ]\n]\n")]
    [InlineData("[[2],[1]]", "group_by(.[])", "[\n  [\n    [\n      1\n    ]\n  ],\n  [\n    [\n      2\n    ]\n  ]\n]\n")]
    [InlineData("[{\"a\":1}]", "sort_by(empty)", "[\n  {\n    \"a\": 1\n  }\n]\n")]
    // Empty key expressions compare equal across elements: stable order,
    // a single group, first-of-group uniqueness, and first-min/last-max ties.
    [InlineData("[3,1,2]", "sort_by(empty)", "[\n  3,\n  1,\n  2\n]\n")]
    [InlineData("[3,1,2]", "group_by(empty)", "[\n  [\n    3,\n    1,\n    2\n  ]\n]\n")]
    [InlineData("[3,1,2]", "unique_by(empty)", "[\n  3\n]\n")]
    [InlineData("[3,1,2]", "min_by(empty)", "3\n")]
    [InlineData("[3,1,2]", "max_by(empty)", "2\n")]
    [InlineData("[[1,2],[1,2],[1]]", "unique_by(.[])", "[\n  [\n    1\n  ],\n  [\n    1,\n    2\n  ]\n]\n")]
    [InlineData("[[2,1],[0]]", "[min_by(.[]), max_by(.[])]", "[\n  [\n    0\n  ],\n  [\n    2,\n    1\n  ]\n]\n")]
    public async Task Jq_SortGroupUnique(string input, string filter, string expected)
    {
        var host = new MockFileSystem();
        host.SetStandardInput(input);
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_AnyAllPropagateLeadingErrors()
    {
        // An error before any decisive output propagates instead of short-circuiting.
        foreach (var (input, filter) in new (string, string)[]
        {
            ("[true]\n", "all(.[]; (error(\"x\"), false))"),
            ("[false]\n", "any(.[]; (error(\"x\"), true))"),
        })
        {
            var host = new MockFileSystem();
            host.SetStandardInput(input);
            var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", filter)));
            Assert.Equal(5, await tool.ExecuteAsync(host, CancellationToken.None));
            Assert.Equal("jq: error (at <stdin>:1): x\n", host.GetOutput(JqFileDescriptor.StdErr));
            Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
        }
    }

    [Fact]
    public async Task Jq_SortRejectsNonArrays()
    {
        var host = new MockFileSystem();
        host.SetStandardInput("5");
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "try sort catch .")));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("\"cannot be sorted, as it is not an array\"\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("\"badness\"", "any(true, error; .)", "true\n")]
    [InlineData("\"badness\"", "all(false, error; .)", "false\n")]
    [InlineData("[]", "any(not)", "false\n")]
    [InlineData("[1,2,3,4,true,false,1,2,3,4,5]", ". as $dot|any($dot[];not)", "true\n")]
    [InlineData("[1,2,3,4,true]", ". as $dot|any($dot[];not)", "false\n")]
    [InlineData("[1,2,3,4,true,false,1,2,3,4,5]", ". as $dot|all($dot[];.)", "false\n")]
    [InlineData("[1,2,3,4,true]", ". as $dot|all($dot[];.)", "true\n")]
    [InlineData("[]", "all(not)", "true\n")]
    [InlineData("[false]", "any(not)", "true\n")]
    [InlineData("[false]", "all(not)", "true\n")]
    [InlineData("[]", "[any, all]", "[\n  false,\n  true\n]\n")]
    [InlineData("null", "range(5; 10) | IN(range(10))", "true\ntrue\ntrue\ntrue\ntrue\n")]
    [InlineData("null", "range(5; 13) | IN(range(0; 10; 3))", "false\ntrue\nfalse\nfalse\ntrue\nfalse\nfalse\nfalse\n")]
    [InlineData("null", "IN(range(10; 20); range(10))", "false\n")]
    [InlineData("null", "IN(range(5; 20); range(10))", "true\n")]
    [InlineData("{\"a\":\"1\",\"b\":\"2\",\"c\":\"3\"}", "any(keys[]|tostring?;true)", "true\n")]
    // Multi-output conditions evaluate per output on both sides of the quantifier.
    [InlineData("[true]", "all(.[]; (., .))", "true\n")]
    [InlineData("[false]", "any(.[]; (., .))", "false\n")]
    public async Task Jq_AnyAllShortCircuit(string input, string filter, string expected)
    {
        var host = new MockFileSystem();
        host.SetStandardInput(input);
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("[0, 1, 2]", "[skip(0.5; .[])]", "[\n  0,\n  1,\n  2\n]\n")]
    [InlineData("[0, 1, 2, 3, 4, 5, 6, 7, 8, 9]", "[skip(3; .[])]", "[\n  3,\n  4,\n  5,\n  6,\n  7,\n  8,\n  9\n]\n")]
    [InlineData("[]", "[skip(3; .[])]", "[]\n")]
    [InlineData("\"a,b, cd\"", "rindex(\",\")", "3\n")]
    [InlineData("\"abc\"", "rindex(\"z\")", "null\n")]
    [InlineData("\"\\u6B63xyz\"", ".[:rindex(\"x\")]", "\"正\"\n")]
    [InlineData("\"abc\"", "rindex(\"\")", "null\n")]
    [InlineData("[0,1,2,3,1,4,2,5,1,2,6,7]", "index([1,2])", "1\n")]
    [InlineData("[0,1,2,1,3,1,4]", "rindex(1)", "5\n")]
    [InlineData("[0,1,2,3,1,4,2,5,1,2,6,7]", "rindex([1,2])", "8\n")]
    public async Task Jq_SkipRindex(string input, string filter, string expected)
    {
        var host = new MockFileSystem();
        host.SetStandardInput(input);
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }
    [Fact]
    public async Task Jq_TransposeRejectsNonArrays()
    {
        // Rows must be arrays or null like the reference map over rows;
        // anything else fails catchably instead of padding.
        var scalar = new MockFileSystem();
        scalar.SetStandardInput("1");
        var scalarTool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", "transpose")));
        Assert.Equal(5, await scalarTool.ExecuteAsync(scalar, CancellationToken.None));
        Assert.Contains("cannot iterate over number", scalar.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(scalar.GetOutput(JqFileDescriptor.StdOut));

        var nil = new MockFileSystem();
        nil.SetStandardInput("null");
        var nilTool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", "transpose")));
        Assert.Equal(5, await nilTool.ExecuteAsync(nil, CancellationToken.None));
        Assert.Contains("cannot iterate over null", nil.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(nil.GetOutput(JqFileDescriptor.StdOut));

        var row = new MockFileSystem();
        row.SetStandardInput("[[1], 5]");
        var rowTool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", "transpose")));
        Assert.Equal(5, await rowTool.ExecuteAsync(row, CancellationToken.None));
        Assert.Contains("cannot iterate over number", row.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(row.GetOutput(JqFileDescriptor.StdOut));
    }
    [Fact]
    public async Task Jq_RangeVectorsMatchReferenceOrder()
    {
        // Upstream range vectors prove first-argument-outer combinations;
        // the generic builtin prelude is last-outer, so range parses
        // through its own filter.
        var cases = new (string Filter, string Expected)[]
        {
            ("[range(0,1;3,4)]", "[0,1,2,0,1,2,3,1,2,1,2,3]\n"),
            ("[range(3,5)]", "[0,1,2,0,1,2,3,4]\n"),
            ("[range(0,1;4,5;1,2)]", "[0,1,2,3,0,2,0,1,2,3,4,0,2,4,1,2,3,1,3,1,2,3,4,1,3]\n"),
            ("[range(0,1,2;4,3,2;2,3)]", "[0,2,0,3,0,2,0,0,0,1,3,1,1,1,1,1,2,2,2,2]\n"),
        };
        foreach (var (filter, expected) in cases)
        {
            var host = new MockFileSystem();
            var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", "-n", filter)));
            Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
            Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
            Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        }
    }
}
