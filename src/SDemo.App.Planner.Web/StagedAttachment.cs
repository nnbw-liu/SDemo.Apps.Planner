namespace SDemo.App.Planner.Web;

/// <summary>
/// 新建任务时还没拿到任务 id，选中的文件先落到临时目录；保存成功后由 <c>TaskEditor</c>
/// 调用 <c>PlannerService.AddAttachment</c> 收进附件目录。取消 / 关闭时删掉临时文件。
/// </summary>
public sealed record StagedAttachment(string FileName, string TempPath, long SizeBytes)
{
    public void DeleteFile()
    {
        try
        {
            if (File.Exists(TempPath))
            {
                File.Delete(TempPath);
            }

            var directory = Path.GetDirectoryName(TempPath);
            if (directory is not null && Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: false);
            }
        }
        catch (IOException)
        {
            // 临时文件删不掉就留给系统清理，不影响任务保存。
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
