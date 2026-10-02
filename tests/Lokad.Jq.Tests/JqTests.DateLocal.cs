using System;
using Lokad.Jq;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    private static TimeZoneInfo BuildDstZone()
    {
        var start = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 5, DayOfWeek.Sunday);
        var end = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 3, 0, 0), 10, 5, DayOfWeek.Sunday);
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(DateTime.MinValue.Date, DateTime.MaxValue.Date, TimeSpan.FromHours(1), start, end);
        return TimeZoneInfo.CreateCustomTimeZone("TEST", TimeSpan.FromHours(1), "TEST", "TEST-STD", "TEST-DST", new[] { rule });
    }

    private static JqCommandInvocation BuildZonedInvocation(TimeZoneInfo zone, params string[] args)
    {
        return JqCommandInvocation.CreateWithStandardDescriptors(
            "jq", args, [], new JqClock(new FixedClock(FixedInstant), zone));
    }

    [Theory]
    [InlineData("\"2015-03-05T23:51:47Z\" | fromdate | localtime", "[\n  2015,\n  2,\n  5,\n  23,\n  51,\n  47,\n  4,\n  63\n]\n")]
    [InlineData("0 | localtime", "[\n  1970,\n  0,\n  1,\n  0,\n  0,\n  0,\n  4,\n  0\n]\n")]
    // Literal and function-produced formats agree under an explicit zone.
    [InlineData("0 | strflocaltime(\"\" | ., @uri)", "\"\"\n\"\"\n")]
    public async Task Jq_LocaltimeUtcZone(string filter, string expected)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildZonedInvocation(TimeZoneInfo.Utc, "-n", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("0 | localtime", "[\n  1970,\n  0,\n  1,\n  5,\n  30,\n  0,\n  4,\n  0\n]\n")]
    [InlineData("0 | strflocaltime(\"%H:%M %z %Z\")", "\"05:30 +0530 PLUS530\"\n")]
    public async Task Jq_FixedOffsetZone(string filter, string expected)
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("PLUS530", TimeSpan.FromHours(5.5), "PLUS530", "PLUS530");
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildZonedInvocation(zone, "-n", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("\"2026-07-01T12:00:00Z\" | fromdate | localtime", "[\n  2026,\n  6,\n  1,\n  14,\n  0,\n  0,\n  3,\n  181\n]\n")]
    [InlineData("\"2026-01-01T12:00:00Z\" | fromdate | localtime", "[\n  2026,\n  0,\n  1,\n  13,\n  0,\n  0,\n  4,\n  0\n]\n")]
    [InlineData("\"2026-07-01T12:00:00Z\" | fromdate | strflocaltime(\"%Y-%m-%d %H:%M %z %Z\")", "\"2026-07-01 14:00 +0200 TEST-DST\"\n")]
    [InlineData("\"2026-01-01T12:00:00Z\" | fromdate | strflocaltime(\"%Y-%m-%d %H:%M %z %Z\")", "\"2026-01-01 13:00 +0100 TEST-STD\"\n")]
    [InlineData("[2026,6,1,14,0,0] | strflocaltime(\"%H %z\")", "\"14 +0200\"\n")]
    // Ambiguous local times report the instant's offset and DST state, not the wall default.
    [InlineData("\"2026-10-25T00:30:00Z\" | fromdate | strflocaltime(\"%Y-%m-%d %H:%M %z %Z\")", "\"2026-10-25 02:30 +0200 TEST-DST\"\n")]
    [InlineData("\"2026-10-25T01:30:00Z\" | fromdate | strflocaltime(\"%Y-%m-%d %H:%M %z %Z\")", "\"2026-10-25 02:30 +0100 TEST-STD\"\n")]
    public async Task Jq_DaylightZone(string filter, string expected)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildZonedInvocation(BuildDstZone(), "-n", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("0 | localtime", "explicit host time zone")]
    [InlineData("0 | strflocaltime(\"%Y\")", "explicit host time zone")]
    [InlineData("0 | strflocaltime(\"\" | ., @uri)", "explicit host time zone")]
    [InlineData("(-1 | sqrt) | localtime", "explicit host time zone")]
    public async Task Jq_LocalWithoutZoneIsExplicit(string filter, string diagnostic)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));

        Assert.Equal(5, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Contains(diagnostic, host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }

    [Theory]
    [InlineData("input_filename", "\"<stdin>\"\n\"<stdin>\"\n", "{\"a\":1}\n{\"b\":2}\n", null)]
    [InlineData("input_line_number", "1\n2\n", "1\n2\n", null)]
    [InlineData("input_line_number", "3\n4\n", "{\n\"a\": 1\n}\n2\n", null)]
    [InlineData("[input_filename]", "[\n  \"/data\"\n]\n", null, "{\"a\":1}\n")]
    [InlineData("[input_line_number]", "[\n  1\n]\n", null, "{\"a\":1}\n")]
    public async Task Jq_InputMetadata(string filter, string expected, string? stdin, string? file)
    {
        var host = new MockFileSystem();
        JqCommandInvocation invocation;
        if (file is null)
        {
            host.SetStandardInput(stdin!);
            invocation = BuildInvocation("jq", filter);
        }
        else
        {
            host.AddFile("/data", file);
            invocation = BuildInvocation("jq", filter, "/data");
        }
        var tool = Assert.IsType<Jq>(Jq.TryParse(invocation));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("input_line_number", "1\n2\n3\n", "a\nb\nc")]
    public async Task Jq_RawInputLines(string filter, string expected, string stdin)
    {
        var host = new MockFileSystem();
        host.SetStandardInput(stdin);
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-R", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("[input_filename, input_line_number]", "[\n  \"<stdin>\",\n  2\n]\n", "1\n2\n")]
    public async Task Jq_SlurpMetadata(string filter, string expected, string stdin)
    {
        var host = new MockFileSystem();
        host.SetStandardInput(stdin);
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-s", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("input_filename", "null\n")]
    [InlineData("input_line_number", "0\n")]
    public async Task Jq_NullInputMetadata(string filter, string expected)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_FilesTrackNamesAndLines()
    {
        var names = new MockFileSystem();
        names.AddFile("/a", "1\n");
        names.AddFile("/b", "2\n");

        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "input_filename", "/a", "/b")));

        Assert.Equal(0, await tool.ExecuteAsync(names, CancellationToken.None));
        Assert.Equal("\"/a\"\n\"/b\"\n", names.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(names.GetOutput(JqFileDescriptor.StdErr));
        var files = new MockFileSystem();
        files.AddFile("/a", "1\n");
        files.AddFile("/b", "2\n");
        var lines = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "input_line_number", "/a", "/b")));

        Assert.Equal(0, await lines.ExecuteAsync(files, CancellationToken.None));
        Assert.Equal("1\n1\n", files.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(files.GetOutput(JqFileDescriptor.StdErr));
    }
}
