using Markdig;

namespace SDemo.App.Planner.Web;

/// <summary>
/// 任务说明 / 完成说明 / 进展正文统一按 Markdown 渲染。
/// 禁用原始 HTML：说明里的 <c>&lt;script&gt;</c> 一类内容只作为文本显示，不会被当成页面结构执行。
/// </summary>
public static class MarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .UseSoftlineBreakAsHardlineBreak()
        .DisableHtml()
        .Build();

    public static string ToHtml(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        return Markdown.ToHtml(text.Replace("\r\n", "\n", StringComparison.Ordinal), Pipeline);
    }
}
