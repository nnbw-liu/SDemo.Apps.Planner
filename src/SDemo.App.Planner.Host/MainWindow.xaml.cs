using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using SDemo.App.Planner.Contracts;
using SDemo.App.Planner.Core;

namespace SDemo.App.Planner.Host;

/// <summary>
/// 主窗口：待办列表 / 日历（日·周·月）/ 回收站 / 草稿审阅。
/// 视图逻辑留在 code-behind：P2 的数据面已经由 Core 承担，UI 只做装配与交互，
/// 不引入第二套 MVVM 模型去复制 DTO。
/// </summary>
public partial class MainWindow : Window
{
    private const string TodoDragFormat = "sdemo.planner.todo";

    private readonly PlannerService _service;
    private readonly List<DraftRowControl> _draftControls = [];
    private readonly List<TrashRowView> _trashRows = [];

    private List<DraftOption> _draftOptions = [];
    private bool _fillingFilters;
    private bool _renderingDraft;
    private bool _closed;
    private string _view = "todos";
    private CalendarViewMode _mode = CalendarViewMode.Month;
    private DateOnly _anchor = DateOnly.FromDateTime(DateTime.Now);
    private Point _dragOrigin;
    private DraftDto? _currentDraft;

    public MainWindow(PlannerService service)
    {
        _service = service;
        InitializeComponent();

        TodayText.Text = DateTime.Now.ToString("yyyy年M月d日 dddd", CultureInfo.GetCultureInfo("zh-CN"));
        StatusFilter.ItemsSource = new[]
        {
            new FilterOption("open", "未完成"),
            new FilterOption("doing", "进行中"),
            new FilterOption("done", "已完成"),
            new FilterOption("all", "全部状态"),
        };
        StatusFilter.DisplayMemberPath = nameof(FilterOption.Label);
        StatusFilter.SelectedValuePath = nameof(FilterOption.Value);
        StatusFilter.SelectedValue = "open";

        _service.Changed += OnServiceChanged;
        Loaded += (_, _) => RefreshAll();
        Closed += (_, _) =>
        {
            _closed = true;
            _service.Changed -= OnServiceChanged;
        };
    }

    private void OnServiceChanged(object? sender, EventArgs e)
    {
        if (_closed)
        {
            return;
        }

        Dispatcher.BeginInvoke(new Action(RefreshAll));
    }

    private void RefreshAll()
    {
        if (_closed)
        {
            return;
        }

        RefreshListFilter();
        RefreshTodos();
        RefreshCalendar();
        RefreshTrash();
        RefreshDrafts();
        RefreshStats();
    }

    private void SetStatus(string text) => StatusText.Text = text;

    // ── 视图切换 ────────────────────────────────────────────────────────

