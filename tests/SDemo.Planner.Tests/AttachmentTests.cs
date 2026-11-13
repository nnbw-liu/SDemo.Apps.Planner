using SDemo.App.Planner.Contracts;
using SDemo.App.Planner.Core;

namespace SDemo.Planner.Tests;

/// <summary>0.7.0：任务 / 项目 / 迭代附件——收集、100 MB 上限、目录切换与一键迁移。</summary>
public sealed class AttachmentTests
{
    [Fact]
    public void AddAttachment_CopiesFileAndListsIt()
    {
        using var host = new TestHost();
        UseTempRoot(host);
        var todo = host.Service.CreateTodos(
            [new TodoCreateInput { Title = "带附件的任务" }],
            WriteContext.Manual()).Items.Single();
        var source = TempFile("需求说明.txt", "hello attachment");

        var attachment = host.Service.AddAttachment(AttachmentOwnerTypes.Todo, todo.Id, source);

        Assert.Equal("需求说明.txt", attachment.FileName);
        Assert.Equal(".txt", attachment.Extension);
        Assert.Equal(16, attachment.SizeBytes);
        Assert.False(string.IsNullOrWhiteSpace(attachment.Sha256));

        var stored = host.Service.ResolveAttachmentPath(attachment.Id);
        Assert.NotNull(stored);
        Assert.True(File.Exists(stored));
        Assert.Equal("hello attachment", File.ReadAllText(stored!));
        Assert.False(string.Equals(source, stored, StringComparison.OrdinalIgnoreCase));

        var list = host.Service.ListAttachments(AttachmentOwnerTypes.Todo, todo.Id);
        Assert.Single(list);
        Assert.Equal(attachment.Id, list[0].Id);
    }

    [Fact]
    public void AddAttachment_RejectsFileOverLimit()
    {
        using var host = new TestHost();
        UseTempRoot(host);
        var project = host.Service.CreateProject(new ProjectInput { Name = "大文件项目" }, WriteContext.Manual());
        var source = TempFile("big.bin", string.Empty);
        using (var stream = new FileStream(source, FileMode.Open, FileAccess.Write))
        {
            stream.SetLength(PlannerConstants.MaxAttachmentBytes + 1);
        }

        var error = Assert.Throws<PlannerException>(
            () => host.Service.AddAttachment(AttachmentOwnerTypes.Project, project.Id, source));

        Assert.Contains("不能超过", error.Message, StringComparison.Ordinal);
        Assert.Empty(host.Service.ListAttachments(AttachmentOwnerTypes.Project, project.Id));
    }

