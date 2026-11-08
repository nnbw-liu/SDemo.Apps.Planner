using Microsoft.EntityFrameworkCore;

namespace SDemo.App.Planner.Storage;

/// <summary>应用级设置（MCP 开关等）。内部配置，不写 outbox / op_log。</summary>
public sealed partial class PlannerStore
{
    public string? GetSetting(string key)
    {
        using var db = Open();
        return db.AppSettings.AsNoTracking().Where(s => s.Key == key).Select(s => s.Value).FirstOrDefault();
    }

    public IReadOnlyDictionary<string, string> GetSettings(string prefix)
    {
        using var db = Open();
        return db.AppSettings.AsNoTracking()
            .Where(s => s.Key.StartsWith(prefix))
            .ToList()
            .ToDictionary(s => s.Key, s => s.Value, StringComparer.Ordinal);
    }

    public void SetSettings(IReadOnlyDictionary<string, string> values, DateTime nowUtc)
    {
        using var db = Open();
        using var transaction = db.Database.BeginTransaction();
        foreach (var (key, value) in values)
        {
            var row = db.AppSettings.FirstOrDefault(s => s.Key == key);
            if (row is null)
            {
                db.AppSettings.Add(new AppSettingRow { Key = key, Value = value, UpdatedAt = nowUtc });
            }
            else
            {
                row.Value = value;
                row.UpdatedAt = nowUtc;
            }
        }

        db.SaveChanges();
        transaction.Commit();
    }
}
