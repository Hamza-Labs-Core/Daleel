using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Daleel.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFilteredContentLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FilteredContentLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Query = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    Geo = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Category = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Rule = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Content = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FilteredContentLogs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FilteredContentLogs_Category_CreatedAt",
                table: "FilteredContentLogs",
                columns: new[] { "Category", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_FilteredContentLogs_CreatedAt",
                table: "FilteredContentLogs",
                column: "CreatedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FilteredContentLogs");
        }
    }
}
