using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Daleel.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class EntityDedupIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Category",
                table: "EntityRecords",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IdentityKey",
                table: "EntityRecords",
                type: "character varying(600)",
                maxLength: 600,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MergedIntoId",
                table: "EntityRecords",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "EntityMergeLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SurvivorId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    LoserId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SurvivorName = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    LoserName = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    Evidence = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    DryRun = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EntityMergeLogs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EntityRecords_IdentityKey",
                table: "EntityRecords",
                column: "IdentityKey");

            migrationBuilder.CreateIndex(
                name: "IX_EntityRecords_MergedIntoId",
                table: "EntityRecords",
                column: "MergedIntoId");

            migrationBuilder.CreateIndex(
                name: "IX_EntityMergeLogs_CreatedAt",
                table: "EntityMergeLogs",
                column: "CreatedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EntityMergeLogs");

            migrationBuilder.DropIndex(
                name: "IX_EntityRecords_IdentityKey",
                table: "EntityRecords");

            migrationBuilder.DropIndex(
                name: "IX_EntityRecords_MergedIntoId",
                table: "EntityRecords");

            migrationBuilder.DropColumn(
                name: "IdentityKey",
                table: "EntityRecords");

            migrationBuilder.DropColumn(
                name: "MergedIntoId",
                table: "EntityRecords");

            migrationBuilder.AlterColumn<string>(
                name: "Category",
                table: "EntityRecords",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200,
                oldNullable: true);
        }
    }
}
