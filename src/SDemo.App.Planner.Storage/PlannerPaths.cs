using System.IO;

namespace SDemo.App.Planner.Storage;

/// <summary>
/// 数据目录（docs/requirements.md §1）：<c>%APPDATA%\SDemo\Apps\sdemo.planner\</c>。
/// 与 DesktopAssembly 的 apps / runtime 目录分开：宿主永远不读这里。
/// </summary>
public static class PlannerPaths
{
    public const string AppId = "sdemo.planner";

    public static string DataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SDemo",
        "Apps",
        AppId);

    public static string DatabasePath => Path.Combine(DataDirectory, "planner.db");

    public static string ExportDirectory => Path.Combine(DataDirectory, "exports");

    /// <summary>附件默认目录；可在「设置 → 附件」改成其它盘，改完用「一键迁移」搬文件。</summary>
    public static string AttachmentsDirectory => Path.Combine(DataDirectory, "attachments");

    public static string LogDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SDemo",
        "logs",
        "planner");

    /// <summary>允许测试注入临时目录，避免测试碰真实用户数据。</summary>
    public static string ResolveDatabasePath(string? overridePath) =>
        string.IsNullOrWhiteSpace(overridePath) ? DatabasePath : overridePath;
}
