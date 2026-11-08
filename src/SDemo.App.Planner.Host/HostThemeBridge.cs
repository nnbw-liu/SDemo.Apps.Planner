using System.Windows;
using SDemo.App.Planner.Web;

namespace SDemo.App.Planner.Host;

/// <summary>把 Web 端主题同步到 WPF 标题栏；Web 可能从任意线程回调。</summary>
internal sealed class HostThemeBridge : IThemeHost
{
    public void Apply(bool dark)
    {
        var app = Application.Current;
        if (app is null)
        {
            return;
        }

        app.Dispatcher.Invoke(() =>
        {
            if (app.MainWindow is MainWindow window)
            {
                window.ApplyTheme(dark);
            }

            TrayIcon.Current?.ApplyTheme(dark);
            ReminderToastWindow.SetTheme(dark);
        });
    }
}
