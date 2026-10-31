using System.Text;
using Lokad.Jq.Testing;

namespace Lokad.Jq.Tests;

// Real-engine semantics through the explicitly temporary published-package bridge.
// These are separate from RegexSubjectTests' provider-neutral resource proofs.
public sealed class RegexCompatibilityTests
{
    [Theory]
    [InlineData("literal", 0)]
    [InlineData(".", 0)]
    [InlineData("a*", 0)]
    [InlineData("(a)?(b)", 2)]
    [InlineData("(?<x>a)|(?<x>b)", 2)]
    [InlineData("(?|(a)|(b)(c))", 2)]
    [InlineData("(?<x>a)(?=b)(?:b)(c)", 2)]
    public void CompiledMetadataIncludesUnnamedAndNonparticipatingGroups(string pattern, int expected)
    {
        Assert.True(Utf8RegexAdapter.Instance.TryCompile(Encoding.UTF8.GetBytes(pattern),
            JqRegexCompileOptions.None, null, CancellationToken.None, out var compiled, out var error), error);
        using (compiled) Assert.Equal(expected, compiled.CaptureCount);
    }

    [Theory]
    // Independently checked against jq 1.8.2. Empty matches skip a same-position
    // nonempty alternative; subsequent searches retain anchors/lookbehind context.
    [InlineData("\"ab\"|[match(\"(?:|ab)\";\"g\")|[.offset,.length]]", "[[0,0],[1,0],[2,0]]\n")]
    [InlineData("\"ab\"|[match(\"^a|(?<=a)b\";\"g\")|[.offset,.length]]", "[[0,1],[1,1]]\n")]
    [InlineData("\"b\"|[scan(\"(a)?(b)\")]", "[[null,\"b\"]]\n")]
    public async Task CursorPreservesJqSearchSemantics(string filter, string expected)
    {
        var host = new MockFileSystem();
        using var invocation = JqCommandInvocation.CreateWithStandardDescriptors("jq", ["-nc", filter], []);
        Assert.Equal(0, await Assert.IsType<Jq>(Jq.TryParse(invocation))
            .ExecuteAsync(host, Utf8RegexAdapter.Instance, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }
}
