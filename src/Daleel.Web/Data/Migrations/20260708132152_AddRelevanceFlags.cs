using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Daleel.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRelevanceFlags : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RelevanceFlags",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Query = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    QueryKey = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    Target = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Geo = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    DedupKey = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    StableId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Brand = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Name = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    Reason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RelevanceFlags", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RelevanceFlags_CreatedAt",
                table: "RelevanceFlags",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_RelevanceFlags_QueryKey_DedupKey",
                table: "RelevanceFlags",
                columns: new[] { "QueryKey", "DedupKey" });

            migrationBuilder.CreateIndex(
                name: "IX_RelevanceFlags_UserHash_QueryKey_DedupKey",
                table: "RelevanceFlags",
                columns: new[] { "UserHash", "QueryKey", "DedupKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RelevanceFlags");
        }
    }
}
