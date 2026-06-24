using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Daleel.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddApiCallLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ApiCallLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<string>(type: "TEXT", nullable: true),
                    JobId = table.Column<int>(type: "INTEGER", nullable: true),
                    Provider = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Endpoint = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    RequestSummary = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    ResponseTimeMs = table.Column<long>(type: "INTEGER", nullable: false),
                    ResponseBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    EstimatedCost = table.Column<decimal>(type: "decimal(12,6)", nullable: false),
                    Model = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    InputTokens = table.Column<int>(type: "INTEGER", nullable: true),
                    OutputTokens = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiCallLogs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApiCallLogs_JobId",
                table: "ApiCallLogs",
                column: "JobId");

            migrationBuilder.CreateIndex(
                name: "IX_ApiCallLogs_Provider_CreatedAt",
                table: "ApiCallLogs",
                columns: new[] { "Provider", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ApiCallLogs_UserId_CreatedAt",
                table: "ApiCallLogs",
                columns: new[] { "UserId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ApiCallLogs");
        }
    }
}
