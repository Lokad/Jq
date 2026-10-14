using Lokad.Utf8Regex.Pcre2;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Fact]
    public void Jq_GovernedRegexCacheRetainsPatternsAndRollsBackFailedCompilation()
    {
        var memory = new TestMemoryBudget(1024 * 1024);
        using var scope = memory.CreateScope();
        var budget = new JqBudget(JqExecutionPolicy.Default, scope, CancellationToken.None);
        using (var regexes = new JqRegexCache(budget))
        {
            var pattern = regexes.Get("x", Pcre2CompileOptions.None);
            Assert.Equal(327744, memory.ReservedBytes);
            Assert.Same(pattern, regexes.Get("x", Pcre2CompileOptions.None));
            Assert.Equal(327744, memory.ReservedBytes);
            Assert.StartsWith("invalid regex:", Assert.Throws<JqException>(() =>
                regexes.Get("[", Pcre2CompileOptions.None)).Message, StringComparison.Ordinal);
            Assert.Equal(327744, memory.ReservedBytes);
            Assert.Equal(655488, memory.PeakBytes);
        }
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public void Jq_GovernedRegexSubjectsAndDetailedResultsHaveOperationLifetimes()
    {
        var memory = new TestMemoryBudget(1024 * 1024);
        using var scope = memory.CreateScope();
        var budget = new JqBudget(JqExecutionPolicy.Default, scope, CancellationToken.None);
        using var regexes = new JqRegexCache(budget);
        var pattern = regexes.Get("x", Pcre2CompileOptions.None);
        long retained = memory.ReservedBytes;
        using (var subject = regexes.EncodeSubject("é🚀"))
        {
            Assert.Equal(retained + 70, memory.ReservedBytes);
            using (var match = regexes.Match(pattern, subject.Value, 0, Pcre2MatchOptions.None))
            {
                Assert.False(match.Value.Success);
                Assert.Equal(retained + 70 + 256, memory.ReservedBytes);
            }
            Assert.Equal(retained + 70, memory.ReservedBytes);
        }
        Assert.Equal(retained, memory.ReservedBytes);
        Assert.Throws<JqException>(() => regexes.EncodeSubject("a\ud800"));
        Assert.Equal(retained, memory.ReservedBytes);
    }

    [Theory]
    [InlineData("test")]
    [InlineData("match")]
    [InlineData("capture")]
    [InlineData("scan")]
    public async Task Jq_GovernedRegexAdmissionBypassesLanguageHandlers(string operation)
    {
        foreach (long allowance in new[] { 1L, 327744L })
        {
            var memory = new TestMemoryBudget(allowance);
            var host = new MockFileSystem();
            string filter = "\"x\" | try " + operation + "(\"x\") catch \"caught\"";
            var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));

            Assert.Equal(5, await command.ExecuteAsync(host, memory, CancellationToken.None));
            Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
            Assert.Equal("jq: memory budget exceeded\n", host.GetOutput(JqFileDescriptor.StdErr));
            Assert.Equal(0, memory.ReservedBytes);
            Assert.InRange(memory.PeakBytes, 0, allowance);
        }
    }

    [Fact]
    public void Jq_GovernedRegexWorkFailureRefundsDetailedAdmission()
    {
        var memory = new TestMemoryBudget(1024 * 1024);
        using var scope = memory.CreateScope();
        var budget = new JqBudget(new JqExecutionPolicy { MaximumRegexWork = 32 }, scope, CancellationToken.None);
        using var regexes = new JqRegexCache(budget);
        var pattern = regexes.Get("(a+)+$", Pcre2CompileOptions.None);
        using var subject = regexes.EncodeSubject(new string('a', 32) + "b");
        long retained = memory.ReservedBytes;
        Assert.Equal("regex work limit exceeded", Assert.Throws<JqQuotaException>(() =>
        {
            using var match = regexes.Match(pattern, subject.Value, 0, Pcre2MatchOptions.None);
        }).Message);
        Assert.Equal(retained, memory.ReservedBytes);
    }

    [Fact]
    public async Task Jq_GovernedRegexRepeatsWithOneRetainedCacheAndRefundedSubjects()
    {
        var memory = new TestMemoryBudget(384 * 1024);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "range(1000) | \"x\" | test(\"x\")")));
        for (int repeat = 0; repeat < 2; repeat++)
        {
            var host = new MockFileSystem();
            Assert.Equal(0, await command.ExecuteAsync(host, memory, CancellationToken.None));
            Assert.Equal(string.Concat(Enumerable.Repeat("true\n", 1000)), host.GetOutput(JqFileDescriptor.StdOut));
            Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
            Assert.Equal(0, memory.ReservedBytes);
        }
        Assert.InRange(memory.PeakBytes, 328065, 384 * 1024);
    }

    [Fact]
    public async Task Jq_GovernedNestedRegexReplacementKeepsParentAdmissionsLive()
    {
        var memory = new TestMemoryBudget(384 * 1024);
        var host = new MockFileSystem();
        host.SetStandardInput(string.Concat(Enumerable.Repeat("\"ab\"\n", 2000)));
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-r",
            "gsub(\"(?<x>.)\"; .x | gsub(\"(?<x>.)\"; \"y\"))")));

        Assert.Equal(0, await command.ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Equal(string.Concat(Enumerable.Repeat("yy\n", 2000)), host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, memory.ReservedBytes);
        Assert.True(memory.PeakBytes < 384 * 1024);
    }
}
