using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Daleel.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddApiCallResponseSummary : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ResponseSummary",
                table: "ApiCallLogs",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ResponseSummary",
                table: "ApiCallLogs");
        }
    }
}
