using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SDemo.App.Planner.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AdoV6 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "external_id",
                table: "todo",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "external_rev",
                table: "todo",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "external_source",
                table: "todo",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "external_url",
                table: "todo",
                type: "TEXT",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "external_id",
                table: "project",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "external_source",
                table: "project",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "external_url",
                table: "project",
                type: "TEXT",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "external_id",
                table: "iteration",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "external_source",
                table: "iteration",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "external_url",
                table: "iteration",
                type: "TEXT",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_todo_external_source_external_id",
                table: "todo",
                columns: new[] { "external_source", "external_id" });

            migrationBuilder.CreateIndex(
                name: "IX_project_external_source_external_id",
                table: "project",
                columns: new[] { "external_source", "external_id" });

            migrationBuilder.CreateIndex(
                name: "IX_iteration_external_source_external_id",
                table: "iteration",
                columns: new[] { "external_source", "external_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_todo_external_source_external_id",
                table: "todo");

            migrationBuilder.DropIndex(
                name: "IX_project_external_source_external_id",
                table: "project");

            migrationBuilder.DropIndex(
                name: "IX_iteration_external_source_external_id",
                table: "iteration");

            migrationBuilder.DropColumn(
                name: "external_id",
                table: "todo");

            migrationBuilder.DropColumn(
                name: "external_rev",
                table: "todo");

            migrationBuilder.DropColumn(
                name: "external_source",
                table: "todo");

            migrationBuilder.DropColumn(
                name: "external_url",
                table: "todo");

            migrationBuilder.DropColumn(
                name: "external_id",
                table: "project");

            migrationBuilder.DropColumn(
                name: "external_source",
                table: "project");

            migrationBuilder.DropColumn(
                name: "external_url",
                table: "project");

            migrationBuilder.DropColumn(
                name: "external_id",
                table: "iteration");

            migrationBuilder.DropColumn(
                name: "external_source",
                table: "iteration");

            migrationBuilder.DropColumn(
                name: "external_url",
                table: "iteration");
        }
    }
}
