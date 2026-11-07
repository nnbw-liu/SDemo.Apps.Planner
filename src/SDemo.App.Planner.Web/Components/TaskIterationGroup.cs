using SDemo.App.Planner.Contracts;

namespace SDemo.App.Planner.Web.Components;

/// <summary>任务列表「项目 + 迭代」分组的一层。大迭代的 Children 是小迭代。</summary>
public sealed record TaskIterationGroup(
    string Key,
    string Label,
    IReadOnlyList<TodoDto> Items,
    IReadOnlyList<TaskIterationGroup> Children)
{
    public int TotalCount => Items.Count + Children.Sum(child => child.TotalCount);
}
