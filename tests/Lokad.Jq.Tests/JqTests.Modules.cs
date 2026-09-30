using System.Threading;
using Lokad.Jq;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    private static JqCommandInvocation BuildModuleInvocation(params string[] args)
    {
        return JqCommandInvocation.CreateWithStandardDescriptors("jq", args, []);
    }

    private static async Task<(int Exit, string Out, string Err)> RunModulesAsync(MockFileSystem fs, params string[] args)
    {
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildModuleInvocation(args)));
        var exit = await tool.ExecuteAsync(fs, CancellationToken.None);
        return (exit, fs.GetOutput(JqFileDescriptor.StdOut), fs.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_Modules_ImportAlias()
    {
        var fs = new MockFileSystem();
        fs.AddFile("/lib/a.jq", "def a: \"a\";");
        var (exit, stdout, stderr) = await RunModulesAsync(fs, "-n", "-L", "/lib", "import \"a\" as foo; foo::a");
        Assert.True(exit == 0, stderr);
        Assert.Equal("\"a\"\n", stdout);
    }

    [Fact]
    public async Task Jq_Modules_IncludeBringsUnqualified()
    {
        var fs = new MockFileSystem();
        fs.AddFile("/lib/c.jq", "def a: 0; def c: \"hi\";");
        var (exit, stdout, stderr) = await RunModulesAsync(fs, "-n", "-L", "/lib", "include \"c\"; [a, c]");
        Assert.True(exit == 0, stderr);
        Assert.Equal("[\n  0,\n  \"hi\"\n]\n", stdout);
    }

    [Fact]
    public async Task Jq_Modules_DataBindsArrays()
    {
        var fs = new MockFileSystem();
        fs.AddFile("/lib/data.json", "{\"this\":\"is a test\",\"that\":\"is too\"}");
        var (exit, stdout, stderr) = await RunModulesAsync(fs, "-n", "-L", "/lib", "import \"data\" as $d; [$d[].this, $d::d[].that] | join(\";\")");
        Assert.True(exit == 0, stderr);
        Assert.Equal("\"is a test;is too\"\n", stdout);
    }

    [Fact]
    public async Task Jq_Modules_ShadowLaterIncludesWin()
    {
        var fs = new MockFileSystem();
        fs.AddFile("/lib/shadow1.jq", "def e: 1; def e: 2;");
        fs.AddFile("/lib/shadow2.jq", "def e: 3;");
        var (exit, stdout, stderr) = await RunModulesAsync(fs, "-n", "-L", "/lib", "include \"shadow1\"; include \"shadow2\"; e");
        Assert.True(exit == 0, stderr);
        Assert.Equal("3\n", stdout);
    }

    [Fact]
    public async Task Jq_Modules_SameAliasLaterWins()
    {
        var fs = new MockFileSystem();
        fs.AddFile("/lib/shadow1.jq", "def e: 1; def e: 2;");
        fs.AddFile("/lib/shadow2.jq", "def e: 3;");
        var (exit, stdout, stderr) = await RunModulesAsync(fs, "-n", "-L", "/lib", "import \"shadow1\" as f; import \"shadow2\" as f; import \"shadow1\" as e; [e::e, f::e]");
        Assert.True(exit == 0, stderr);
        Assert.Equal("[\n  2,\n  3\n]\n", stdout);
    }

    [Fact]
    public async Task Jq_Modules_BuiltinsLists()
    {
        var fs = new MockFileSystem();
        var (exit, stdout, stderr) = await RunModulesAsync(fs, "-n", "builtins | length > 10");
        Assert.True(exit == 0, stderr);
        Assert.Equal("true\n", stdout);
    }

    [Fact]
    public async Task Jq_Modules_LocInline()
    {
        var fs = new MockFileSystem();
        var (exit, stdout, stderr) = await RunModulesAsync(fs, "-n", "$__loc__");
        Assert.True(exit == 0, stderr);
        Assert.Contains("\"file\": \"<top-level>\"", stdout);
        Assert.Contains("\"line\": 1", stdout);
    }

    [Fact]
    public async Task Jq_Modules_TransitiveWithSearch()
    {
        var fs = new MockFileSystem();
        fs.AddFile("/lib/a.jq", "def a: \"a\";");
        fs.AddFile("/lib/c/c.jq", "import \"a\" as foo; import \"d\" as d {\"search\":\"./\"}; def a: 0; def c: foo::a + \"c\" + d::meh;");
        fs.AddFile("/lib/c/d.jq", "def meh: \"meh\";");
        var (exit, stdout, stderr) = await RunModulesAsync(fs, "-n", "-L", "/lib", "import \"c\" as foo; [foo::a, foo::c]");
        Assert.True(exit == 0, stderr);
        Assert.Equal("[\n  0,\n  \"acmeh\"\n]\n", stdout);
    }

    [Fact]
    public async Task Jq_Modules_DiamondReusesCache()
    {
        var fs = new MockFileSystem();
        fs.AddFile("/lib/base.jq", "def v: 1;");
        fs.AddFile("/lib/left.jq", "import \"base\" as b; def v: b::v + 1;");
        fs.AddFile("/lib/right.jq", "import \"base\" as b; def v: b::v + 2;");
        fs.AddFile("/lib/top.jq", "import \"left\" as l; import \"right\" as r; def v: [l::v, r::v];");
        var (exit, stdout, stderr) = await RunModulesAsync(fs, "-n", "-L", "/lib", "import \"top\" as t; t::v");
        Assert.True(exit == 0, stderr);
        Assert.Equal("[\n  2,\n  3\n]\n", stdout);
    }

    [Fact]
    public async Task Jq_Modules_CircularFails()
    {
        var fs = new MockFileSystem();
        fs.AddFile("/lib/cycle_a.jq", "import \"cycle_b\" as b; def f: null;");
        fs.AddFile("/lib/cycle_b.jq", "import \"cycle_a\" as a; def f: null;");
        var (exit, stdout, stderr) = await RunModulesAsync(fs, "-n", "-L", "/lib", "import \"cycle_a\" as a; 0");
        Assert.True(exit != 0, "expected failure, got " + stdout);
        Assert.Contains("circular import", stderr);
        Assert.Equal("", stdout);
    }

    [Fact]
    public async Task Jq_Modules_MissingFails()
    {
        var fs = new MockFileSystem();
        var (exit, stdout, stderr) = await RunModulesAsync(fs, "-n", "-L", "/lib", "import \"missing\" as m; 0");
        Assert.True(exit != 0, "expected failure");
        Assert.Contains("module not found: missing", stderr);
    }

    [Fact]
    public async Task Jq_Modules_OptionalMissingSkips()
    {
        var fs = new MockFileSystem();
        var (exit, stdout, stderr) = await RunModulesAsync(fs, "-n", "-L", "/lib", "import \"missing\" as m {\"optional\":true}; 0");
        Assert.True(exit == 0, stderr);
        Assert.Equal("0\n", stdout);
    }

    [Fact]
    public async Task Jq_Modules_LibraryMainRejected()
    {
        var fs = new MockFileSystem();
        fs.AddFile("/lib/bad.jq", "def a: 1; 0");
        var (exit, stdout, stderr) = await RunModulesAsync(fs, "-n", "-L", "/lib", "import \"bad\" as b; 0");
        Assert.True(exit != 0, "expected failure");
        Assert.Contains("library should only have function definitions", stderr);
    }

    [Fact]
    public async Task Jq_Modules_MetadataMustBeObject()
    {
        var fs = new MockFileSystem();
        var (exit, stdout, stderr) = await RunModulesAsync(fs, "-n", "module []; 0");
        Assert.True(exit == 3, stderr);
        Assert.Contains("Module metadata must be an object", stderr);
    }

    [Fact]
    public async Task Jq_Modules_ImportPathMustBeConstant()
    {
        var fs = new MockFileSystem();
        var (exit, stdout, stderr) = await RunModulesAsync(fs, "-n", "include \"\\(a)\"; 0");
        Assert.True(exit != 0, stderr);
        Assert.Contains("Import path must be constant", stderr);
    }

    [Fact]
    public async Task Jq_Modules_DataReuseNoLeak()
    {
        var fs = new MockFileSystem();
        fs.AddFile("/lib/data.json", "{\"this\":\"is a test\",\"that\":\"is too\"}");
        var (exit, stdout, stderr) = await RunModulesAsync(fs, "-n", "-L", "/lib", "import \"data\" as $a; import \"data\" as $b; def f: {$a, $b}; f");
        Assert.True(exit == 0, stderr);
        Assert.Contains("\"a\": [", stdout);
        Assert.Contains("\"b\": [", stdout);
    }

    [Fact]
    public async Task Jq_Modules_DataMutationIsolated()
    {
        var fs = new MockFileSystem();
        fs.AddFile("/lib/data.json", "{\"n\":1}");
        var (exit, stdout, stderr) = await RunModulesAsync(fs, "-n", "-L", "/lib", "import \"data\" as $a; import \"data\" as $b; [($a[0] | .n = 99 | .n), $b[0].n]");
        Assert.True(exit == 0, stderr);
        Assert.Equal("[\n  99,\n  1\n]\n", stdout);
    }

    [Fact]
    public async Task Jq_Modules_LibraryRepeatFlag()
    {
        var fs = new MockFileSystem();
        fs.AddFile("/lib2/b.jq", "def b: \"from-lib2\";");
        var (exit, stdout, stderr) = await RunModulesAsync(fs, "-n", "-L", "/lib", "-L", "/lib2", "import \"b\" as x; x::b");
        Assert.True(exit == 0, stderr);
        Assert.Equal("\"from-lib2\"\n", stdout);
    }

    [Fact]
    public async Task Jq_Modules_RelativeViaFilterFile()
    {
        var fs = new MockFileSystem();
        fs.AddFile("/proj/main.jq", "import \"lib\" as l; l::x");
        fs.AddFile("/proj/lib.jq", "def x: 42;");
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildModuleInvocation("-n", "-f", "/proj/main.jq")));
        var exit = await tool.ExecuteAsync(fs, CancellationToken.None);
        Assert.True(exit == 0, fs.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal("42\n", fs.GetOutput(JqFileDescriptor.StdOut));
    }

    [Fact]
    public async Task Jq_Modules_PathValidation()
    {
        var fs = new MockFileSystem();
        var (exit1, _, err1) = await RunModulesAsync(fs, "-n", "-L", "/lib", "import \"a/../b\" as x; 0");
        Assert.True(exit1 != 0, "expected .. failure");
        Assert.Contains("may not traverse to parent directories", err1);
        var (exit2, _, err2) = await RunModulesAsync(fs, "-n", "-L", "/lib", "import \"a/a\" as x; 0");
        Assert.True(exit2 != 0, "expected equal-components failure");
        Assert.Contains("equal consecutive components", err2);
        var (exit3, _, err3) = await RunModulesAsync(fs, "-n", "-L", "/lib", "import \"a\\b\" as x; 0");
        Assert.True(exit3 != 0, "expected backslash failure");
        Assert.Contains("not", err3);
    }

    [Fact]
    public async Task Jq_Modules_NestedDefsNotExported()
    {
        var fs = new MockFileSystem();
        fs.AddFile("/lib/m.jq", "def a: (def inner: 1; inner);");
        var (exit, stdout, stderr) = await RunModulesAsync(fs, "-n", "-L", "/lib", "import \"m\" as foo; foo::inner");
        Assert.True(exit != 0, "expected failure for private inner, got " + stdout);
        Assert.Contains("undefined function", stderr);
    }

    [Fact]
    public async Task Jq_Modules_BindOrder()
    {
        var fs = new MockFileSystem();
        fs.AddFile("/lib/t0.jq", "def sym0: 0; def sym1: 0;");
        fs.AddFile("/lib/t1.jq", "def sym1: 1; def sym2: 1;");
        fs.AddFile("/lib/t2.jq", "def sym2: 2;");
        fs.AddFile("/lib/check.jq", "import \"t0\" as t; import \"t1\" as t; import \"t2\" as t; def check: if [t::sym0, t::sym1, t::sym2] == [0,1,2] then true else false end;");
        var (exit, stdout, stderr) = await RunModulesAsync(fs, "-n", "-L", "/lib", "import \"check\" as c; c::check");
        Assert.True(exit == 0, stderr);
        Assert.Equal("true\n", stdout);
    }

    [Fact]
    public async Task Jq_Modules_LocObjectShorthand()
    {
        var fs = new MockFileSystem();
        fs.AddFile("/stdin.json", "{\"a\":[1,2,3],\"c\":{\"hi\":\"hey\"}}");
        fs.SetStandardInput("{\"a\":[1,2,3],\"c\":{\"hi\":\"hey\"}}");
        var (exit, stdout, stderr) = await RunModulesAsync(fs, "{a, $__loc__, c}");
        Assert.True(exit == 0, stderr);
        Assert.Contains("\"__loc__\": {\n    \"file\": \"<top-level>\"", stdout);
    }

    [Fact]
    public async Task Jq_Modules_SelfCycleFails()
    {
        var fs = new MockFileSystem();
        fs.AddFile("/lib/self.jq", "import \"self\" as s; def f: null;");
        var (exit, _, stderr) = await RunModulesAsync(fs, "-n", "-L", "/lib", "import \"self\" as s; 0");
        Assert.True(exit != 0, "expected cycle failure");
        Assert.Contains("circular import", stderr);
    }

    [Fact]
    public async Task Jq_Modules_SyntaxErrorFails()
    {
        var fs = new MockFileSystem();
        fs.AddFile("/lib/bad.jq", "wat;");
        var (exit, _, stderr) = await RunModulesAsync(fs, "-n", "-L", "/lib", "import \"bad\" as b; 0");
        Assert.True(exit != 0, "expected syntax failure");
        Assert.Contains("library should only have function definitions", stderr);
    }

    [Fact]
    public async Task Jq_Modules_ImportMetadataMustBeObject()
    {
        var fs = new MockFileSystem();
        fs.AddFile("/lib/a.jq", "def a: 1;");
        var (exit, _, stderr) = await RunModulesAsync(fs, "-n", "-L", "/lib", "import \"a\" as foo []; 0");
        Assert.True(exit == 3, stderr);
        Assert.Contains("Module metadata must be an object", stderr);
    }

    [Fact]
    public async Task Jq_Modules_LocInModule()
    {
        var fs = new MockFileSystem();
        fs.AddFile("/lib/m.jq", "def loc: $__loc__;");
        var (exit, stdout, stderr) = await RunModulesAsync(fs, "-n", "-L", "/lib", "import \"m\" as foo; foo::loc");
        Assert.True(exit == 0, stderr);
        Assert.Contains("\"file\": \"/lib/m.jq\"", stdout);
        Assert.Contains("\"line\": 1", stdout);
    }

    [Fact]
    public async Task Jq_Modules_BuiltinsFormat()
    {
        var fs = new MockFileSystem();
        var (exit, stdout, stderr) = await RunModulesAsync(fs, "-n", "all(builtins[] / \"/\"; .[1] | tonumber >= 0)");
        Assert.True(exit == 0, stderr);
        Assert.Equal("true\n", stdout);
        var fs2 = new MockFileSystem();
        var (exit2, stdout2, stderr2) = await RunModulesAsync(fs2, "-n", "builtins | any(.[0:1] == \"_\")");
        Assert.True(exit2 == 0, stderr2);
        Assert.Equal("false\n", stdout2);
    }

    [Fact]
    public async Task Jq_Modules_SearchNullTerminates()
    {
        var fs = new MockFileSystem();
        fs.AddFile("/lib/a.jq", "def a: 1;");
        var (exit, _, stderr) = await RunModulesAsync(fs, "-n", "-L", "/lib", "import \"a\" as foo {\"search\":null}; 0");
        Assert.True(exit != 0, "expected not-found with null search");
        Assert.Contains("module not found", stderr);
    }

    [Fact]
    public async Task Jq_Modules_Modulemeta()
    {
        var fs = new MockFileSystem();
        fs.AddFile("/lib/a.jq", "module {\"version\":1.7}; def a: \"a\";");
        fs.AddFile("/lib/sub/d.jq", "def meh: \"meh\";");
        fs.AddFile("/lib/data.json", "{\"this\":\"is a test\"}");
        fs.AddFile("/lib/mtest.jq", "module {\"whatever\":null}; import \"a\" as foo; import \"d\" as d {\"search\":\"./sub\"}; import \"data\" as $d; def a: 0; def c: 1;");
        var (exit, stdout, stderr) = await RunModulesAsync(fs, "-n", "-L", "/lib", "\"mtest\" | modulemeta | .whatever");
        Assert.True(exit == 0, stderr);
        Assert.Equal("null\n", stdout);
        var fs2 = new MockFileSystem();
        fs2.AddFile("/lib/a.jq", "module {\"version\":1.7}; def a: \"a\";");
        fs2.AddFile("/lib/sub/d.jq", "def meh: \"meh\";");
        fs2.AddFile("/lib/data.json", "{\"this\":\"is a test\"}");
        fs2.AddFile("/lib/mtest.jq", "module {\"whatever\":null}; import \"a\" as foo; import \"d\" as d {\"search\":\"./sub\"}; import \"data\" as $d; def a: 0; def c: 1;");
        var (exit2, stdout2, stderr2) = await RunModulesAsync(fs2, "-n", "-L", "/lib", "\"mtest\" | modulemeta | .deps | length");
        Assert.True(exit2 == 0, stderr2);
        Assert.Equal("3\n", stdout2);
        var fs3 = new MockFileSystem();
        fs3.AddFile("/lib/a.jq", "def a: 1;");
        fs3.AddFile("/lib/sub/d.jq", "def meh: \"meh\";");
        fs3.AddFile("/lib/data.json", "{}");
        fs3.AddFile("/lib/mtest.jq", "module {\"whatever\":null}; import \"a\" as foo; import \"d\" as d {\"search\":\"./sub\"}; import \"data\" as $d; def a: 0; def c: 1;");
        var (exit3, stdout3, stderr3) = await RunModulesAsync(fs3, "-n", "-L", "/lib", "\"mtest\" | modulemeta | .defs | length");
        Assert.True(exit3 == 0, stderr3);
        Assert.Equal("2\n", stdout3);
    }

    [Fact]
    public async Task Jq_Modules_ModulemetaNeedsString()
    {
        var fs = new MockFileSystem();
        var (exit, _, stderr) = await RunModulesAsync(fs, "-n", "0 | modulemeta");
        Assert.True(exit != 0, "expected failure");
        Assert.Contains("modulemeta input module name must be a string", stderr);
    }
}



