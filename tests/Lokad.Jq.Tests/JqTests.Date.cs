using System;
using Lokad.Jq;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("0 | gmtime", "[1970,0,1,0,0,0,4,0]\n")]
    [InlineData("1425599507 | gmtime", "[2015,2,5,23,51,47,4,63]\n")]
    [InlineData("0.5 | gmtime", "[1970,0,1,0,0,0.5,4,0]\n")]
    [InlineData("0 - 1 | gmtime", "[1969,11,31,23,59,59,3,364]\n")]
    [InlineData("0 - 1.5 | gmtime", "[1969,11,31,23,59,59.5,3,364]\n")]
    [InlineData("[2015,2,5,23,51,47,4,63] | mktime", "1425599507\n")]
    [InlineData("1425599507 | gmtime | mktime", "1425599507\n")]
    [InlineData("[1969,11,31,23,59,59] | mktime", "-1\n")]
    [InlineData("0 - 1 | gmtime | mktime", "-1\n")]
    [InlineData("[2024,1,29] | mktime", "1709164800\n")]
    [InlineData("[2015,12,1,0,0,0] | mktime", "1451606400\n")]
    [InlineData("[2015,0,1,0,0,1.9] | mktime", "1420070401\n")]
    [InlineData("\"2015-03-05T23:51:47Z\" | fromdate", "1425599507\n")]
    [InlineData("1425599507 | todate", "\"2015-03-05T23:51:47Z\"\n")]
    [InlineData("\"2015-03-05T23:51:47Z\" | fromdate | todate", "\"2015-03-05T23:51:47Z\"\n")]
    public async Task Jq_DateEpochVectors(string filter, string expected)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("\"2015-03-05T23:51:47Z\" | strptime(\"%Y-%m-%dT%H:%M:%SZ\")", "[2015,2,5,23,51,47,4,63]\n")]
    [InlineData("\"2015-03-05 \" | strptime(\"%Y-%m-%d\")", "[2015,2,5,0,0,0,4,63,\" \"]\n")]
    [InlineData("\"March 5, 2015\" | strptime(\"%B %d, %Y\")", "[2015,2,5,0,0,0,4,63]\n")]
    [InlineData("\"03/05/15\" | strptime(\"%m/%d/%y\")", "[2015,2,5,0,0,0,4,63]\n")]
    [InlineData("\"11 PM\" | strptime(\"%I %p\")", "[1900,0,0,23,0,0,8,367]\n")]
    [InlineData("\"12 AM\" | strptime(\"%I %p\")", "[1900,0,0,0,0,0,8,367]\n")]
    [InlineData("\"060\" | strptime(\"%j\")", "[1900,2,1,0,0,0,4,59]\n")]
    [InlineData("\"2015 10 4\" | strptime(\"%Y %U %w\")", "[2015,2,12,0,0,0,4,70]\n")]
    [InlineData("\"2015-W10-4\" | strptime(\"%G-W%V-%u\")", "[2015,2,5,0,0,0,4,63]\n")]
    [InlineData("\"1425599507\" | strptime(\"%s\")", "[2015,2,5,23,51,47,4,63]\n")]
    [InlineData("\"2015-03-05T23:51:47 EST\" | strptime(\"%Y-%m-%dT%H:%M:%S %Z\")", "[2015,2,5,23,51,47,4,63]\n")]
    [InlineData("\"2015-03-05T23:51:47+02:00\" | strptime(\"%Y-%m-%dT%H:%M:%S%z\")", "[2015,2,5,23,51,47,4,63]\n")]
    [InlineData("\"Thu Mar  5 23:51:47 2015\" | strptime(\"%c\")", "[2015,2,5,23,51,47,4,63]\n")]
    [InlineData("[2015,2,5,23,51,47,4,63] | strftime(\"%Y-%m-%dT%H:%M:%SZ\")", "\"2015-03-05T23:51:47Z\"\n")]
    [InlineData("1425599507 | strftime(\"%Y-%m-%dT%H:%M:%SZ\")", "\"2015-03-05T23:51:47Z\"\n")]
    [InlineData("1425599507 | strftime(\"%A, %B %e, %Y\")", "\"Thursday, March  5, 2015\"\n")]
    [InlineData("1425599507 | strftime(\"%U %W %V %G %u %w %j\")", "\"09 09 10 2015 4 4 064\"\n")]
    [InlineData("1425599507 | strftime(\"%z %Z\")", "\"+0000 UTC\"\n")]
    [InlineData("1425599507 | strftime(\"%s\")", "\"1425599507\"\n")]
    [InlineData("1425599507 | strftime(\"%x\")", "\"03/05/15\"\n")]
    [InlineData("1425599507 | strftime(\"%X\")", "\"23:51:47\"\n")]
    [InlineData("1425599507 | strftime(\"%c\")", "\"Thu Mar  5 23:51:47 2015\"\n")]
    [InlineData("1425599507 | strftime(\"a%Qb\")", "\"a%Qb\"\n")]
    [InlineData("1425599507 | strftime(\"\")", "\"\"\n")]
    [InlineData("\"2021-01-01T00:00:00Z\" | fromdate | strftime(\"%V %G\")", "\"53 2020\"\n")]
    public async Task Jq_DateFormatVectors(string filter, string expected)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("\"x\" | gmtime", "string (\"x\") gmtime() requires numeric inputs")]
    [InlineData("1e1000 | gmtime", "error converting number of seconds since epoch to datetime")]
    [InlineData("(-1 | sqrt) | gmtime", "error converting number of seconds since epoch to datetime")]
    [InlineData("5 | mktime", "mktime requires array inputs")]
    [InlineData("[2015,\"x\"] | mktime", "mktime requires parsed datetime inputs")]
    [InlineData("\"nope\" | fromdate", "date \"nope\" does not match format")]
    [InlineData("\"2015-13-01\" | strptime(\"%Y-%m-%d\")", "does not match format")]
    [InlineData("5 | strptime(\"%Y\")", "strptime/1 requires string inputs and arguments")]
    [InlineData("\"x\" | strftime(5)", "strftime/1 requires parsed datetime inputs")]
    [InlineData("\"x\" | strftime(\"%Y\")", "strftime/1 requires parsed datetime inputs")]
    [InlineData("[2015,\"x\"] | strftime(\"%Y\")", "strftime/1 requires parsed datetime inputs")]
    [InlineData("\"x\" | localtime", "string (\"x\") localtime() requires numeric inputs")]
    public async Task Jq_DateFailures(string filter, string diagnostic)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));

        Assert.Equal(5, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Contains(diagnostic, host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }
}
