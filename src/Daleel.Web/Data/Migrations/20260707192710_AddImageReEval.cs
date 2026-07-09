using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Daleel.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddImageReEval : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ImageModerationLogs_SearchJobId_ImageUrl",
                table: "ImageModerationLogs");

            migrationBuilder.AddColumn<long>(
                name: "ReEvalRequestedAt",
                table: "ImageModerationLogs",
                type: "bigint",
                nullable: true);

            // The table becomes a REGISTRY (one row per distinct URL). Before the unique(ImageUrl) index:
            // (1) drop junk non-http rows the pre-fix extraction produced (e.g. the literal "null"), and
            // (2) collapse any duplicate URLs to the newest row — so the unique index can be created.
            migrationBuilder.Sql(@"DELETE FROM ""ImageModerationLogs"" WHERE ""ImageUrl"" NOT LIKE 'http%';");
            migrationBuilder.Sql(@"DELETE FROM ""ImageModerationLogs"" a USING ""ImageModerationLogs"" b " +
                @"WHERE a.""ImageUrl"" = b.""ImageUrl"" AND a.""Id"" < b.""Id"";");

            migrationBuilder.CreateIndex(
                name: "IX_ImageModerationLogs_ImageUrl",
                table: "ImageModerationLogs",
                column: "ImageUrl",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ImageModerationLogs_ReEvalRequestedAt",
                table: "ImageModerationLogs",
                column: "ReEvalRequestedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ImageModerationLogs_ImageUrl",
                table: "ImageModerationLogs");

            migrationBuilder.DropIndex(
                name: "IX_ImageModerationLogs_ReEvalRequestedAt",
                table: "ImageModerationLogs");

            migrationBuilder.DropColumn(
                name: "ReEvalRequestedAt",
                table: "ImageModerationLogs");

            migrationBuilder.CreateIndex(
                name: "IX_ImageModerationLogs_SearchJobId_ImageUrl",
                table: "ImageModerationLogs",
                columns: new[] { "SearchJobId", "ImageUrl" });
        }
    }
}
