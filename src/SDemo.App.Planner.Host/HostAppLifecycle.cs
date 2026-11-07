using SDemo.App.Planner.Web;

namespace SDemo.App.Planner.Host;

internal sealed class HostAppLifecycle : IAppLifecycle
{
    public Action? ExitRequested { get; set; }

    public void Exit() => ExitRequested?.Invoke();
}
