using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Daleel.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSmartProductIdentification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "DiscoveredAt",
                table: "BrandModels",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "FinalSpecsJson",
                table: "BrandModels",
                type: "TEXT",
                maxLength: 8000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FinalSpecsR2Url",
                table: "BrandModels",
                type: "TEXT",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ImageR2Urls",
                table: "BrandModels",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "IsDiscontinued",
                table: "BrandModels",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "RegionalAliases",
                table: "BrandModels",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "VisionMatchCaches",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    StoreImageHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    BrandModelId = table.Column<int>(type: "INTEGER", nullable: false),
                    Confidence = table.Column<double>(type: "REAL", nullable: false),
                    MatchedModelName = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    MatchedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VisionMatchCaches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VisionMatchCaches_BrandModels_BrandModelId",
                        column: x => x.BrandModelId,
                        principalTable: "BrandModels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VisionMatchCaches_BrandModelId",
                table: "VisionMatchCaches",
                column: "BrandModelId");

            migrationBuilder.CreateIndex(
                name: "IX_VisionMatchCaches_StoreImageHash_BrandModelId",
                table: "VisionMatchCaches",
                columns: new[] { "StoreImageHash", "BrandModelId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "VisionMatchCaches");

            migrationBuilder.DropColumn(
                name: "DiscoveredAt",
                table: "BrandModels");

            migrationBuilder.DropColumn(
                name: "FinalSpecsJson",
                table: "BrandModels");

            migrationBuilder.DropColumn(
                name: "FinalSpecsR2Url",
                table: "BrandModels");

            migrationBuilder.DropColumn(
                name: "ImageR2Urls",
                table: "BrandModels");

            migrationBuilder.DropColumn(
                name: "IsDiscontinued",
                table: "BrandModels");

            migrationBuilder.DropColumn(
                name: "RegionalAliases",
                table: "BrandModels");
        }
    }
}
