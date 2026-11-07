namespace SDemo.App.Planner.Web;

/// <summary>
/// 宿主生命周期桥。Web 层只表达「用户要退出」，由 Host 决定如何关闭进程。
/// </summary>
public interface IAppLifecycle
{
    void Exit();
}
