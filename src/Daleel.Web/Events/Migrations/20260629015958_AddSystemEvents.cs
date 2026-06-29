using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Daleel.Web.Events.Migrations
{
    /// <inheritdoc />
    public partial class AddSystemEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "system_events",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Category = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    EventType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Severity = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Source = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: false),
                    Summary = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    DetailsJson = table.Column<string>(type: "jsonb", nullable: false),
                    CorrelationId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UserHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_system_events", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_system_events_Category_Timestamp",
                table: "system_events",
                columns: new[] { "Category", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_system_events_CorrelationId",
                table: "system_events",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_system_events_Severity_Timestamp",
                table: "system_events",
                columns: new[] { "Severity", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_system_events_Timestamp",
                table: "system_events",
                column: "Timestamp");

            migrationBuilder.CreateIndex(
                name: "IX_system_events_UserHash",
                table: "system_events",
                column: "UserHash");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "system_events");
        }
    }
}
