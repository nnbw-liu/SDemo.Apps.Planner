using System.Globalization;
using System.Windows;
using SDemo.App.Planner.Contracts;
using SDemo.App.Planner.Core;

namespace SDemo.App.Planner.Host;

/// <summary>新建 / 编辑待办的小对话框；时间只在 UI 层拼成带偏移的 ISO，再交给 Core 校验。</summary>
public partial class TodoEditWindow : Window
{
    private readonly TodoDto? _existing;

    public TodoEditWindow(PlannerService service, TodoDto? existing, DateOnly? presetDate = null)
    {
        InitializeComponent();
        _existing = existing;

        HeaderText.Text = existing is null ? "新建待办" : "编辑待办";

        PriorityBox.ItemsSource = new[]
        {
            new PriorityOption(0, "0 · 最低"),
            new PriorityOption(1, "1 · 低"),
            new PriorityOption(2, "2 · 普通"),
            new PriorityOption(3, "3 · 高"),
            new PriorityOption(4, "4 · 紧急"),
        };
        PriorityBox.DisplayMemberPath = nameof(PriorityOption.Label);
        PriorityBox.SelectedValuePath = nameof(PriorityOption.Value);

        ListBox.ItemsSource = service.GetLists();
        ListBox.DisplayMemberPath = nameof(ListDto.Name);
        ListBox.SelectedValuePath = nameof(ListDto.Id);

        if (existing is not null)
        {
            TitleBox.Text = existing.Title;
            NotesBox.Text = existing.Notes ?? string.Empty;
            PriorityBox.SelectedValue = existing.Priority;
            ListBox.SelectedValue = existing.ListId;
            TagsBox.Text = string.Join(", ", existing.Tags);
            if (existing.DueAt is { } due)
            {
                var local = due.ToLocalTime();
                DueDate.SelectedDate = local.Date;
                DueTimeBox.Text = local.ToString("HH:mm", CultureInfo.InvariantCulture);
            }
            else
            {
                DueTimeBox.Text = "09:00";
            }
        }
        else
        {
            PriorityBox.SelectedValue = 2;
            ListBox.SelectedValue = PlannerStoreDefaultList;
            if (presetDate is { } date)
            {
                DueDate.SelectedDate = date.ToDateTime(TimeOnly.MinValue);
                DueTimeBox.Text = "09:00";
            }
        }

        Loaded += (_, _) => TitleBox.Focus();
    }

    public TodoEditResult? Result { get; private set; }

    private const string PlannerStoreDefaultList = "li_inbox";

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        var title = TitleBox.Text.Trim();
        if (title.Length == 0)
        {
            ErrorText.Text = "标题不能为空。";
            return;
        }

        DateTimeOffset? dueAt = null;
        var clearDue = false;
        if (DueDate.SelectedDate is { } date)
        {
            if (!TimeOnly.TryParseExact(DueTimeBox.Text.Trim(), "HH:mm", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var time))
            {
                ErrorText.Text = "时间请用 HH:mm 格式，例如 09:30。";
                return;
            }

            dueAt = IsoTime.FromLocalDate(DateOnly.FromDateTime(date), time);
        }
        else if (_existing?.DueAt is not null)
        {
            clearDue = true;
        }

        var tags = TagsBox.Text
            .Split([',', '，', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(PlannerConstants.MaxTagsPerTodo)
            .ToList();

        Result = new TodoEditResult(
            title,
            NotesBox.Text.Trim() is { Length: > 0 } notes ? notes : null,
            dueAt,
            clearDue,
            PriorityBox.SelectedValue as int? ?? 2,
            ListBox.SelectedValue as string,
            tags);

        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private sealed record PriorityOption(int Value, string Label);
}

public sealed record TodoEditResult(
    string Title,
    string? Notes,
    DateTimeOffset? DueAt,
    bool ClearDue,
    int Priority,
    string? ListId,
    IReadOnlyList<string> Tags);
