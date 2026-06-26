using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Daleel.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddBrandModelAndScrapedPrice : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BrandModels",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BrandId = table.Column<int>(type: "INTEGER", nullable: false),
                    ModelName = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    ModelKey = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Category = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    SpecsJson = table.Column<string>(type: "TEXT", maxLength: 8000, nullable: true),
                    ImageUrl = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    LocalPrice = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    GlobalPrice = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    Currency = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    IsAvailable = table.Column<bool>(type: "INTEGER", nullable: false),
                    SourceUrl = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    LastRefreshed = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BrandModels", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BrandModels_Brands_BrandId",
                        column: x => x.BrandId,
                        principalTable: "Brands",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ScrapedPrices",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProductName = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    ProductKey = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    StoreName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Price = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    Currency = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    SourceUrl = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    Provider = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ScrapedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScrapedPrices", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BrandModels_BrandId_ModelKey",
                table: "BrandModels",
                columns: new[] { "BrandId", "ModelKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BrandModels_LastRefreshed",
                table: "BrandModels",
                column: "LastRefreshed");

            migrationBuilder.CreateIndex(
                name: "IX_ScrapedPrices_ProductKey_ScrapedAt",
                table: "ScrapedPrices",
                columns: new[] { "ProductKey", "ScrapedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ScrapedPrices_ScrapedAt",
                table: "ScrapedPrices",
                column: "ScrapedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BrandModels");

            migrationBuilder.DropTable(
                name: "ScrapedPrices");
        }
    }
}
