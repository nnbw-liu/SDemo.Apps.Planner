using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Microsoft.Win32;
using SDemo.App.Planner.Contracts;

namespace SDemo.App.Planner.Host;

/// <summary>
/// 右下角的非激活提醒卡。不抢焦点，不进入任务栏，点一下即可关闭。
/// 颜色不读 CSS token（WPF 窗口拿不到），由 <see cref="HostThemeBridge"/> 把当前主题同步进来。
/// </summary>
internal sealed class ReminderToastWindow : Window
{
    private static ReminderToastWindow? _current;
    private static bool _dark = DetectSystemDark();

    private readonly DispatcherTimer _timer;
    private readonly List<TextBlock> _titles = [];
    private readonly List<TextBlock> _details = [];
    private readonly List<Border> _icons = [];
    private readonly List<Border> _separators = [];
    private Border _card = null!;
    private Border _accentDot = null!;
    private TextBlock _eyebrow = null!;
    private TextBlock _footer = null!;
    private DropShadowEffect _shadow = null!;
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
        ApplyTheme(_dark);

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

    /// <summary>由宿主主题桥调用；既更新当前提醒卡，也记住给下一条用。</summary>
    public static void SetTheme(bool dark)
    {
        _dark = dark;
        var current = _current;
        if (current is null)
        {
            return;
        }

        if (!current.Dispatcher.CheckAccess())
        {
            current.Dispatcher.Invoke(() => current.ApplyTheme(dark));
            return;
        }

        current.ApplyTheme(dark);
    }

    private Border BuildCard(IReadOnlyList<RoutineReminderDto> reminders)
    {
        var rows = new StackPanel();
        for (var i = 0; i < reminders.Count; i++)
        {
            if (i > 0)
            {
                var separator = new Border { Height = 1, Margin = new Thickness(0, 9, 0, 9) };
                _separators.Add(separator);
                rows.Children.Add(separator);
            }

            rows.Children.Add(BuildRow(reminders[i]));
        }

        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 12),
        };
        _accentDot = new Border
        {
            Width = 6,
            Height = 6,
            CornerRadius = new CornerRadius(3),
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _eyebrow = new TextBlock
        {
            Text = "日常提醒",
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
        };
        header.Children.Add(_accentDot);
        header.Children.Add(_eyebrow);

        _footer = new TextBlock
        {
            Text = reminders.Count > 1 ? $"{reminders.Count} 条提醒 · 点击关闭" : "点击关闭",
            FontSize = 10,
            Margin = new Thickness(0, 13, 0, 0),
        };

        var content = new StackPanel();
        content.Children.Add(header);
        content.Children.Add(rows);
        content.Children.Add(_footer);

        _shadow = new DropShadowEffect
        {
            BlurRadius = 30,
            ShadowDepth = 9,
            Direction = 270,
            Opacity = 0.4,
            Color = Colors.Black,
        };

        _card = new Border
        {
            Width = 340,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(18, 16, 18, 14),
            Effect = _shadow,
            Child = content,
        };

        return _card;
    }

    private UIElement BuildRow(RoutineReminderDto reminder)
    {
        var title = new TextBlock
        {
            Text = reminder.Title,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };
        _titles.Add(title);

        var detail = new TextBlock
        {
            Text = BuildDetail(reminder),
            FontSize = 11,
            Margin = new Thickness(0, 3, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        };
        _details.Add(detail);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(title);
        text.Children.Add(detail);

        var icon = new Border
        {
            Width = 38,
            Height = 38,
            CornerRadius = new CornerRadius(11),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(reminder.Emoji) ? "⏰" : reminder.Emoji,
                FontSize = 20,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        _icons.Add(icon);

        var row = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 2, 0, 2) };
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        row.Children.Add(text);
        return row;
    }

    private static string BuildDetail(RoutineReminderDto reminder)
    {
        var time = reminder.ScheduledFor.ToLocalTime().ToString("HH:mm");
        var note = reminder.Message?.Trim();
        if (string.IsNullOrEmpty(note))
        {
            note = reminder.IsInterval ? "间隔提醒" : "到点提醒";
        }

        return $"{time} · {note}";
    }

    private void ApplyTheme(bool dark)
    {
        var p = dark ? Palette.Dark : Palette.Light;

        _card.Background = new LinearGradientBrush(p.CardTop, p.CardBottom, 90);
        _card.BorderBrush = new SolidColorBrush(p.Border);
        _shadow.Opacity = p.ShadowOpacity;

        _accentDot.Background = new SolidColorBrush(p.Accent);
        _eyebrow.Foreground = new SolidColorBrush(p.Accent);
        _footer.Foreground = new SolidColorBrush(p.Footer);

        foreach (var title in _titles)
        {
            title.Foreground = new SolidColorBrush(p.Title);
        }

        foreach (var detail in _details)
        {
            detail.Foreground = new SolidColorBrush(p.Detail);
        }

        foreach (var icon in _icons)
        {
            icon.Background = new SolidColorBrush(p.IconBackground);
            icon.BorderBrush = new SolidColorBrush(p.IconBorder);
        }

        foreach (var separator in _separators)
        {
            separator.Background = new SolidColorBrush(p.Separator);
        }
    }

    private static bool DetectSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is not int light || light == 0;
        }
        catch
        {
            // 读不到就按深色，ThemeService 初始化后会立即纠正。
            return true;
        }
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

    private sealed record Palette(
        Color CardTop,
        Color CardBottom,
        Color Border,
        Color Title,
        Color Detail,
        Color Accent,
        Color Footer,
        Color IconBackground,
        Color IconBorder,
        Color Separator,
        double ShadowOpacity)
    {
        public static Palette Dark { get; } = new(
            Hex("#1A2231"),
            Hex("#111722"),
            Hex("#2B374B"),
            Hex("#F3F6FC"),
            Hex("#C2CCDC"),
            Hex("#9BB0FF"),
            Hex("#A3B0C4"),
            Hex("#212C3F"),
            Hex("#32415A"),
            Hex("#263246"),
            0.38);

        public static Palette Light { get; } = new(
            Hex("#FFFFFF"),
            Hex("#F7F9FD"),
            Hex("#DCE4F0"),
            Hex("#162033"),
            Hex("#53627A"),
            Hex("#4F6EF7"),
            Hex("#738199"),
            Hex("#EEF3FF"),
            Hex("#D9E3FA"),
            Hex("#E6ECF5"),
            0.18);

        private static Color Hex(string value) => (Color)ColorConverter.ConvertFromString(value);
    }
}
