using Lokad.Jq;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("\"useful but not for é\"", "ascii_upcase", "\"USEFUL BUT NOT FOR é\"\n")]
    [InlineData("\"ABCxyzÉ\"", "ascii_downcase", "\"abcxyzÉ\"\n")]
    [InlineData("\"  hello  \"", "trim", "\"hello\"\n")]
    [InlineData("\"\\u00a0hello\\u3000\"", "trim", "\"hello\"\n")]
    [InlineData("\"  hello  \"", "ltrim", "\"hello  \"\n")]
    [InlineData("\"  hello  \"", "rtrim", "\"  hello\"\n")]
    [InlineData("\"hello world\"", "ltrimstr(\"hello \")", "\"world\"\n")]
    [InlineData("\"hello world\"", "rtrimstr(\" world\")", "\"hello\"\n")]
    [InlineData("\"hello\"", "ltrimstr(\"\")", "\"hello\"\n")]
    [InlineData("\"hello\"", "rtrimstr(\"\")", "\"hello\"\n")]
    [InlineData("\"\"", "trim", "\"\"\n")]
    [InlineData("\"--hello--\"", "trimstr(\"--\")", "\"hello\"\n")]
    [InlineData("\"hello\"", "startswith(\"he\")", "true\n")]
    [InlineData("\"hi\"", "try ltrimstr(1) catch \"x\", try rtrimstr(1) catch \"x\" | \"ok\"", "\"ok\"\n\"ok\"\n")]
    [InlineData("[\" \\n\\t\\r\\f\\u000b\", \"\",\"  \", \"a\", \" a \", \"abc\", \"  abc  \", \"  abc\", \"abc  \"]", "map(trim), map(ltrim), map(rtrim)", "[\n  \"\",\n  \"\",\n  \"\",\n  \"a\",\n  \"a\",\n  \"abc\",\n  \"abc\",\n  \"abc\",\n  \"abc\"\n]\n[\n  \"\",\n  \"\",\n  \"\",\n  \"a\",\n  \"a \",\n  \"abc\",\n  \"abc  \",\n  \"abc\",\n  \"abc  \"\n]\n[\n  \"\",\n  \"\",\n  \"\",\n  \"a\",\n  \" a\",\n  \"abc\",\n  \"  abc\",\n  \"  abc\",\n  \"abc\"\n]\n")]
    [InlineData("123", "try trim catch ., try ltrim catch ., try rtrim catch .", "\"trim input must be a string\"\n\"trim input must be a string\"\n\"trim input must be a string\"\n")]
