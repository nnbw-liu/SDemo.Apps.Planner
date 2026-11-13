using System.Security.Cryptography;
using System.Text;
using SDemo.App.Planner.Contracts;
using SDemo.App.Planner.Storage;

namespace SDemo.App.Planner.Core;

/// <summary>
/// 附件：任务 / 项目 / 迭代都可以挂文件。文件按 <c>{ownerType}/{ownerId}/{attachmentId}{ext}</c>
/// 存放在「设置 → 附件」指定的目录；目录改动后可用一键迁移把旧文件搬过来。
/// 单文件上限见 <see cref="PlannerConstants.MaxAttachmentBytes"/>。
/// </summary>
public sealed partial class PlannerService
{
    private const string AttachmentRootKey = "attachments.rootDir";
    private const string AttachmentPreviousRootKey = "attachments.previousRootDir";

    public IReadOnlyList<AttachmentDto> ListAttachments(string ownerType, string ownerId)
    {
        if (!AttachmentOwnerTypes.IsSupported(ownerType) || !IsSafeAttachmentSegment(ownerId))
        {
            return [];
        }

        return _store.QueryAttachments(ownerType, ownerId).Select(MapAttachment).ToList();
    }

    /// <summary>
    /// 把一个外部文件收进附件目录。超过 100 MB、源文件不存在、宿主对象不存在都直接拒绝。
    /// </summary>
    public AttachmentDto AddAttachment(string ownerType, string ownerId, string sourcePath)
    {
        EnsureAttachmentOwner(ownerType, ownerId);

        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            throw PlannerException.Validation("没有选择要添加的文件");
        }

        FileInfo source;
        try
        {
            source = new FileInfo(Path.GetFullPath(sourcePath));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw PlannerException.Validation("文件路径不合法");
        }

        if (!source.Exists)
        {
            throw PlannerException.Validation("文件不存在，可能已经被移动或删除");
        }

        if (source.Length > PlannerConstants.MaxAttachmentBytes)
        {
            throw PlannerException.Validation(
                $"单个附件不能超过 {FormatSize(PlannerConstants.MaxAttachmentBytes)}，这个文件是 {FormatSize(source.Length)}");
        }

        var fileName = SanitizeAttachmentFileName(source.Name);
        var extension = NormalizeAttachmentExtension(Path.GetExtension(fileName));
        var id = NewId("at");
        var relativePath = $"{ownerType}/{ownerId}/{id}{extension}";
        var root = AttachmentRoot();
        var destination = ResolveAttachmentFullPath(root, relativePath);
        var now = NowUtc();

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        try
        {
            CopyFile(source.FullName, destination);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw PlannerException.Validation($"保存附件失败：{ex.Message}");
        }

        var row = new AttachmentRow
        {
            Id = id,
            OwnerType = ownerType,
            OwnerId = ownerId,
            FileName = fileName,
            RelativePath = relativePath,
            Extension = extension,
            SizeBytes = source.Length,
            Sha256 = ComputeSha256(destination),
            CreatedAt = now,
            UpdatedAt = now,
        };

        try
        {
            _store.InsertAttachment(row, "manual");
        }
        catch
        {
            TryDeleteFile(destination);
            throw;
        }

