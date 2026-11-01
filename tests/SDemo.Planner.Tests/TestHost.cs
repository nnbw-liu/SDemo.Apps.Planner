using System.IO;
using SDemo.App.Planner.Core;
using SDemo.App.Planner.Storage;

namespace SDemo.Planner.Tests;

/// <summary>
/// 每个测试一个独立临时库：跑真实 EF migrations（含 FTS5 建表），不碰用户数据目录。
/// </summary>
internal sealed class TestHost : IDisposable
{
    private readonly string _directory;

    public TestHost(TimeProvider? timeProvider = null)
    {
        _directory = Path.Combine(Path.GetTempPath(), "sdemo-planner-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);

        Store = new PlannerStore(Path.Combine(_directory, "planner.db"));
        Store.Migrate();
        Service = new PlannerService(Store, timeProvider);
    }

    public PlannerStore Store { get; }

    public PlannerService Service { get; }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响测试结论。
        }
    }
}

/// <summary>可控时钟：只用于「今日到期 / 完成率」这类与本地日期相关的断言。</summary>
internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
