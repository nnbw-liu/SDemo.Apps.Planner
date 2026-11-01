using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SDemo.App.Planner.Storage.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "draft",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    source = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    source_session_id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    list_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    accepted_at = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_draft", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "draft_item",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    draft_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    title = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    notes = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    due_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    priority = table.Column<int>(type: "INTEGER", nullable: false),
                    excerpt = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    suspect_duplicate_of = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    sort = table.Column<int>(type: "INTEGER", nullable: false),
                    decision = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_draft_item", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "event",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    title = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    notes = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    start_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    end_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    location = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    participants_json = table.Column<string>(type: "TEXT", nullable: true),
                    linked_todo_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    list_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    revision = table.Column<int>(type: "INTEGER", nullable: false),
                    source = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    deleted_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    restorable_until = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_event", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "idempotency",
                columns: table => new
                {
                    client_request_id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    response_json = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_idempotency", x => x.client_request_id);
                });

            migrationBuilder.CreateTable(
                name: "link",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    owner_type = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    owner_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    uri = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_link", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "list",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    color = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    is_default = table.Column<bool>(type: "INTEGER", nullable: false),
                    sort = table.Column<int>(type: "INTEGER", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_list", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "mcp_audit",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    tool = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    params_summary = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    item_count = table.Column<int>(type: "INTEGER", nullable: false),
                    result_code = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    duration_ms = table.Column<long>(type: "INTEGER", nullable: false),
                    request_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mcp_audit", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "op_log",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    entity_type = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    entity_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    op = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    before_json = table.Column<string>(type: "TEXT", nullable: true),
                    after_json = table.Column<string>(type: "TEXT", nullable: true),
                    actor = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    request_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    reason = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_op_log", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "outbox",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    entity_type = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    entity_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    op = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    revision = table.Column<int>(type: "INTEGER", nullable: false),
                    payload_json = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    synced_at = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outbox", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "tag",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tag", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "todo",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    title = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    notes = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    priority = table.Column<int>(type: "INTEGER", nullable: false),
                    due_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    completed_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    list_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    revision = table.Column<int>(type: "INTEGER", nullable: false),
                    source = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    source_session_id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    source_excerpt = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    deleted_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    restorable_until = table.Column<DateTime>(type: "TEXT", nullable: true),
                    fts_row_id = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_todo", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "todo_tag",
                columns: table => new
                {
                    todo_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    tag_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_todo_tag", x => new { x.todo_id, x.tag_id });
                });

            migrationBuilder.CreateIndex(
                name: "IX_draft_status",
                table: "draft",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "IX_draft_item_draft_id",
                table: "draft_item",
                column: "draft_id");

            migrationBuilder.CreateIndex(
                name: "IX_event_deleted_at",
                table: "event",
                column: "deleted_at");

            migrationBuilder.CreateIndex(
                name: "IX_event_start_at",
                table: "event",
                column: "start_at");

            migrationBuilder.CreateIndex(
                name: "IX_idempotency_created_at",
                table: "idempotency",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "IX_link_owner_type_owner_id",
                table: "link",
                columns: new[] { "owner_type", "owner_id" });

            migrationBuilder.CreateIndex(
                name: "IX_mcp_audit_tool",
                table: "mcp_audit",
                column: "tool");

            migrationBuilder.CreateIndex(
                name: "IX_op_log_entity_type_entity_id",
                table: "op_log",
                columns: new[] { "entity_type", "entity_id" });

            migrationBuilder.CreateIndex(
                name: "IX_outbox_created_at",
                table: "outbox",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "IX_tag_name",
                table: "tag",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_todo_deleted_at",
                table: "todo",
                column: "deleted_at");

            migrationBuilder.CreateIndex(
                name: "IX_todo_due_at",
                table: "todo",
                column: "due_at");

            migrationBuilder.CreateIndex(
                name: "IX_todo_list_id",
                table: "todo",
                column: "list_id");

            migrationBuilder.CreateIndex(
                name: "IX_todo_status",
                table: "todo",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "IX_todo_tag_todo_id",
                table: "todo_tag",
                column: "todo_id");

            // FTS5 全文索引（PL-13）：索引与查询都在应用侧做 2-gram 预分词（见 TodoFts）。
            // 放在 EF 迁移里而不是 EnsureCreated，保证老库升级路径一致。
            migrationBuilder.Sql("CREATE VIRTUAL TABLE todo_fts USING fts5(todo_id UNINDEXED, terms, tokenize = 'unicode61');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TABLE IF EXISTS todo_fts;");

            migrationBuilder.DropTable(
                name: "draft");

            migrationBuilder.DropTable(
                name: "draft_item");

            migrationBuilder.DropTable(
                name: "event");

            migrationBuilder.DropTable(
                name: "idempotency");

            migrationBuilder.DropTable(
                name: "link");

            migrationBuilder.DropTable(
                name: "list");

            migrationBuilder.DropTable(
                name: "mcp_audit");

            migrationBuilder.DropTable(
                name: "op_log");

            migrationBuilder.DropTable(
                name: "outbox");

            migrationBuilder.DropTable(
                name: "tag");

            migrationBuilder.DropTable(
                name: "todo");

            migrationBuilder.DropTable(
                name: "todo_tag");
        }
    }
}
