using System.Globalization;
using SDemo.App.Planner.Contracts;
using SDemo.App.Planner.Storage;

namespace SDemo.App.Planner.Core;

/// <summary>法定节假日：手动维护 + 联网结果落库。手动记录优先，同步不会覆盖。</summary>
public sealed partial class PlannerService
{
    public IReadOnlyList<HolidayDto> ListHolidays(int year)
    {
        if (year is < 1900 or > 2200)
        {
            throw new PlannerException(ErrorCodes.Validation, "年份超出范围");
        }

        return _store.QueryHolidays(year).Select(PlannerMapper.ToHoliday).ToList();
    }

    public HolidayDto SaveHoliday(HolidayInput input, WriteContext context)
    {
        var date = input.Date;
        if (date.Year is < 1900 or > 2200)
        {
            throw new PlannerException(ErrorCodes.Validation, "日期超出范围");
        }

        var name = (input.Name ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            throw new PlannerException(ErrorCodes.Validation, "假期名称不能为空");
        }

        if (name.Length > 100)
        {
            throw new PlannerException(ErrorCodes.Validation, "假期名称最多 100 个字符");
        }

        var now = NowUtc();
        var row = new HolidayRow
        {
            Date = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            Name = name,
            IsWorkday = input.IsWorkday,
            Source = "manual",
            UpdatedAt = now,
        };

        _store.WriteHoliday(new HolidayWrite
        {
            Row = row,
            Op = "upsert",
            Actor = Actor(context),
            RequestId = context.RequestId,
            Reason = "手动维护假期",
        });
        RaiseChanged();
        return PlannerMapper.ToHoliday(row);
    }

    public void DeleteHoliday(DateOnly date, WriteContext context)
    {
        _store.DeleteHoliday(date, Actor(context), context.RequestId, "手动删除假期");
        RaiseChanged();
    }

    /// <summary>用远程结果替换某一年的 remote 记录；manual 记录由存储层保留。</summary>
    public int ApplyRemoteHolidays(int year, IReadOnlyList<HolidayInput> items, WriteContext context)
    {
        if (year is < 1900 or > 2200)
        {
            throw new PlannerException(ErrorCodes.Validation, "年份超出范围");
        }

        var now = NowUtc();
        var rows = new List<HolidayRow>();
        foreach (var item in items)
        {
            if (item.Date.Year != year)
            {
                continue;
            }

            var name = (item.Name ?? string.Empty).Trim();
            if (name.Length == 0)
            {
                name = item.IsWorkday ? "调休上班" : "法定假日";
            }

            rows.Add(new HolidayRow
            {
                Date = item.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                Name = name.Length > 100 ? name[..100] : name,
                IsWorkday = item.IsWorkday,
                Source = "remote",
                UpdatedAt = now,
            });
        }

        var count = _store.ReplaceRemoteHolidays(year, rows, Actor(context), context.RequestId);
        RaiseChanged();
        return count;
    }

    private static string Actor(WriteContext context) =>
        context.Source == TodoSource.Ai ? "ai" : "manual";
}
