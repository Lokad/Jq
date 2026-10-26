using System.Globalization;

namespace Lokad.Jq.Tests;

public sealed class CommandRecognitionTests
{
    [Theory]
    [InlineData("")]
    [InlineData("en-US")]
    [InlineData("tr-TR")]
    public void RecognitionUsesExactCommandNamesAcrossCultures(string culture)
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            var cases = new (string Name, bool Recognized)[]
            {
                ("jq", true), ("/bin/jq", true), ("tools/jq", true),
                ("JQ", false), ("/bin/JQ", false), ("j\u00adq", false),
                ("/bin/j\u00adq", false), ("/bin/jq\u00ad", false),
                ("/bin/j\u200bq", false), ("/bin/jq\u200b", false),
                ("/bin/jq\0", false), ("jq\0", false), ("otherjq", false), ("", false)
            };
            foreach (var item in cases)
            {
                using var invocation = JqCommandInvocation.CreateWithStandardDescriptors(item.Name, ["-nc", "1"], []);
                Assert.Equal(item.Recognized, Jq.TryParse(invocation) is not null);
            }
        }
        finally { CultureInfo.CurrentCulture = original; }
    }
}
