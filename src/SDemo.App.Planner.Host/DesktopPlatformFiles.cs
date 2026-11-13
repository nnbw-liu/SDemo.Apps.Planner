using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using SDemo.App.Planner.Web;

namespace SDemo.App.Planner.Host;

/// <summary>
/// 桌面壳的文件能力：WPF 原生对话框 + shell 打开。
/// 不要换成 WinForms 的 OpenFileDialog：Host 加了 UseWPF 后会和 WPF 的
/// Application/MessageBox 等类型冲突（CS0104）。
/// </summary>
public sealed class DesktopPlatformFiles : IPlatformFiles
{
    public bool Available => true;

    public string? PickFile(string title, string? filter = null)
    {
        var dialog = new OpenFileDialog
        {
            Title = string.IsNullOrWhiteSpace(title) ? "选择文件" : title,
            CheckFileExists = true,
            Multiselect = false,
            Filter = string.IsNullOrWhiteSpace(filter) ? "所有文件|*.*" : filter,
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? PickFolder(string title, string? initialDirectory = null)
    {
        var dialog = new OpenFolderDialog
        {
            Title = string.IsNullOrWhiteSpace(title) ? "选择目录" : title,
            Multiselect = false,
        };

        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    public bool OpenFile(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool RevealInFolder(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
            {
                UseShellExecute = true,
            });
            return true;
        }
        catch
        {
            return false;
        }
    }
}
