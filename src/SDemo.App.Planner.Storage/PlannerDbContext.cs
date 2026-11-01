using System.IO;
using Microsoft.EntityFrameworkCore;

namespace SDemo.App.Planner.Storage;

/// <summary>
/// 一个应用一个库（docs/requirements.md §1）。表名与列名固定为 snake_case，
/// 便于排障与 P5 的零服务器导出；不要用 EF 默认的 PascalCase。
/// </summary>
public sealed class PlannerDbContext(DbContextOptions<PlannerDbContext> options) : DbContext(options)
{
    public DbSet<TodoRow> Todos => Set<TodoRow>();

    public DbSet<EventRow> Events => Set<EventRow>();

    public DbSet<ListRow> Lists => Set<ListRow>();

    public DbSet<TagRow> Tags => Set<TagRow>();

    public DbSet<TodoTagRow> TodoTags => Set<TodoTagRow>();

    public DbSet<LinkRow> Links => Set<LinkRow>();

    public DbSet<DraftRow> Drafts => Set<DraftRow>();

    public DbSet<DraftItemRow> DraftItems => Set<DraftItemRow>();

    public DbSet<OutboxRow> Outbox => Set<OutboxRow>();

    public DbSet<OpLogRow> OpLog => Set<OpLogRow>();

    public DbSet<McpAuditRow> McpAudit => Set<McpAuditRow>();

    public DbSet<IdempotencyRow> Idempotency => Set<IdempotencyRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TodoRow>(entity =>
        {
            entity.ToTable("todo");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id").HasMaxLength(64);
            entity.Property(x => x.Title).HasColumnName("title").HasMaxLength(200).IsRequired();
            entity.Property(x => x.Notes).HasColumnName("notes").HasMaxLength(4000);
            entity.Property(x => x.Status).HasColumnName("status").HasMaxLength(16).IsRequired();
            entity.Property(x => x.Priority).HasColumnName("priority");
            entity.Property(x => x.DueAt).HasColumnName("due_at").HasConversion(UtcConverter);
            entity.Property(x => x.CompletedAt).HasColumnName("completed_at").HasConversion(UtcConverter);
            entity.Property(x => x.ListId).HasColumnName("list_id").HasMaxLength(64);
            entity.Property(x => x.Revision).HasColumnName("revision");
            entity.Property(x => x.Source).HasColumnName("source").HasMaxLength(16).IsRequired();
            entity.Property(x => x.SourceSessionId).HasColumnName("source_session_id").HasMaxLength(128);
            entity.Property(x => x.SourceExcerpt).HasColumnName("source_excerpt").HasMaxLength(1024);
            entity.Property(x => x.CreatedAt).HasColumnName("created_at").HasConversion(UtcConverter);
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasConversion(UtcConverter);
            entity.Property(x => x.DeletedAt).HasColumnName("deleted_at").HasConversion(UtcConverter);
            entity.Property(x => x.RestorableUntil).HasColumnName("restorable_until").HasConversion(UtcConverter);
            entity.Property(x => x.FtsRowId).HasColumnName("fts_row_id");
            entity.HasIndex(x => x.DueAt);
            entity.HasIndex(x => x.Status);
            entity.HasIndex(x => x.ListId);
            entity.HasIndex(x => x.DeletedAt);
        });

        modelBuilder.Entity<EventRow>(entity =>
        {
            entity.ToTable("event");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id").HasMaxLength(64);
            entity.Property(x => x.Title).HasColumnName("title").HasMaxLength(200).IsRequired();
            entity.Property(x => x.Notes).HasColumnName("notes").HasMaxLength(4000);
            entity.Property(x => x.StartAt).HasColumnName("start_at").HasConversion(UtcRequiredConverter).IsRequired();
            entity.Property(x => x.EndAt).HasColumnName("end_at").HasConversion(UtcRequiredConverter).IsRequired();
            entity.Property(x => x.Location).HasColumnName("location").HasMaxLength(256);
            entity.Property(x => x.ParticipantsJson).HasColumnName("participants_json");
            entity.Property(x => x.LinkedTodoId).HasColumnName("linked_todo_id").HasMaxLength(64);
            entity.Property(x => x.ListId).HasColumnName("list_id").HasMaxLength(64);
            entity.Property(x => x.Revision).HasColumnName("revision");
            entity.Property(x => x.Source).HasColumnName("source").HasMaxLength(16).IsRequired();
            entity.Property(x => x.CreatedAt).HasColumnName("created_at").HasConversion(UtcConverter);
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasConversion(UtcConverter);
            entity.Property(x => x.DeletedAt).HasColumnName("deleted_at").HasConversion(UtcConverter);
            entity.Property(x => x.RestorableUntil).HasColumnName("restorable_until").HasConversion(UtcConverter);
            entity.HasIndex(x => x.StartAt);
            entity.HasIndex(x => x.DeletedAt);
        });

