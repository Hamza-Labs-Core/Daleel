using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Daleel.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class DynamicModerationRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AutoRating",
                table: "FilteredContentLogs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AutoReviewNote",
                table: "FilteredContentLogs",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "AutoReviewedAt",
                table: "FilteredContentLogs",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ModerationRules",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Category = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Term = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Language = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    Reason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    Source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                    ResolvedAt = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ModerationRules", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FilteredContentLogs_AutoReviewedAt",
                table: "FilteredContentLogs",
                column: "AutoReviewedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ModerationRules_Category_Term_Language",
                table: "ModerationRules",
                columns: new[] { "Category", "Term", "Language" });

            migrationBuilder.CreateIndex(
                name: "IX_ModerationRules_Status",
                table: "ModerationRules",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ModerationRules");

            migrationBuilder.DropIndex(
                name: "IX_FilteredContentLogs_AutoReviewedAt",
                table: "FilteredContentLogs");

            migrationBuilder.DropColumn(
                name: "AutoRating",
                table: "FilteredContentLogs");

            migrationBuilder.DropColumn(
                name: "AutoReviewNote",
                table: "FilteredContentLogs");

            migrationBuilder.DropColumn(
                name: "AutoReviewedAt",
                table: "FilteredContentLogs");
        }
    }
}
