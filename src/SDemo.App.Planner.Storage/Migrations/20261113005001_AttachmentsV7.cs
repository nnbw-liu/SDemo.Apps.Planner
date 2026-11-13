using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SDemo.App.Planner.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AttachmentsV7 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "attachment",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    owner_type = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    owner_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    file_name = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    relative_path = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    extension = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    size_bytes = table.Column<long>(type: "INTEGER", nullable: false),
                    sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    deleted_at = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_attachment", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_attachment_deleted_at",
                table: "attachment",
                column: "deleted_at");

            migrationBuilder.CreateIndex(
                name: "IX_attachment_owner_type_owner_id",
                table: "attachment",
                columns: new[] { "owner_type", "owner_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "attachment");
        }
    }
}
