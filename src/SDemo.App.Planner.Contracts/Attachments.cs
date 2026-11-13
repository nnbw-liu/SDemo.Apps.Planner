namespace SDemo.App.Planner.Contracts;

/// <summary>
/// 附件宿主类型。附件跟任务 / 项目 / 迭代走，不单独有「附件库」页面，
/// 但文件实体集中存放在可配置目录里（设置 → 附件），支持整体迁移。
/// </summary>
public static class AttachmentOwnerTypes
{
    public const string Todo = "todo";
    public const string Project = "project";
    public const string Iteration = "iteration";

    public static bool IsSupported(string? ownerType) =>
        ownerType is Todo or Project or Iteration;
}

/// <summary>附件元数据。文件本体落在 <see cref="AttachmentSettingsDto.RootDirectory"/> 下的相对路径里。</summary>
public sealed record AttachmentDto
{
    public required string Id { get; init; }

    public required string OwnerType { get; init; }

    public required string OwnerId { get; init; }

    /// <summary>用户看到 / 另存时使用的原始文件名（不含目录）。</summary>
    public required string FileName { get; init; }

    /// <summary>含点的小写扩展名，例如 <c>.pdf</c>；无扩展名时为空串。</summary>
    public required string Extension { get; init; }

    public long SizeBytes { get; init; }

    public string? Sha256 { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>附件存储设置。目录改动后旧文件留在原目录，直到执行迁移。</summary>
public sealed record AttachmentSettingsDto
{
    public required string RootDirectory { get; init; }

    /// <summary>默认目录：<c>%APPDATA%\SDemo\Apps\sdemo.planner\attachments</c>。</summary>
    public required string DefaultDirectory { get; init; }

    /// <summary>上一次使用的目录；与当前目录不同时说明还有文件没迁过来。</summary>
    public string? PreviousRootDirectory { get; init; }

    public int FileCount { get; init; }

    public long TotalBytes { get; init; }

    public long MaxFileBytes { get; init; }

    /// <summary>旧目录里仍有本库登记的附件文件，建议执行「一键迁移」。</summary>
    public bool NeedsMigration { get; init; }
}

public sealed record AttachmentSettingsInput
{
    public string RootDirectory { get; init; } = string.Empty;

    /// <summary>保存后立即把旧目录的附件迁到新目录。</summary>
    public bool Migrate { get; init; }
}

public sealed record AttachmentMigrationResultDto
{
    public bool Ok { get; init; }

    public required string Message { get; init; }

    public string? FromDirectory { get; init; }

    public string? ToDirectory { get; init; }

    public int FilesMoved { get; init; }

    public long BytesMoved { get; init; }

    public int FilesSkipped { get; init; }
}
