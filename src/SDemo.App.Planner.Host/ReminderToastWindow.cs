using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using SDemo.App.Planner.Contracts;

namespace SDemo.App.Planner.Host;

/// <summary>
/// 右下角的非激活提醒卡。不抢焦点，不进入任务栏，点一下即可关闭。
/// </summary>
internal sealed class ReminderToastWindow : Window
{
    private static ReminderToastWindow? _current;

    private readonly DispatcherTimer _timer;
    private bool _closed;

    private ReminderToastWindow(IReadOnlyList<RoutineReminderDto> reminders)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Cursor = Cursors.Hand;

        Content = BuildCard(reminders);

        Loaded += (_, _) =>
        {
            var work = SystemParameters.WorkArea;
            Left = Math.Max(work.Left + 16, work.Right - ActualWidth - 24);
            Top = Math.Max(work.Top + 16, work.Bottom - ActualHeight - 24);
        };

        MouseLeftButtonDown += (_, _) => Close();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(12) };
        _timer.Tick += (_, _) => Close();
        _timer.Start();

        Closed += (_, _) =>
        {
            _closed = true;
            _timer.Stop();
            if (ReferenceEquals(_current, this))
            {
                _current = null;
            }
        };
    }

    public static void ShowReminders(IReadOnlyList<RoutineReminderDto> reminders)
    {
        if (reminders.Count == 0)
        {
            return;
        }

        try
        {
            _current?.Close();
            var toast = new ReminderToastWindow(reminders);
            _current = toast;
            toast.Show();
        }
        catch (Exception ex)
        {
            // 提醒是尽力而为，不因为窗口失败影响主流程；失败要留痕，否则静默丢提醒。
            PlannerLog.Warn($"提醒窗口显示失败：{ex}");
        }
    }

    private static Border BuildCard(IReadOnlyList<RoutineReminderDto> reminders)
    {
        var rows = new StackPanel();
        foreach (var reminder in reminders)
        {
            rows.Children.Add(BuildRow(reminder));
        }

        var content = new StackPanel();
        content.Children.Add(new TextBlock
        {
            Text = "日常提醒",
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(124, 149, 255)),
            Margin = new Thickness(0, 0, 0, 10),
        });
        content.Children.Add(rows);
        content.Children.Add(new TextBlock
        {
            Text = "点击关闭",
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.FromRgb(124, 134, 156)),
            Margin = new Thickness(0, 12, 0, 0),
        });

        return new Border
        {
            Width = 320,
            Background = new SolidColorBrush(Color.FromRgb(24, 30, 46)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(58, 72, 104)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(16),
            Effect = new DropShadowEffect
            {
                BlurRadius = 28,
                ShadowDepth = 8,
                Opacity = 0.45,
                Color = Colors.Black,
            },
            Child = content,
        };
    }

    private static UIElement BuildRow(RoutineReminderDto reminder)
    {
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = reminder.Title,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(236, 240, 248)),
        });

        var detail = string.IsNullOrWhiteSpace(reminder.Message)
            ? reminder.ScheduledFor.ToLocalTime().ToString("HH:mm")
            : reminder.Message;
        text.Children.Add(new TextBlock
        {
            Text = detail,
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(150, 160, 180)),
            Margin = new Thickness(0, 3, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        });

        var row = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 4, 0, 4) };
        var emoji = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(reminder.Emoji) ? "⏰" : reminder.Emoji,
            FontSize = 22,
            Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        DockPanel.SetDock(emoji, Dock.Left);
        row.Children.Add(emoji);
        row.Children.Add(text);
        return row;
    }

    protected override void OnClosed(EventArgs e)
    {
        if (!_closed)
        {
            _closed = true;
            _timer.Stop();
        }

        base.OnClosed(e);
    }
}