[InlineData("[\"fo\", \"foo\", \"barfoo\", \"foobar\", \"afoo\"]", "[.[]|ltrimstr(\"foo\")]", "[\n  \"fo\",\n  \"\",\n  \"barfoo\",\n  \"bar\",\n  \"afoo\"\n]\n")]
[InlineData("[\"fo\", \"foo\", \"barfoo\", \"foobar\", \"foob\"]", "[.[]|rtrimstr(\"foo\")]", "[\n  \"fo\",\n  \"\",\n  \"bar\",\n  \"foobar\",\n  \"foob\"\n]\n")]
[InlineData("[\"fo\", \"foo\", \"barfoo\", \"foobarfoo\", \"foob\"]", "[.[]|trimstr(\"foo\")]", "[\n  \"fo\",\n  \"\",\n  \"bar\",\n  \"bar\",\n  \"b\"\n]\n")]
[InlineData("{\"hey\":[]}", "try ltrimstr(\"x\") catch \"x\", try rtrimstr(\"x\") catch \"x\" | \"ok\"", "\"ok\"\n\"ok\"\n")]
[InlineData("[[\"hi\",1],[1,\"hi\"],[\"hi\",\"hi\"],[1,1]]", "[.[] as [$x, $y] | try [\"ok\", ($x | ltrimstr($y))] catch [\"ko\", .]]", "[\n  [\n    \"ko\",\n    \"startswith() requires string inputs\"\n  ],\n  [\n    \"ko\",\n    \"startswith() requires string inputs\"\n  ],\n  [\n    \"ok\",\n    \"\"\n  ],\n  [\n    \"ko\",\n    \"startswith() requires string inputs\"\n  ]\n]\n")]
[InlineData("[[\"hi\",1],[1,\"hi\"],[\"hi\",\"hi\"],[1,1]]", "[.[] as [$x, $y] | try [\"ok\", ($x | rtrimstr($y))] catch [\"ko\", .]]", "[\n  [\n    \"ko\",\n    \"endswith() requires string inputs\"\n  ],\n  [\n    \"ko\",\n    \"endswith() requires string inputs\"\n  ],\n  [\n    \"ok\",\n    \"\"\n  ],\n  [\n    \"ko\",\n    \"endswith() requires string inputs\"\n  ]\n]\n")]
    [InlineData("\"hello\"", "endswith(\"lo\")", "true\n")]
    [InlineData("\"abc\"", "startswith(\"\")", "true\n")]
    [InlineData("\"abc\"", "endswith(\"\")", "true\n")]
    [InlineData("[\"fo\", \"foo\", \"barfoo\", \"foobar\", \"barfoob\"]", "[.[]|startswith(\"foo\")]", "[\n  false,\n  true,\n  false,\n  true,\n  false\n]\n")]
    [InlineData("[\"foobar\", \"barfoo\"]", "[.[]|endswith(\"foo\")]", "[\n  false,\n  true\n]\n")]
    [InlineData("\" abc \"", "trim, ltrim, rtrim", "\"abc\"\n\"abc \"\n\" abc\"\n")]
    [InlineData("[\"a\", \"xx\", \"\"]", "[.[]|ltrimstr(\"\")]", "[\n  \"a\",\n  \"xx\",\n  \"\"\n]\n")]
    [InlineData("[\"a\", \"xx\", \"\"]", "[.[]|rtrimstr(\"\")]", "[\n  \"a\",\n  \"xx\",\n  \"\"\n]\n")]
    [InlineData("[\"a\", \"xx\", \"\"]", "[.[]|trimstr(\"\")]", "[\n  \"a\",\n  \"xx\",\n  \"\"\n]\n")]
    public async Task Jq_CaseTrimAffix(string input, string filter, string expected)
    {
        var host = new MockFileSystem();
        host.SetStandardInput(input);
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("123", "trim", "trim input must be a string")]
    [InlineData("1", "startswith(\"1\")", "startswith() requires string inputs")]
    [InlineData("\"a\"", "endswith(1)", "endswith() requires string inputs")]
    public async Task Jq_TrimAffixFailures(string input, string filter, string diagnostic)
    {
        var host = new MockFileSystem();
        host.SetStandardInput(input);
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", filter)));

        Assert.Equal(5, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Contains(diagnostic, host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }

    [Theory]
    [InlineData("1", "tonumber", "1\n")]
    [InlineData("\"1.5\"", "tonumber", "1.5\n")]
    [InlineData("\"42\"", "tonumber | type", "\"number\"\n")]
    [InlineData("\"+5.43\"", "tonumber", "5.43\n")]
    [InlineData("\" 4\"", "try tonumber catch \"caught\"", "\"caught\"\n")]
    [InlineData("\"True\"", "try toboolean catch \"caught\"", "\"caught\"\n")]
    [InlineData("[\"false\", \"true\", false, true]", "map(toboolean)", "[\n  false,\n  true,\n  false,\n  true\n]\n")]
    [InlineData("0", "\"\\uFEFF[1, 2]\" | fromjson", "[\n  1,\n  2\n]\n")]
    [InlineData("\"1 \"", "fromjson", "1\n")]
    [InlineData("\" 1\"", "fromjson", "1\n")]
    [InlineData("1", "try toboolean catch .", "\"number (1) cannot be parsed as a boolean\"\n")]
    [InlineData("\"1 2\"", "try fromjson catch .", "\"expected a single JSON value\"\n")]
    [InlineData("[\"NaN\",\"-NaN\",\"NaN1\",\"NaN10\",\"NaN100\",\"NaN1000\",\"NaN10000\",\"NaN100000\"]", ".[] | try (fromjson | isnan) catch .", "true\ntrue\n\"Invalid numeric literal at EOF at line 1, column 4 (while parsing 'NaN1')\"\n\"Invalid numeric literal at EOF at line 1, column 5 (while parsing 'NaN10')\"\n\"Invalid numeric literal at EOF at line 1, column 6 (while parsing 'NaN100')\"\n\"Invalid numeric literal at EOF at line 1, column 7 (while parsing 'NaN1000')\"\n\"Invalid numeric literal at EOF at line 1, column 8 (while parsing 'NaN10000')\"\n\"Invalid numeric literal at EOF at line 1, column 9 (while parsing 'NaN100000')\"\n")]
    [InlineData("[[], {}, [1,2], {\"a\":42}, \"asdf\", \"μ\"]", "[.[] | length]", "[\n  0,\n  0,\n  2,\n  1,\n  4,\n  1\n]\n")]
    [InlineData("[-9007199254740993, -9007199254740992, 9007199254740992, 9007199254740993, 13911860366432393]", ".[] as $n | $n+0 | [., tostring, . == $n]", "[\n  -9007199254740992,\n  \"-9007199254740992\",\n  true\n]\n[\n  -9007199254740992,\n  \"-9007199254740992\",\n  true\n]\n[\n  9007199254740992,\n  \"9007199254740992\",\n  true\n]\n[\n  9007199254740992,\n  \"9007199254740992\",\n  true\n]\n[\n  13911860366432392,\n  \"13911860366432392\",\n  true\n]\n")]
    [InlineData("[1, \"1\"]", ".[] | tonumber", "1\n1\n")]
    [InlineData("[1, \"1\", [1]]", ".[] | tostring", "\"1\"\n\"1\"\n\"[1]\"\n")]
    [InlineData("[\"true\", \"false\", true, false]", ".[] | toboolean", "true\nfalse\ntrue\nfalse\n")]
    [InlineData("\"a🚀b\"", "length", "3\n")]
    [InlineData("\"a\"", "tostring", "\"a\"\n")]
    [InlineData("4", "1 + tonumber + (\"10\" | tonumber)", "15\n")]
    [InlineData("null", "nan | length", "null\n")]
    [InlineData("null", "infinite | length", "1.7976931348623157E+308\n")]
    [InlineData("null", "-0.0 | length", "0\n")]
    [InlineData("null", "1e100 | length", "1E+100\n")]
    [InlineData("-10", "length", "10\n")]
    [InlineData("5", "length", "5\n")]
    [InlineData("true", "try length catch .", "\"boolean (true) has no length\"\n")]
    [InlineData("[-10, -1.1, -1e-1, 1000000000000000002]", "map(abs == length) | unique", "[\n  true\n]\n")]
    [InlineData("42", "\"The input was \\(.), which is one less than \\(.+1)\"", "\"The input was 42, which is one less than 43\"\n")]
    [InlineData("[\"foo\", 1, [\"a\", 1, \"b\", 2, {\"foo\":\"bar\"}]]", "[.[]|tojson|fromjson]", "[\n  \"foo\",\n  1,\n  [\n    \"a\",\n    1,\n    \"b\",\n    2,\n    {\n      \"foo\": \"bar\"\n    }\n  ]\n]\n")]
    [InlineData("[1, \"foo\", [\"foo\"]]", "[.[]|tojson|fromjson]", "[\n  1,\n  \"foo\",\n  [\n    \"foo\"\n  ]\n]\n")]
    // Self-consistency sweep over valid-JSON shapes: tojson|fromjson and getpath(path(.)) fix every value, including duplicate keys (last-wins), NUL, and signed zero.
    [InlineData("{\"a\":1,\"a\":2}", "tojson|fromjson", "{\n  \"a\": 2\n}\n")]
    [InlineData("\"\\u0000\"", "tojson|fromjson", "\"\\u0000\"\n")]
    [InlineData("-0", "tojson|fromjson", "-0\n")]
    [InlineData("{\"a\":1,\"a\":2}", "getpath(path(.))", "{\n  \"a\": 2\n}\n")]
    [InlineData("null", "\"123\\u0000456\" | try tonumber catch .", "\"string (\\\"123\\\\u0000456\\\") cannot be parsed as a number\"\n")]
    [InlineData("null", "\"true\\u0000x\", \"false\\u0000\" | try toboolean catch .", "\"string (\\\"true\\\\u0000x\\\") cannot be parsed as a boolean\"\n\"string (\\\"false\\\\u0000\\\") cannot be parsed as a boolean\"\n")]
    [InlineData("[[], {}, [1,2], 55, true, false]", "[.[] | try utf8bytelength catch .]", "[\n  \"array ([]) only strings have UTF-8 byte length\",\n  \"object ({}) only strings have UTF-8 byte length\",\n  \"array ([1,2]) only strings have UTF-8 byte length\",\n  \"number (55) only strings have UTF-8 byte length\",\n  \"boolean (true) only strings have UTF-8 byte length\",\n  \"boolean (false) only strings have UTF-8 byte length\"\n]\n")]
    [InlineData("0", "nan | tostring", "\"null\"\n")]
    [InlineData("0", "infinite | tostring", "\"1.7976931348623157E+308\"\n")]
    [InlineData("[1, \"a\", true, null]", "map(tostring)", "[\n  \"1\",\n  \"a\",\n  \"true\",\n  \"null\"\n]\n")]
    [InlineData("[1, \"foo\", [\"foo\"]]", "[.[]|tostring]", "[\n  \"1\",\n  \"foo\",\n  \"[\\\"foo\\\"]\"\n]\n")]
    [InlineData("[1, \"foo\", [\"foo\"]]", "[.[]|tojson]", "[\n  \"1\",\n  \"\\\"foo\\\"\",\n  \"[\\\"foo\\\"]\"\n]\n")]
    [InlineData("\"hello\"", "utf8bytelength", "5\n")]
    [InlineData("\".89\"", "tonumber", "0.89\n")]
    [InlineData("\"-.5\"", "tonumber", "-0.5\n")]
    // Non-finite spellings follow the reference strtod fallback: an optional
    // sign with case-insensitive nan, inf, or infinity; values render through
    // the double-domain profile (NaN as null, infinities clamped).
    [InlineData("\"nan\"", "tonumber | isnan", "true\n")]
    [InlineData("\"nan\"", "tonumber", "null\n")]
    [InlineData("\"Infinity\"", "tonumber", "1.7976931348623157E+308\n")]
    [InlineData("\"-inf\"", "tonumber", "-1.7976931348623157E+308\n")]
    [InlineData("\"+INF\"", "tonumber | isinfinite", "true\n")]
    [InlineData("[\"1\", \"2a\", \"3\", \" 4\", \"5 \", \"6.7\", \".89\", \"-876\", \"+5.43\", 21]", ".[] |= try tonumber", "[\n  1,\n  3,\n  6.7,\n  0.89,\n  -876,\n  5.43,\n  21\n]\n")]
    [InlineData("[null, 0, \"tru\", \"truee\", \"fals\", \"falsee\", [], {}]", "[.[] | try toboolean catch .]", "[\n  \"null (null) cannot be parsed as a boolean\",\n  \"number (0) cannot be parsed as a boolean\",\n  \"string (\\\"tru\\\") cannot be parsed as a boolean\",\n  \"string (\\\"truee\\\") cannot be parsed as a boolean\",\n  \"string (\\\"fals\\\") cannot be parsed as a boolean\",\n  \"string (\\\"falsee\\\") cannot be parsed as a boolean\",\n  \"array ([]) cannot be parsed as a boolean\",\n  \"object ({}) cannot be parsed as a boolean\"\n]\n")]
    [InlineData("\"é🚀\"", "utf8bytelength", "6\n")]
    [InlineData("1", "try utf8bytelength catch .", "\"number (1) only strings have UTF-8 byte length\"\n")]
    public async Task Jq_Conversions(string input, string filter, string expected)
    {
        var host = new MockFileSystem();
        host.SetStandardInput(input);
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("null", "tonumber", "null (null) cannot be parsed as a number")]
    [InlineData("\"\"", "tonumber", "string (\"\") cannot be parsed as a number")]
    [InlineData("\"0x10\"", "tonumber", "string (\"0x10\") cannot be parsed as a number")]
    [InlineData("\"nanx\"", "tonumber", "string (\"nanx\") cannot be parsed as a number")]
    [InlineData("5", "utf8bytelength", "only strings have UTF-8 byte length")]
    [InlineData("null", "toboolean", "null (null) cannot be parsed as a boolean")]
    [InlineData("0", "toboolean", "number (0) cannot be parsed as a boolean")]
    [InlineData("\"TRUE\"", "toboolean", "string (\"TRUE\") cannot be parsed as a boolean")]
    [InlineData("\" true\"", "toboolean", "string (\" true\") cannot be parsed as a boolean")]
    [InlineData("1", "toboolean", "number (1) cannot be parsed as a boolean")]
    [InlineData("\"NaN1\"", "fromjson", "Invalid numeric literal at EOF at line 1, column 4 (while parsing 'NaN1')")]
    [InlineData("\"123\\u0000456\"", "tonumber", "string (\"123\\u0000456\") cannot be parsed as a number")]
    [InlineData("\"true\\u0000x\"", "toboolean", "string (\"true\\u0000x\") cannot be parsed as a boolean")]
    public async Task Jq_ConversionFailures(string input, string filter, string diagnostic)
    {
        var host = new MockFileSystem();
        host.SetStandardInput(input);
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", filter)));

        Assert.Equal(5, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Contains(diagnostic, host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }

    [Theory]
    [InlineData("[\"1\",2,true,false,3.4]", "join(\",\")", "\"1,2,true,false,3.4\"\n")]
    [InlineData("[[],[null],[null,null],[null,null,null]]", ".[] | join(\",\")", "\"\"\n\"\"\n\",\"\n\",,\"\n")]
    [InlineData("[[\"a\",null],[null,\"a\"]]", ".[] | join(\",\")", "\"a,\"\n\",a\"\n")]
    [InlineData("[[],[\"\"],[\"\",\"\"],[\"\",\"\",\"\"]]", "[.[]|join(\"a\")]", "[\n  \"\",\n  \"\",\n  \"a\",\n  \"aa\"\n]\n")]
    [InlineData("null", "join(\",\")", "\"\"\n")]
    [InlineData("[]", "join(\",\")", "\"\"\n")]
    [InlineData("[\"a\",\"b\"]", "join((\",\", \";\"))", "\"a,b\"\n\"a;b\"\n")]
    [InlineData("[123,[\"a\"],[nan]]", "map(try implode catch .)", "[\n  \"implode input must be an array\",\n  \"string (\\\"a\\\") can't be imploded, unicode codepoint needs to be numeric\",\n  \"number (null) can't be imploded, unicode codepoint needs to be numeric\"\n]\n")]
    [InlineData("[1, 2]", "join(null)", "\"12\"\n")]
    [InlineData("[\"1\",\"2\",{\"a\":{\"b\":{\"c\":33}}}]", "try join(\",\") catch .", "\"string (\\\"1,2,\\\") and object ({\\\"a\\\":{\\\"b\\\":{\\\"c\\\":33}}}) cannot be added\"\n")]
    [InlineData("[\"1\",\"2\",[3,4,5]]", "try join(\",\") catch .", "\"string (\\\"1,2,\\\") and array ([3,4,5]) cannot be added\"\n")]
    [InlineData("\"a,b,c\"", "split(\",\")", "[\n  \"a\",\n  \"b\",\n  \"c\"\n]\n")]
    [InlineData("\"a,\"", "split(\",\")", "[\n  \"a\",\n  \"\"\n]\n")]
[InlineData("\"a, b,c,d, e, \"", "split(\", \")", "[\n  \"a\",\n  \"b,c,d\",\n  \"e\",\n  \"\"\n]\n")]
    [InlineData("\"\"", "split(\",\")", "[]\n")]
    [InlineData("\"a,,b\"", "split(\",\")", "[\n  \"a\",\n  \"\",\n  \"b\"\n]\n")]
    [InlineData("\"abc\"", "split(\"\")", "[\n  \"a\",\n  \"b\",\n  \"c\"\n]\n")]
    [InlineData("\"a🚀b\"", "split(\"\")", "[\n  \"a\",\n  \"\\uD83D\\uDE80\",\n  \"b\"\n]\n")]
    [InlineData("\"hello\"", "explode", "[\n  104,\n  101,\n  108,\n  108,\n  111\n]\n")]
    [InlineData("\"a\\u0000b\"", "explode", "[\n  97,\n  0,\n  98\n]\n")]
    [InlineData("\"bca\"", "explode | sort | implode", "\"abc\"\n")]
    [InlineData("\"bca\"", "explode | unique | implode", "\"abc\"\n")]
    [InlineData("\"bca\"", "explode | min", "97\n")]
    [InlineData("\"bca\"", "[explode[] | . * 2] | implode", "\"ÄÆÂ\"\n")]
    [InlineData("[104,101]", "implode", "\"he\"\n")]
    [InlineData("[-1,1114112,55296,1.9]", "implode|explode", "[\n  65533,\n  65533,\n  65533,\n  1\n]\n")]
    [InlineData("\"abc\"", "explode | implode", "\"abc\"\n")]
    [InlineData("[-1, 0, 1, 2, 3, 1114111, 1114112, 55295, 55296, 57343, 57344, 1.1, 1.9]", "implode|explode", "[\n  65533,\n  0,\n  1,\n  2,\n  3,\n  1114111,\n  65533,\n  55295,\n  65533,\n  65533,\n  57344,\n  1,\n  1\n]\n")]
    [InlineData("\"\"", "split(\"\")", "[]\n")]
    [InlineData("[]", "implode", "\"\"\n")]
    [InlineData("[\"a, bc, def, ghij, jklmn, a,b, c,d, e,f\", \"a,b,c,d, e,f,g,h\"]", "[.[] / \",\"]", "[\n  [\n    \"a\",\n    \" bc\",\n    \" def\",\n    \" ghij\",\n    \" jklmn\",\n    \" a\",\n    \"b\",\n    \" c\",\n    \"d\",\n    \" e\",\n    \"f\"\n  ],\n  [\n    \"a\",\n    \"b\",\n    \"c\",\n    \"d\",\n    \" e\",\n    \"f\",\n    \"g\",\n    \"h\"\n  ]\n]\n")]
    [InlineData("[\"a, bc, def, ghij, jklmn, a,b, c,d, e,f\", \"a,b,c,d, e,f,g,h\"]", "[.[] / \", \"]", "[\n  [\n    \"a\",\n    \"bc\",\n    \"def\",\n    \"ghij\",\n    \"jklmn\",\n    \"a,b\",\n    \"c,d\",\n    \"e,f\"\n  ],\n  [\n    \"a,b,c,d\",\n    \"e,f,g,h\"\n  ]\n]\n")]
    [InlineData("[\"a\",\"b\",\"c\",\"d\"]", "join(\",\",\"/\")", "\"a,b,c,d\"\n\"a/b/c/d\"\n")]
    // Caught split/explode failures surface the message alone, like the reference freed-operand errors.
    [InlineData("1", "try split(\",\") catch .", "\"split input and separator must be strings\"\n")]
    [InlineData("\"a\"", "try split(1) catch .", "\"split input and separator must be strings\"\n")]
    [InlineData("5", "try explode catch .", "\"explode input must be a string\"\n")]
    public async Task Jq_SplitJoinExplode(string input, string filter, string expected)
    {
        var host = new MockFileSystem();
        host.SetStandardInput(input);
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("1", "split(\",\")", "split input and separator must be strings")]
    [InlineData("\"a\"", "split(1)", "split input and separator must be strings")]
    [InlineData("5", "join(\",\")", "cannot iterate over number")]
    [InlineData("[\"1\",\"2\",{\"a\":{\"b\":{\"c\":33}}}]", "join(\",\")", "string (\"1,2,\") and object ({\"a\":{\"b\":{\"c\":33}}}) cannot be added")]
    [InlineData("[\"1\",\"2\",[3,4,5]]", "join(\",\")", "string (\"1,2,\") and array ([3,4,5]) cannot be added")]
    [InlineData("5", "explode", "explode input must be a string")]
    [InlineData("123", "implode", "implode input must be an array")]
    [InlineData("[\"a\"]", "implode", "string (\"a\") can't be imploded, unicode codepoint needs to be numeric")]
    public async Task Jq_SplitJoinExplodeFailures(string input, string filter, string diagnostic)
    {
        var host = new MockFileSystem();
        host.SetStandardInput(input);
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", filter)));

        Assert.Equal(5, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Contains(diagnostic, host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }

    [Theory]
    [InlineData("\"\\\\(\"", "\"\\\\(\"\n")]
    [InlineData("\"a\\\\(.)\"", "\"a\\\\(.)\"\n")]
    [InlineData("\"\\\\\\\\\"", "\"\\\\\\\\\"\n")]
    [InlineData("\"\\\\(1+2)\"", "\"\\\\(1+2)\"\n")]
    [InlineData("\"\\\\\\(1+2)\"", "\"\\\\3\"\n")]
    [InlineData("\"Aa\\r\\n\\t\\b\\f\\u03bc\"", "\"Aa\\r\\n\\t\\b\\fμ\"\n")]
    [InlineData("\"inter\\(\"pol\" + \"ation\")\"", "\"interpolation\"\n")]
    public async Task Jq_LiteralBackslashParen(string filter, string expected)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));
        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_UnterminatedInterpolationIsCompileError()
    {
        // The lexer depth-tracking rejects the unclosed marker before evaluation,
        // so no try/catch handler can observe it.
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "try \"\\(\" catch .")));
        Assert.Equal(3, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Contains("unterminated string", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }

    [Theory]
    [InlineData("\"\\($undefined)\"", "undefined variable")]
    [InlineData("\"\\(missing_function)\"", "unsupported function")]
    [InlineData("\"\\(length(1))\"", "length expects no arguments")]
    public async Task Jq_InterpolationPiecesFailAtCompileTime(string filter, string diagnostic)
    {
        // Pieces parse once with definition-site scopes, so unknown names are
        // stage-3 compile errors like the surrounding filter, not stage-5
        // evaluation failures, and try/catch cannot observe them.
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));
        Assert.Equal(3, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Contains(diagnostic, host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }

    [Fact]
    public async Task Jq_InterpolationRejectsLateBoundVariables()
    {
        // $x is unbound where f is defined, so the piece must fail at compile
        // time even though $x is bound at the call site.
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "def f: \"\\($x)\"; 1 as $x | f")));
        Assert.Equal(3, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Contains("undefined variable", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }

    [Fact]
    public async Task Jq_InterpolationCapturesDefinitionVariables()
    {
        // Like function bodies, pieces observe bindings visible at their
        // definition site; later shadowing never leaks into older closures.
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "1 as $x | def f: \"\\($x)\"; 2 as $x | f")));
        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("\"1\"\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }
    [Fact]
    public async Task Jq_FormattedInterpolationCapturesDefinitionVariables()
    {
        // The @format template path shares the definition-site parser, so it
        // observes the same closure bindings as plain strings.
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "1 as $x | def f: @json \"\\($x)\"; 2 as $x | f")));
        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("\"1\"\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_ObjectKeyInterpolationCapturesDefinitionVariables()
    {
        // Dynamic object keys interpolate through the same definition-site
        // parser as string values.
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "1 as $x | def f: {(\"\\($x)\"): 2}; 9 as $x | f")));
        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("{\n  \"1\": 2\n}\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }
    [Theory]
    [InlineData("try \"\\($undefined)\" catch \"caught\"")]
    [InlineData("try \"\\(missing_function)\" catch \"caught\"")]
    public async Task Jq_InterpolationCompileErrorsEscapeTry(string filter)
    {
        // Definition-site compile failures are stage-3 errors like any other
        // unknown name, so user-level try/catch cannot observe them.
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));
        Assert.Equal(3, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }
}