        modelBuilder.Entity<ListRow>(entity =>
        {
            entity.ToTable("list");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id").HasMaxLength(64);
            entity.Property(x => x.Name).HasColumnName("name").HasMaxLength(100).IsRequired();
            entity.Property(x => x.Color).HasColumnName("color").HasMaxLength(32);
            entity.Property(x => x.IsDefault).HasColumnName("is_default");
            entity.Property(x => x.Sort).HasColumnName("sort");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at").HasConversion(UtcConverter);
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasConversion(UtcConverter);
        });

        modelBuilder.Entity<TagRow>(entity =>
        {
            entity.ToTable("tag");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id").HasMaxLength(64);
            entity.Property(x => x.Name).HasColumnName("name").HasMaxLength(32).IsRequired();
            entity.Property(x => x.CreatedAt).HasColumnName("created_at").HasConversion(UtcConverter);
            entity.HasIndex(x => x.Name).IsUnique();
        });

        modelBuilder.Entity<TodoTagRow>(entity =>
        {
            entity.ToTable("todo_tag");
            entity.HasKey(x => new { x.TodoId, x.TagId });
            entity.Property(x => x.TodoId).HasColumnName("todo_id").HasMaxLength(64);
            entity.Property(x => x.TagId).HasColumnName("tag_id").HasMaxLength(64);
            entity.HasIndex(x => x.TodoId);
        });

        modelBuilder.Entity<LinkRow>(entity =>
        {
            entity.ToTable("link");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id").HasMaxLength(64);
            entity.Property(x => x.OwnerType).HasColumnName("owner_type").HasMaxLength(16).IsRequired();
            entity.Property(x => x.OwnerId).HasColumnName("owner_id").HasMaxLength(64).IsRequired();
            entity.Property(x => x.Uri).HasColumnName("uri").HasMaxLength(2048).IsRequired();
            entity.Property(x => x.CreatedAt).HasColumnName("created_at").HasConversion(UtcConverter);
            entity.HasIndex(x => new { x.OwnerType, x.OwnerId });
        });

        modelBuilder.Entity<DraftRow>(entity =>
        {
            entity.ToTable("draft");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id").HasMaxLength(64);
            entity.Property(x => x.Status).HasColumnName("status").HasMaxLength(16).IsRequired();
            entity.Property(x => x.Source).HasColumnName("source").HasMaxLength(16).IsRequired();
            entity.Property(x => x.SourceSessionId).HasColumnName("source_session_id").HasMaxLength(128);
            entity.Property(x => x.ListId).HasColumnName("list_id").HasMaxLength(64);
            entity.Property(x => x.CreatedAt).HasColumnName("created_at").HasConversion(UtcConverter);
            entity.Property(x => x.AcceptedAt).HasColumnName("accepted_at").HasConversion(UtcConverter);
            entity.HasIndex(x => x.Status);
        });

        modelBuilder.Entity<DraftItemRow>(entity =>
        {
            entity.ToTable("draft_item");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id").HasMaxLength(64);
            entity.Property(x => x.DraftId).HasColumnName("draft_id").HasMaxLength(64).IsRequired();
            entity.Property(x => x.Title).HasColumnName("title").HasMaxLength(200).IsRequired();
            entity.Property(x => x.Notes).HasColumnName("notes").HasMaxLength(4000);
            entity.Property(x => x.DueAt).HasColumnName("due_at").HasConversion(UtcConverter);
            entity.Property(x => x.Priority).HasColumnName("priority");
            entity.Property(x => x.Excerpt).HasColumnName("excerpt").HasMaxLength(1024).IsRequired();
            entity.Property(x => x.SuspectDuplicateOf).HasColumnName("suspect_duplicate_of").HasMaxLength(64);
            entity.Property(x => x.Sort).HasColumnName("sort");
            entity.Property(x => x.Decision).HasColumnName("decision").HasMaxLength(16).IsRequired();
            entity.HasIndex(x => x.DraftId);
        });

