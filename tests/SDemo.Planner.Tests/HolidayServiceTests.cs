using SDemo.App.Planner.Contracts;
using SDemo.App.Planner.Core;

namespace SDemo.Planner.Tests;

public sealed class HolidayServiceTests
{
    [Fact]
    public void SaveAndDeleteHoliday_WorksForYear()
    {
        using var host = new TestHost();
        host.Service.SaveHoliday(new HolidayInput
        {
            Date = new DateOnly(2026, 10, 1),
            Name = "国庆节",
            IsWorkday = false,
        }, WriteContext.Manual());

        var items = host.Service.ListHolidays(2026);
        var item = Assert.Single(items);
        Assert.Equal("国庆节", item.Name);
        Assert.False(item.IsWorkday);
        Assert.Equal("manual", item.Source);

        host.Service.DeleteHoliday(new DateOnly(2026, 10, 1), WriteContext.Manual());
        Assert.Empty(host.Service.ListHolidays(2026));
    }

    [Fact]
    public void RemoteSync_PreservesManualDateAndAddsRestAndWorkday()
    {
        using var host = new TestHost();
        host.Service.SaveHoliday(new HolidayInput
        {
            Date = new DateOnly(2026, 10, 1),
            Name = "公司假",
            IsWorkday = false,
        }, WriteContext.Manual());

        var count = host.Service.ApplyRemoteHolidays(2026,
            [
                new HolidayInput { Date = new DateOnly(2026, 10, 1), Name = "国庆节", IsWorkday = false },
                new HolidayInput { Date = new DateOnly(2026, 10, 10), Name = "国庆后补班", IsWorkday = true },
            ],
            WriteContext.Manual());

        Assert.Equal(1, count);
        var items = host.Service.ListHolidays(2026);
        Assert.Equal(2, items.Count);
        Assert.Equal("公司假", items.Single(i => i.Date == new DateOnly(2026, 10, 1)).Name);
        Assert.Equal("manual", items.Single(i => i.Date == new DateOnly(2026, 10, 1)).Source);
        Assert.True(items.Single(i => i.Date == new DateOnly(2026, 10, 10)).IsWorkday);
        Assert.Equal("remote", items.Single(i => i.Date == new DateOnly(2026, 10, 10)).Source);
    }

    [Fact]
    public void RemoteSync_ReplacesPreviousRemoteButKeepsManual()
    {
        using var host = new TestHost();
        host.Service.ApplyRemoteHolidays(2026,
            [new HolidayInput { Date = new DateOnly(2026, 1, 1), Name = "元旦", IsWorkday = false }],
            WriteContext.Manual());
        host.Service.SaveHoliday(new HolidayInput
        {
            Date = new DateOnly(2026, 2, 1),
            Name = "公司假",
            IsWorkday = false,
        }, WriteContext.Manual());

        host.Service.ApplyRemoteHolidays(2026,
            [new HolidayInput { Date = new DateOnly(2026, 2, 17), Name = "春节", IsWorkday = false }],
            WriteContext.Manual());

        var items = host.Service.ListHolidays(2026);
        Assert.DoesNotContain(items, i => i.Date == new DateOnly(2026, 1, 1));
        Assert.Contains(items, i => i.Date == new DateOnly(2026, 2, 1) && i.Source == "manual");
        Assert.Contains(items, i => i.Date == new DateOnly(2026, 2, 17) && i.Source == "remote");
    }

    [Fact]
    public void Parse_MapsRestAndWorkdayFromPublicPayload()
    {
        const string json = """
        {
          "code": 0,
          "holiday": {
            "01-01": { "holiday": true, "name": "元旦", "date": "2026-01-01" },
            "01-04": { "holiday": false, "name": "元旦后补班", "date": "2026-01-04" }
          }
        }
        """;

        var items = HolidaySyncService.Parse(json, 2026);

        Assert.Equal(2, items.Count);
        var rest = items.Single(i => i.Date == new DateOnly(2026, 1, 1));
        Assert.False(rest.IsWorkday);
        Assert.Equal("元旦", rest.Name);
        var work = items.Single(i => i.Date == new DateOnly(2026, 1, 4));
        Assert.True(work.IsWorkday);
        Assert.Equal("元旦后补班", work.Name);
    }
}
