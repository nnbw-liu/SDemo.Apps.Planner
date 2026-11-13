namespace SDemo.App.Planner.Web;

/// <summary>
/// 宿主能力：文件选择、打开、在资源管理器里定位。
/// Blazor 在 WebView 里拿不到桌面 API，所以由 Host 注册真实实现；
/// 组件页（只读 widget）没有主窗口，注册 <see cref="NullPlatformFiles"/>，按钮自动置灰。
/// </summary>
public interface IPlatformFiles
{
    /// <summary>宿主是否支持弹窗；false 时附件面板禁用「添加附件」。</summary>
    bool Available { get; }

    /// <summary>选单个文件，取消返回 null。</summary>
    string? PickFile(string title, string? filter = null);

    /// <summary>选目录，取消返回 null。</summary>
    string? PickFolder(string title, string? initialDirectory = null);

    /// <summary>用系统默认程序打开文件。</summary>
    bool OpenFile(string path);

    /// <summary>在资源管理器里选中文件。</summary>
    bool RevealInFolder(string path);
}

/// <summary>无桌面能力时的空实现：所有操作都安全地失败。</summary>
public sealed class NullPlatformFiles : IPlatformFiles
{
    public bool Available => false;

    public string? PickFile(string title, string? filter = null) => null;

    public string? PickFolder(string title, string? initialDirectory = null) => null;

    public bool OpenFile(string path) => false;

    public bool RevealInFolder(string path) => false;
}
