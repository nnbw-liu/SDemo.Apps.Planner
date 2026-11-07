namespace SDemo.App.Planner.Web.Components;

/// <summary>自定义下拉框的一项。Value 由调用方解释，Label 是展示文案。</summary>
/// <param name="CompactLabel">选中值超宽时改用这段短文案；为 null 时始终显示 Label。</param>
public sealed record SelectOption<TValue>(TValue Value, string Label, string? Hint = null, string? CompactLabel = null);
