using System.Globalization;
using Xunit;

namespace MarkdownMidget.Tests;

public class TimingLogTests
{
    [Fact]
    public void A_line_is_the_time_of_day_sequence_phase_and_whole_milliseconds_in_any_culture()
    {
        var was = CultureInfo.CurrentCulture; CultureInfo.CurrentCulture = new CultureInfo("fi-FI");   // '.' in times, ',' in numbers
        try { Assert.Equal("12:58:01.123 open read 12ms", TimingLog.Line(new DateTime(2026, 9, 14, 12, 58, 1, 123), "open", "read", 12.4)); }
        finally { CultureInfo.CurrentCulture = was; }
    }
}
