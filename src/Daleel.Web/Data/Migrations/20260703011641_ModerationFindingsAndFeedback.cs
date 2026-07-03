using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Daleel.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class ModerationFindingsAndFeedback : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Rule",
                table: "FilteredContentLogs",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(128)",
                oldMaxLength: 128,
                oldNullable: true);

            // Backfill: every pre-existing row was a deterministic keyword removal — confidence 1.0.
            migrationBuilder.AddColumn<double>(
                name: "Confidence",
                table: "FilteredContentLogs",
                type: "double precision",
                nullable: false,
                defaultValue: 1.0);

            migrationBuilder.AddColumn<string>(
                name: "ContentHash",
                table: "FilteredContentLogs",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DecisionSource",
                table: "FilteredContentLogs",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Field",
                table: "FilteredContentLogs",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ImageUrl",
                table: "FilteredContentLogs",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: true);

            // Backfill: pre-existing rows all represent removed items (image-strip findings are new).
            migrationBuilder.AddColumn<bool>(
                name: "ItemRemoved",
                table: "FilteredContentLogs",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<long>(
                name: "RatedAt",
                table: "FilteredContentLogs",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Rating",
                table: "FilteredContentLogs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceUrl",
                table: "FilteredContentLogs",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "WhitelistEntryId",
                table: "FilteredContentLogs",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ModerationWhitelist",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Key = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    MatchType = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Category = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Note = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    SourceLogId = table.Column<long>(type: "bigint", nullable: true),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ModerationWhitelist", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ModerationWhitelist_Key",
                table: "ModerationWhitelist",
                column: "Key");

            migrationBuilder.CreateIndex(
                name: "IX_ModerationWhitelist_SourceLogId",
                table: "ModerationWhitelist",
                column: "SourceLogId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ModerationWhitelist");

            migrationBuilder.DropColumn(
                name: "Confidence",
                table: "FilteredContentLogs");

            migrationBuilder.DropColumn(
                name: "ContentHash",
                table: "FilteredContentLogs");

            migrationBuilder.DropColumn(
                name: "DecisionSource",
                table: "FilteredContentLogs");

            migrationBuilder.DropColumn(
                name: "Field",
                table: "FilteredContentLogs");

            migrationBuilder.DropColumn(
                name: "ImageUrl",
                table: "FilteredContentLogs");

            migrationBuilder.DropColumn(
                name: "ItemRemoved",
                table: "FilteredContentLogs");

            migrationBuilder.DropColumn(
                name: "RatedAt",
                table: "FilteredContentLogs");

            migrationBuilder.DropColumn(
                name: "Rating",
                table: "FilteredContentLogs");

            migrationBuilder.DropColumn(
                name: "SourceUrl",
                table: "FilteredContentLogs");

            migrationBuilder.DropColumn(
                name: "WhitelistEntryId",
                table: "FilteredContentLogs");

            migrationBuilder.AlterColumn<string>(
                name: "Rule",
                table: "FilteredContentLogs",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(256)",
                oldMaxLength: 256,
                oldNullable: true);
        }
    }
}
