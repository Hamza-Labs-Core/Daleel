using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Daleel.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddBrandStoreProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Brands",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    NameKey = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    CountryOfOrigin = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    ReputationScore = table.Column<double>(type: "REAL", nullable: true),
                    Description = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    Pros = table.Column<string>(type: "TEXT", nullable: false),
                    Cons = table.Column<string>(type: "TEXT", nullable: false),
                    PopularModels = table.Column<string>(type: "TEXT", nullable: false),
                    PriceRange = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Website = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    LastRefreshed = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Brands", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Stores",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    NameKey = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Location = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    Type = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Website = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    BrandsCarried = table.Column<string>(type: "TEXT", nullable: false),
                    Rating = table.Column<double>(type: "REAL", nullable: true),
                    LastRefreshed = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Stores", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Brands_LastRefreshed",
                table: "Brands",
                column: "LastRefreshed");

            migrationBuilder.CreateIndex(
                name: "IX_Brands_NameKey",
                table: "Brands",
                column: "NameKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Stores_LastRefreshed",
                table: "Stores",
                column: "LastRefreshed");

            migrationBuilder.CreateIndex(
                name: "IX_Stores_NameKey",
                table: "Stores",
                column: "NameKey",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Brands");

            migrationBuilder.DropTable(
                name: "Stores");
        }
    }
}