        modelBuilder.Entity<OutboxRow>(entity =>
        {
            entity.ToTable("outbox");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(x => x.EntityType).HasColumnName("entity_type").HasMaxLength(16).IsRequired();
            entity.Property(x => x.EntityId).HasColumnName("entity_id").HasMaxLength(64).IsRequired();
            entity.Property(x => x.Op).HasColumnName("op").HasMaxLength(16).IsRequired();
            entity.Property(x => x.Revision).HasColumnName("revision");
            entity.Property(x => x.PayloadJson).HasColumnName("payload_json").IsRequired();
            entity.Property(x => x.CreatedAt).HasColumnName("created_at").HasConversion(UtcConverter);
            entity.Property(x => x.SyncedAt).HasColumnName("synced_at").HasConversion(UtcConverter);
            entity.HasIndex(x => x.CreatedAt);
        });

        modelBuilder.Entity<OpLogRow>(entity =>
        {
            entity.ToTable("op_log");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(x => x.EntityType).HasColumnName("entity_type").HasMaxLength(16).IsRequired();
            entity.Property(x => x.EntityId).HasColumnName("entity_id").HasMaxLength(64).IsRequired();
            entity.Property(x => x.Op).HasColumnName("op").HasMaxLength(16).IsRequired();
            entity.Property(x => x.BeforeJson).HasColumnName("before_json");
            entity.Property(x => x.AfterJson).HasColumnName("after_json");
            entity.Property(x => x.Actor).HasColumnName("actor").HasMaxLength(16).IsRequired();
            entity.Property(x => x.RequestId).HasColumnName("request_id").HasMaxLength(64);
            entity.Property(x => x.Reason).HasColumnName("reason").HasMaxLength(256);
            entity.Property(x => x.CreatedAt).HasColumnName("created_at").HasConversion(UtcConverter);
            entity.HasIndex(x => new { x.EntityType, x.EntityId });
        });

        modelBuilder.Entity<McpAuditRow>(entity =>
        {
            entity.ToTable("mcp_audit");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(x => x.Tool).HasColumnName("tool").HasMaxLength(64).IsRequired();
            entity.Property(x => x.ParamsSummary).HasColumnName("params_summary").HasMaxLength(512).IsRequired();
            entity.Property(x => x.ItemCount).HasColumnName("item_count");
            entity.Property(x => x.ResultCode).HasColumnName("result_code").HasMaxLength(32).IsRequired();
            entity.Property(x => x.DurationMs).HasColumnName("duration_ms");
            entity.Property(x => x.RequestId).HasColumnName("request_id").HasMaxLength(64);
            entity.Property(x => x.CreatedAt).HasColumnName("created_at").HasConversion(UtcConverter);
            entity.HasIndex(x => x.Tool);
        });

        modelBuilder.Entity<IdempotencyRow>(entity =>
        {
            entity.ToTable("idempotency");
            entity.HasKey(x => x.ClientRequestId);
            entity.Property(x => x.ClientRequestId).HasColumnName("client_request_id").HasMaxLength(128);
            entity.Property(x => x.ResponseJson).HasColumnName("response_json").IsRequired();
            entity.Property(x => x.CreatedAt).HasColumnName("created_at").HasConversion(UtcConverter);
            entity.HasIndex(x => x.CreatedAt);
        });
    }

    /// <summary>SQLite 读出的 DateTime 没有 Kind；统一标成 UTC，避免二次转换漂移。</summary>
    private static readonly Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTime?, DateTime?> UtcConverter =
        new(v => v, v => v == null ? null : DateTime.SpecifyKind(v.Value, DateTimeKind.Utc));

    private static readonly Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTime, DateTime> UtcRequiredConverter =
        new(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
}

/// <summary>
/// 设计时工厂：给 <c>dotnet ef migrations add</c> 用，落到临时库，不碰用户数据目录。
/// </summary>
public sealed class PlannerDesignTimeDbContextFactory : Microsoft.EntityFrameworkCore.Design.IDesignTimeDbContextFactory<PlannerDbContext>
{
    public PlannerDbContext CreateDbContext(string[] args)
    {
        var path = Path.Combine(Path.GetTempPath(), "sdemo-planner-design.db");
        var options = new DbContextOptionsBuilder<PlannerDbContext>()
            .UseSqlite($"Data Source={path}")
            .Options;
        return new PlannerDbContext(options);
    }
}
