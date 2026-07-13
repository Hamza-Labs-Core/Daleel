using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Daleel.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSiteSearchProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SiteSearchProfiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Domain = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SearchUrlTemplate = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    DiscoveredVia = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    LastSuccessAt = table.Column<long>(type: "bigint", nullable: false),
                    ConsecutiveFailures = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SiteSearchProfiles", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SiteSearchProfiles_Domain",
                table: "SiteSearchProfiles",
                column: "Domain",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SiteSearchProfiles");
        }
    }
}