        RaiseChanged();
        return MapAttachment(row);
    }

    public void DeleteAttachment(string id)
    {
        var row = _store.GetAttachment(id) ?? throw PlannerException.NotFound("附件不存在或已删除");
        var path = ResolveAttachmentFullPath(AttachmentRoot(), row.RelativePath);
        _store.DeleteAttachment(id, NowUtc(), "manual");
        TryDeleteFile(path);
        RaiseChanged();
    }

    /// <summary>桌面端打开附件用；文件不在当前目录（例如还没迁移）时返回 null。</summary>
    public string? ResolveAttachmentPath(string id)
    {
        var row = _store.GetAttachment(id);
        if (row is null)
        {
            return null;
        }

        var path = ResolveAttachmentFullPath(AttachmentRoot(), row.RelativePath);
        return File.Exists(path) ? path : null;
    }

    public AttachmentSettingsDto GetAttachmentSettings()
    {
        var root = AttachmentRoot();
        var previous = NullIfEmpty(_store.GetSetting(AttachmentPreviousRootKey));
        var rows = _store.QueryAllAttachments();
        var needsMigration = false;
        if (previous is not null && !SameDirectory(previous, root))
        {
            needsMigration = rows.Any(row =>
                !File.Exists(ResolveAttachmentFullPath(root, row.RelativePath))
                && File.Exists(ResolveAttachmentFullPath(previous, row.RelativePath)));
        }

        return new AttachmentSettingsDto
        {
            RootDirectory = root,
            DefaultDirectory = PlannerPaths.AttachmentsDirectory,
            PreviousRootDirectory = previous,
            FileCount = rows.Count,
            TotalBytes = rows.Sum(row => row.SizeBytes),
            MaxFileBytes = PlannerConstants.MaxAttachmentBytes,
            NeedsMigration = needsMigration,
        };
    }

    /// <summary>保存目录。改了目录且 <paramref name="input"/>.Migrate 时顺手搬迁已有文件。</summary>
    public AttachmentSettingsDto SaveAttachmentSettings(AttachmentSettingsInput input)
    {
        var root = ValidateAttachmentRoot(input.RootDirectory);
        try
        {
            Directory.CreateDirectory(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw PlannerException.Validation($"目录不可用：{ex.Message}");
        }

        var current = AttachmentRoot();
        var previous = NullIfEmpty(_store.GetSetting(AttachmentPreviousRootKey));
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AttachmentRootKey] = root,
        };

        if (!SameDirectory(current, root))
        {
            // 旧目录成为迁移源；迁移成功后会把 previous 指回新目录。
            values[AttachmentPreviousRootKey] = current;
        }
        else if (previous is not null && SameDirectory(previous, root))
        {
            values[AttachmentPreviousRootKey] = string.Empty;
        }

        _store.SetSettings(values, NowUtc());
        if (input.Migrate)
        {
            MigrateAttachments();
        }

        RaiseChanged();
        return GetAttachmentSettings();
    }

    /// <summary>
    /// 一键迁移：把 DB 里登记的附件从「上一次目录」搬到当前目录。
    /// 目标已存在算跳过；源缺失也跳过（在结果里体现），不删除任何源文件直到复制成功。
    /// </summary>
    public AttachmentMigrationResultDto MigrateAttachments()
    {
        var root = AttachmentRoot();
        var previous = NullIfEmpty(_store.GetSetting(AttachmentPreviousRootKey));
        if (previous is null || SameDirectory(previous, root))
        {
            return new AttachmentMigrationResultDto
            {
                Ok = true,
                Message = "附件已经都在当前目录，无需迁移",
                FromDirectory = previous,
                ToDirectory = root,
            };
        }

        var rows = _store.QueryAllAttachments();
        var moved = 0;
        var skipped = 0;
        long bytes = 0L;
        var failures = new List<string>();

        foreach (var row in rows)
        {
            var target = ResolveAttachmentFullPath(root, row.RelativePath);
            var origin = ResolveAttachmentFullPath(previous, row.RelativePath);

            if (File.Exists(target))
            {
                skipped++;
                continue;
            }

            if (!File.Exists(origin))
            {
                skipped++;
                failures.Add($"缺少源文件：{row.FileName}");
                continue;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                CopyFile(origin, target);
                TryDeleteFile(origin);
                moved++;
                bytes += row.SizeBytes;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures.Add($"{row.FileName}：{ex.Message}");
            }
        }

        var ok = failures.Count == 0;
        if (ok)
        {
            _store.SetSettings(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AttachmentPreviousRootKey] = root,
            }, NowUtc());
        }

        var message = moved == 0 && skipped == 0
            ? "没有需要迁移的附件"
            : $"已迁移 {moved} 个文件（{FormatSize(bytes)}）"
              + (skipped > 0 ? $"，跳过 {skipped} 个" : string.Empty)
              + (failures.Count > 0 ? $"；{failures.Count} 个失败：{string.Join("；", failures.Take(3))}" : string.Empty);

        RaiseChanged();
        return new AttachmentMigrationResultDto
        {
            Ok = ok,
            Message = message,
            FromDirectory = previous,
            ToDirectory = root,
            FilesMoved = moved,
            BytesMoved = bytes,
            FilesSkipped = skipped,
        };
    }

    // ── 内部 ──────────────────────────────────────────────────────────────

    private static AttachmentDto MapAttachment(AttachmentRow row) => new()
    {
        Id = row.Id,
        OwnerType = row.OwnerType,
        OwnerId = row.OwnerId,
        FileName = row.FileName,
        Extension = row.Extension,
        SizeBytes = row.SizeBytes,
        Sha256 = string.IsNullOrWhiteSpace(row.Sha256) ? null : row.Sha256,
        CreatedAt = new DateTimeOffset(DateTime.SpecifyKind(row.CreatedAt, DateTimeKind.Utc)),
    };

    private void EnsureAttachmentOwner(string ownerType, string ownerId)
    {
        if (!AttachmentOwnerTypes.IsSupported(ownerType))
        {
            throw PlannerException.Validation("附件只能挂在任务、项目或迭代上");
        }

        if (!IsSafeAttachmentSegment(ownerId))
        {
            throw PlannerException.Validation("附件所属对象 id 不合法");
        }

        var exists = ownerType switch
        {
            AttachmentOwnerTypes.Todo => _store.GetTodo(ownerId) is not null,
            AttachmentOwnerTypes.Project => _store.GetProject(ownerId) is not null,
            AttachmentOwnerTypes.Iteration => _store.GetIteration(ownerId) is not null,
            _ => false,
        };

        if (!exists)
        {
            throw PlannerException.NotFound("附件要挂的对象不存在或已删除");
        }
    }

    private string AttachmentRoot()
    {
        var configured = NullIfEmpty(_store.GetSetting(AttachmentRootKey));
        return configured is null ? PlannerPaths.AttachmentsDirectory : configured;
    }

    private static string ValidateAttachmentRoot(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw PlannerException.Validation("附件目录不能为空");
        }

        var trimmed = raw.Trim();
        if (!Path.IsPathFullyQualified(trimmed))
        {
            throw PlannerException.Validation("附件目录必须是绝对路径");
        }

        string full;
        try
        {
            full = Path.GetFullPath(trimmed);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw PlannerException.Validation("附件目录不是合法路径");
        }

        if (!Path.IsPathFullyQualified(full))
        {
            throw PlannerException.Validation("附件目录必须是绝对路径");
        }

        var root = Path.GetPathRoot(full);
        return string.Equals(full, root, StringComparison.OrdinalIgnoreCase)
            ? full
            : full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool SameDirectory(string a, string b) =>
        string.Equals(
            Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>把库里的相对路径拼到根目录下，并挡住 <c>..</c> 越界。</summary>
    private static string ResolveAttachmentFullPath(string root, string relativePath)
    {
        var segments = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment => segment is "." or ".."))
        {
            throw PlannerException.Validation("附件路径不合法");
        }

        var full = Path.GetFullPath(Path.Combine(root, Path.Combine(segments)));
        var rootFull = Path.GetFullPath(root);
        var prefix = rootFull.EndsWith(Path.DirectorySeparatorChar)
            ? rootFull
            : rootFull + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw PlannerException.Validation("附件路径越界");
        }

        return full;
    }

    private static bool IsSafeAttachmentSegment(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 64
        && value.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-');

    private static string SanitizeAttachmentFileName(string name)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            trimmed = "附件";
        }

        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(trimmed.Length);
        foreach (var ch in trimmed)
        {
            builder.Append(invalid.Contains(ch) || char.IsControl(ch) ? '_' : ch);
        }

        var clean = builder.ToString();
        if (clean.Length <= PlannerConstants.MaxAttachmentFileNameChars)
        {
            return clean;
        }

        var extension = Path.GetExtension(clean);
        var stem = Path.GetFileNameWithoutExtension(clean);
        var keep = Math.Max(1, PlannerConstants.MaxAttachmentFileNameChars - extension.Length);
        return new string(stem.Take(keep).ToArray()) + extension;
    }

    private static string NormalizeAttachmentExtension(string? extension)
    {
        var clean = new string((extension ?? string.Empty)
            .ToLowerInvariant()
            .Where(ch => char.IsAsciiLetterOrDigit(ch) || ch == '.')
            .ToArray());
        if (!clean.StartsWith('.'))
        {
            return string.Empty;
        }

        return clean.Length is > 1 and <= 32 ? clean : string.Empty;
    }

    private static void CopyFile(string source, string destination)
    {
        var temp = destination + ".part";
        TryDeleteFile(temp);
        File.Copy(source, temp, overwrite: true);
        File.Move(temp, destination, overwrite: true);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // 文件被占用时留到下次；元数据已经删掉了。
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
        {
            return $"{bytes / (1024d * 1024 * 1024):0.#} GB";
        }

        if (bytes >= 1024L * 1024)
        {
            return $"{bytes / (1024d * 1024):0.#} MB";
        }

        if (bytes >= 1024)
        {
            return $"{bytes / 1024d:0.#} KB";
        }

        return $"{bytes} B";
    }
}
