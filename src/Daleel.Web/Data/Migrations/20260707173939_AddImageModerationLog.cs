using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Daleel.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddImageModerationLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ImageModerationLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SearchJobId = table.Column<int>(type: "integer", nullable: true),
                    Query = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Geo = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ImageUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    ItemName = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    ItemKind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Decision = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Category = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Score = table.Column<double>(type: "double precision", nullable: true),
                    Reason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    DecisionSource = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImageModerationLogs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ImageModerationLogs_CreatedAt",
                table: "ImageModerationLogs",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ImageModerationLogs_Decision_CreatedAt",
                table: "ImageModerationLogs",
                columns: new[] { "Decision", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ImageModerationLogs_SearchJobId_ImageUrl",
                table: "ImageModerationLogs",
                columns: new[] { "SearchJobId", "ImageUrl" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ImageModerationLogs");
        }
    }
}
