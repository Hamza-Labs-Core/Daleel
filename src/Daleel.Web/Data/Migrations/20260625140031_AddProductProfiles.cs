using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Daleel.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddProductProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProductProfiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    NameKey = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Brand = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Model = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Details = table.Column<string>(type: "TEXT", maxLength: 8000, nullable: true),
                    SourceUrl = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    LastRefreshed = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductProfiles", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProductProfiles_LastRefreshed",
                table: "ProductProfiles",
                column: "LastRefreshed");

            migrationBuilder.CreateIndex(
                name: "IX_ProductProfiles_NameKey",
                table: "ProductProfiles",
                column: "NameKey",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProductProfiles");
        }
    }
}
