using System.Globalization;
using Microsoft.EntityFrameworkCore;

namespace SDemo.App.Planner.Storage;

/// <summary>节假日 / 调休的持久化。日历日按 yyyy-MM-dd 字符串存，不做时区换算。</summary>
public sealed partial class PlannerStore
{
    public IReadOnlyList<HolidayRow> QueryHolidays(int year)
    {
        using var db = Open();
        var prefix = $"{year:0000}-";
        return db.Holidays.AsNoTracking()
            .Where(h => h.Date.StartsWith(prefix))
            .OrderBy(h => h.Date)
            .ToList();
    }

    public HolidayRow? GetHoliday(DateOnly date)
    {
        using var db = Open();
        var key = Format(date);
        return db.Holidays.AsNoTracking().FirstOrDefault(h => h.Date == key);
    }

    public void WriteHoliday(HolidayWrite write)
    {
        using var db = Open();
        try
        {
            using var transaction = db.Database.BeginTransaction();
            var row = write.Row;
            var current = db.Holidays.FirstOrDefault(h => h.Date == row.Date);
            string? beforeJson = null;

            if (current is null)
            {
                db.Holidays.Add(row);
                AddOutbox(db, "holiday", row.Date, write.Op, 0, row);
                AddOpLog(db, "holiday", row.Date, write.Op, null, Json(row), write.Actor, write.RequestId, write.Reason);
            }
            else
            {
                beforeJson = Json(current);
                db.Entry(current).CurrentValues.SetValues(row);
                AddOutbox(db, "holiday", row.Date, write.Op, 0, row);
                AddOpLog(db, "holiday", row.Date, write.Op, beforeJson, Json(row), write.Actor, write.RequestId, write.Reason);
            }

            db.SaveChanges();
            transaction.Commit();
        }
        catch (Exception ex) when (IsLocked(ex))
        {
            throw new PlannerStoreLockedException(ex);
        }
    }

    public void DeleteHoliday(DateOnly date, string actor, string? requestId, string? reason)
    {
        using var db = Open();
        using var transaction = db.Database.BeginTransaction();
        var key = Format(date);
        var current = db.Holidays.FirstOrDefault(h => h.Date == key);
        if (current is null)
        {
            transaction.Commit();
            return;
        }

        var beforeJson = Json(current);
        db.Holidays.Remove(current);
        AddOutbox(db, "holiday", key, "delete", 0, new { date = key });
        AddOpLog(db, "holiday", key, "delete", beforeJson, null, actor, requestId, reason);
        db.SaveChanges();
        transaction.Commit();
    }

    /// <summary>
    /// 用联网结果替换某一年的 remote 记录；manual 记录永远保留且优先。
    /// 单事务，旧 remote 全部清掉后写入新结果。
    /// </summary>
    public int ReplaceRemoteHolidays(int year, IReadOnlyList<HolidayRow> rows, string actor, string? requestId)
    {
        using var db = Open();
        using var transaction = db.Database.BeginTransaction();
        var prefix = $"{year:0000}-";

        var manualDates = db.Holidays.AsNoTracking()
            .Where(h => h.Date.StartsWith(prefix) && h.Source == "manual")
            .Select(h => h.Date)
            .ToHashSet();

        var oldRemote = db.Holidays.Where(h => h.Date.StartsWith(prefix) && h.Source == "remote").ToList();
        db.Holidays.RemoveRange(oldRemote);

        var added = 0;
        foreach (var row in rows)
        {
            if (row.Date.Length != 10 || !row.Date.StartsWith(prefix) || manualDates.Contains(row.Date))
            {
                continue;
            }

            db.Holidays.Add(row);
            added++;
        }

        AddOutbox(db, "holiday", $"{year:0000}", "sync", 0, new { year, count = added, removed = oldRemote.Count });
        AddOpLog(db, "holiday", $"{year:0000}", "sync", null,
            Json(new { year, count = added, removed = oldRemote.Count }), actor, requestId, "联网同步法定节假日");
        db.SaveChanges();
        transaction.Commit();
        return added;
    }

    private static string Format(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
