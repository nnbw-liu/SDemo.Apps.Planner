using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using SDemo.App.Planner.Contracts;
using SDemo.App.Planner.Core;

namespace SDemo.App.Planner.Host;

/// <summary>
/// 桌面组件：≤8 条未完成待办 + 快速添加 + 勾完成 + 拖动移动。
/// 自己不做置顶/位置/尺寸——这些由宿主按 widget.json 管；应用只处理 content 与透明度。
/// </summary>
public partial class WidgetWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private const int WmSysCommand = 0x0112;
    private const int ScMinimize = 0xF020;

    private readonly PlannerService _service;
    private readonly Action? _openMain;
    private readonly List<WidgetTodoView> _items = [];

    private IntPtr _hwnd;
    private int _maxItems = 8;
    private bool _closed;
    private double _opacity = 0.94;

    public WidgetWindow(PlannerService service, Action? openMain)
    {
        _service = service;
        _openMain = openMain;

        InitializeComponent();
        TodoItems.ItemsSource = _items;

        _service.Changed += OnServiceChanged;
        Loaded += (_, _) => RefreshItems();
    }

    public IntPtr Hwnd => _hwnd;

    public (bool Visible, double Opacity) GetState() => (IsWindowVisible(_hwnd), _opacity);

    /// <summary>宿主指令入口；从 HTTP 线程调用，内部切回 UI 线程。</summary>
    public void ApplyCommand(WidgetCommandPayload command)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            switch (command.Command)
            {
                case "show":
                    ShowWithoutActivation();
                    RefreshItems();
                    break;
                case "hide":
                    Hide();
                    break;
                case "focus":
                    ShowWithoutActivation();
                    Activate();
                    QuickAddBox.Focus();
                    break;
                case "refresh":
                    RefreshItems();
                    break;
                case "themeChanged":
                    ApplyTheme(command.Theme);
                    break;
                case "configChanged":
                    _maxItems = command.MaxItems;
                    RefreshItems();
                    break;
            }

            if (command.Opacity is { } opacity)
            {
                _opacity = Math.Clamp(opacity, 0.35, 1.0);
                Opacity = _opacity;
            }
        }));
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        var source = HwndSource.FromHwnd(_hwnd);
        source?.AddHook(WndProc);

        var style = GetWindowLongPtr(_hwnd, GwlExStyle);
        SetWindowLongPtr(_hwnd, GwlExStyle, style | WsExToolWindow);
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _service.Changed -= OnServiceChanged;
        base.OnClosed(e);
    }

    private void OnServiceChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(new Action(RefreshItems));

    private void RefreshItems()
    {
        if (_closed)
        {
            return;
        }

        var rows = _service
            .ListTodos(new TodoQuery { Status = "all", Limit = 50 })
            .Where(todo => todo.Status != TodoStatus.Done)
            .OrderBy(todo => todo.DueAt is null)
            .ThenBy(todo => todo.DueAt?.ToLocalTime())
            .ThenBy(todo => -todo.Priority)
            .Take(_maxItems)
            .ToList();

        _items.Clear();
        foreach (var todo in rows)
        {
            _items.Add(new WidgetTodoView(
                todo.Id,
                todo.Title,
                todo.Status == TodoStatus.Doing ? "◐" : "○",
                BuildSubtitle(todo)));
        }

        TodoItems.Items.Refresh();
        CountText.Text = $"{_items.Count} 项";
        EmptyHint.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string BuildSubtitle(TodoDto todo)
    {
        var due = FormatDue(todo.DueAt);
        return todo.ListName is { Length: > 0 } list ? $"{due} · {list}" : due;
    }

    internal static string FormatDue(DateTimeOffset? dueAt)
    {
        if (dueAt is null)
        {
            return "未排期";
        }

        var local = dueAt.Value.ToLocalTime();
        var today = DateTime.Now.Date;
        var date = local.Date;
        var time = local.ToString("HH:mm");
        var prefix = date == today
            ? "今天"
            : date == today.AddDays(1)
                ? "明天"
                : local.ToString("MM-dd");
        var overdue = local < DateTimeOffset.Now;
        return (overdue ? "逾期 · " : string.Empty) + prefix + " " + time;
    }

    private void OnToggleClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string id } || id.Length == 0)
        {
            return;
        }

        try
        {
            var todo = _service.GetTodo(id);
            if (todo is null)
            {
                return;
            }

            if (todo.Status == TodoStatus.Done)
            {
                _service.ReopenTodo(id, todo.Revision, WriteContext.Manual());
            }
            else
            {
                _service.CompleteTodo(id, todo.Revision, WriteContext.Manual());
            }

            HintText.Text = "已更新 · 点击 ○ 完成";
        }
        catch (PlannerException ex)
        {
            HintText.Text = ex.Message;
        }
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e) => RefreshItems();

    private void OnHideClick(object sender, RoutedEventArgs e) => Hide();

    private void OnOpenClick(object sender, RoutedEventArgs e) => _openMain?.Invoke();

    private void OnQuickAddClick(object sender, RoutedEventArgs e) => AddFromQuickBox();

    private void OnQuickAddKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            AddFromQuickBox();
            e.Handled = true;
        }
    }

    private void AddFromQuickBox()
    {
        var title = QuickAddBox.Text.Trim();
        if (title.Length == 0)
        {
            return;
        }

        try
        {
            _service.CreateTodos([new TodoCreateInput { Title = title }], WriteContext.Manual());
            QuickAddBox.Clear();
            HintText.Text = "已添加";
            RefreshItems();
        }
        catch (PlannerException ex)
        {
            HintText.Text = ex.Message;
        }
    }

    private void OnHeaderDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void ShowWithoutActivation()
    {
        if (!IsVisible)
        {
            Show();
        }
    }

    private void ApplyTheme(string? theme)
    {
        var light = string.Equals(theme, "light", StringComparison.OrdinalIgnoreCase);
        Shell.Background = new SolidColorBrush(light
            ? Color.FromArgb(0xF0, 0xF7, 0xF8, 0xFB)
            : Color.FromArgb(0xE6, 0x1B, 0x22, 0x30));
        Shell.BorderBrush = new SolidColorBrush(light ? Color.FromRgb(0xD2, 0xD9, 0xE6) : Color.FromRgb(0x34, 0x42, 0x5E));
        TitleText.Foreground = new SolidColorBrush(light ? Color.FromRgb(0x1B, 0x22, 0x30) : Color.FromRgb(0xE8, 0xEC, 0xF4));
        CountText.Foreground = new SolidColorBrush(light ? Color.FromRgb(0x5C, 0x69, 0x80) : Color.FromRgb(0x7F, 0x8C, 0xA3));
        HintText.Foreground = new SolidColorBrush(light ? Color.FromRgb(0x5C, 0x69, 0x80) : Color.FromRgb(0x6E, 0x7A, 0x90));
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmSysCommand && (wParam.ToInt64() & 0xFFF0) == ScMinimize)
        {
            Hide();
            handled = true;
        }

        return IntPtr.Zero;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);
}

/// <summary>组件列表行；DataTemplate 直接绑定这些只读属性。</summary>
public sealed record WidgetTodoView(string Id, string Title, string Glyph, string Subtitle);
