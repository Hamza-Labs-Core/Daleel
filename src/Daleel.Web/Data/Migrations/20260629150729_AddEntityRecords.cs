using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Daleel.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddEntityRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EntityRecords",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Intent = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    NameKey = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    Geo = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    SearchId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    BrandId = table.Column<int>(type: "integer", nullable: true),
                    StoreId = table.Column<int>(type: "integer", nullable: true),
                    ProductKey = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    ParentProductKey = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    R2Key = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    R2Url = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    LastRefreshed = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EntityRecords", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EntityRecords_Brands_BrandId",
                        column: x => x.BrandId,
                        principalTable: "Brands",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_EntityRecords_Stores_StoreId",
                        column: x => x.StoreId,
                        principalTable: "Stores",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EntityRecords_BrandId",
                table: "EntityRecords",
                column: "BrandId");

            migrationBuilder.CreateIndex(
                name: "IX_EntityRecords_Intent_NameKey",
                table: "EntityRecords",
                columns: new[] { "Intent", "NameKey" });

            migrationBuilder.CreateIndex(
                name: "IX_EntityRecords_LastRefreshed",
                table: "EntityRecords",
                column: "LastRefreshed");

            migrationBuilder.CreateIndex(
                name: "IX_EntityRecords_ProductKey",
                table: "EntityRecords",
                column: "ProductKey");

            migrationBuilder.CreateIndex(
                name: "IX_EntityRecords_SearchId",
                table: "EntityRecords",
                column: "SearchId");

            migrationBuilder.CreateIndex(
                name: "IX_EntityRecords_StoreId",
                table: "EntityRecords",
                column: "StoreId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EntityRecords");
        }
    }
}
