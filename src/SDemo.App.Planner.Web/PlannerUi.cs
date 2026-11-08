using SDemo.App.Planner.Contracts;

namespace SDemo.App.Planner.Web;

/// <summary>
/// 跨页面的轻量 UI 状态：只放「哪个弹窗开着」和 toast，不复制领域状态。
/// 由宿主注册为 scoped，生命周期跟随 BlazorWebView 的 circuit。
/// </summary>
public sealed class PlannerUi
{
    public bool TaskEditorOpen { get; private set; }

    public TodoDto? EditingTodo { get; private set; }

    public bool CompleteTaskOpen { get; private set; }

    public TodoDto? CompletingTodo { get; private set; }

    public bool TaskDetailOpen { get; private set; }

    public TodoDto? ViewingTodo { get; private set; }

    public bool BugEditorOpen { get; private set; }

    public TodoDto? BugParentTodo { get; private set; }

    public bool TaskPostponeOpen { get; private set; }

    public TodoDto? PostponingTodo { get; private set; }

    public string? PresetProjectId { get; private set; }

    public string? PresetIterationId { get; private set; }

    public DateTime? PresetDueDate { get; private set; }

    public WorkItemKind PresetKind { get; private set; } = WorkItemKind.Task;

    public bool SettingsOpen { get; private set; }

    public bool ProjectEditorOpen { get; private set; }

    public ProjectDto? EditingProject { get; private set; }

    public bool ProjectGroupsOpen { get; private set; }

    public bool IterationEditorOpen { get; private set; }

    public IterationDto? EditingIteration { get; private set; }

    public string? IterationProjectId { get; private set; }

    public string? IterationParentId { get; private set; }

    public bool RoutineEditorOpen { get; private set; }

    public RoutineDto? EditingRoutine { get; private set; }

    public bool EventEditorOpen { get; private set; }

    public EventDto? EditingEvent { get; private set; }

    public DateTime? PresetEventDate { get; private set; }

    public event Action? Changed;

    public event Action<string>? ToastRequested;

    public event Action<Guid>? SelectOpened;

    public void NotifySelectOpened(Guid id) => SelectOpened?.Invoke(id);

    public void OpenTask(
        TodoDto? todo = null,
        string? projectId = null,
        string? iterationId = null,
        WorkItemKind kind = WorkItemKind.Task,
        DateTime? dueDate = null)
    {
        EditingTodo = todo;
        PresetProjectId = projectId ?? todo?.ProjectId;
        PresetIterationId = iterationId ?? todo?.IterationId;
        PresetDueDate = todo is null ? dueDate : null;
        PresetKind = todo?.Kind ?? kind;
        TaskEditorOpen = true;
        Changed?.Invoke();
    }

    public void OpenComplete(TodoDto todo)
    {
        CompletingTodo = todo;
        CompleteTaskOpen = true;
        Changed?.Invoke();
    }

    public void OpenTaskDetail(TodoDto todo)
    {
        ViewingTodo = todo;
        TaskDetailOpen = true;
        Changed?.Invoke();
    }

    public void OpenBugEditor(TodoDto parent)
    {
        BugParentTodo = parent;
        BugEditorOpen = true;
        Changed?.Invoke();
    }

    public void CloseBugEditor()
    {
        BugEditorOpen = false;
        BugParentTodo = null;
        Changed?.Invoke();
    }

    public void OpenPostpone(TodoDto todo)
    {
        PostponingTodo = todo;
        TaskPostponeOpen = true;
        Changed?.Invoke();
    }

    public void OpenProject(ProjectDto? project = null)
    {
        EditingProject = project;
        ProjectEditorOpen = true;
        Changed?.Invoke();
    }

    public void OpenIteration(string projectId, IterationDto? iteration = null, string? parentId = null)
    {
        EditingIteration = iteration;
        IterationProjectId = projectId;
        IterationParentId = parentId ?? iteration?.ParentId;
        IterationEditorOpen = true;
        Changed?.Invoke();
    }

    public void OpenRoutine(RoutineDto? routine = null)
    {
        EditingRoutine = routine;
        RoutineEditorOpen = true;
        Changed?.Invoke();
    }

    public void OpenEvent(EventDto? evt = null, DateTime? date = null)
    {
        EditingEvent = evt;
        PresetEventDate = date;
        EventEditorOpen = true;
        Changed?.Invoke();
    }

    public void CloseTask()
    {
        TaskEditorOpen = false;
        EditingTodo = null;
        PresetDueDate = null;
        Changed?.Invoke();
    }

    public void OpenSettings()
    {
        SettingsOpen = true;
        Changed?.Invoke();
    }

    public void CloseSettings()
    {
        SettingsOpen = false;
        Changed?.Invoke();
    }

    public void CloseComplete()
    {
        CompleteTaskOpen = false;
        CompletingTodo = null;
        Changed?.Invoke();
    }

    public void CloseTaskDetail()
    {
        TaskDetailOpen = false;
        ViewingTodo = null;
        Changed?.Invoke();
    }

    public void ClosePostpone()
    {
        TaskPostponeOpen = false;
        PostponingTodo = null;
        Changed?.Invoke();
    }

    public void CloseProject()
    {
        ProjectEditorOpen = false;
        EditingProject = null;
        Changed?.Invoke();
    }

    public void OpenProjectGroups()
    {
        ProjectGroupsOpen = true;
        Changed?.Invoke();
    }

    public void CloseProjectGroups()
    {
        ProjectGroupsOpen = false;
        Changed?.Invoke();
    }

    public void CloseIteration()
    {
        IterationEditorOpen = false;
        EditingIteration = null;
        Changed?.Invoke();
    }

    public void CloseRoutine()
    {
        RoutineEditorOpen = false;
        EditingRoutine = null;
        Changed?.Invoke();
    }

    public void CloseEvent()
    {
        EventEditorOpen = false;
        EditingEvent = null;
        PresetEventDate = null;
        Changed?.Invoke();
    }

    public void Toast(string message) => ToastRequested?.Invoke(message);
}
