using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Daleel.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddProductProfileSpecsJson : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SpecsJson",
                table: "ProductProfiles",
                type: "character varying(8000)",
                maxLength: 8000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SpecsJson",
                table: "ProductProfiles");
        }
    }
}
