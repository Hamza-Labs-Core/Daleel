using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Daleel.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkContexts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WorkContexts",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SearchJobId = table.Column<int>(type: "integer", nullable: false),
                    Scope = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    FindingsJson = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    Synthesis = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    SynthesizedFindingCount = table.Column<int>(type: "integer", nullable: false),
                    SynthesisVersion = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                    SynthesizedAt = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkContexts", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkContexts_SearchJobId",
                table: "WorkContexts",
                column: "SearchJobId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkContexts_SearchJobId_Scope_Key",
                table: "WorkContexts",
                columns: new[] { "SearchJobId", "Scope", "Key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkContexts");
        }
    }
}
