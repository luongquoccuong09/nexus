using System.Globalization;
using Nexus.Web.Modules.Notifications;

namespace Nexus.IntegrationTests;

public sealed class NotificationQuietHoursTests
{
    [Theory]
    [InlineData("2026-11-01T01:59:00Z", false)]
    [InlineData("2026-11-01T02:00:00Z", true)]
    [InlineData("2026-11-01T05:30:00Z", true)]
    [InlineData("2026-11-01T06:30:00Z", true)]
    [InlineData("2026-11-01T11:59:00Z", true)]
    [InlineData("2026-11-01T12:00:00Z", false)]
    public void OvernightQuietHoursFollowTheLocalClockAcrossDaylightSaving(string timestamp, bool expected)
    {
        var hours = new NotificationQuietHours
        {
            Start = new TimeOnly(22, 0), End = new TimeOnly(7, 0), TimeZoneId = "America/New_York"
        };
        Assert.Equal(expected, hours.IsQuietAt(DateTimeOffset.Parse(timestamp, CultureInfo.InvariantCulture)));
    }
}
