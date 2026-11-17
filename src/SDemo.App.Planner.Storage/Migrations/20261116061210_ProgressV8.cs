using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SDemo.App.Planner.Storage.Migrations
{
    /// <inheritdoc />
    public partial class ProgressV8 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "todo_progress",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    todo_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    title = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    notes = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    percent = table.Column<int>(type: "INTEGER", nullable: true),
                    artifacts_json = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    occurred_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    revision = table.Column<int>(type: "INTEGER", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    deleted_at = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_todo_progress", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_todo_progress_deleted_at",
                table: "todo_progress",
                column: "deleted_at");

            migrationBuilder.CreateIndex(
                name: "IX_todo_progress_todo_id_occurred_at",
                table: "todo_progress",
                columns: new[] { "todo_id", "occurred_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "todo_progress");
        }
    }
}