    [Fact]
    public void AddAttachment_RequiresExistingOwner()
    {
        using var host = new TestHost();
        UseTempRoot(host);
        var source = TempFile("orphan.txt", "x");

        var error = Assert.Throws<PlannerException>(
            () => host.Service.AddAttachment(AttachmentOwnerTypes.Iteration, "it_missing", source));

        Assert.Contains("不存在", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddAttachment_CanHangOnIteration()
    {
        using var host = new TestHost();
        UseTempRoot(host);
        var project = host.Service.CreateProject(new ProjectInput { Name = "迭代附件项目" }, WriteContext.Manual());
        var iteration = host.Service.CreateIteration(
            new IterationInput { ProjectId = project.Id, Level = IterationLevel.Big, Name = "1.0" },
            WriteContext.Manual());
        var source = TempFile("原型.png", "png-bytes");

        var attachment = host.Service.AddAttachment(AttachmentOwnerTypes.Iteration, iteration.Id, source);

        Assert.Single(host.Service.ListAttachments(AttachmentOwnerTypes.Iteration, iteration.Id));
        Assert.NotNull(host.Service.ResolveAttachmentPath(attachment.Id));
    }

    [Fact]
    public void DeleteAttachment_RemovesRowAndFile()
    {
        using var host = new TestHost();
        UseTempRoot(host);
        var todo = host.Service.CreateTodos([new TodoCreateInput { Title = "删附件" }], WriteContext.Manual()).Items.Single();
        var source = TempFile("临时.txt", "bye");
        var attachment = host.Service.AddAttachment(AttachmentOwnerTypes.Todo, todo.Id, source);
        var stored = host.Service.ResolveAttachmentPath(attachment.Id)!;

        host.Service.DeleteAttachment(attachment.Id);

        Assert.Empty(host.Service.ListAttachments(AttachmentOwnerTypes.Todo, todo.Id));
        Assert.Null(host.Service.ResolveAttachmentPath(attachment.Id));
        Assert.False(File.Exists(stored));
    }

    [Fact]
    public void SaveAttachmentSettings_MigratesExistingFiles()
    {
        using var host = new TestHost();
        var originalRoot = UseTempRoot(host);
        var todo = host.Service.CreateTodos([new TodoCreateInput { Title = "迁移附件" }], WriteContext.Manual()).Items.Single();
        var attachment = host.Service.AddAttachment(AttachmentOwnerTypes.Todo, todo.Id, TempFile("方案.md", "# 方案"));
        var before = host.Service.ResolveAttachmentPath(attachment.Id)!;
        Assert.StartsWith(originalRoot, before, StringComparison.OrdinalIgnoreCase);

        var target = Path.Combine(Path.GetTempPath(), "sdemo-attach-target", Guid.NewGuid().ToString("N"));
        var saved = host.Service.SaveAttachmentSettings(new AttachmentSettingsInput { RootDirectory = target });

        Assert.Equal(target, saved.RootDirectory.TrimEnd(Path.DirectorySeparatorChar));
        Assert.True(saved.NeedsMigration);
        Assert.NotNull(saved.PreviousRootDirectory);

        var migration = host.Service.MigrateAttachments();

        Assert.True(migration.Ok);
        Assert.Equal(1, migration.FilesMoved);
        Assert.Equal(System.Text.Encoding.UTF8.GetByteCount("# 方案"), migration.BytesMoved);
        Assert.False(File.Exists(before));

        var after = host.Service.ResolveAttachmentPath(attachment.Id);
        Assert.NotNull(after);
        Assert.StartsWith(target, after, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(after));
        Assert.False(host.Service.GetAttachmentSettings().NeedsMigration);
    }

    [Fact]
    public void SaveAttachmentSettings_SaveAndMigrateInOneCall()
    {
        using var host = new TestHost();
        UseTempRoot(host);
        var project = host.Service.CreateProject(new ProjectInput { Name = "一次迁移" }, WriteContext.Manual());
        var attachment = host.Service.AddAttachment(
            AttachmentOwnerTypes.Project, project.Id, TempFile("预算.xlsx", "sheet"));
        var target = Path.Combine(Path.GetTempPath(), "sdemo-attach-target", Guid.NewGuid().ToString("N"));

        host.Service.SaveAttachmentSettings(new AttachmentSettingsInput { RootDirectory = target, Migrate = true });

        Assert.False(host.Service.GetAttachmentSettings().NeedsMigration);
        Assert.NotNull(host.Service.ResolveAttachmentPath(attachment.Id));
    }

    [Fact]
    public void SaveAttachmentSettings_RejectsRelativePath()
    {
        using var host = new TestHost();

        var error = Assert.Throws<PlannerException>(
            () => host.Service.SaveAttachmentSettings(new AttachmentSettingsInput { RootDirectory = "attachments" }));

        Assert.Contains("绝对路径", error.Message, StringComparison.Ordinal);
    }

    private static string UseTempRoot(TestHost host)
    {
        var root = Path.Combine(Path.GetTempPath(), "sdemo-attach-root", Guid.NewGuid().ToString("N"));
        host.Service.SaveAttachmentSettings(new AttachmentSettingsInput { RootDirectory = root });
        return root;
    }

    private static string TempFile(string name, string content)
    {
        var directory = Path.Combine(Path.GetTempPath(), "sdemo-attach-source", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, content);
        return path;
    }
}
