using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SDemo.App.Planner.Storage.Migrations
{
    /// <inheritdoc />
    public partial class ProjectGroupsAndSettingsV5 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "app_setting",
                columns: table => new
                {
                    key = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    value = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_app_setting", x => x.key);
                });

            migrationBuilder.CreateTable(
                name: "project_group",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    color = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    sort = table.Column<int>(type: "INTEGER", nullable: false),
                    revision = table.Column<int>(type: "INTEGER", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    deleted_at = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_project_group", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "project_group_member",
                columns: table => new
                {
                    project_group_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    project_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_project_group_member", x => new { x.project_group_id, x.project_id });
                });

            migrationBuilder.CreateIndex(
                name: "IX_project_group_deleted_at",
                table: "project_group",
                column: "deleted_at");

            migrationBuilder.CreateIndex(
                name: "IX_project_group_member_project_id",
                table: "project_group_member",
                column: "project_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "app_setting");

            migrationBuilder.DropTable(
                name: "project_group");

            migrationBuilder.DropTable(
                name: "project_group_member");
        }
    }
}