    private void OnNavClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag })
        {
            ShowView(tag);
        }
    }

    private void ShowView(string view)
    {
        _view = view;
        TodoPanel.Visibility = view == "todos" ? Visibility.Visible : Visibility.Collapsed;
        CalendarPanel.Visibility = view == "calendar" ? Visibility.Visible : Visibility.Collapsed;
        TrashPanel.Visibility = view == "trash" ? Visibility.Visible : Visibility.Collapsed;
        DraftsPanel.Visibility = view == "drafts" ? Visibility.Visible : Visibility.Collapsed;

        foreach (var button in new[] { NavTodos, NavCalendar, NavTrash, NavDrafts })
        {
            button.Background = Brushes.Transparent;
            button.Foreground = new SolidColorBrush(Color.FromRgb(0xA9, 0xB6, 0xCA));
        }

        var active = view switch
        {
            "calendar" => NavCalendar,
            "trash" => NavTrash,
            "drafts" => NavDrafts,
            _ => NavTodos,
        };
        active.Background = new SolidColorBrush(Color.FromRgb(0x23, 0x2C, 0x42));
        active.Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xEC, 0xF4));

        if (view == "calendar")
        {
            RefreshCalendar();
        }
        else if (view == "trash")
        {
            RefreshTrash();
        }
        else if (view == "drafts")
        {
            RefreshDrafts();
        }
    }

    // ── 待办列表 ────────────────────────────────────────────────────────

    private void RefreshListFilter()
    {
        var previous = ListFilter.SelectedValue as string ?? string.Empty;
        _fillingFilters = true;
        var options = new List<FilterOption> { new(string.Empty, "全部清单") };
        options.AddRange(_service.GetLists().Select(list => new FilterOption(list.Id, list.Name)));
        ListFilter.ItemsSource = options;
        ListFilter.DisplayMemberPath = nameof(FilterOption.Label);
        ListFilter.SelectedValuePath = nameof(FilterOption.Value);
        ListFilter.SelectedValue = options.Any(o => o.Value == previous) ? previous : string.Empty;
        _fillingFilters = false;
    }

    private void RefreshTodos()
    {
        var status = StatusFilter.SelectedValue as string ?? "open";
        var listId = ListFilter.SelectedValue as string;
        var q = SearchBox.Text.Trim();

        var todos = _service.ListTodos(new TodoQuery
        {
            Status = status,
            ListId = string.IsNullOrEmpty(listId) ? null : listId,
            Q = q.Length == 0 ? null : q,
            Limit = PlannerConstants.MaxListLimit,
        });

        var rows = todos.Select(todo => new TodoRowView(todo)).ToList();
        TodoList.ItemsSource = rows;
        TodoCountText.Text = q.Length > 0 ? $"搜索「{q}」：{rows.Count} 条" : $"{rows.Count} 条";
    }

    private void OnFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_fillingFilters || _closed)
        {
            return;
        }

        RefreshTodos();
    }

    private void OnClearFilterClick(object sender, RoutedEventArgs e)
    {
        SearchBox.Clear();
        StatusFilter.SelectedValue = "open";
        ListFilter.SelectedValue = string.Empty;
        SetStatus("筛选已清除");
        RefreshTodos();
    }

    private void OnSearchClick(object sender, RoutedEventArgs e) => RefreshTodos();

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            RefreshTodos();
            e.Handled = true;
        }
    }

    private void OnToggleTodoClick(object sender, RoutedEventArgs e)
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
                SetStatus($"已重新打开「{todo.Title}」");
            }
            else
            {
                _service.CompleteTodo(id, todo.Revision, WriteContext.Manual());
                SetStatus($"已完成「{todo.Title}」");
            }
        }
        catch (PlannerException ex)
        {
            ShowError(ex);
        }
    }

    private void OnEditTodoClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string id })
        {
            return;
        }

        var todo = _service.GetTodo(id);
        if (todo is null)
        {
            SetStatus("条目已不存在");
            return;
        }

        var dialog = new TodoEditWindow(_service, todo) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is not { } result)
        {
            return;
        }

        try
        {
            var patch = new TodoPatch
            {
                Title = result.Title,
                Notes = result.Notes,
                ClearNotes = result.Notes is null && todo.Notes is not null,
                Priority = result.Priority,
                DueAt = result.DueAt,
                ClearDueAt = result.ClearDue,
                ListId = result.ListId,
                Tags = result.Tags,
            };

            _service.UpdateTodo(id, todo.Revision, patch, WriteContext.Manual());
            SetStatus($"已保存「{result.Title}」");
        }
        catch (PlannerException ex)
        {
            ShowError(ex);
        }
    }

    private void OnDeleteTodoClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string id })
        {
            return;
        }

        var todo = _service.GetTodo(id);
        if (todo is null)
        {
            return;
        }

        var confirm = MessageBox.Show(
            this,
            $"删除「{todo.Title}」？\n它会进回收站保留 7 天，可恢复。",
            "删除待办",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK)
        {
            return;
        }

        try
        {
            _service.DeleteTodo(id, todo.Revision, "用户从主窗口删除", WriteContext.Manual());
            SetStatus($"已删除「{todo.Title}」（回收站保留 7 天）");
        }
        catch (PlannerException ex)
        {
            ShowError(ex);
        }
    }

    private void OnNewTodoClick(object sender, RoutedEventArgs e) => CreateTodo(null);

    private void CreateTodo(DateOnly? presetDate)
    {
        var dialog = new TodoEditWindow(_service, null, presetDate) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is not { } result)
        {
            return;
        }

        try
        {
            var created = _service.CreateTodos(
                [new TodoCreateInput
                {
                    Title = result.Title,
                    Notes = result.Notes,
                    DueAt = result.DueAt,
                    Priority = result.Priority,
                    ListId = result.ListId,
                    Tags = result.Tags,
                }],
                WriteContext.Manual());

            SetStatus(created.Warnings.Count > 0
                ? $"已创建「{result.Title}」（{created.Warnings[0]}）"
                : $"已创建「{result.Title}」");
        }
        catch (PlannerException ex)
        {
            ShowError(ex);
        }
    }

    private void OnQuickAddKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            AddQuickTodo();
            e.Handled = true;
        }
    }

    private void OnQuickAddClick(object sender, RoutedEventArgs e) => AddQuickTodo();

    private void AddQuickTodo()
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
            SetStatus($"已添加「{title}」");
            RefreshTodos();
        }
        catch (PlannerException ex)
        {
            ShowError(ex);
        }
    }

    private void OnRestoreClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string id })
        {
            return;
        }

        var row = _trashRows.FirstOrDefault(r => r.Id == id);
        if (row is null)
        {
            return;
        }

        try
        {
            if (row.Kind == "event")
            {
                _service.RestoreEvent(id, WriteContext.Manual());
            }
            else
            {
                _service.RestoreTodo(id, WriteContext.Manual());
            }

            SetStatus($"已恢复「{row.Title}」");
        }
        catch (PlannerException ex)
        {
            ShowError(ex);
        }
    }

    private void RefreshTrash()
    {
        _trashRows.Clear();
        var todos = _service.ListDeletedTodos();
        var events = _service.ListDeletedEvents();
        var now = DateTimeOffset.Now;

        foreach (var todo in todos)
        {
            var until = todo.RestorableUntil?.ToLocalTime();
            var remaining = until is null ? "—" : Math.Max(0, (until.Value - now).TotalDays).ToString("0.#");
            _trashRows.Add(new TrashRowView(
                todo.Id,
                "todo",
                todo.Title,
                $"待办 · 删除于 {todo.DeletedAt?.ToLocalTime():yyyy-MM-dd HH:mm} · 剩余 {remaining} 天"));
        }

        foreach (var evt in events)
        {
            _trashRows.Add(new TrashRowView(
                evt.Id,
                "event",
                evt.Title,
                $"日程 · {evt.StartAt.ToLocalTime():MM-dd HH:mm} · 删除于 {evt.DeletedAt?.ToLocalTime():yyyy-MM-dd HH:mm}"));
        }

        TrashList.ItemsSource = _trashRows.ToList();
    }

    // ── 日历 ────────────────────────────────────────────────────────────

    private void OnModeClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag })
        {
            return;
        }

        _mode = tag switch
        {
            "week" => CalendarViewMode.Week,
            "day" => CalendarViewMode.Day,
            _ => CalendarViewMode.Month,
        };
        RefreshCalendar();
    }

    private void OnPrevRangeClick(object sender, RoutedEventArgs e) => MoveAnchor(-1);

    private void OnNextRangeClick(object sender, RoutedEventArgs e) => MoveAnchor(1);

    private void OnTodayRangeClick(object sender, RoutedEventArgs e)
    {
        _anchor = DateOnly.FromDateTime(DateTime.Now);
        RefreshCalendar();
    }

    private void MoveAnchor(int direction)
    {
        _anchor = _mode switch
        {
            CalendarViewMode.Month => _anchor.AddMonths(direction),
            CalendarViewMode.Week => _anchor.AddDays(direction * 7),
            _ => _anchor.AddDays(direction),
        };
        RefreshCalendar();
    }

    private void RefreshCalendar()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        DateOnly gridStart;
        DateOnly gridEnd;

        if (_mode == CalendarViewMode.Month)
        {
            var first = new DateOnly(_anchor.Year, _anchor.Month, 1);
            gridStart = first.AddDays(-(int)(first.DayOfWeek == DayOfWeek.Sunday ? 6 : first.DayOfWeek - DayOfWeek.Monday));
            gridEnd = gridStart.AddDays(41);
            RangeLabel.Text = _anchor.ToString("yyyy年M月", CultureInfo.GetCultureInfo("zh-CN"));
        }
        else if (_mode == CalendarViewMode.Week)
        {
            gridStart = _anchor.AddDays(-(int)(_anchor.DayOfWeek == DayOfWeek.Sunday ? 6 : _anchor.DayOfWeek - DayOfWeek.Monday));
            gridEnd = gridStart.AddDays(6);
            RangeLabel.Text = $"{gridStart:yyyy-MM-dd} ~ {gridEnd:MM-dd}";
        }
        else
        {
            gridStart = gridEnd = _anchor;
            RangeLabel.Text = _anchor.ToString("yyyy年M月d日 dddd", CultureInfo.GetCultureInfo("zh-CN"));
        }

        var from = IsoTime.FromLocalDate(gridStart, TimeOnly.MinValue);
        var to = IsoTime.FromLocalDate(gridEnd.AddDays(1), TimeOnly.MinValue);
        var range = _service.CalendarRange(from, to);

        var todosByDate = range.Todos
            .Where(t => t.DueAt is not null)
            .GroupBy(t => IsoTime.LocalDate(t.DueAt!.Value))
            .ToDictionary(g => g.Key, g => g.ToList());
        var eventsByDate = range.Events
            .GroupBy(e => IsoTime.LocalDate(e.StartAt))
            .ToDictionary(g => g.Key, g => g.ToList());

        CalendarGrid.Children.Clear();
        CalendarGrid.RowDefinitions.Clear();
        CalendarGrid.ColumnDefinitions.Clear();
        CalendarHeader.Children.Clear();
        CalendarHeader.ColumnDefinitions.Clear();

        var columns = _mode == CalendarViewMode.Day ? 1 : 7;
        var rows = _mode == CalendarViewMode.Month ? 6 : 1;

        for (var c = 0; c < columns; c++)
        {
            CalendarGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            CalendarHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        for (var r = 0; r < rows; r++)
        {
            CalendarGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        }

        if (_mode != CalendarViewMode.Day)
        {
            var names = new[] { "一", "二", "三", "四", "五", "六", "日" };
            for (var c = 0; c < 7; c++)
            {
                var label = new TextBlock
                {
                    Text = names[c],
                    Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x94, 0xA6)),
                    FontSize = 10,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 2, 0, 2),
                };
                Grid.SetColumn(label, c);
                CalendarHeader.Children.Add(label);
            }
        }
        else
        {
            var label = new TextBlock
            {
                Text = "当日日程",
                Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x94, 0xA6)),
                FontSize = 10,
                Margin = new Thickness(4, 2, 0, 2),
            };
            Grid.SetColumn(label, 0);
            CalendarHeader.Children.Add(label);
        }

        for (var index = 0; index < rows * columns; index++)
        {
            var date = gridStart.AddDays(index);
            var cell = BuildDayCell(date, today, todosByDate.GetValueOrDefault(date), eventsByDate.GetValueOrDefault(date));
            Grid.SetRow(cell, index / columns);
            Grid.SetColumn(cell, index % columns);
            CalendarGrid.Children.Add(cell);
        }

        ModeMonth.Background = Brushes.Transparent;
        ModeWeek.Background = Brushes.Transparent;
        ModeDay.Background = Brushes.Transparent;
        ( _mode switch
        {
            CalendarViewMode.Month => ModeMonth,
            CalendarViewMode.Week => ModeWeek,
            _ => ModeDay,
        }).Background = new SolidColorBrush(Color.FromRgb(0x2E, 0x63, 0xC8));

        RefreshUnscheduled();
    }

    private Border BuildDayCell(
        DateOnly date,
        DateOnly today,
        IReadOnlyList<TodoDto>? todos,
        IReadOnlyList<EventDto>? events)
    {
        var isToday = date == today;
        var cell = new Border
        {
            Background = new SolidColorBrush(isToday
                ? Color.FromRgb(0x1E, 0x2A, 0x41)
                : Color.FromRgb(0x15, 0x1B, 0x27)),
            BorderBrush = new SolidColorBrush(isToday
                ? Color.FromRgb(0x4C, 0x8D, 0xFF)
                : Color.FromRgb(0x27, 0x31, 0x48)),
            BorderThickness = new Thickness(isToday ? 1.4 : 1),
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(2),
            Padding = new Thickness(4),
            AllowDrop = true,
            Tag = date,
        };
        cell.DragOver += OnCalendarDragOver;
        cell.Drop += OnCalendarDrop;
        cell.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2)
            {
                CreateTodo(date);
            }
        };

        var stack = new StackPanel();
        var header = new TextBlock
        {
            Text = _mode == CalendarViewMode.Month && date.Day == 1
                ? date.ToString("M月d日", CultureInfo.GetCultureInfo("zh-CN"))
                : date.Day.ToString(CultureInfo.InvariantCulture),
            Foreground = new SolidColorBrush(isToday
                ? Color.FromRgb(0x9F, 0xC1, 0xFF)
                : Color.FromRgb(0x8A, 0x94, 0xA6)),
            FontSize = 10,
            FontWeight = isToday ? FontWeights.SemiBold : FontWeights.Normal,
            Margin = new Thickness(0, 0, 0, 2),
        };
        stack.Children.Add(header);

        var maxChips = _mode == CalendarViewMode.Day ? 12 : _mode == CalendarViewMode.Week ? 3 : 3;
        var shown = 0;

        foreach (var evt in events ?? [])
        {
            if (shown >= maxChips)
            {
                break;
            }

            stack.Children.Add(BuildChip(
                $"{evt.StartAt.ToLocalTime():HH:mm} {evt.Title}",
                Color.FromRgb(0x24, 0x33, 0x2A),
                Color.FromRgb(0x9D, 0xD3, 0xB0),
                $"{evt.StartAt.ToLocalTime():HH:mm}-{evt.EndAt.ToLocalTime():HH:mm}\n{evt.Location}"));
            shown++;
        }

        foreach (var todo in todos ?? [])
        {
            if (shown >= maxChips)
            {
                break;
            }

            var overdue = todo.DueAt < DateTimeOffset.Now && todo.Status != TodoStatus.Done;
            var done = todo.Status == TodoStatus.Done;
            stack.Children.Add(BuildChip(
                $"{todo.DueAt!.Value.ToLocalTime():HH:mm} {todo.Title}",
                done
                    ? Color.FromRgb(0x20, 0x2B, 0x28)
                    : overdue
                        ? Color.FromRgb(0x3A, 0x22, 0x2A)
                        : Color.FromRgb(0x22, 0x2F, 0x4D),
                done
                    ? Color.FromRgb(0x78, 0x9A, 0x8A)
                    : overdue
                        ? Color.FromRgb(0xF4, 0xB0, 0xB8)
                        : Color.FromRgb(0xC7, 0xD8, 0xFF),
                todo.Notes));
            shown++;
        }

        var total = (events?.Count ?? 0) + (todos?.Count ?? 0);
        if (total > shown)
        {
            stack.Children.Add(new TextBlock
            {
                Text = $"+{total - shown} 更多",
                FontSize = 9,
                Foreground = new SolidColorBrush(Color.FromRgb(0x6E, 0x7A, 0x90)),
                Margin = new Thickness(2, 1, 0, 0),
            });
        }

        var scroll = new ScrollViewer
        {
            Content = stack,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            IsHitTestVisible = false,
        };
        cell.Child = scroll;
        return cell;
    }

    private static Border BuildChip(string text, Color background, Color foreground, string? toolTip)
    {
        var chip = new Border
        {
            Background = new SolidColorBrush(background),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(4, 2, 4, 2),
            Margin = new Thickness(0, 1, 0, 1),
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = text,
                FontSize = 9,
                Foreground = new SolidColorBrush(foreground),
                TextTrimming = TextTrimming.CharacterEllipsis,
            },
        };

        if (!string.IsNullOrWhiteSpace(toolTip))
        {
            chip.ToolTip = toolTip;
        }

        return chip;
    }

    private void RefreshUnscheduled()
    {
        var todos = _service
            .ListTodos(new TodoQuery { Status = "all", Limit = PlannerConstants.MaxListLimit })
            .Where(t => t.Status != TodoStatus.Done)
            .OrderBy(t => t.DueAt is null)
            .ThenBy(t => t.DueAt?.ToLocalTime())
            .Take(30)
            .Select(t => new UnscheduledRowView(
                t.Id,
                t.Title,
                (t.DueAt is null ? "未排期" : $"已排 {t.DueAt.Value.ToLocalTime():MM-dd HH:mm}") + $" · P{t.Priority}"))
            .ToList();

        UnscheduledList.ItemsSource = todos;
    }

    private void OnNewEventClick(object sender, RoutedEventArgs e)
    {
        var dialog = new EventEditWindow(_anchor) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is not { } result)
        {
            return;
        }

        try
        {
            var created = _service.CreateEvent(new EventCreateInput
            {
                Title = result.Title,
                StartAt = result.StartAt,
                EndAt = result.EndAt,
                Location = result.Location,
                Notes = result.Notes,
            }, WriteContext.Manual());

            SetStatus(created.Conflicts.Count > 0
                ? $"已创建日程「{result.Title}」，与 {created.Conflicts[0].Title} 时间重叠"
                : $"已创建日程「{result.Title}」");
        }
        catch (PlannerException ex)
        {
            ShowError(ex);
        }
    }

    private static void OnCalendarDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(TodoDragFormat) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnCalendarDrop(object sender, DragEventArgs e)
    {
        if (sender is not Border { Tag: DateOnly date } || e.Data.GetData(TodoDragFormat) is not string id)
        {
            return;
        }

        var todo = _service.GetTodo(id);
        if (todo is null)
        {
            return;
        }

        var time = todo.DueAt is { } due
            ? TimeOnly.FromDateTime(due.ToLocalTime().DateTime)
            : new TimeOnly(9, 0);
        var dueAt = IsoTime.FromLocalDate(date, time);

        try
        {
            _service.UpdateTodo(id, todo.Revision, new TodoPatch { DueAt = dueAt }, WriteContext.Manual());
            SetStatus($"「{todo.Title}」改到 {date:MM-dd} {time:HH:mm}");
            e.Handled = true;
        }
        catch (PlannerException ex)
        {
            ShowError(ex);
        }
    }

    private void OnUnscheduledMouseDown(object sender, MouseButtonEventArgs e) =>
        _dragOrigin = e.GetPosition(UnscheduledList);

    private void OnUnscheduledMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var position = e.GetPosition(UnscheduledList);
        if ((position - _dragOrigin).Length < 8)
        {
            return;
        }

        var row = FindUnscheduledRow(position);
        if (row is null)
        {
            return;
        }

        var data = new DataObject(TodoDragFormat, row.Id);
        DragDrop.DoDragDrop(UnscheduledList, data, DragDropEffects.Move);
    }

    private UnscheduledRowView? FindUnscheduledRow(Point position)
    {
        var hit = UnscheduledList.InputHitTest(position) as DependencyObject;
        while (hit is not null and not ListBoxItem)
        {
            hit = VisualTreeHelper.GetParent(hit);
        }

        return (hit as ListBoxItem)?.DataContext as UnscheduledRowView;
    }

    // ── 草稿 ────────────────────────────────────────────────────────────

    private void RefreshDrafts()
    {
        var drafts = _service.ListDrafts();
        var previous = _currentDraft?.DraftId;
        _draftOptions = drafts
            .Select(d => new DraftOption(d, $"{d.CreatedAt.ToLocalTime():MM-dd HH:mm} · {d.Items.Count} 条 · {SourceLabel(d.Source)}"))
            .ToList();

        DraftCombo.ItemsSource = _draftOptions;
        DraftCombo.DisplayMemberPath = nameof(DraftOption.Label);
        DraftCombo.SelectedValuePath = nameof(DraftOption.DraftId);

        if (_draftOptions.Count == 0)
        {
            _currentDraft = null;
            DraftCombo.SelectedIndex = -1;
            RenderDraft(null);
            return;
        }

        var match = _draftOptions.FirstOrDefault(o => o.DraftId == previous) ?? _draftOptions[0];
        DraftCombo.SelectedValue = match.DraftId;
        RenderDraft(match.Draft);
    }

    private void OnDraftSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_renderingDraft)
        {
            return;
        }

        if (DraftCombo.SelectedItem is DraftOption option)
        {
            RenderDraft(option.Draft);
        }
    }

    private void RenderDraft(DraftDto? draft)
    {
        _renderingDraft = true;
        _currentDraft = draft;
        _draftControls.Clear();
        DraftItemsPanel.Children.Clear();

        if (draft is null)
        {
            DraftItemsPanel.Children.Add(new TextBlock
            {
                Text = "目前没有待审草稿。让 DesktopAgent 调用 todo_plan_from_text 后，草稿会出现在这里。",
                Foreground = new SolidColorBrush(Color.FromRgb(0x6E, 0x7A, 0x90)),
                FontSize = 11,
                Margin = new Thickness(2, 8, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            });
            _renderingDraft = false;
            return;
        }

        if (draft.SourceSessionId is { Length: > 0 } sessionId)
        {
            DraftItemsPanel.Children.Add(new TextBlock
            {
                Text = $"来源会话：{sessionId}",
                Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x94, 0xA6)),
                FontSize = 10,
                Margin = new Thickness(2, 0, 0, 8),
                TextWrapping = TextWrapping.Wrap,
            });
        }

        foreach (var item in draft.Items)
        {
            var check = new CheckBox
            {
                IsChecked = item.Decision != "rejected",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
            };

            var title = new TextBox
            {
                Text = item.Title,
                FontSize = 12,
                VerticalContentAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(Color.FromRgb(0x12, 0x18, 0x26)),
                Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xEC, 0xF4)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x2C, 0x3A, 0x55)),
                Padding = new Thickness(6, 4, 6, 4),
            };

            var meta = new TextBlock
            {
                Text = $"来源句：{item.Excerpt}"
                       + (item.SuspectDuplicateOf is null ? string.Empty : $" · 疑似与 {item.SuspectDuplicateOf} 重复"),
                FontSize = 9,
                Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x94, 0xA6)),
                Margin = new Thickness(0, 3, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            };

            var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            check.SetValue(Grid.ColumnProperty, 0);
            row.Children.Add(check);
            var content = new StackPanel();
            content.Children.Add(title);
            content.Children.Add(meta);
            content.SetValue(Grid.ColumnProperty, 1);
            row.Children.Add(content);

            var card = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x25, 0x36)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x27, 0x31, 0x48)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 8, 10, 8),
                Child = row,
            };

            DraftItemsPanel.Children.Add(card);
            _draftControls.Add(new DraftRowControl(item.DraftItemId, check, title));
        }

        _renderingDraft = false;
    }

    private void OnSelectAllDraftsClick(object sender, RoutedEventArgs e)
    {
        foreach (var control in _draftControls)
        {
            control.Check.IsChecked = true;
        }
    }

    private void OnAcceptDraftClick(object sender, RoutedEventArgs e)
    {
        if (_currentDraft is not { } draft)
        {
            return;
        }

        if (_draftControls.Count == 0)
        {
            return;
        }

        var decisions = _draftControls
            .Select(control => new DraftAcceptItem
            {
                DraftItemId = control.ItemId,
                Reject = control.Check.IsChecked != true,
                Title = string.IsNullOrWhiteSpace(control.Title.Text) ? null : control.Title.Text.Trim(),
            })
            .ToList();

        try
        {
            var result = _service.AcceptDraft(
                new DraftAcceptRequest { DraftId = draft.DraftId, Items = decisions },
                new WriteContext
                {
                    Source = TodoSource.Ai,
                    SessionId = draft.SourceSessionId,
                    RequestId = "ui_" + Guid.NewGuid().ToString("N")[..10],
                });

            SetStatus(result.Accepted
                ? $"已采纳 {result.Items.Count} 条，拒收 {result.Rejected} 条"
                : $"已处理草稿（未采纳任何条目）");
        }
        catch (PlannerException ex)
        {
            ShowError(ex);
        }
    }

    private void OnRejectDraftClick(object sender, RoutedEventArgs e)
    {
        if (_currentDraft is not { } draft)
        {
            return;
        }

        var confirm = MessageBox.Show(this, "拒绝整份草稿？不会写入任何待办。", "拒绝草稿",
            MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK)
        {
            return;
        }

        try
        {
            _service.RejectDraft(draft.DraftId, WriteContext.Manual());
            SetStatus("草稿已拒绝");
        }
        catch (PlannerException ex)
        {
            ShowError(ex);
        }
    }

    // ── 导出 / 统计 ────────────────────────────────────────────────────

    private void OnExportIcsClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "iCalendar (*.ics)|*.ics",
            FileName = $"sdemo-planner-{DateTime.Now:yyyyMMdd}.ics",
            DefaultExt = ".ics",
            AddExtension = true,
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var count = _service.ListTodos(new TodoQuery { Status = "all", Limit = PlannerConstants.MaxListLimit }).Count;
            _service.ExportIcsToFile(dialog.FileName);
            SetStatus($"已导出 {count} 条待办与日程到 {dialog.FileName}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlannerException)
        {
            MessageBox.Show(this, $"导出失败：{ex.Message}", "导出 ICS", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnRefreshAllClick(object sender, RoutedEventArgs e)
    {
        RefreshAll();
        SetStatus("已刷新");
    }

    private void RefreshStats()
    {
        var stats = _service.Stats();
        StatsTodayText.Text = $"{stats.DueToday} 项到期";
        StatsMoreText.Text = $"逾期 {stats.Overdue} · 未完成 {stats.OpenTotal} · 完成率 {stats.CompletionRateLast7Days:P0}";
    }

    private void ShowError(PlannerException ex)
    {
        SetStatus(ex.Hint is null ? ex.Message : $"{ex.Message}（{ex.Hint}）");
        MessageBox.Show(this, ex.Hint is null ? ex.Message : $"{ex.Message}\n\n{ex.Hint}", "提示",
            MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private static string SourceLabel(TodoSource source) => source switch
    {
        TodoSource.Ai => "AI",
        TodoSource.Meeting => "会议",
        TodoSource.Ics => "ICS",
        TodoSource.Import => "导入",
        _ => "手动",
    };

    private sealed record FilterOption(string Value, string Label);

    private sealed record DraftOption(DraftDto Draft, string Label)
    {
        public string DraftId => Draft.DraftId;
    }

    private sealed record DraftRowControl(string ItemId, CheckBox Check, TextBox Title);

    private sealed record TrashRowView(string Id, string Kind, string Title, string Meta);

    private sealed record UnscheduledRowView(string Id, string Title, string Meta);

    private enum CalendarViewMode
    {
        Month,
        Week,
        Day,
    }

    private sealed record TodoRowView(TodoDto Item)
    {
        public string Id => Item.Id;

        public string Title => Item.Title;

        public string Glyph => Item.Status switch
        {
            TodoStatus.Done => "✓",
            TodoStatus.Doing => "◐",
            _ => "○",
        };

        public string Meta
        {
            get
            {
                var parts = new List<string> { $"P{Item.Priority}" };
                if (Item.ListName is { Length: > 0 } list)
                {
                    parts.Add(list);
                }

                if (Item.Source != TodoSource.Manual)
                {
                    parts.Add(SourceLabel(Item.Source));
                }

                if (Item.Tags.Count > 0)
                {
                    parts.Add(string.Join(" · ", Item.Tags.Select(t => "#" + t)));
                }

                return string.Join(" · ", parts);
            }
        }

        public string DueText => Item.Status == TodoStatus.Done
            ? "已完成"
            : WidgetWindow.FormatDue(Item.DueAt);

        public string DueBrush
        {
            get
            {
                if (Item.Status == TodoStatus.Done)
                {
                    return "#37C978";
                }

                return Item.DueAt is { } due && due < DateTimeOffset.Now ? "#F45B69" : "#8A94A6";
            }
        }
    }
}
