using System.Globalization;
using Microsoft.AspNetCore.Components;
using SDemo.App.Planner.Contracts;
using SDemo.App.Planner.Core;

namespace SDemo.App.Planner.Web;

/// <summary>页面基类：服务写操作后自动重绘，页面只关心自己的取数。</summary>
public abstract class PlannerPageBase : ComponentBase, IDisposable
{
    private bool _disposed;

    [Inject]
    protected PlannerService Service { get; set; } = default!;

    [Inject]
    protected PlannerUi Ui { get; set; } = default!;

    protected override void OnInitialized()
    {
        Service.Changed += OnServiceChanged;
        Reload();
    }

    /// <summary>取数钩子：写操作后由基类自动重调，再统一重绘。</summary>
    protected virtual void Reload()
    {
    }

    private void OnServiceChanged(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        _ = InvokeAsync(() =>
        {
            Reload();
            StateHasChanged();
        });
    }

    public virtual void Dispose()
    {
        _disposed = true;
        Service.Changed -= OnServiceChanged;
        GC.SuppressFinalize(this);
    }

    protected static string FormatDue(DateTimeOffset? due)
    {
        if (due is null)
        {
            return "未排期";
        }

        var local = due.Value.ToLocalTime();
        var today = DateTimeOffset.Now.Date;
        var date = local.Date;
        if (date == today)
        {
            return local.ToString("HH:mm");
        }

        if (date == today.AddDays(1))
        {
            return "明天 " + local.ToString("HH:mm");
        }

        if (date.Year == today.Year)
        {
            return local.ToString("M月d日");
        }

        return local.ToString("yyyy年M月d日");
    }

    protected static string KindLabel(WorkItemKind kind) => kind switch
    {
        WorkItemKind.Bug => "Bug",
        WorkItemKind.Research => "调研",
        WorkItemKind.Chore => "杂务",
        _ => "任务",
    };

    protected static string KindClass(WorkItemKind kind) => kind switch
    {
        WorkItemKind.Bug => "kind-bug",
        WorkItemKind.Research => "kind-research",
        WorkItemKind.Chore => "kind-chore",
        _ => "kind-task",
    };

    /// <summary>把 input[type=date] 的值规范成 yyyy-MM-dd，避免区域格式差异。</summary>
    protected static string DateValue(ChangeEventArgs args) => args.Value switch
    {
        DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTime date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTimeOffset date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        var raw => NormalizeDate(raw?.ToString()),
    };

    /// <summary>把 input[type=time] 的值规范成 HH:mm（兼容带秒的格式）。</summary>
    protected static string TimeValue(ChangeEventArgs args) => args.Value switch
    {
        TimeOnly time => time.ToString("HH:mm", CultureInfo.InvariantCulture),
        TimeSpan span => $"{(int)span.TotalHours:00}:{span.Minutes:00}",
        var raw => NormalizeTime(raw?.ToString()),
    };

    private static string NormalizeDate(string? raw)
    {
        var value = raw?.Trim() ?? string.Empty;
        if (DateOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var stamp))
        {
            return stamp.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        return value;
    }

    private static string NormalizeTime(string? raw)
    {
        var value = raw?.Trim() ?? string.Empty;
        if (TimeOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
        {
            return time.ToString("HH:mm", CultureInfo.InvariantCulture);
        }

        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var stamp))
        {
            return stamp.ToString("HH:mm", CultureInfo.InvariantCulture);
        }

        return value;
    }
}
