using System.Globalization;
using System.Windows;
using SDemo.App.Planner.Contracts;

namespace SDemo.App.Planner.Host;

/// <summary>新建日程的小对话框；事件与待办分开建，不做「万能条目」。</summary>
public partial class EventEditWindow : Window
{
    public EventEditWindow(DateOnly date)
    {
        InitializeComponent();
        EventDate.SelectedDate = date.ToDateTime(TimeOnly.MinValue);
        Loaded += (_, _) => TitleBox.Focus();
    }

    public EventEditResult? Result { get; private set; }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        var title = TitleBox.Text.Trim();
        if (title.Length == 0)
        {
            ErrorText.Text = "标题不能为空。";
            return;
        }

        if (EventDate.SelectedDate is not { } date)
        {
            ErrorText.Text = "请选择日期。";
            return;
        }

        if (!TryParseTime(StartTimeBox.Text, out var start) || !TryParseTime(EndTimeBox.Text, out var end))
        {
            ErrorText.Text = "时间请用 HH:mm 格式，例如 09:30。";
            return;
        }

        if (end <= start)
        {
            ErrorText.Text = "结束时间必须晚于开始时间。";
            return;
        }

        var day = DateOnly.FromDateTime(date);
        Result = new EventEditResult(
            title,
            IsoTime.FromLocalDate(day, start),
            IsoTime.FromLocalDate(day, end),
            LocationBox.Text.Trim() is { Length: > 0 } location ? location : null,
            NotesBox.Text.Trim() is { Length: > 0 } notes ? notes : null);

        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private static bool TryParseTime(string text, out TimeOnly time) =>
        TimeOnly.TryParseExact(text.Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out time);
}

public sealed record EventEditResult(
    string Title,
    DateTimeOffset StartAt,
    DateTimeOffset EndAt,
    string? Location,
    string? Notes);
