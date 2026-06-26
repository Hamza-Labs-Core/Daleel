using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Daleel.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class ApiAndFilteredLogUnixMs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // CreatedAt changes from a TEXT DateTimeOffset to a Unix-ms INTEGER. Existing rows hold ISO
            // text that can't be read back as a long, so clear these two append-only audit/analytics logs
            // before the type change. They carry no user-facing or billing state — only diagnostics.
            migrationBuilder.Sql("DELETE FROM \"FilteredContentLogs\";");
            migrationBuilder.Sql("DELETE FROM \"ApiCallLogs\";");

            migrationBuilder.AlterColumn<long>(
                name: "CreatedAt",
                table: "FilteredContentLogs",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(DateTimeOffset),
                oldType: "TEXT");

            migrationBuilder.AlterColumn<long>(
                name: "CreatedAt",
                table: "ApiCallLogs",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(DateTimeOffset),
                oldType: "TEXT");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "CreatedAt",
                table: "FilteredContentLogs",
                type: "TEXT",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "INTEGER");

            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "CreatedAt",
                table: "ApiCallLogs",
                type: "TEXT",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "INTEGER");
        }
    }
}
