using System.IO;
using System.Text;

namespace SDemo.App.Planner.Host;

/// <summary>
/// 最小文件日志：%LOCALAPPDATA%\SDemo\logs\planner\yyyy-MM-dd.log。
/// 只记录启动、宿主回调、MCP 错误这类排障需要的信息；业务数据不进日志。
/// </summary>
internal static class PlannerLog
{
    private static readonly object Gate = new();
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SDemo", "logs", "planner");

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message} :: {ex}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(LogDirectory);
                var path = Path.Combine(LogDirectory, DateTime.Now.ToString("yyyy-MM-dd") + ".log");
                var line = $"{DateTimeOffset.Now:O} [{level}] {message}{Environment.NewLine}";
                File.AppendAllText(path, line, Encoding.UTF8);
            }
        }
        catch (Exception)
        {
            // 日志失败不能影响应用主流程。
        }
    }
}
