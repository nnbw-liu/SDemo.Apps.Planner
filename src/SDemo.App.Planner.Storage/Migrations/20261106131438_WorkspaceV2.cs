using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SDemo.App.Planner.Storage.Migrations
{
    /// <inheritdoc />
    public partial class WorkspaceV2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "iteration_id",
                table: "todo",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "kind",
                table: "todo",
                type: "TEXT",
                maxLength: 16,
                nullable: false,
                defaultValue: "task");

            migrationBuilder.AddColumn<string>(
                name: "parent_todo_id",
                table: "todo",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "project_id",
                table: "todo",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "sort",
                table: "todo",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "iteration",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    project_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    parent_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    level = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    goal = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    start_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    end_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    sort = table.Column<int>(type: "INTEGER", nullable: false),
                    revision = table.Column<int>(type: "INTEGER", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    deleted_at = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_iteration", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "project",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    color = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    start_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    target_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    sort = table.Column<int>(type: "INTEGER", nullable: false),
                    revision = table.Column<int>(type: "INTEGER", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    deleted_at = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_project", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "routine",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    title = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    emoji = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    notes = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    days_mask = table.Column<int>(type: "INTEGER", nullable: false),
                    time_of_day = table.Column<string>(type: "TEXT", maxLength: 5, nullable: true),
                    interval_minutes = table.Column<int>(type: "INTEGER", nullable: true),
                    active_end = table.Column<string>(type: "TEXT", maxLength: 5, nullable: true),
                    target_count = table.Column<int>(type: "INTEGER", nullable: false),
                    remind = table.Column<bool>(type: "INTEGER", nullable: false),
                    enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    sort = table.Column<int>(type: "INTEGER", nullable: false),
                    revision = table.Column<int>(type: "INTEGER", nullable: false),
                    last_fired_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    deleted_at = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_routine", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "routine_check",
                columns: table => new
                {
                    routine_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    local_date = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    count = table.Column<int>(type: "INTEGER", nullable: false),
                    last_completed_at = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_routine_check", x => new { x.routine_id, x.local_date });
                });

            migrationBuilder.CreateIndex(
                name: "IX_todo_iteration_id",
                table: "todo",
                column: "iteration_id");

            migrationBuilder.CreateIndex(
                name: "IX_todo_project_id",
                table: "todo",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "IX_iteration_deleted_at",
                table: "iteration",
                column: "deleted_at");

            migrationBuilder.CreateIndex(
                name: "IX_iteration_project_id",
                table: "iteration",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "IX_iteration_project_id_parent_id",
                table: "iteration",
                columns: new[] { "project_id", "parent_id" });

            migrationBuilder.CreateIndex(
                name: "IX_project_deleted_at",
                table: "project",
                column: "deleted_at");

            migrationBuilder.CreateIndex(
                name: "IX_project_status",
                table: "project",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "IX_routine_deleted_at",
                table: "routine",
                column: "deleted_at");

            migrationBuilder.CreateIndex(
                name: "IX_routine_enabled",
                table: "routine",
                column: "enabled");

            migrationBuilder.CreateIndex(
                name: "IX_routine_check_local_date",
                table: "routine_check",
                column: "local_date");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "iteration");

            migrationBuilder.DropTable(
                name: "project");

            migrationBuilder.DropTable(
                name: "routine");

            migrationBuilder.DropTable(
                name: "routine_check");

            migrationBuilder.DropIndex(
                name: "IX_todo_iteration_id",
                table: "todo");

            migrationBuilder.DropIndex(
                name: "IX_todo_project_id",
                table: "todo");

            migrationBuilder.DropColumn(
                name: "iteration_id",
                table: "todo");

            migrationBuilder.DropColumn(
                name: "kind",
                table: "todo");

            migrationBuilder.DropColumn(
                name: "parent_todo_id",
                table: "todo");

            migrationBuilder.DropColumn(
                name: "project_id",
                table: "todo");

            migrationBuilder.DropColumn(
                name: "sort",
                table: "todo");
        }
    }
}
